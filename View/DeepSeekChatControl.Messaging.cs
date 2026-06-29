using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 核心消息发送与 API 交互：SendMessage、流式处理、MCP 工具调用、上下文构建。
    /// 搜索优化 → Search.cs | 技能系统 → Skills.cs | Agent/重试 → Agent.cs
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Core Messaging

#pragma warning disable VSTHRD100 // async void 用于 WPF 事件链，已通过双层 try-catch 加固异常安全
        private async void SendMessage()
        {
            try
            {
                await SendMessageCoreAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] SendMessage 致命异常（async void 安全网）: {ex.Message}", ex);
                lock (_lock) { _isGenerating = false; }
                DisposeStreamingCts();
                UpdateButtonsState();
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.error"], ex.Message);
                }
                catch { }
            }
        }

        /// <summary>
        /// SendMessage 的核心异步逻辑，从 async void 中分离以加固异常边界。
        /// </summary>
        private async Task SendMessageCoreAsync()
        {
            lock (_lock)
            {
                if (_isGenerating) return;
            }

            var userText = InputTextBox.Text?.Trim();
            // ── 安全净化：防止用户通过 <|tool_calls|> 等标记注入工具调用 ──
            userText = StringExtensions.SanitizeUserInput(userText ?? string.Empty);
            // ── 时间词语解析：将"今天""本周"等替换为具体日期 ──
            userText = ResolveTimeExpressions(userText ?? string.Empty);
            if (string.IsNullOrWhiteSpace(userText)) userText = string.Empty;
            bool hasAttachments = _attachedFilePaths.Count > 0;

            // 编辑模式
            if (_pendingEditMsgIndex >= 0)
            {
                if (string.IsNullOrEmpty(userText))
                {
                    _pendingEditMsgIndex = -1;
                    StatusLabel.Text = LocalizationService.Instance["status.ready"];
                    return;
                }
                await HandleEditResendAsync(_pendingEditMsgIndex, userText);
                return;
            }

            lock (_lock)
            {
                if (_isGenerating) return;
                _isGenerating = true;
            }

            // ── 🚀 立即更新 UI：清空输入框、禁用按钮，让用户看到即时反馈 ──
            InputTextBox.Text = string.Empty;
            UpdateButtonsState();

            // 斜杠命令处理
            string? skillInstructions = null;
            if (!string.IsNullOrEmpty(userText) && userText.StartsWith("/"))
            {
                skillInstructions = await ResolveSlashCommandAsync(userText);
                if (skillInstructions == null)
                {
                    lock (_lock) { _isGenerating = false; }
                    UpdateButtonsState();
                    return;
                }
            }

            if (string.IsNullOrEmpty(userText) && !hasAttachments)
            {
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                return;
            }

            // 校验 API 密钥
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
                var warningMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = ApiKeyMissingMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(warningMsg);
                AddMessagesHtml("assistant", ApiKeyMissingMessage);
                UpdateBrowser();
                StatusLabel.Text = LocalizationService.Instance["status.apiKeyMissing"];
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                return;
            }

            // 仅在 ApiService 未初始化时创建，避免重置累计 Token 计数器
            if (_apiService == null)
            {
                InitializeApiService();
            }
            if (_apiService == null)
            {
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                return;
            }

            // ── 单轮 Cache 统计快照：本次问答开始时的累计值 ──
            _apiService?.TakeCacheSnapshot();

            InitializeContextServices();

            // 解析上传的文件
            string fileContext = string.Empty;
            List<string> attachedFileNames = new();
            List<FileParseResult> parseResults = new();

            if (_attachedFilePaths.Count > 0)
            {
                StatusLabel.Text = LocalizationService.Instance["status.parsingFile"];
                parseResults = await FileParserService.ParseFilesAsync(_attachedFilePaths);
                attachedFileNames = parseResults.Where(r => r.Success).Select(r => r.FileName).ToList();
                fileContext = FileParserService.FormatParseResultsForContext(parseResults);
                if (!string.IsNullOrEmpty(fileContext))
                    Logger.Info($"文件解析完成: {attachedFileNames.Count} 个文件");
            }

            // 构建用户消息内容
            string userDisplayContent = userText ?? string.Empty;
            if (string.IsNullOrEmpty(userDisplayContent) && attachedFileNames.Count > 0)
                userDisplayContent = $"[已上传 {attachedFileNames.Count} 个文件]";

            string fullUserContent;
            if (!string.IsNullOrEmpty(fileContext) && !string.IsNullOrEmpty(userText))
                fullUserContent = fileContext + "\n" + userText;
            else if (!string.IsNullOrEmpty(fileContext))
                fullUserContent = fileContext + "\n请分析以上文件内容。";
            else
                fullUserContent = userText ?? string.Empty;

            // ── 🚀 立即显示用户消息气泡（在路由/技能调用之前）──
            var earlyUserMsg = new ChatMessage
            {
                Role = "user",
                Content = userDisplayContent,
                AttachedFileNames = attachedFileNames,
                AttachedFiles = parseResults,
                Timestamp = DateTime.Now,
            };
            int earlyUserMsgIndex;
            lock (_lock)
            {
                var tree = EnsureTree();
                tree.AddChildMessage(earlyUserMsg);
                SyncMessagesFromTree();
                _contextManager.AddUserMessage(fullUserContent);
                earlyUserMsgIndex = _messages.Count - 1;
            }
            AddMessagesHtml("user", userDisplayContent, null, parseResults, earlyUserMsgIndex);
            UpdateBrowser();
            ClearAttachedFiles();
            AutoTitleSession();
            // 注意：InputTextBox 和 UpdateButtonsState 已在上方立即执行

                // ── URL 采用模型驱动的工具调用模式处理 ──
                // URL 作为纯文本原样发送给 LLM，由 LLM 自行决定是否调用 fetch_webpage 工具抓取。
                // 不做预处理（不提取、不预抓取），避免浪费 token 和网络请求。

                // @agent 显式路由
                string agentRoutedUserText = userText ?? string.Empty;
                AgentRoutingResult? explicitRoute = null;
                string? agentSkillInstructions = null;
                if (!string.IsNullOrEmpty(userText) && userText.StartsWith("@"))
                {
                    // 提取 @agent 后的内容：格式 @agent [/skill] [message]
                    var atParts = userText.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    string agentName = atParts.Length > 0 ? atParts[0] : string.Empty;
                    agentRoutedUserText = atParts.Length > 1 ? atParts[1] : string.Empty;

                    // 显式路由到指定 Agent（UI 层简单解析）
                    explicitRoute = ParseExplicitAgentRoute(agentName);

                    // ── @agent /skill 组合：先解析技能指令，注入到 Agent 工作流 ──
                    if (!string.IsNullOrWhiteSpace(agentRoutedUserText) && agentRoutedUserText.StartsWith("/"))
                    {
                        string? skillResult = await ResolveSlashCommandAsync(agentRoutedUserText);
                        if (skillResult == null)
                        {
                            lock (_lock) { _isGenerating = false; }
                            UpdateButtonsState();
                            return;
                        }
                        if (!string.IsNullOrEmpty(skillResult))
                        {
                            agentSkillInstructions = skillResult;
                            Logger.Info($"[Agent] @agent /skill 组合: Agent={explicitRoute.TargetAgent}, Skill 指令已解析 ({skillResult.Length} 字符)");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(agentRoutedUserText))
                    {
                        StatusLabel.Text = string.Format(LocalizationService.Instance["status.switchedAgent"], explicitRoute.TargetAgent);
                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        return;
                    }
                    Logger.Info($"[Agent] @agent 显式路由: → {explicitRoute.TargetAgent}, 消息: \"{agentRoutedUserText}\""
                        + (agentSkillInstructions != null ? " [含Skill指令]" : ""));
                }

                // 所有非斜杠命令消息统一走 Agent 工作流（从 AskAgent 起始）
                if (_activeAgent != null && _agentFactory != null && !string.IsNullOrEmpty(userText) && !userText.StartsWith("/"))
                {
                    // ── 确保系统提示词已初始化（新会话时 _fixedSystemPrompt 为 null，
                    //     BuildRequestMessagesAsync 在上方未被调用，需在此处补做初始化）──
                    await EnsureSystemPromptInitializedAsync();

                    // ── 预分类任务规模，Large 任务提前路由到 Plan Agent ──
                    var taskSize = Services.Agents.EditAgent.ClassifyTaskSize(userText);
                    Logger.Info($"[TaskSize] \"{userText.Truncate(60)}\" → {taskSize}");

                    var routing = explicitRoute ?? new AgentRoutingResult
                    {
                        TargetAgent = AgentType.Ask,
                        Confidence = "high",
                        Reason = "统一入口 AskAgent",
                        NeedsPlanning = taskSize == TaskSize.Large,
                        TaskSize = taskSize,
                    };

                    // ── 上下文感知意图覆盖：当存在待处理计划时的特殊路由 ──
                    routing = OverrideRoutingForPlanContext(userText, routing);

                    // ── 统一走 Agent 工作流：所有非斜杠命令消息均由 AskAgent 入口处理 ──

                        // ── 更新用户消息的 Agent 类型（用于编辑/重试时判断是否分支）──
                        lock (_lock)
                        {
                            if (_messages.Count > 0)
                                _messages[_messages.Count - 1].AgentType = routing.TargetAgent;

                            // ── @agent /skill 组合：注入技能指令到 Agent 上下文 ──
                            if (!string.IsNullOrEmpty(agentSkillInstructions))
                            {
                                _contextManager.AddCustomMessage("system", agentSkillInstructions);
                                Logger.Info($"[AgentFlow] Skill 指令已注入 Agent 上下文 (长度: {agentSkillInstructions.Length})");
                            }
                        }
                        int capturedUserMsgIndex = _messages.Count - 1;

                        var capturedUserText = agentRoutedUserText;
                        var capturedFileContext = fileContext;
                        var capturedRoute = routing;
                        var capturedMsgIdx = capturedUserMsgIndex;

                        // ── 创建 Agent 路径的 CancellationTokenSource（停止按钮依赖此 CTS）──
                        var agentCts = CreateNewStreamingCts();

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await RunAgentWorkflowAsync(capturedUserText, capturedFileContext, capturedRoute);
                                RecordAgentFileChanges(capturedMsgIdx);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[AgentFlow] 工作流异常: {ex.Message}", ex);
                            }
                            finally
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                lock (_lock) { _isGenerating = false; }
                                UpdateButtonsState();
                                StatusLabel.Text = LocalizationService.Instance["status.ready"];

                                // ── 兜底标题生成：确保新会话标题一定会更新 ──
                                if (_pendingAiTitle && !string.IsNullOrWhiteSpace(_firstUserMessageForTitle))
                                {
                                    FallbackAutoTitle(_firstUserMessageForTitle);
                                }
                            }
                        });
                        return;
                }

        }
#pragma warning restore VSTHRD100
        private string? BuildRoutingContext(string userText, string? fileContext, List<FileParseResult>? parseResults)
        {
            // 如果用户消息已经很长（≥50字），不需要附加上下文
            if (!string.IsNullOrEmpty(userText) && userText.Length >= 50)
                return null;

            var sb = new StringBuilder();

            // ── 1. 最近对话历史摘要（最多最近 5 轮）──
            try
            {
                var history = _contextManager?.GetConversationHistory();
                if (history != null && history.Count > 0)
                {
                    // 只取最近的角色交替消息（user/assistant），最多 5 对
                    var recentMessages = history
                        .Where(m => m.Role == "user" || m.Role == "assistant")
                        .Reverse()
                        .Take(10)
                        .Reverse()
                        .ToList();

                    if (recentMessages.Count > 0)
                    {
                        sb.AppendLine("最近对话:");
                        foreach (var msg in recentMessages)
                        {
                            string role = msg.Role == "user" ? "用户" : "AI";
                            string content = (msg.Content ?? "").Truncate(120);
                            if (!string.IsNullOrWhiteSpace(content))
                                sb.AppendLine($"- {role}: {content}");
                        }
                    }
                }
            }
            catch { }

            // ── 2. 附加文件信息 ──
            if (parseResults != null && parseResults.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("用户附加了以下文件:");
                foreach (var pr in parseResults)
                {
                    if (pr.FileName != null)
                    {
                        string snippet = (pr.Content ?? "").Truncate(200);
                        sb.AppendLine($"- {pr.FileName}");
                        if (!string.IsNullOrWhiteSpace(snippet))
                            sb.AppendLine($"  内容片段: {snippet}");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(fileContext))
            {
                sb.AppendLine();
                sb.AppendLine($"用户附加了文件上下文: {fileContext.Truncate(300)}");
            }

            string result = sb.ToString().Trim();
            return result.Length > 0 ? result : null;
        }

        /// <summary>
        /// 构建发送给 API 的消息列表。
        /// 委托 InitializeSystemContextAsync 完成 prompt/skill/memory 初始化，
        /// 此处只处理每轮可变的 RAG + 搜索上下文 + 统计。
        /// </summary>
        private async Task<List<ChatApiMessage>> BuildRequestMessagesAsync(string searchContext = "")
        {
            await InitializeSystemContextAsync();

            if (_ragService != null && _ragService.IsEnabled && _options?.EnableRag == true)
            {
                try
                {
                    var userMessages = _contextManager.GetConversationHistory().Where(m => m.Role == "user").ToList();
                    string query = userMessages.Count > 0 ? (userMessages.Last().Content ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        int topK = _options?.RagTopK ?? 5;
                        string ragContext = await _ragService.RetrieveContextAsync(query, topK);
                        _contextManager.SetRagContext(string.IsNullOrWhiteSpace(ragContext) ? null : ragContext);
                        if (!string.IsNullOrWhiteSpace(ragContext))
                            Logger.Info($"[RAG] 检索上下文已注入 (topK={topK}, 长度={ragContext.Length})");
                    }
                }
                catch (Exception ex) { Logger.Warn($"[RAG] 检索失败: {ex.Message}"); _contextManager.SetRagContext(null); }
            }

            _contextManager.SetSearchContext(string.IsNullOrWhiteSpace(searchContext) ? null : searchContext);

            if (_options?.ShowContextStats == true || _contextManager.UsageRatio > 0.7)
            {
                var stats = _contextManager.GetStats();
                string level = stats.UsageRatio > 0.9 ? "⚠️" : stats.UsageRatio > 0.7 ? "ℹ️" : "";
                Logger.Info($"[ContextStats] {level} Token: {stats.EstimatedTokens:N0}/{stats.TokenBudget:N0} ({stats.UsagePercent:F1}%) | 轮次: {stats.TurnCount} | 消息: {stats.MessageCount}");
                if (stats.UsageRatio > 0.9)
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.compressionTriggered"], stats.UsagePercent);
            }

            return _contextManager.BuildApiMessages();
        }

        private void StopGeneration()
        {
            try
            {
                // ── 停止前捕获当前流式消息的索引和部分内容 ──
                int streamingIdx;
                string partialContent;
                string partialReasoning;
                lock (_lock)
                {
                    streamingIdx = _currentStreamingMsgIndex;
                    if (streamingIdx >= 0 && streamingIdx < _messages.Count)
                    {
                        var msg = _messages[streamingIdx];
                        partialContent = msg.Content ?? string.Empty;
                        partialReasoning = msg.ReasoningContent ?? string.Empty;
                        msg.IsStreaming = false;
                    }
                    else
                    {
                        partialContent = string.Empty;
                        partialReasoning = string.Empty;
                    }
                    _currentStreamingMsgIndex = -1;
                }

                CancelStreaming();
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                StatusLabel.Text = LocalizationService.Instance["status.stopped"];

                // ── 发送 streamEnd 以渲染 Markdown 并注入重试按钮 ──
                if (streamingIdx >= 0 && !string.IsNullOrEmpty(partialContent))
                {
                    PostStreamEnd(streamingIdx, partialContent, partialReasoning);
                }
            }
            catch (Exception ex) { Logger.Error($"StopGeneration 异常: {ex.Message}", ex); }
        }

#pragma warning disable VSTHRD100
        private async void ClearConversation()
        {
            try
            {
                lock (_lock)
                {
                    if (_isGenerating) { CancelStreaming(); _isGenerating = false; }
                }
                UpdateButtonsState();
                ClearCurrentSessionMessages();
                Logger.Info("清空对话完成");
            }
            catch (Exception ex)
            {
                Logger.Error($"ClearConversation 异常: {ex.Message}", ex);
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.clearFailed"], ex.Message);
                }
                catch { }
            }
        }
#pragma warning restore VSTHRD100

        private void UpdateButtonsState()
        {
            SendButton.IsEnabled = !_isGenerating;
            StopButton.Visibility = _isGenerating ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Visibility = _isGenerating ? Visibility.Collapsed : Visibility.Visible;
            InputTextBox.IsReadOnly = _isGenerating;
            ClearButton.IsEnabled = !_isGenerating;

            // 流式生成结束时刷新消费显示
            if (!_isGenerating)
            {
                RefreshConsumptionDisplay();
            }
        }

        #endregion

        #region Utility Helpers

        /// <summary>
        /// 将用户输入中的时间词语替换为具体日期。
        /// </summary>
        private static string ResolveTimeExpressions(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return query;

            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");
            string yesterday = now.AddDays(-1).ToString("yyyy-MM-dd");
            string tomorrow = now.AddDays(1).ToString("yyyy-MM-dd");
            string thisWeekStart = now.AddDays(-(int)now.DayOfWeek + 1).ToString("yyyy-MM-dd");
            string thisWeekEnd = now.AddDays(7 - (int)now.DayOfWeek).ToString("yyyy-MM-dd");
            string thisMonth = now.ToString("yyyy年M月");
            string lastMonth = now.AddMonths(-1).ToString("yyyy年M月");
            string thisYear = now.ToString("yyyy年");

            var result = query;

            var replacements = new Dictionary<string, string>
            {
                ["今天"] = today,
                ["今日"] = today,
                ["昨天"] = yesterday,
                ["昨日"] = yesterday,
                ["明天"] = tomorrow,
                ["明日"] = tomorrow,
                ["本周"] = $"{thisWeekStart} 至 {thisWeekEnd}",
                ["这周"] = $"{thisWeekStart} 至 {thisWeekEnd}",
                ["这个月"] = thisMonth,
                ["本月"] = thisMonth,
                ["上个月"] = lastMonth,
                ["上月"] = lastMonth,
                ["今年"] = thisYear,
                ["当前日期"] = today,
                ["目前"] = $"最新(截至{today})",
                ["最近"] = $"最近(截至{today})",
                ["最新"] = $"最新(截至{today})",
                ["近期"] = $"近期(截至{today})",
                ["最近一周"] = $"最近一周({thisWeekStart} 至 {thisWeekEnd})",
                ["最近一个月"] = $"最近一个月({lastMonth} 至 {thisMonth})",
                ["最近几天"] = $"最近几天({yesterday} 至 {today})",
                ["前几天"] = $"前几天({yesterday} 至 {today})",
            };

            foreach (var kvp in replacements)
                result = result.Replace(kvp.Key, kvp.Value);

            if (result != query)
                Logger.Info($"时间词语解析: \"{query}\" → \"{result}\"");

            return result;
        }

        /// <summary>
        /// 截断文本到指定长度，超出部分用 "..." 替代。
        /// </summary>
        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 预处理 OCR 工具参数：AI 可能传文件名而非 base64，自动转换。
        /// 同时记录 OCR 调用参数格式提醒（input_data 必需，output_mode/file_type 可选）。
        /// 静态方法，可由 BaseAgent 在执行工具前调用。
        /// </summary>
        internal static string SanitizeOcrToolArguments(string toolName, string argumentsJson)
        {
            var ocrKeywords = new[] { "ocr", "recognize_text", "paddle_ocr", "ocr_image", "image_to_text", "read_text" };
            bool isOcrTool = ocrKeywords.Any(k => toolName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!isOcrTool)
                return argumentsJson;

            // ── OCR 参数格式提醒 ──
            Logger.Info($"[OCR] 📋 调用 OCR 工具 `{toolName}`，期望参数格式:\n" +
                "  • input_data (必需): 文件路径、URL 或 Base64 字符串\n" +
                "  • output_mode (可选, 默认 \"simple\"): \"simple\" | \"detailed\"\n" +
                "  • file_type (可选): \"image\" | \"pdf\" | null（input_data 为 URL 时必需）");

            try
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
                if (args == null || args.Count == 0)
                {
                    Logger.Warn($"[OCR] ⚠️ OCR 工具 `{toolName}` 未收到任何参数！请确保传入 input_data。");
                    return argumentsJson;
                }

                var imageParamNames = new[] { "input_data", "input", "data", "image", "image_base64", "base64", "image_data", "image_path", "file_path", "path", "file_url", "url", "image_url" };
                string? imageKey = null;
                string? imageValue = null;

                foreach (var kvp in args)
                {
                    if (imageParamNames.Any(n => string.Equals(kvp.Key, n, StringComparison.OrdinalIgnoreCase)))
                    {
                        imageKey = kvp.Key;
                        imageValue = kvp.Value.GetString();
                        break;
                    }
                }

                if (imageKey == null || string.IsNullOrWhiteSpace(imageValue))
                    return argumentsJson;

                bool isProbablyBase64 = imageValue.Length > 200
                    && !imageValue.Contains(' ')
                    && !imageValue.Contains('\n')
                    && imageValue.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');

                if (isProbablyBase64)
                    return argumentsJson;

                // ── URL 检测：如果是 URL 且未提供 file_type，发出提醒 ──
                bool isUrl = imageValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || imageValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (isUrl)
                {
                    bool hasFileType = args.ContainsKey("file_type") || args.ContainsKey("type");
                    if (!hasFileType)
                    {
                        Logger.Warn($"[OCR] ⚠️ input_data 为 URL 但未提供 file_type 参数！\n" +
                            "  • 请补充 \"file_type\": \"image\" 或 \"file_type\": \"pdf\"\n" +
                            $"  • 当前 URL: {imageValue.Substring(0, Math.Min(80, imageValue.Length))}...");
                    }
                }

                if (File.Exists(imageValue))
                {
                    try
                    {
                        // RAG-SOURCE: file-read 读取图片文件（OCR 预处理 base64 转换）
                        byte[] fileBytes = File.ReadAllBytes(imageValue);
                        string base64 = Convert.ToBase64String(fileBytes);
                        args[imageKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(base64));
                        return JsonSerializer.Serialize(args);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[OCR-Sanitize] 读取本地文件失败: {ex.Message}");
                        return argumentsJson;
                    }
                }

                string fileName = Path.GetFileName(imageValue);
                var searchDirs = new List<string>();

                string clipboardDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekVS", "temp", "clipboard");
                if (Directory.Exists(clipboardDir)) searchDirs.Add(clipboardDir);

                string contextDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekVS", "temp", "context");
                if (Directory.Exists(contextDir)) searchDirs.Add(contextDir);

                string? resolvedPath = null;
                foreach (var dir in searchDirs.Distinct())
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) { resolvedPath = candidate; break; }
                    try
                    {
                        var matches = Directory.GetFiles(dir, fileName, SearchOption.TopDirectoryOnly);
                        if (matches.Length > 0) { resolvedPath = matches[0]; break; }
                    }
                    catch { }
                }

                if (resolvedPath != null)
                {
                    // RAG-SOURCE: file-read 读取图片文件（OCR base64 转换）
                    byte[] fileBytes = File.ReadAllBytes(resolvedPath);
                    string base64 = Convert.ToBase64String(fileBytes);
                    args[imageKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(base64));
                    return JsonSerializer.Serialize(args);
                }

                Logger.Warn($"[OCR-Sanitize] 无法解析文件 '{imageValue}'");
                return argumentsJson;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OCR-Sanitize] 参数预处理异常: {ex.Message}");
                return argumentsJson;
            }
        }

        /// <summary>
        /// 记录 DeepSeek Prompt Cache 命中率到日志。
        /// 从 _apiService.LastUsage 读取 cache 统计信息并格式化输出。
        /// </summary>
        /// <param name="round">当前工具调用轮次（0 表示非工具循环模式）</param>
        private void LogCacheHitRate(int round = 0)
        {
            try
            {
                var usage = _apiService?.LastUsage;
                if (usage == null) return;

                int hit = usage.PromptCacheHitTokens;
                int miss = usage.PromptCacheMissTokens;
                int total = hit + miss;
                double rate = usage.CacheHitRate;

                string roundInfo = round > 0 ? $"[轮次#{round}] " : "";
                string level = rate >= 0.90 ? "🟢" : rate >= 0.50 ? "🟡" : rate >= 0.20 ? "🟠" : "🔴";

                Logger.Info($"[Cache] {level} {roundInfo}命中率: {usage.CacheHitRatePercent} " +
                    $"(命中 {hit:N0} / 未命中 {miss:N0} / 总计 {total:N0} tokens)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录命中率异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录跨所有工具调用轮次的累计 Cache 命中率到日志。
        /// 在所有轮次结束后调用，输出逐轮明细 + 汇总总计。
        /// </summary>
        private void LogTotalCacheHitRate(int finalRound, long totalHit, long totalMiss,
            long totalPrompt, long totalCompletion)
        {
            try
            {
                long totalCacheable = totalHit + totalMiss;
                if (totalCacheable == 0) return;

                double aggregateRate = (double)totalHit / totalCacheable;
                string level = aggregateRate >= 0.90 ? "🟢" : aggregateRate >= 0.50 ? "🟡" : aggregateRate >= 0.20 ? "🟠" : "🔴";

                Logger.Info($"[Cache] ═══════════════════════════════════════");
                Logger.Info($"[Cache] {level} 累计汇总 ({finalRound} 轮)");
                Logger.Info($"[Cache]   总 Cache 命中率: {aggregateRate * 100:F1}%");
                Logger.Info($"[Cache]   累计命中: {totalHit:N0} tokens");
                Logger.Info($"[Cache]   累计未命中: {totalMiss:N0} tokens");
                Logger.Info($"[Cache]   累计 Prompt: {totalPrompt:N0} tokens");
                Logger.Info($"[Cache]   累计 Completion: {totalCompletion:N0} tokens");
                Logger.Info($"[Cache] ═══════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录汇总命中率异常: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// 解析用户显式指定的 Agent（如 "@edit" → AgentType.Edit）。
        /// 从 AgentDispatcher.ParseExplicitAgentRoute 搬过来，保留在 UI 层。
        /// </summary>
        private static AgentRoutingResult ParseExplicitAgentRoute(string agentName)
        {
            AgentType target = agentName.ToLowerInvariant() switch
            {
                "ask" or "问答" => AgentType.Ask,
                "plan" or "规划" => AgentType.Plan,
                "edit" or "修改" => AgentType.Edit,
                "explore" or "探索" => AgentType.Explore,
                "build" or "构建" or "编译" => AgentType.Build,
                _ => AgentType.Ask,
            };

            return new AgentRoutingResult
            {
                TargetAgent = target,
                Confidence = "high",
                Reason = $"用户显式指定 @{agentName}",
                NeedsPlanning = target == AgentType.Plan,
                IsExplicit = true,
            };
        }
    }
}
