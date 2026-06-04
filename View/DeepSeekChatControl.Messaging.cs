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

            InitializeApiService();
            if (_apiService == null)
            {
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                return;
            }

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
                if (_agentDispatcher != null && !string.IsNullOrEmpty(userText) && userText.StartsWith("@"))
                {
                    // 提取 @agent 后的内容：格式 @agent [/skill] [message]
                    var atParts = userText.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    string agentName = atParts.Length > 0 ? atParts[0] : string.Empty;
                    agentRoutedUserText = atParts.Length > 1 ? atParts[1] : string.Empty;

                    // 显式路由到指定 Agent
                    explicitRoute = await _agentDispatcher.RouteAsync($"@{agentName}");

                    // ── @agent /skill 组合：先解析技能指令，注入到 Agent 工作流 ──
                    if (!string.IsNullOrWhiteSpace(agentRoutedUserText) && agentRoutedUserText.StartsWith("/"))
                    {
                        string? skillResult = await ResolveSlashCommandAsync(agentRoutedUserText);
                        if (skillResult == null)
                        {
                            // 内置命令已直接执行（help/create-skill/refresh-skills），无需继续
                            lock (_lock) { _isGenerating = false; }
                            UpdateButtonsState();
                            return;
                        }
                        if (!string.IsNullOrEmpty(skillResult))
                        {
                            // 技能指令将在 Agent 工作流中作为 system 消息注入
                            agentSkillInstructions = skillResult;
                            Logger.Info($"[AgentDispatcher] @agent /skill 组合: Agent={explicitRoute.TargetAgent}, Skill 指令已解析 ({skillResult.Length} 字符)");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(agentRoutedUserText))
                    {
                        StatusLabel.Text = string.Format(LocalizationService.Instance["status.switchedAgent"], explicitRoute.TargetAgent);
                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        return;
                    }
                    Logger.Info($"[AgentDispatcher] @agent 显式路由: → {explicitRoute.TargetAgent}, 消息: \"{agentRoutedUserText}\""
                        + (agentSkillInstructions != null ? " [含Skill指令]" : ""));
                }

                // 多 Agent 路由
                if (_agentDispatcher != null && !string.IsNullOrEmpty(userText) && !userText.StartsWith("/"))
                {
                    // ── 构建对话上下文摘要，帮助路由 AI 理解"进行修复"等短消息的指代 ──
                    string? conversationContext = BuildRoutingContext(userText, fileContext, parseResults);
                    var routing = explicitRoute ?? await _agentDispatcher.RouteAsync(userText, conversationContext);

                    // ── 上下文感知意图覆盖：当存在待处理计划时的特殊路由 ──
                    routing = OverrideRoutingForPlanContext(userText, routing);

                    bool needsAgent = routing.TargetAgent != AgentType.Ask
                        || routing.NeedsPlanning;

                    if (needsAgent)
                    {
                        // ── 更新用户消息的 Agent 类型（用于编辑/重试时判断是否分支）──
                        lock (_lock)
                        {
                            if (_messages.Count > 0)
                                _messages[_messages.Count - 1].AgentType = routing.TargetAgent;

                            // ── @agent /skill 组合：注入技能指令到 Agent 上下文 ──
                            if (!string.IsNullOrEmpty(agentSkillInstructions))
                            {
                                _contextManager.AddCustomMessage("system", agentSkillInstructions);
                                Logger.Info($"[AgentDispatcher] Skill 指令已注入 Agent 上下文 (长度: {agentSkillInstructions.Length})");
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
                                Logger.Error($"[AgentDispatcher] 工作流异常: {ex.Message}", ex);
                            }
                            finally
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                lock (_lock) { _isGenerating = false; }
                                UpdateButtonsState();
                                StatusLabel.Text = LocalizationService.Instance["status.ready"];
                            }
                        });
                        return;
                    }
                }

                // ── @agent 清洁：只清除 API 内容中的 @agent 前缀，保留气泡显示 ──
                if (explicitRoute != null && !string.IsNullOrEmpty(agentRoutedUserText))
                {
                    // fullUserContent 中替换 @agent 部分为干净文本（API 不感知 @agent）
                    string cleanText = agentRoutedUserText;
                    if (!string.IsNullOrEmpty(fullUserContent) && fullUserContent.StartsWith(userText ?? string.Empty))
                    {
                        fullUserContent = cleanText + fullUserContent.Substring((userText ?? string.Empty).Length);
                    }
                    // userDisplayContent 保留原始 @agent 前缀，使对话气泡正常显示 @
                    Logger.Info($"[AgentDispatcher] @agent API 内容已清洁，气泡保留 @: \"{userDisplayContent}\"");
                }

                // ── 更新用户消息的 @agent 显式路由标记 ──
                if (explicitRoute != null)
                {
                    lock (_lock)
                    {
                        if (_messages.Count > 0)
                            _messages[_messages.Count - 1].AgentType = explicitRoute.TargetAgent;
                    }
                }

                // 技能路由
                // 优先使用 @agent /skill 组合中已解析的技能指令
                bool isSlashCommand = !string.IsNullOrEmpty(userText) && userText.StartsWith("/");
                bool isAutoMatched = false;
                if (!string.IsNullOrEmpty(agentSkillInstructions))
                {
                    // @agent /skill 组合已在前面解析，直接使用
                    skillInstructions = agentSkillInstructions;
                    isSlashCommand = true;
                    Logger.Info($"[Skill] @agent /skill 组合: 技能指令已就绪, 跳过 RouteSkillAsync");
                }
                else if (string.IsNullOrEmpty(skillInstructions) && !string.IsNullOrEmpty(fullUserContent))
                {
                    skillInstructions = await RouteSkillAsync(fullUserContent);
                    isAutoMatched = skillInstructions != null;
                }

                // ── 注入技能指令到上下文 ──
                if (!string.IsNullOrEmpty(skillInstructions))
                {
                    lock (_lock)
                    {
                        _contextManager.AddCustomMessage("system", skillInstructions);
                    }

                    if (isSlashCommand)
                    {
                        string slashText = !string.IsNullOrEmpty(agentSkillInstructions) && !string.IsNullOrEmpty(agentRoutedUserText)
                            ? agentRoutedUserText
                            : userText!;

                        var calledSkillName = slashText.Substring(1).Split(' ')[0];
                        var skillDef = SkillService.Instance.FindSkill(calledSkillName, _skillDiscoveryResult);
                        string source = !string.IsNullOrEmpty(agentSkillInstructions)
                            ? $"@agent /skill 组合 (Agent 上下文)"
                            : $"斜杠命令 (来源: {skillDef?.Source.ToString() ?? "N/A"})";
                        Logger.Info($"[Skill] 技能指令已注入: \"{calledSkillName}\" ({source}, 长度: {skillInstructions.Length})");
                    }
                    else if (isAutoMatched)
                        Logger.Info($"[Skill] 技能指令已注入 (AI 自动匹配, 长度: {skillInstructions.Length})");
                    else
                        Logger.Info($"[Skill] 技能指令已注入 (长度: {skillInstructions.Length})");
                }

                int userMsgIndex = earlyUserMsgIndex;  // 使用早期 UI 更新中的索引

                // 创建助手消息占位
                var assistantMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ReasoningContent = string.Empty,
                    Timestamp = DateTime.Now,
                    IsStreaming = true,
                    IsRendered = false,
                };
                int assistantMsgIndex;
                lock (_lock)
                {
                    // ── 树状结构：通过 Tree 添加助手消息 ──
                    if (_tree != null)
                    {
                        _tree.AddChildMessage(assistantMsg);
                        SyncMessagesFromTree();
                        assistantMsgIndex = _messages.Count - 1;
                    }
                    else
                    {
                        _messages.Add(assistantMsg);
                        assistantMsgIndex = _messages.Count - 1;
                    }
                }
                _currentStreamingMsgIndex = assistantMsgIndex;

                // UI 已在早期更新中添加了用户气泡，此处只需添加助手占位
                AddMessagesHtml("assistant", string.Empty);
                UpdateBrowser();

                _isGenerating = true;
                UpdateButtonsState();

                bool isWebSearchEnabled = _webSearchEngine != "Off";
                StatusLabel.Text = isWebSearchEnabled ? LocalizationService.Instance["status.webSearching"] : LocalizationService.Instance["status.thinking"];

                var streamingCts = CreateNewStreamingCts();

                // 联网搜索
                string searchContext = string.Empty;
                List<WebSearchResult> capturedSearchResults = new();
                string? engineSwitchNote = null;
                if (isWebSearchEnabled && _webSearchService != null)
                {
                    ApplyWebSearchConfig();
                    if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                    {
                        StatusLabel.Text = LocalizationService.Instance["status.baiduKeyMissing"];
                        assistantMsg.Content = LocalizationService.Instance["websearch.notConfigured"];
                        assistantMsg.IsStreaming = false;
                        BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: true);
                        PostStreamEnd(assistantMsgIndex, assistantMsg.Content, string.Empty);
                        _isGenerating = false;
                        UpdateButtonsState();
                        return;
                    }

                    string timeAwareQuery = ResolveTimeExpressions(userText!);
                    string searchOptimizationInput = timeAwareQuery;

                    if (!string.IsNullOrEmpty(fileContext) && _apiService != null)
                    {
                        try
                        {
                            StatusLabel.Text = LocalizationService.Instance["status.extractingInfo"];
                            string? extractedKeyInfo = await ExtractKeyInfoForSearchAsync(fileContext, userText!, streamingCts.Token);
                            if (!string.IsNullOrWhiteSpace(extractedKeyInfo))
                            {
                                searchOptimizationInput = extractedKeyInfo + "\n用户问题：" + timeAwareQuery;
                                Logger.Info($"从附件提取关键信息成功 ({extractedKeyInfo.Length} 字符)");
                            }
                        }
                        catch (Exception ex) { Logger.Info($"附件提取失败: {ex.Message}"); }
                    }

                    string optimizedQuery = timeAwareQuery;
                    string? searchRecency = null;

                    try
                    {
                        if (_apiService != null)
                        {
                            try
                            {
                                StatusLabel.Text = LocalizationService.Instance["status.optimizingSearch"];
                                bool isBaidu = _webSearchEngine == "Baidu";
                                var optimization = await OptimizeSearchQueryAsync(searchOptimizationInput, streamingCts.Token, isBaidu);
                                if (optimization != null && !string.IsNullOrWhiteSpace(optimization.SearchQuery) && optimization.NeedSearch)
                                {
                                    optimizedQuery = optimization.SearchQuery;
                                    searchRecency = optimization.SearchRecency;
                                    Logger.Info($"AI 优化搜索词: \"{userText}\" → \"{optimizedQuery}\"");
                                }
                            }
                            catch (Exception ex) { Logger.Info($"搜索词优化失败: {ex.Message}"); }
                        }

                        var searchResults = await _webSearchService.SearchAsync(optimizedQuery, streamingCts.Token, searchRecency);
                        capturedSearchResults = searchResults;
                        if (searchResults.Count > 0)
                        {
                            string providerLabel = _webSearchService.ActiveProvider == SearchProvider.Baidu
                                ? LocalizationService.Instance["websearch.searchEngine.baidu"]
                                : LocalizationService.Instance["websearch.searchEngine.duckduckgo"];
                            StatusLabel.Text = string.Format(LocalizationService.Instance["status.searchResults"], searchResults.Count);
                            assistantMsg.Content = string.Format(LocalizationService.Instance["websearch.searchResultsHtml"],
                                searchResults.Count, providerLabel);
                            PostStreamingUpdate(assistantMsgIndex, assistantMsg.Content, string.Empty, false);

                            await EnrichSearchContextAsync(searchResults, streamingCts.Token);
                            searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);
                            Logger.Info($"联网搜索完成: {searchResults.Count} 条结果");
                        }
                        else
                        {
                            if (_webSearchService.IsBaiduQuotaExhausted)
                            {
                                engineSwitchNote = LocalizationService.Instance["websearch.quotaExhausted"];
                                assistantMsg.Content = LocalizationService.Instance["websearch.quotaExhaustedShort"];
                                PostStreamingUpdate(assistantMsgIndex, assistantMsg.Content, string.Empty, false);
                                searchResults = await _webSearchService.SearchAsync(optimizedQuery, streamingCts.Token);
                                capturedSearchResults = searchResults;
                                if (searchResults.Count > 0)
                                {
                                    await EnrichSearchContextAsync(searchResults, streamingCts.Token);
                                    searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);
                                }
                            }
                            else
                                StatusLabel.Text = LocalizationService.Instance["status.noSearchResults"];
                        }
                    }
                    catch (ApiKeyInvalidException ex)
                    {
                        Logger.Error($"[Render] 百度 API Key 无效", ex);
                        assistantMsg.Content = LocalizationService.Instance["websearch.invalidApiKey"];
                        assistantMsg.IsStreaming = false;
                        BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                        PostStreamEnd(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent);
                        lock (_lock) { _messages.Remove(assistantMsg); }
                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        CancelStreaming();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"联网搜索异常: {ex.Message}", ex);
                        StatusLabel.Text = LocalizationService.Instance["status.searchFailed"];
                    }
                }

                if (string.IsNullOrEmpty(engineSwitchNote) && _webSearchEngine == "Baidu"
                    && _webSearchService != null && _webSearchService.ActiveProvider == SearchProvider.DuckDuckGo)
                {
                    engineSwitchNote = LocalizationService.Instance["websearch.noResultsFallback"];
                }
                if (!string.IsNullOrEmpty(engineSwitchNote))
                    _pendingWarnings.Add(engineSwitchNote!);

                try
                {
                    // 带工具调用的对话循环 — 智能循环检测替代硬编码轮次限制
                    var reasoningBuffer = new StringBuilder();
                    var contentBuffer = new StringBuilder();
                    var toolCallAccumulator = new Dictionary<int, Models.ToolCallAccumulator>();
                    int streamRenderTick = 0;
                    int lastReasoningLength = 0;

                    // ── 循环检测状态 ──
                    var callSignatureHistory = new Queue<string>();          // O(1) 入队/出队
                    var callSignatureCount = new Dictionary<string, int>();  // O(1) 计数
                    var lastResultBySignature = new Dictionary<string, string>(); // 跟踪每次调用结果
                    var toolCallHistory = new List<(int Round, string Summary)>();
                    int consecutiveErrorRounds = 0;                         // 连续错误轮次计数
                    const int maxHistorySize = 30;
                    const int maxRepeatedSameCall = 5;    // 同一调用最多重复 5 次
                    const int maxConsecutiveErrors = 5;             // 连续错误上限
                    const int safetyLimit = 200;                     // 绝对安全上限
                    bool loopDetected = false;

                    // ── 重置累计 Cache 统计（本次用户消息的所有 API 调用将统一累加到 _apiService）──
                    _apiService?.ResetAccumulatedStats();

                    // ── 初始化流式渲染节流器 ──
                    _streamRenderStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    _statusUpdateStopwatch = System.Diagnostics.Stopwatch.StartNew();

                    int round = 0;
                    while (!loopDetected)
                    {
                        round++;
                        if (round > safetyLimit)
                        {
                            Logger.Warn($"[MCP] 达到安全上限 {safetyLimit} 轮，强制结束");
                            contentBuffer.Append("\n\n> ⚠️ 工具调用已达安全上限，分析可能不完整。");
                            break;
                        }

                        toolCallAccumulator.Clear();
                        reasoningBuffer.Clear();
                        contentBuffer.Clear();
                        streamRenderTick = 0;
                        lastReasoningLength = 0;

                        var requestMessages = await BuildRequestMessagesAsync(searchContext);

                        List<ToolDefinition>? toolDefs = null;

                        // ── 收集工具定义（内置工具 + MCP 外部工具）──
                        var allDefs = new List<ToolDefinition>();

                        // 内置工作区工具
                        if (_builtInToolService != null)
                        {
                            var allowedTools = _agentDispatcher?.ActiveAgentAllowedTools;
                            var builtInDefs = _builtInToolService.GetFilteredToolDefinitions(allowedTools);
                            allDefs.AddRange(builtInDefs);
                        }

                        // MCP 外部工具
                        if (_mcpManager != null && _mcpManager.AllTools.Count > 0)
                        {
                            var allowedTools = _agentDispatcher?.ActiveAgentAllowedTools;
                            var mcpDefs = _mcpManager.GetFilteredToolDefinitions(allowedTools);
                            allDefs.AddRange(mcpDefs);
                        }

                        if (allDefs.Count > 0)
                        {
                            toolDefs = allDefs;
                            Logger.Info($"[MCP] 本轮携带 {toolDefs.Count} 个工具定义"
                                + (_agentDispatcher?.ActiveAgentAllowedTools != null ? " (已按 Agent 白名单过滤)" : ""));
                        }

                        var apiService = _apiService!;

                        // ── 流中断恢复：最多 3 次重试（指数退避 2s / 4s / 8s）──
                        bool streamSuccess = false;
                        int streamAttempt = 0;
                        const int maxStreamAttempts = 4; // 1 initial + 3 retries
                        string savedPartialContent = "";
                        string savedPartialReasoning = "";

                        while (!streamSuccess && streamAttempt < maxStreamAttempts)
                        {
                            try
                            {
                                // 如果是重试，将已接收的部分内容注入为对话上下文
                                if (streamAttempt > 0)
                                {
                                    requestMessages = BuildResumeMessages(requestMessages, savedPartialContent, savedPartialReasoning);
                                    // 将部分内容预置到缓冲区，新内容追加其后
                                    contentBuffer.Append(savedPartialContent);
                                    reasoningBuffer.Append(savedPartialReasoning);
                                    Logger.Info($"[Stream] 断点续传：第 {streamAttempt + 1}/{maxStreamAttempts} 次尝试，已注入 {savedPartialContent.Length} 字符部分内容");
                                }

                                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, toolDefs, streamingCts.Token))
                                {
                            if (chunk.StartsWith("[THINKING]"))
                            {
                                var thinking = chunk.Substring(10);
                                reasoningBuffer.Append(thinking);

                                int curReasoningLen = reasoningBuffer.Length;
                                // 增大节流阈值：从 80 → 200 字符，减少渲染频率
                                if (curReasoningLen - lastReasoningLength >= 200)
                                {
                                    string reasoningText = reasoningBuffer.ToString();
                                    assistantMsg.ReasoningContent = reasoningText;
                                    lastReasoningLength = curReasoningLen;
                                    // ── 使用批处理 + 非阻塞 PostWebMessageAsString ──
                                    string status = _statusUpdateStopwatch != null
                                        && _statusUpdateStopwatch.ElapsedMilliseconds >= StatusUpdateMinIntervalMs
                                        ? LocalizationService.Instance["status.deepThinking"] : null;
                                    BatchStreamingUpdate(assistantMsgIndex, contentBuffer.ToString(), reasoningText, status: status);
                                    if (status != null) _statusUpdateStopwatch?.Restart();
                                }
                            }
                            else if (chunk.StartsWith("[TOOL_CALL]"))
                            {
                                var tcJson = chunk.Substring(11);
                                try
                                {
                                    var deltas = JsonSerializer.Deserialize<List<ToolCallDelta>>(tcJson);
                                    if (deltas != null)
                                    {
                                        foreach (var delta in deltas)
                                        {
                                            if (!toolCallAccumulator.ContainsKey(delta.Index))
                                                toolCallAccumulator[delta.Index] = new Models.ToolCallAccumulator();
                                            var acc = toolCallAccumulator[delta.Index];
                                            if (!string.IsNullOrEmpty(delta.Id)) acc.Id = delta.Id!;
                                            if (!string.IsNullOrEmpty(delta.Type)) acc.Type = delta.Type;
                                            if (delta.Function != null)
                                            {
                                                if (!string.IsNullOrEmpty(delta.Function.Name)) acc.FunctionName = delta.Function.Name;
                                                if (!string.IsNullOrEmpty(delta.Function.Arguments)) acc.ArgumentsBuilder.Append(delta.Function.Arguments);
                                            }
                                        }
                                    }
                                }
                                catch (JsonException) { }
                                // 节流 + LINQ 优化：仅在前缀变化时更新
                                UpdateStatusToolCall(string.Join(", ",
                                    toolCallAccumulator.Values
                                        .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                                        .Select(a => a.FunctionName)));
                            }
                            else if (chunk.StartsWith("[CACHE]"))
                            {
                                // ── Cache 统计信息（由 DeepSeekApiService 在流结束时注入）──
                                // 格式: [CACHE]hitTokens|missTokens|promptTokens|completionTokens
                                // 日志记录在流结束后统一处理，此处仅捕获
                            }
                            else
                            {
                                if (reasoningBuffer.Length > 0 && lastReasoningLength < reasoningBuffer.Length)
                                {
                                    assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                                    lastReasoningLength = reasoningBuffer.Length;
                                }

                                contentBuffer.Append(chunk);
                                streamRenderTick += chunk.Length;

                                // 双重节流：字符数足够 + 距上次渲染至少 120ms
                                bool timeElapsed = _streamRenderStopwatch == null
                                    || _streamRenderStopwatch.ElapsedMilliseconds >= StreamRenderMinIntervalMs;
                                if (streamRenderTick >= StreamRenderInterval && timeElapsed)
                                {
                                    streamRenderTick = 0;
                                    _streamRenderStopwatch?.Restart();
                                    assistantMsg.Content = contentBuffer.ToString();
                                    // ── 使用批处理 + 非阻塞 PostWebMessageAsString ──
                                    string status = _statusUpdateStopwatch != null
                                        && _statusUpdateStopwatch.ElapsedMilliseconds >= StatusUpdateMinIntervalMs
                                        ? LocalizationService.Instance["status.replying"] : null;
                                    BatchStreamingUpdate(assistantMsgIndex, contentBuffer.ToString(), reasoningBuffer.ToString(), status: status);
                                    if (status != null) _statusUpdateStopwatch?.Restart();
                                }
                            }
                                }

                                streamSuccess = true;
                                if (streamAttempt > 0)
                                {
                                    Logger.Info($"[Stream] 断点续传成功 (尝试 {streamAttempt + 1}/{maxStreamAttempts})");
                                }
                            }
                            catch (HttpRequestException ex) when (IsTransientNetworkError(ex) && streamAttempt < maxStreamAttempts - 1)
                            {
                                streamAttempt++;
                                savedPartialContent = contentBuffer.ToString();
                                savedPartialReasoning = reasoningBuffer.ToString();
                                double backoffSec = Math.Pow(2, streamAttempt);
                                Logger.Warn($"[Stream] 流中断 (尝试 {streamAttempt}/{maxStreamAttempts})，已收到 {savedPartialContent.Length} 字符部分内容，{backoffSec}s 后恢复…");
                                contentBuffer.Clear();
                                reasoningBuffer.Clear();
                                toolCallAccumulator.Clear();
                                streamRenderTick = 0;
                                lastReasoningLength = 0;
                                await Task.Delay(TimeSpan.FromSeconds(backoffSec), streamingCts.Token);
                            }
                            catch (TaskCanceledException) when (!streamingCts.Token.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                            {
                                // 超时（非用户取消）
                                streamAttempt++;
                                savedPartialContent = contentBuffer.ToString();
                                savedPartialReasoning = reasoningBuffer.ToString();
                                double backoffSec = Math.Pow(2, streamAttempt);
                                Logger.Warn($"[Stream] 流超时 (尝试 {streamAttempt}/{maxStreamAttempts})，{backoffSec}s 后恢复…");
                                contentBuffer.Clear();
                                reasoningBuffer.Clear();
                                toolCallAccumulator.Clear();
                                streamRenderTick = 0;
                                lastReasoningLength = 0;
                                await Task.Delay(TimeSpan.FromSeconds(backoffSec), streamingCts.Token);
                            }
                        }

                        // ── 记录本轮 Cache 命中率 ──
                        LogCacheHitRate(round);

                        if (toolCallAccumulator.Count > 0)
                        {
                            Logger.Info($"[MCP] 检测到 {toolCallAccumulator.Count} 个工具调用");
                            UpdateStatusText(LocalizationService.Instance["status.executingMcp"]);

                            // ── 构建详细的工具调用显示（含参数信息）──
                            var toolCallLines = new List<string>();
                            foreach (var acc in toolCallAccumulator.Values)
                            {
                                if (string.IsNullOrEmpty(acc.FunctionName)) continue;
                                string displayText = BuiltInToolService.GetToolCallDisplayText(
                                    acc.FunctionName!, acc.ArgumentsBuilder.ToString());
                                toolCallLines.Add($"- {displayText} ⏳");
                            }

                            // ── 收集本轮工具调用摘要到历史 ──
                            foreach (var acc in toolCallAccumulator.Values)
                            {
                                if (string.IsNullOrEmpty(acc.FunctionName)) continue;
                                string displayText = BuiltInToolService.GetToolCallDisplayText(
                                    acc.FunctionName!, acc.ArgumentsBuilder.ToString());
                                toolCallHistory.Add((round, displayText));
                            }
                            while (toolCallHistory.Count > 20)
                                toolCallHistory.RemoveAt(0);

                            string toolCallSummary = "🔧 **" + LocalizationService.Instance["status.callingToolsHtml"].Replace("🔧 ", "") + "**\n"
                                + string.Join("\n", toolCallLines) + "\n";
                            assistantMsg.Content = contentBuffer.Length > 0
                                ? contentBuffer.ToString() + "\n\n" + toolCallSummary
                                : toolCallSummary;
                            BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, reasoningBuffer.ToString(), isComplete: false);

                            var assistantToolCalls = toolCallAccumulator.Values
                                .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                                .Select(a => new ToolCall
                                {
                                    Id = a.Id,
                                    Type = a.Type ?? "function",
                                    Function = new ToolCallFunction { Name = a.FunctionName!, Arguments = a.ArgumentsBuilder.ToString() }
                                }).ToList();

                            if (assistantToolCalls.Count > 0)
                                _contextManager.AddAssistantMessage(contentBuffer.Length > 0 ? contentBuffer.ToString() : null,
                                    reasoningBuffer.Length > 0 ? reasoningBuffer.ToString() : null, assistantToolCalls);

                            // ── 执行工具并收集结果摘要 ──
                            var toolResultSummaries = new List<string>();
                            var recentToolFullResults = new List<(string Name, string Result)>();
                            foreach (var acc in toolCallAccumulator.Values)
                            {
                                if (string.IsNullOrEmpty(acc.FunctionName)) continue;
                                string toolResult;
                                try
                                {
                                    // ── 终端命令需要用户审批 ──
                                    if (acc.FunctionName == "run_in_terminal")
                                    {
                                        string cmd = string.Empty;
                                        string exp = string.Empty;
                                        string purpose = string.Empty;
                                        try
                                        {
                                            using var doc = System.Text.Json.JsonDocument.Parse(acc.ArgumentsBuilder.ToString());
                                            if (doc.RootElement.TryGetProperty("command", out var cmdProp))
                                                cmd = cmdProp.GetString() ?? string.Empty;
                                            if (doc.RootElement.TryGetProperty("explanation", out var expProp))
                                                exp = expProp.GetString() ?? string.Empty;
                                            if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                                                purpose = purProp.GetString() ?? string.Empty;
                                            // goal 是 DeepSeek 模型原生使用的参数名，作为 purpose 的 fallback
                                            if (string.IsNullOrWhiteSpace(purpose) && doc.RootElement.TryGetProperty("goal", out var goalProp))
                                                purpose = goalProp.GetString() ?? string.Empty;
                                        }
                                        catch { }

                                        if (!string.IsNullOrWhiteSpace(cmd))
                                        {
                                            var activeAgent = _agentDispatcher?.GetActiveAgent();
                                            if (activeAgent != null)
                                            {
                                                bool approved = await activeAgent.RequestTerminalApprovalAsync(cmd, exp, purpose);
                                                if (!approved)
                                                {
                                                    toolResult = $"⏭️ 用户跳过了终端命令: {cmd}";
                                                    toolResultSummaries.Add($"⏭️ 用户跳过了终端命令");
                                                    _contextManager.AddToolResult(acc.Id, acc.FunctionName!, toolResult);
                                                    Logger.Info($"[MCP] 工具 {acc.FunctionName} 被用户跳过");
                                                    continue;
                                                }
                                            }
                                        }
                                    }

                                    // ── 优先 MCP 工具，其次内置工具（同名时 MCP 覆盖）──
                                    bool isMcpTool = _mcpManager?.AllTools.Any(
                                        t => string.Equals(t.Name, acc.FunctionName, StringComparison.OrdinalIgnoreCase)) == true;
                                    if (isMcpTool && _mcpManager != null)
                                    {
                                        string sanitizedArgs = SanitizeOcrToolArguments(acc.FunctionName!, acc.ArgumentsBuilder.ToString());
                                        toolResult = await _mcpManager.CallToolAsync(acc.FunctionName!, sanitizedArgs, streamingCts.Token);
                                    }
                                    else if (_builtInToolService != null
                                        && BuiltInToolService.IsBuiltInTool(acc.FunctionName!))
                                    {
                                        // ── 注入 ExploreHandler / HandoffHandler（主流程桥接 AgentDispatcher）──
                                        InjectToolHandlers();

                                        // ── 诊断日志：记录工具调用时传入的 _solutionPath ──
                                        Logger.Info($"[ToolCall] 执行 {acc.FunctionName}，_solutionPath=[{_solutionPath ?? "(null)"}]");
                                        toolResult = await _builtInToolService.ExecuteBuiltInToolAsync(
                                            acc.FunctionName!, acc.ArgumentsBuilder.ToString(), _solutionPath)
                                            ?? "❌ 内置工具未返回结果";
                                    }
                                    else
                                    {
                                        toolResult = $"❌ 未知工具: {acc.FunctionName} (无可用工具服务)";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    toolResult = $"❌ 工具执行异常: {ex.Message}";
                                    Logger.Error($"[MCP] 工具 {acc.FunctionName} 执行异常: {ex.Message}", ex);
                                }
                                _contextManager.AddToolResult(acc.Id, acc.FunctionName!, toolResult);
                                Logger.Info($"[MCP] 工具 {acc.FunctionName} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");

                                // ── 收集结果摘要 ──
                                string resultSummary = BuiltInToolService.GetToolResultSummary(acc.FunctionName!, toolResult);
                                toolResultSummaries.Add(resultSummary);
                                recentToolFullResults.Add((acc.FunctionName!, toolResult));
                            }

                            // ── 构建完成后的工具调用结果展示 ──
                            var completedLines = new List<string>();
                            var accValues = toolCallAccumulator.Values
                                .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                                .ToList();
                            for (int i = 0; i < accValues.Count; i++)
                            {
                                var acc = accValues[i];
                                string displayText = BuiltInToolService.GetToolCallDisplayText(
                                    acc.FunctionName!, acc.ArgumentsBuilder.ToString());
                                string summary = i < toolResultSummaries.Count ? toolResultSummaries[i] : "完成";
                                completedLines.Add($"- {displayText} → {summary}");
                            }

                            string completedSummary = "🔧 **" + LocalizationService.Instance["status.toolCallCompleted"].Replace("🔧 ", "") + "**\n"
                                + string.Join("\n", completedLines) + "\n\n";
                            assistantMsg.Content = contentBuffer.Length > 0
                                ? contentBuffer.ToString() + "\n\n" + completedSummary + "\n_" + LocalizationService.Instance["status.toolCallAnalyzing"] + "_\n"
                                : completedSummary + "\n_" + LocalizationService.Instance["status.toolCallAnalyzing"] + "_\n";
                            BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, reasoningBuffer.ToString());

                            // ── 移交检测：request_handoff 被调用后立即终止循环 ──
                            if (_pendingHandoff != null)
                            {
                                Logger.Info("[MainFlow] 🔄 检测到移交请求，终止工具循环");
                                // 在最后一条 assistant 消息中注入移交提示
                                assistantMsg.Content = (contentBuffer.Length > 0
                                    ? contentBuffer.ToString() + "\n\n"
                                    : "") + $"🔄 任务已移交给 {_pendingHandoff.TargetAgent} Agent...";
                                BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, reasoningBuffer.ToString());
                                loopDetected = true;
                                continue;
                            }

                            // ── 循环检测 ──
                            // 收集本轮所有工具调用的签名和结果用于检测
                            // 注：此处仅建签名，结果稍后在工具执行完成后回填
                            var roundResults = new List<(string Signature, string Result)>();
                            foreach (var acc in toolCallAccumulator.Values)
                            {
                                if (string.IsNullOrEmpty(acc.FunctionName)) continue;
                                string sig = acc.FunctionName! + "|" +
                                    (acc.ArgumentsBuilder.Length > 200
                                        ? acc.ArgumentsBuilder.ToString().Substring(0, 200)
                                        : acc.ArgumentsBuilder.ToString());

                                callSignatureHistory.Enqueue(sig);

                                // 更新计数
                                callSignatureCount.TryGetValue(sig, out int prevCount);
                                callSignatureCount[sig] = prevCount + 1;

                                // 维护固定大小窗口
                                while (callSignatureHistory.Count > maxHistorySize)
                                {
                                    string removed = callSignatureHistory.Dequeue();
                                    if (callSignatureCount.TryGetValue(removed, out int cnt))
                                    {
                                        if (cnt <= 1)
                                            callSignatureCount.Remove(removed);
                                        else
                                            callSignatureCount[removed] = cnt - 1;
                                    }
                                }

                                roundResults.Add((sig, string.Empty)); // result 稍后回填
                            }

                            // 检测同一调用重复（带同结果判断：只有每次返回相同结果才终止）
                            // 先回填结果：按索引对应 recentToolFullResults
                            for (int i = 0; i < roundResults.Count && i < recentToolFullResults.Count; i++)
                            {
                                roundResults[i] = (roundResults[i].Signature, recentToolFullResults[i].Result);
                            }

                            foreach (var (sig, currentResult) in roundResults)
                            {
                                if (callSignatureCount.TryGetValue(sig, out int repeatCount)
                                    && repeatCount >= maxRepeatedSameCall)
                                {
                                    string toolName = sig.Split('|')[0];

                                    // ── 同结果检测：比较本次与上次结果 ──
                                    bool sameResult = !string.IsNullOrWhiteSpace(currentResult)
                                        && lastResultBySignature.TryGetValue(sig, out string? prevResult)
                                        && AreToolResultsSame(prevResult, currentResult);

                                    if (sameResult)
                                    {
                                        // 结果相同 → 真正的死循环，终止
                                        loopDetected = true;
                                        Logger.Warn($"[MCP] 🔄 检测到循环调用: {toolName} 已重复 {repeatCount} 次且每次返回相同结果");

                                        var terminatedBuilder = new System.Text.StringBuilder();
                                        terminatedBuilder.Append(contentBuffer);

                                        if (toolCallHistory.Count > 0)
                                        {
                                            terminatedBuilder.Append("\n\n---\n### 🔙 此前 AI 执行的操作\n");
                                            int startRound = toolCallHistory[0].Round;
                                            foreach (var (r, summary) in toolCallHistory)
                                            {
                                                string prefix = r == round ? "🔄" : $"第{r - startRound + 1}轮";
                                                terminatedBuilder.AppendLine($"- {prefix} {summary}");
                                            }
                                        }

                                        foreach (var (name, result) in recentToolFullResults)
                                        {
                                            if (!string.IsNullOrWhiteSpace(result))
                                                terminatedBuilder.Append($"\n\n### 📋 最后一次 `{name}` 结果\n\n{result.Truncate(3000)}");
                                        }

                                        terminatedBuilder.Append($"\n\n> ⚠️ 检测到 `{toolName}` 重复调用 {repeatCount} 次且每次返回相同结果，已自动终止循环。请根据以上工具结果修复问题后重新请求。");
                                        contentBuffer.Clear();
                                        contentBuffer.Append(terminatedBuilder.ToString());
                                        break;
                                    }
                                    else
                                    {
                                        // 结果不同 → 用户正在修复问题，重置该签名的计数
                                        if (!string.IsNullOrWhiteSpace(currentResult))
                                            lastResultBySignature[sig] = currentResult;
                                        callSignatureCount[sig] = 0; // 重置计数
                                        Logger.Info($"[MCP] 🔄 {toolName} 重复 {repeatCount} 次但结果不同，可能是用户在修复问题，继续执行");
                                    }
                                }
                                else
                                {
                                    // 正常记录结果（用于后续比较）
                                    if (!string.IsNullOrWhiteSpace(currentResult))
                                        lastResultBySignature[sig] = currentResult;
                                }
                            }

                            // 保留最近 30 条签名防止内存增长
                            while (callSignatureHistory.Count > maxHistorySize)
                                callSignatureHistory.Dequeue();

                            // 检测连续错误：检查本轮工具结果是否全部以 ❌ 开头
                            if (!loopDetected)
                            {
                                // 从 context 中取最近添加的 tool 消息来判断
                                var allToolMsgs = _contextManager.GetConversationHistory()
                                    .Where(e => e.Role == "tool")
                                    .ToList();
                                int takeCount = toolCallAccumulator.Count;
                                var recentToolMsgs = allToolMsgs
                                    .Skip(System.Math.Max(0, allToolMsgs.Count - takeCount))
                                    .ToList();
                                bool allErrors = recentToolMsgs.Count > 0 &&
                                    recentToolMsgs.All(m => (m.Content ?? "").StartsWith("❌"));

                                if (allErrors)
                                    consecutiveErrorRounds++;
                                else
                                    consecutiveErrorRounds = 0;

                                if (consecutiveErrorRounds >= maxConsecutiveErrors)
                                {
                                    loopDetected = true;
                                    Logger.Warn($"[MCP] 🔄 连续 {consecutiveErrorRounds} 轮工具调用全部返回错误，强制结束");

                                    // ── 附加上次 AI 的上下文总结和最后一次工具调用结果摘要 ──
                                    var terminatedBuilder = new System.Text.StringBuilder();

                                    // 1. AI 本轮文字回复
                                    terminatedBuilder.Append(contentBuffer);

                                    // 2. 历史工具调用总结
                                    if (toolCallHistory.Count > 0)
                                    {
                                        terminatedBuilder.Append("\n\n---\n### 🔙 此前 AI 执行的操作\n");
                                        int startRound = toolCallHistory[0].Round;
                                        foreach (var (r, summary) in toolCallHistory)
                                        {
                                            string prefix = r == round ? "🔄" : $"第{r - startRound + 1}轮";
                                            terminatedBuilder.AppendLine($"- {prefix} {summary}");
                                        }
                                    }

                                    // 3. 最后一次工具调用结果
                                    foreach (var (name, result) in recentToolFullResults)
                                    {
                                        if (!string.IsNullOrWhiteSpace(result))
                                        {
                                            terminatedBuilder.Append($"\n\n### 📋 最后一次 `{name}` 结果\n\n{result.Truncate(3000)}");
                                        }
                                    }

                                    terminatedBuilder.Append($"\n\n> ⚠️ 连续 {consecutiveErrorRounds} 轮工具调用均失败，已自动终止。请检查工作区路径是否正确。");
                                    contentBuffer.Clear();
                                    contentBuffer.Append(terminatedBuilder.ToString());
                                }
                            }

                            continue;
                        }

                        break;
                    }

                    // ── 汇总本轮 Cache 统计（跨所有轮次，从 _apiService 读取累计值）──
                    long totalCacheHitTokens = _apiService?.TotalCacheHitTokens ?? 0;
                    long totalCacheMissTokens = _apiService?.TotalCacheMissTokens ?? 0;
                    long totalPromptTokens = _apiService?.TotalPromptTokens ?? 0;
                    long totalCompletionTokens = _apiService?.TotalCompletionTokens ?? 0;
                    LogTotalCacheHitRate(round, totalCacheHitTokens, totalCacheMissTokens, totalPromptTokens, totalCompletionTokens);

                    // 流式完成
                    assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                    assistantMsg.Content = contentBuffer.ToString();
                    assistantMsg.IsStreaming = false;

                    Logger.Info($"[Render] 流式结束: 内容={contentBuffer.Length}, 思考={reasoningBuffer.Length}");

                    // ── 构建 Cache 命中率统计卡片 HTML ──
                    string cacheFooterHtml = ChatHtmlService.BuildCacheHitFooterHtml(
                        totalCacheHitTokens, totalCacheMissTokens,
                        totalPromptTokens, totalCompletionTokens, round);

                    // ── 同步最终内容并强制刷新，确保增量内容已推送 ──
                    BatchStreamingUpdate(assistantMsgIndex, contentBuffer.ToString(), reasoningBuffer.ToString(), isComplete: true);

                    // ── 使用非阻塞 PostWebMessageAsString 发送最终渲染（含 Markdown HTML）──
                    PostStreamEnd(assistantMsgIndex, contentBuffer.ToString(), reasoningBuffer.ToString(), cacheFooterHtml);

                    if (capturedSearchResults.Count > 0)
                    {
                        string providerLabel = _webSearchService?.ActiveProvider == SearchProvider.Baidu
                            ? LocalizationService.Instance["websearch.searchEngine.baidu"]
                            : LocalizationService.Instance["websearch.searchEngine.duckduckgo"];
                        string searchCardJs = ChatHtmlService.BuildSearchResultsInjectionJs(assistantMsgIndex, capturedSearchResults, providerLabel);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(searchCardJs);
                    }

                    _contextManager.AddAssistantMessage(contentBuffer.ToString(), reasoningBuffer.Length > 0 ? reasoningBuffer.ToString() : null);

                    // ── 主流程移交处理：request_handoff 触发的 _pendingHandoff 在此执行 ──
                    if (_pendingHandoff != null && _agentDispatcher != null)
                    {
                        Logger.Info($"[MainFlow] 执行移交 → {_pendingHandoff.TargetAgent} ({_pendingHandoff.Label})");
                        var handoff = _pendingHandoff;
                        _pendingHandoff = null; // 防止重复执行

                        // 构建上下文并执行移交
                        var handoffContext = new AgentContext
                        {
                            SolutionPath = _solutionPath,
                            ConversationHistory = _contextManager.GetConversationHistory(),
                            ContextManager = _contextManager,
                            CancellationToken = CancellationToken.None,
                        };

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var handoffResult = await _agentDispatcher.ExecuteHandoffAsync(handoff, handoffContext);
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                // 移交结果由 Agent 流程的 RunAgentWorkflowAsync 后续处理
                                Logger.Info($"[MainFlow] 移交执行完成: → {handoff.TargetAgent}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[MainFlow] 移交执行失败: {ex.Message}", ex);
                            }
                            finally
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                lock (_lock) { _isGenerating = false; }
                                UpdateButtonsState();
                                StatusLabel.Text = LocalizationService.Instance["status.ready"];
                            }
                        });
                        return; // 移交在后台执行，当前消息流结束
                    }

                    // ── AI 自动生成会话标题（首轮对话完成后触发） ──
                    if (_pendingAiTitle && !string.IsNullOrWhiteSpace(_firstUserMessageForTitle))
                    {
                        var capturedFirstUserMsg = _firstUserMessageForTitle;
                        var capturedFirstAssistantReply = contentBuffer.ToString();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await GenerateAiTitleAsync(capturedFirstUserMsg, capturedFirstAssistantReply);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[AI标题] 异步生成异常: {ex.Message}");
                            }
                        });
                    }

                    var capturedMsg = assistantMsg;
                    _ = Task.Run(() =>
                    {
                        capturedMsg.HtmlContent = "rendered";
                        capturedMsg.IsRendered = true;
                        SaveCurrentSession();
                    });
                }
                catch (ApiKeyInvalidException ex)
                {
                    Logger.Error($"[Render] API Key 无效", ex);
                    assistantMsg.Content = $"⚠️ {ex.Message}";
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent);
                    lock (_lock) { _messages.Remove(assistantMsg); }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("[Render] 用户停止生成");
                    assistantMsg.Content += "\n\n*[已停止]*";
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent);
                }
                catch (ObjectDisposedException) when (_currentStreamingCts?.IsCancellationRequested == true)
                {
                    // 取消令牌触发时释放 SslStream 可能导致 ReadLineAsync 抛出
                    // ObjectDisposedException 而非 OperationCanceledException，兜底处理为停止。
                    Logger.Info("[Render] 用户停止生成 (ObjectDisposed)");
                    assistantMsg.Content += "\n\n*[已停止]*";
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
                {
                    Logger.Error($"[Render] API 认证失败", ex);
                    assistantMsg.Content = "⚠️ DeepSeek API Key 无效或已过期，请重新配置。";
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent);
                    lock (_lock) { _messages.Remove(assistantMsg); }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Render] API 出错", ex);
                    assistantMsg.Content = string.Format(LocalizationService.Instance["status.apiError"], ex.Message);
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(assistantMsgIndex, assistantMsg.Content, assistantMsg.ReasoningContent);
                }
                finally
                {
                    assistantMsg.IsStreaming = false;
                    lock (_lock) { _isGenerating = false; }
                    StatusLabel.Text = string.Empty;
                    DisposeStreamingCts();
                    UpdateButtonsState();
                }
        }
#pragma warning restore VSTHRD100

        /// <summary>
        /// 构建路由上下文摘要，帮助 AI 理解短消息（如"进行修复"）的指代。
        /// 包含：最近几轮对话摘要 + 用户附加的文件内容摘要。
        /// </summary>
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
        /// 构建发送给 API 的消息列表，注入系统提示词、技能上下文、RAG 检索结果、搜索上下文。
        /// </summary>
        private async Task<List<ChatApiMessage>> BuildRequestMessagesAsync(string searchContext = "")
        {
            // ── 惰性解析：确保 _solutionPath 在首次使用时已就绪 ──
            // StartControl 中 ResolveSolutionPathAsync 是 fire-and-forget，
            // 可能在首条消息发送时尚未完成。此处兜底保证路径已解析。
            if (_solutionPath == null)
            {
                await ResolveSolutionPathAsync();
            }

            string systemPrompt = _options?.SystemPrompt ?? string.Empty;

            if (_agentDispatcher != null)
            {
                string askAgentPrompt = _agentDispatcher.AskAgent.Definition.SystemPrompt;
                if (!string.IsNullOrWhiteSpace(askAgentPrompt))
                    systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? askAgentPrompt : systemPrompt + "\n\n" + askAgentPrompt;
                systemPrompt += "\n\n" + AiPrompts.MultiAgentSystemPromptFragment;
            }

            // ── 注入记忆系统使用指导 ──
            systemPrompt += "\n\n" + AiPrompts.MemoryInstructionsFragment;

            // ── 注入工作区路径信息，让 AI 知道项目根目录 ──
            string workspaceRoot = _solutionPath ?? string.Empty;
            Logger.Info($"[Workspace] 构建系统提示时 _solutionPath=[{_solutionPath ?? "(null)"}], workspaceRoot=[{workspaceRoot}]");
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                // 如果是 .sln/.slnx 文件路径，取其目录
                try
                {
                    if (File.Exists(workspaceRoot) && Path.GetExtension(workspaceRoot).StartsWith(".sln", StringComparison.OrdinalIgnoreCase))
                        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
                }
                catch { }

                string wsInfo = $"\n\n## 工作区信息\n当前工作区根目录: `{workspaceRoot}`\n所有文件操作请使用此目录下的 Windows 绝对路径。";
                systemPrompt += wsInfo;
            }

            _contextManager.SetSystemPrompt(string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt);

            string skillContext = string.Empty;
            try
            {
                if (_skillDiscoveryResult == null)
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                skillContext = SkillService.Instance.GenerateSkillsDiscoveryContext(_skillDiscoveryResult);
            }
            catch (Exception ex) { Logger.Warn($"[Skill] 构建技能上下文失败: {ex.Message}"); }
            _contextManager.SetSkillContext(string.IsNullOrWhiteSpace(skillContext) ? null : skillContext);

            if (!string.IsNullOrWhiteSpace(skillContext) && _skillDiscoveryResult != null)
            {
                var skillNames = string.Join(", ", _skillDiscoveryResult.AutoLoadableSkills.ConvertAll(s => s.Name));
                Logger.Info($"[Skill] 系统提示注入: {_skillDiscoveryResult.AutoLoadableSkills.Count} 个 → {skillNames}");
            }

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

            // ── 注入记忆上下文（用户记忆 + 仓库记忆）──
            try
            {
                if (_memoryService != null)
                {
                    string userMemory = _memoryService.GetMemoryContext(MemoryScope.User);
                    string repoMemory = _memoryService.GetMemoryContext(MemoryScope.Repo, solutionPath: _solutionPath);
                    var memoryContext = new System.Text.StringBuilder();
                    if (!string.IsNullOrWhiteSpace(userMemory))
                        memoryContext.AppendLine(userMemory);
                    if (!string.IsNullOrWhiteSpace(repoMemory))
                        memoryContext.AppendLine(repoMemory);
                    string combined = memoryContext.ToString().Trim();
                    _contextManager.SetMemoryContext(string.IsNullOrWhiteSpace(combined) ? null : combined);
                }
            }
            catch (Exception ex) { Logger.Warn($"[Memory] 记忆上下文注入失败: {ex.Message}"); }

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

        // ── 断点续传辅助方法 ──

        /// <summary>
        /// 判断是否为暂态网络错误（可重试），排除客户端错误。
        /// HTTP 4xx（除 429 限流外）是请求本身的问题，重试不会改变结果。
        /// </summary>
        private static bool IsTransientNetworkError(HttpRequestException ex)
        {
            string msg = ex.Message;
            // 所有 4xx 客户端错误不应重试（400 Bad Request, 401 Unauthorized, 402 Payment Required,
            // 403 Forbidden, 404 Not Found, 405 Method Not Allowed, 409 Conflict, 422 Unprocessable Entity）
            if (msg.Contains("400") || msg.Contains("401") || msg.Contains("402") ||
                msg.Contains("403") || msg.Contains("404") || msg.Contains("405") ||
                msg.Contains("409") || msg.Contains("422"))
                return false;
            // HTTP 5xx、连接重置、DNS 解析失败、超时等可重试
            return true;
        }

        /// <summary>
        /// 构建断点续传的消息列表：在原消息基础上追加部分 AI 回复 + 继续指令。
        /// 不对 _contextManager 产生副作用——仅在重试时使用临时副本。
        /// </summary>
        private static List<ChatApiMessage> BuildResumeMessages(
            List<ChatApiMessage> originalMessages,
            string partialContent,
            string partialReasoning)
        {
            var resumed = new List<ChatApiMessage>(originalMessages);

            // 追加已接收的部分 AI 回复
            resumed.Add(new ChatApiMessage
            {
                Role = "assistant",
                Content = string.IsNullOrEmpty(partialContent) ? null : partialContent,
                ReasoningContent = string.IsNullOrEmpty(partialReasoning) ? null : partialReasoning
            });

            // 追加系统级继续指令（以 user 角色注入，确保 AI 遵循）
            string tailContent = partialContent.Length > 300
                ? "…(截断)…" + partialContent.Substring(partialContent.Length - 300)
                : partialContent;
            resumed.Add(new ChatApiMessage
            {
                Role = "user",
                Content = $"[系统指令] 你之前的回复因网络中断被截断。以下是已发送的末尾内容：\n```\n{tailContent}\n```\n请从截断处**精确**继续，不要重复任何已发送的内容，不要道歉或解释中断。直接继续未完成的句子或代码块。"
            });

            return resumed;
        }

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
        /// 注入 ExploreHandler / HandoffHandler 到 BuiltInToolService。
        /// 确保主聊天流程的工具执行（非 Agent 上下文）也能使用 runSubagent / request_handoff。
        /// </summary>
        private void InjectToolHandlers()
        {
            if (_builtInToolService == null) return;

            var exploreAgent = _agentDispatcher?.ExploreAgent;
            if (exploreAgent != null)
            {
                // 同步工具服务
                if (exploreAgent.BuiltInTools == null)
                    exploreAgent.BuiltInTools = _builtInToolService;

                _builtInToolService.ExploreHandler = async (ctx) =>
                {
                    exploreAgent.Context = new AgentContext
                    {
                        SolutionPath = ctx.WorkspaceRoot ?? _solutionPath,
                        CancellationToken = CancellationToken.None,
                    };

                    // ── 转发 ExploreAgent 日志到主流程，让用户看到探索进度 ──
                    Action<AgentLogEntry> forwardLog = (entry) =>
                    {
                        Logger.Info($"[ExploreAgent] {entry.Level} {entry.Message.Truncate(200)}");
                    };
                    exploreAgent.LogEntryAdded += forwardLog;
                    try
                    {
                        var exploreResult = await exploreAgent.ExecuteAsync(ctx.Prompt, exploreAgent.Context);
                        return exploreResult.Success && !string.IsNullOrEmpty(exploreResult.Content)
                            ? exploreResult.Content
                            : $"❌ ExploreAgent 失败: {exploreResult.ErrorMessage ?? "未知错误"}";
                    }
                    finally
                    {
                        exploreAgent.LogEntryAdded -= forwardLog;
                    }
                };
            }

            _builtInToolService.HandoffHandler = async (request) =>
            {
                Logger.Info($"[MainFlow] 🔄 移交请求: → {request.TargetAgent} (原因: {request.Reason})");
                // 构建 AgentHandoff 并设置 _pendingHandoff，主流程将在本轮工具调用后执行移交
                string label = request.TargetAgent switch
                {
                    AgentType.Edit => "执行修改",
                    AgentType.Ask => "生成总结",
                    AgentType.Plan => "制定计划",
                    AgentType.Build => "诊断修复",
                    AgentType.Explore => "探索代码库",
                    _ => "移交任务"
                };
                _pendingHandoff = new AgentHandoff
                {
                    Label = label,
                    TargetAgent = request.TargetAgent,
                    Prompt = $"[移交自 {request.SourceAgent}] {request.Reason}\n\n{request.TaskDescription}",
                    AutoSend = request.AutoSend,
                    ShowContinueOn = !request.AutoSend,
                };
                await Task.CompletedTask;
            };
        }

        /// <summary>
        /// 预处理 OCR 工具参数：AI 可能传文件名而非 base64，自动转换。
        /// 同时记录 OCR 调用参数格式提醒（input_data 必需，output_mode/file_type 可选）。
        /// </summary>
        private string SanitizeOcrToolArguments(string toolName, string argumentsJson)
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

                lock (_lock)
                {
                    if (_attachedFilePaths.Count > 0)
                    {
                        foreach (var attachedPath in _attachedFilePaths)
                        {
                            if (Path.GetFileName(attachedPath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                            {
                                searchDirs.Insert(0, Path.GetDirectoryName(attachedPath)!);
                                break;
                            }
                        }
                    }
                }

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
        /// 判断两次工具调用结果是否实质相同（用于循环检测）。
        /// 对错误类工具（get_errors、build_solution）提取核心错误行比较，
        /// 避免因时间戳等噪声导致误判。
        /// </summary>
        private static bool AreToolResultsSame(string prevResult, string currentResult)
        {
            if (string.IsNullOrEmpty(prevResult) && string.IsNullOrEmpty(currentResult))
                return true;
            if (string.IsNullOrEmpty(prevResult) || string.IsNullOrEmpty(currentResult))
                return false;

            // ── 快速路径：完全一致 ──
            string a = prevResult.Trim();
            string b = currentResult.Trim();
            if (a == b) return true;

            // ── 提取错误行进行比较 ──
            var prevErrors = ExtractToolErrorLines(a);
            var currErrors = ExtractToolErrorLines(b);

            if (prevErrors.Count == 0 && currErrors.Count == 0)
            {
                // 无错误行时，比较长度相似度（容忍 10% 差异）
                int lenDiff = Math.Abs(a.Length - b.Length);
                int maxLen = Math.Max(a.Length, b.Length);
                return maxLen > 0 && (double)lenDiff / maxLen < 0.1;
            }

            if (prevErrors.Count != currErrors.Count)
                return false;

            for (int i = 0; i < prevErrors.Count; i++)
            {
                if (!string.Equals(prevErrors[i], currErrors[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 从工具结果中提取核心错误行（忽略时间戳、构建配置等可变前缀）。
        /// </summary>
        private static List<string> ExtractToolErrorLines(string result)
        {
            var errors = new List<string>();
            foreach (var line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("0 Error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("0 错误", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("0 Error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("0 错误", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(trimmed);
                }
            }
            return errors;
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
                string level = rate >= 0.95 ? "🟢" : rate >= 0.70 ? "🟡" : rate >= 0.30 ? "🟠" : "🔴";

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
                string level = aggregateRate >= 0.95 ? "🟢" : aggregateRate >= 0.70 ? "🟡" : aggregateRate >= 0.30 ? "🟠" : "🔴";

                Logger.Info($"[Cache] ═══════════════════════════════════════");
                Logger.Info($"[Cache] {level} 累计汇总 ({finalRound} 轮)");
                Logger.Info($"[Cache]   总 Cache 命中率: {aggregateRate * 100:F1}%");
                Logger.Info($"[Cache]   累计命中: {totalHit:N0} tokens");
                Logger.Info($"[Cache]   累计未命中: {totalMiss:N0} tokens");
                Logger.Info($"[Cache]   累计 Prompt: {totalPrompt:N0} tokens");
                Logger.Info($"[Cache]   累计 Completion: {totalCompletion:N0} tokens");
                Logger.Info($"[Cache]   节省比例: {aggregateRate * 100:F1}% (DeepSeek Cache 对命中 token 仅按 $0.014/M 计费)");
                Logger.Info($"[Cache] ═══════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录汇总命中率异常: {ex.Message}");
            }
        }

        #endregion
    }
}
