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
    /// 消息发送与 API 交互：SendMessage、联网搜索、流式处理、搜索优化等。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Private Methods - API Interaction

#pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件处理，符合 WPF 模式
        private async void SendMessage()
        {
            lock (_lock)
            {
                if (_isGenerating) return;
            }

            var userText = InputTextBox.Text?.Trim();
            bool hasAttachments = _attachedFilePaths.Count > 0;

            // ── 编辑模式：将输入内容作为编辑后的消息重新发送 ──
            if (_pendingEditMsgIndex >= 0)
            {
                if (string.IsNullOrEmpty(userText))
                {
                    // 用户清空了输入框 → 取消编辑
                    _pendingEditMsgIndex = -1;
                    StatusLabel.Text = "就绪";
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

            try
            {

                // ── 斜杠命令处理：/skill-name 调用技能 ──
                string? skillInstructions = null;
                if (!string.IsNullOrEmpty(userText) && userText.StartsWith("/"))
                {
                    skillInstructions = await ResolveSlashCommandAsync(userText);
                    if (skillInstructions == null)
                    {
                        // 元命令（/help, /refresh-skills 等）已处理完毕，清空输入框
                        InputTextBox.Text = string.Empty;
                        lock (_lock) { _isGenerating = false; }
                        return;
                    }
                }

                if (string.IsNullOrEmpty(userText) && !hasAttachments)
                {
                    lock (_lock) { _isGenerating = false; }
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
                    StatusLabel.Text = "⚠️ 请先配置 API 密钥 (工具 → 选项 → DeepSeek Chat)";
                    lock (_lock) { _isGenerating = false; }
                    return;
                }

                // 热重载 API 服务
                InitializeApiService();
                if (_apiService == null)
                {
                    lock (_lock) { _isGenerating = false; }
                    return;
                }

                // ── 解析上传的文件（Agent 和非 Agent 路径共用）──
                string fileContext = string.Empty;
                List<string> attachedFileNames = new();
                List<FileParseResult> parseResults = new();

                if (_attachedFilePaths.Count > 0)
                {
                    StatusLabel.Text = "正在解析文件…";
                    parseResults = await FileParserService.ParseFilesAsync(_attachedFilePaths);
                    attachedFileNames = parseResults
                        .Where(r => r.Success)
                        .Select(r => r.FileName)
                        .ToList();

                    fileContext = FileParserService.FormatParseResultsForContext(parseResults);
                    if (!string.IsNullOrEmpty(fileContext))
                    {
                        Logger.Info($"文件解析完成: {attachedFileNames.Count} 个文件");
                    }
                }

                // ── 构建完整的用户消息内容 ──
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

                // ── @agent 显式路由：用户可输入 @ask / @plan / @edit 指定 Agent ──
                string agentRoutedUserText = userText ?? string.Empty;
                AgentRoutingResult? explicitRoute = null;
                if (_agentDispatcher != null && !string.IsNullOrEmpty(userText) && userText.StartsWith("@"))
                {
                    explicitRoute = await _agentDispatcher.RouteAsync(userText);
                    // 去掉 @agent 前缀，保留实际消息内容
                    var parts = userText.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    agentRoutedUserText = parts.Length > 1 ? parts[1] : string.Empty;
                    if (string.IsNullOrWhiteSpace(agentRoutedUserText))
                    {
                        // 只有 @agent 没有消息 → 切换到该 Agent 并提示
                        StatusLabel.Text = $"已切换到 {explicitRoute.TargetAgent} Agent";
                        InputTextBox.Text = string.Empty;
                        lock (_lock) { _isGenerating = false; }
                        return;
                    }
                    Logger.Info($"[AgentDispatcher] @agent 显式路由: → {explicitRoute.TargetAgent}, 消息: \"{agentRoutedUserText}\"");
                }

                // ── 多 Agent 路由：判断是否需要启动 Agent 工作流 ──
                if (_agentDispatcher != null && !string.IsNullOrEmpty(userText) && !userText.StartsWith("/"))
                {
                    var routing = explicitRoute ?? await _agentDispatcher.RouteAsync(userText);
                    bool needsAgent = routing.TargetAgent == AgentType.Plan
                        || routing.TargetAgent == AgentType.Edit
                        || (routing.NeedsPlanning);

                    if (needsAgent)
                    {
                        // ── 添加用户消息到 UI（含附件信息）──
                        var agentUserMsg = new ChatMessage
                        {
                            Role = "user",
                            Content = userDisplayContent,
                            AttachedFileNames = attachedFileNames,
                            AttachedFiles = parseResults,
                            Timestamp = DateTime.Now,
                        };
                        lock (_lock)
                        {
                            _messages.Add(agentUserMsg);
                            _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = fullUserContent });
                        }
                        AddMessagesHtml("user", userDisplayContent, null, parseResults);
                        UpdateBrowser();
                        ClearAttachedFiles();
                        AutoTitleSession();
                        InputTextBox.Text = string.Empty;
                        UpdateButtonsState(); // 显示停止按钮

                        // ── 启动多 Agent 工作流（异步，不阻塞 UI 线程）──
                        var capturedUserText = agentRoutedUserText;
                        var capturedFileContext = fileContext;
                        var capturedRoute = routing;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await RunAgentWorkflowAsync(capturedUserText, capturedFileContext, capturedRoute);
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
                                StatusLabel.Text = "就绪";
                            }
                        });
                        return; // SendMessage 立即返回，Agent 在后台运行
                    }
                    // 否则走 Ask Agent 问答流程
                }

                InputTextBox.Text = string.Empty;

                // ── 技能路由：AI 自动判断是否应调用某个技能 ──
                bool isSlashCommand = !string.IsNullOrEmpty(userText) && userText.StartsWith("/");
                bool isAutoMatched = false;
                if (string.IsNullOrEmpty(skillInstructions) && !string.IsNullOrEmpty(fullUserContent))
                {
                    skillInstructions = await RouteSkillAsync(fullUserContent);
                    isAutoMatched = skillInstructions != null;
                }

                // ── 添加用户消息 ──
                var userMsg = new ChatMessage
                {
                    Role = "user",
                    Content = userDisplayContent,
                    AttachedFileNames = attachedFileNames,
                    AttachedFiles = parseResults,
                    Timestamp = DateTime.Now,
                };
                lock (_lock)
                {
                    _messages.Add(userMsg);

                    // ── 如果用户通过 /skill-name 调用了技能，先注入技能指令 ──
                    if (!string.IsNullOrEmpty(skillInstructions))
                    {
                        _conversationHistory.Add(new ChatApiMessage
                        {
                            Role = "system",
                            Content = skillInstructions
                        });

                        // ── 日志：记录技能指令注入 ──
                        if (isSlashCommand)
                        {
                            var calledSkillName = userText!.Substring(1).Split(' ')[0];
                            var skillDef = SkillService.Instance.FindSkill(calledSkillName, _skillDiscoveryResult);
                            Logger.Info($"[Skill] 技能指令已注入对话: \"{calledSkillName}\" " +
                                $"(来源: {skillDef?.Source.ToString() ?? "N/A"}, " +
                                $"指令长度: {skillInstructions.Length} 字符)");
                            Logger.Info($"[Skill] 调用方式: 用户显式 (斜杠命令), 原因: 用户输入 /{calledSkillName}");
                        }
                        else if (isAutoMatched)
                        {
                            Logger.Info($"[Skill] 技能指令已注入对话 (指令长度: {skillInstructions.Length} 字符)");
                            Logger.Info($"[Skill] 调用方式: AI 自动匹配, 原因: RouteSkillAsync 根据用户输入自动选择最佳技能");
                        }
                        else
                        {
                            Logger.Info($"[Skill] 技能指令已注入对话 (指令长度: {skillInstructions.Length} 字符)");
                            Logger.Info($"[Skill] 调用方式: 内置路由");
                        }
                    }

                    _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = fullUserContent });
                }

                // ── 清空附件列表 ──
                ClearAttachedFiles();

                // 自动设置会话标题（使用第一条用户消息）
                AutoTitleSession();

                // ── 创建助手消息占位 ──
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
                    _messages.Add(assistantMsg);
                    assistantMsgIndex = _messages.Count - 1;
                }

                // ── 批量构建 HTML（用户消息 + 助手占位），仅调用一次 UpdateBrowser 避免竞态重复渲染 ──
                // 对于用户消息，只显示用户的原始文本 + 可折叠文件块
                AddMessagesHtml("user", userDisplayContent, null, parseResults);
                AddMessagesHtml("assistant", string.Empty);
                UpdateBrowser();

                _isGenerating = true;
                UpdateButtonsState();

                bool isWebSearchEnabled = _webSearchEngine != "Off";
                StatusLabel.Text = isWebSearchEnabled ? "正在联网搜索…" : "DeepSeek 思考中…";

                _currentStreamingCts?.Cancel();
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = new CancellationTokenSource();

                // ── 联网搜索（在 API 调用之前执行） ──
                string searchContext = string.Empty;
                List<WebSearchResult> capturedSearchResults = new List<WebSearchResult>();
                string? engineSwitchNote = null; // 引擎切换原因提示
                if (isWebSearchEnabled && _webSearchService != null)
                {
                    // ── 热重载 API Key（支持不重启生效） ──
                    ApplyWebSearchConfig();
                    // ── 检查百度 API Key ──
                    if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                    {
                        StatusLabel.Text = "⚠️ 请先配置百度 API Key (工具→选项→DeepSeek Chat→Web Search)";
                        assistantMsg.Content = "⚠️ **百度搜索未配置**\n\n请通过菜单 **工具 → 选项 → DeepSeek Chat → Web Search** 配置百度千帆 API Key。\n\n获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n\n也可以切换到 DuckDuckGo 搜索（免费，无需 Key）。\n\n";
                        assistantMsg.IsStreaming = false;
                        _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: true);
                        _isGenerating = false;
                        UpdateButtonsState();
                        StatusLabel.Text = "⚠️ 百度 API Key 未配置";
                        return;
                    }

                    // ── 时间词语替换（如"今天"→具体日期） ──
                    string timeAwareQuery = ResolveTimeExpressions(userText!);

                    // ── 如果有附件，先用 AI 从附件中提取关键信息用于搜索优化 ──
                    string searchOptimizationInput = timeAwareQuery;
                    if (!string.IsNullOrEmpty(fileContext) && _apiService != null)
                    {
                        try
                        {
                            StatusLabel.Text = "AI 正在从附件中提取关键信息…";
                            string? extractedKeyInfo = await ExtractKeyInfoForSearchAsync(
                                fileContext, userText!, _currentStreamingCts.Token);
                            if (!string.IsNullOrWhiteSpace(extractedKeyInfo))
                            {
                                searchOptimizationInput = extractedKeyInfo + "\n用户问题：" + timeAwareQuery;
                                Logger.Info($"从附件提取关键信息成功 ({extractedKeyInfo.Length} 字符)，用于搜索优化");
                                StatusLabel.Text = "已提取附件关键信息，正在优化搜索词…";
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"从附件提取关键信息失败，使用原始查询: {ex.Message}");
                            // 提取失败不影响流程，使用原始查询继续
                        }
                    }

                    // ── AI 优化搜索查询 ──
                    string optimizedQuery = timeAwareQuery;
                    string? searchRecency = null;

                    try
                    {

                        if (_apiService != null)
                        {
                            try
                            {
                                StatusLabel.Text = "AI 正在优化搜索词…";
                                bool isBaidu = _webSearchEngine == "Baidu";
                                var optimization = await OptimizeSearchQueryAsync(searchOptimizationInput, _currentStreamingCts.Token, isBaidu);
                                if (optimization != null && !string.IsNullOrWhiteSpace(optimization.SearchQuery) && optimization.NeedSearch)
                                {
                                    optimizedQuery = optimization.SearchQuery;
                                    searchRecency = optimization.SearchRecency;
                                    Logger.Info($"AI 优化搜索词: \"{userText}\" → \"{optimizedQuery}\", recency={searchRecency}");
                                    StatusLabel.Text = $"搜索词已优化: \"{optimizedQuery}\"";
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Info($"搜索词优化失败，使用原始查询: {ex.Message}");
                                // 优化失败不影响流程，使用原始查询
                            }
                        }

                        var searchResults = await _webSearchService.SearchAsync(optimizedQuery, _currentStreamingCts.Token, searchRecency);
                        capturedSearchResults = searchResults;
                        if (searchResults.Count > 0)
                        {
                            string providerLabel = _webSearchService.ActiveProvider == SearchProvider.Baidu
                                ? "百度搜索" : "DuckDuckGo";
                            StatusLabel.Text = $"已通过 {providerLabel} 获取 {searchResults.Count} 条搜索结果，正在抓取网页内容…";

                            // 在助手消息中显示搜索状态
                            assistantMsg.Content = $"🔍 已联网搜索到 {searchResults.Count} 条结果（{providerLabel}），正在抓取网页内容…\n\n";
                            _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                            // ── 抓取网页内容增强上下文（await 确保完成后才构建 AI 上下文） ──
                            await EnrichSearchContextAsync(searchResults, _currentStreamingCts.Token);
                            searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);

                            Logger.Info($"联网搜索完成，通过 {providerLabel} 获取 {searchResults.Count} 条结果");
                        }
                        else
                        {
                            // 检查是否是百度额度耗尽
                            if (_webSearchService.IsBaiduQuotaExhausted)
                            {
                                engineSwitchNote = "⚠️ 百度搜索免费额度已用尽，本次已自动切换至 DuckDuckGo。请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待次日重置。";
                                StatusLabel.Text = "⚠️ 百度搜索额度已耗尽，已自动切换至 DuckDuckGo";
                                assistantMsg.Content = "⚠️ 百度搜索免费额度已用尽，已自动切换至 DuckDuckGo 搜索…\n\n";
                                _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                                // 立即用 DuckDuckGo 重试（使用优化后的搜索词）
                                searchResults = await _webSearchService.SearchAsync(optimizedQuery, _currentStreamingCts.Token);
                                capturedSearchResults = searchResults;
                                if (searchResults.Count > 0)
                                {
                                    StatusLabel.Text = $"已通过 DuckDuckGo 获取 {searchResults.Count} 条结果，正在抓取网页内容…";
                                    assistantMsg.Content = $"🔍 已通过 DuckDuckGo 搜索到 {searchResults.Count} 条结果，正在抓取网页内容…\n\n";
                                    _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                                    await EnrichSearchContextAsync(searchResults, _currentStreamingCts.Token);
                                    searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);
                                }
                            }
                            else
                            {
                                StatusLabel.Text = "未找到搜索结果，使用内置知识回复…";
                            }
                            Logger.Info("联网搜索未找到结果");
                        }
                    }
                    catch (ApiKeyInvalidException ex)
                    {
                        // 百度 Key 无效 → 与 DeepSeek API Key 无效相同逻辑：直接报错并停止，不静默回退
                        Logger.Error($"[Render] 百度 API Key 无效", ex);
                        assistantMsg.Content = "⚠️ 百度搜索 API Key 无效，请检查配置：工具 → 选项 → DeepSeek Chat → Web Search。\n\n获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n\n也可以切换到 DuckDuckGo 搜索（免费，无需 Key）。";
                        assistantMsg.IsStreaming = false;
                        await UpdateStreamingMessageAsync(assistantMsgIndex,
                            assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                        lock (_lock) { _messages.Remove(assistantMsg); } // 不保存到对话记录
                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        StatusLabel.Text = "⚠️ 百度 API Key 无效";
                        _currentStreamingCts?.Cancel();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"联网搜索异常: {ex.Message}", ex);
                        StatusLabel.Text = "搜索失败，使用内置知识回复…";
                    }
                }

                // ── 引擎切换提示：若用户选择百度但实际使用了 DuckDuckGo，记录原因 ──
                if (string.IsNullOrEmpty(engineSwitchNote) &&
                    _webSearchEngine == "Baidu" &&
                    _webSearchService != null &&
                    _webSearchService.ActiveProvider == SearchProvider.DuckDuckGo)
                {
                    engineSwitchNote = "⚠️ 百度搜索未返回结果，本次已自动切换至 DuckDuckGo。";
                }
                if (!string.IsNullOrEmpty(engineSwitchNote))
                {
                    _pendingWarnings.Add(engineSwitchNote!);
                }

                try
                {
                    // ── 带工具调用的对话循环（最多 5 轮工具调用） ──
                    const int maxToolCallRounds = 5;
                    var reasoningBuffer = new StringBuilder();
                    var contentBuffer = new StringBuilder();
                    var toolCallAccumulator = new Dictionary<int, ToolCallAccumulator>();
                    int streamRenderTick = 0;
                    int lastReasoningLength = 0;

                    for (int round = 0; round < maxToolCallRounds; round++)
                    {
                        toolCallAccumulator.Clear();
                        reasoningBuffer.Clear();
                        contentBuffer.Clear();
                        streamRenderTick = 0;
                        lastReasoningLength = 0;

                        var requestMessages = BuildRequestMessages(searchContext);

                        // 获取工具定义（第一轮时传递，后续轮也可传递）
                        List<ToolDefinition>? toolDefs = null;
                        if (_mcpManager != null && _mcpManager.AllTools.Count > 0)
                        {
                            toolDefs = _mcpManager.GetToolDefinitions();
                            Logger.Info($"[MCP] 本轮携带 {toolDefs.Count} 个工具定义");
                        }

                        var apiService = _apiService!;
                        await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, toolDefs, _currentStreamingCts.Token))
                        {
                            if (chunk.StartsWith("[THINKING]"))
                            {
                                var thinking = chunk.Substring(10);
                                reasoningBuffer.Append(thinking);
                                StatusLabel.Text = "DeepSeek 深度思考中…";

                                if (reasoningBuffer.Length - lastReasoningLength >= 80)
                                {
                                    assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                                    lastReasoningLength = reasoningBuffer.Length;
                                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                                        contentBuffer.ToString(),
                                        reasoningBuffer.ToString(),
                                        isComplete: false);
                                }
                            }
                            else if (chunk.StartsWith("[TOOL_CALL]"))
                            {
                                // ── 解析工具调用增量 ──
                                var tcJson = chunk.Substring(11);
                                try
                                {
                                    var deltas = System.Text.Json.JsonSerializer.Deserialize<List<ToolCallDelta>>(tcJson);
                                    if (deltas != null)
                                    {
                                        foreach (var delta in deltas)
                                        {
                                            if (!toolCallAccumulator.ContainsKey(delta.Index))
                                                toolCallAccumulator[delta.Index] = new ToolCallAccumulator();

                                            var acc = toolCallAccumulator[delta.Index];
                                            if (!string.IsNullOrEmpty(delta.Id))
                                                acc.Id = delta.Id!;
                                            if (!string.IsNullOrEmpty(delta.Type))
                                                acc.Type = delta.Type;
                                            if (delta.Function != null)
                                            {
                                                if (!string.IsNullOrEmpty(delta.Function.Name))
                                                    acc.FunctionName = delta.Function.Name;
                                                if (!string.IsNullOrEmpty(delta.Function.Arguments))
                                                    acc.ArgumentsBuilder.Append(delta.Function.Arguments);
                                            }
                                        }
                                    }
                                }
                                catch (System.Text.Json.JsonException) { }

                                StatusLabel.Text = $"调用工具: {string.Join(", ", toolCallAccumulator.Values.Where(a => !string.IsNullOrEmpty(a.FunctionName)).Select(a => a.FunctionName))}...";
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
                                StatusLabel.Text = "DeepSeek 回复中...";

                                if (streamRenderTick >= StreamRenderInterval)
                                {
                                    streamRenderTick = 0;
                                    assistantMsg.Content = contentBuffer.ToString();
                                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                                        contentBuffer.ToString(),
                                        reasoningBuffer.ToString(),
                                        isComplete: false);
                                }
                            }
                        }

                        // ── 检查是否有工具调用 ──
                        if (toolCallAccumulator.Count > 0)
                        {
                            Logger.Info($"[MCP] 检测到 {toolCallAccumulator.Count} 个工具调用，开始执行...");
                            StatusLabel.Text = $"正在执行 MCP 工具...";

                            // 更新 UI：显示正在调用工具
                            string toolCallSummary = "🔧 正在调用工具:\n";
                            foreach (var acc in toolCallAccumulator.Values)
                            {
                                toolCallSummary += $"- `{acc.FunctionName}`\n";
                            }
                            assistantMsg.Content = contentBuffer.Length > 0
                                ? contentBuffer.ToString() + "\n\n" + toolCallSummary
                                : toolCallSummary;
                            await UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, reasoningBuffer.ToString(), isComplete: false);

                            // ── 将助手的 tool_calls 消息加入对话历史 ──
                            var assistantToolCalls = toolCallAccumulator.Values
                                .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                                .Select(a => new ToolCall
                                {
                                    Id = a.Id,
                                    Type = a.Type ?? "function",
                                    Function = new ToolCallFunction
                                    {
                                        Name = a.FunctionName!,
                                        Arguments = a.ArgumentsBuilder.ToString()
                                    }
                                }).ToList();

                            if (assistantToolCalls.Count > 0)
                            {
                                _conversationHistory.Add(new ChatApiMessage
                                {
                                    Role = "assistant",
                                    Content = contentBuffer.Length > 0 ? contentBuffer.ToString() : null,
                                    ToolCalls = assistantToolCalls
                                });
                            }

                            // ── 执行每个工具调用并将结果加入对话历史 ──
                            foreach (var acc in toolCallAccumulator.Values)
                            {
                                if (string.IsNullOrEmpty(acc.FunctionName)) continue;

                                string toolResult;
                                try
                                {
                                    // ── 预处理 OCR 工具参数：AI 可能传文件名而非 base64，需自动转换 ──
                                    string sanitizedArgs = SanitizeOcrToolArguments(
                                        acc.FunctionName!,
                                        acc.ArgumentsBuilder.ToString());

                                    toolResult = await _mcpManager!.CallToolAsync(
                                        acc.FunctionName!,
                                        sanitizedArgs,
                                        _currentStreamingCts.Token);
                                }
                                catch (Exception ex)
                                {
                                    toolResult = $"❌ 工具执行异常: {ex.Message}";
                                    Logger.Error($"[MCP] 工具 {acc.FunctionName} 执行异常: {ex.Message}", ex);
                                }

                                // 添加 tool 角色消息
                                _conversationHistory.Add(new ChatApiMessage
                                {
                                    Role = "tool",
                                    Content = toolResult,
                                    ToolCallId = acc.Id,
                                    Name = acc.FunctionName
                                });

                                Logger.Info($"[MCP] 工具 {acc.FunctionName} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");
                            }

                            // ── 更新 UI：显示工具调用结果摘要 ──
                            string resultSummary = contentBuffer.Length > 0
                                ? contentBuffer.ToString() + "\n\n✅ 工具调用完成，AI 正在分析结果...\n"
                                : "✅ 工具调用完成，AI 正在分析结果...\n";
                            assistantMsg.Content = resultSummary;
                            await UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, reasoningBuffer.ToString(), isComplete: false);

                            // 继续循环，让 AI 处理工具结果
                            continue;
                        }

                        // ── 没有工具调用，正常结束 ──
                        break;
                    }

                    // ── 流式完成：渲染最终 Markdown ──
                    assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                    assistantMsg.Content = contentBuffer.ToString();
                    assistantMsg.IsStreaming = false;

                    Logger.Info($"[Render] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                    string finalJs = ChatHtmlService.BuildFinalRenderJs(
                        assistantMsgIndex,
                        contentBuffer.ToString(),
                        reasoningBuffer.ToString());

                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

                    // ── 注入搜索结果链接卡片到 AI 消息上方 ──
                    if (capturedSearchResults.Count > 0)
                    {
                        string providerLabel = _webSearchService?.ActiveProvider == SearchProvider.Baidu
                            ? "百度搜索" : "DuckDuckGo";
                        string searchCardJs = ChatHtmlService.BuildSearchResultsInjectionJs(
                            assistantMsgIndex, capturedSearchResults, providerLabel);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(searchCardJs);
                    }

                    _conversationHistory.Add(new ChatApiMessage { Role = "assistant", Content = contentBuffer.ToString() });

                    // 后台持久化
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
                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    lock (_lock) { _messages.Remove(assistantMsg); } // 不保存到对话记录
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("[Render] 用户停止生成");
                    assistantMsg.Content += "\n\n*[已停止]*";
                    assistantMsg.IsStreaming = false;
                    // 使用 BuildFinalRenderJs 完成渲染（含重试按钮注入）
                    string finalJs = ChatHtmlService.BuildFinalRenderJs(
                        assistantMsgIndex,
                        assistantMsg.Content,
                        assistantMsg.ReasoningContent);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
                {
                    Logger.Error($"[Render] API 认证失败", ex);
                    assistantMsg.Content = "⚠️ DeepSeek API Key 无效或已过期，请通过 工具 → 选项 → DeepSeek Chat 重新配置。\n获取密钥：https://platform.deepseek.com/api_keys";
                    assistantMsg.IsStreaming = false;
                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    lock (_lock) { _messages.Remove(assistantMsg); } // 不保存到对话记录
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Render] API 出错", ex);
                    assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                    assistantMsg.IsStreaming = false;
                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                }
                finally
                {
                    assistantMsg.IsStreaming = false;
                    lock (_lock)
                    {
                        _isGenerating = false;
                    }
                    StatusLabel.Text = string.Empty;
                    _currentStreamingCts?.Dispose();
                    _currentStreamingCts = null;
                    UpdateButtonsState();
                }
            }
            catch (Exception ex)
            {
                // 顶层兜底：捕获任何未预期的异常
                Logger.Error($"[Render] SendMessage 未处理异常: {ex.Message}", ex);
                lock (_lock)
                {
                    _isGenerating = false;
                }
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"发生错误: {ex.Message}";
                }
                catch { }
            }
        }
#pragma warning restore VSTHRD100

        /// <summary>
        /// 从已解析的附件内容中提取关键信息，用于优化联网搜索查询。
        /// 当用户上传文件并开启联网搜索时调用，在搜索优化之前执行。
        /// 使用 AI（非流式）从文件内容中提取核心主题、技术关键词、专有名词等，
        /// 返回简洁的摘要供搜索优化阶段使用。
        /// </summary>
        /// <param name="fileContent">已解析的文件内容（由 FileParserService 生成）。</param>
        /// <param name="userQuestion">用户的原始问题。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>提取的关键信息摘要；失败或无需提取时返回 null。</returns>
        private async Task<string?> ExtractKeyInfoForSearchAsync(string fileContent, string userQuestion, CancellationToken ct)
        {
            if (_apiService == null || string.IsNullOrWhiteSpace(fileContent))
                return null;

            // 截断过长的文件内容，避免 token 消耗过多（取前 8000 字符）
            string truncatedContent = fileContent.Length > 8000
                ? fileContent.Substring(0, 8000) + "\n...[内容已截断]"
                : fileContent;

            var extractionPrompt = AiPrompts.BuildFileExtractionPrompt(userQuestion, truncatedContent);

            try
            {
                var extractionMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = AiPrompts.FileExtractionSystem },
                    new ChatApiMessage { Role = "user", Content = extractionPrompt },
                };

                Logger.Info("开始从附件提取关键信息用于搜索优化");
                var rawResponse = await _apiService.CompleteAsync(extractionMessages, ct);
                Logger.Info($"附件关键信息提取原始响应: {rawResponse}");

                string result = rawResponse?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(result) ||
                    result.Equals("NO_INFO", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // 截断过长结果
                if (result.Length > 500)
                    result = result.Substring(0, 500);

                return result;
            }
            catch (OperationCanceledException)
            {
                Logger.Info("附件关键信息提取已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"附件关键信息提取异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 调用 AI 分析用户问题和上下文，生成优化的搜索关键词。
        /// 百度引擎：返回严格 JSON（含 search_recency 时效过滤）。
        /// DuckDuckGo：仅返回优化后的纯文本关键词。
        /// </summary>
        /// <param name="userQuery">用户原始问题</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="isBaiduSearch">是否使用百度搜索（true=JSON格式，false=纯文本关键词）</param>
        /// <returns>优化后的搜索查询对象，失败返回 null</returns>
        private async Task<SearchQueryOptimization?> OptimizeSearchQueryAsync(string userQuery, CancellationToken ct, bool isBaiduSearch = true)
        {
            if (_apiService == null)
                return null;

            // ── 构建优化提示词 ──
            string contextSummary = string.Empty;
            if (_conversationHistory.Count > 1)
            {
                // 取最近几条用户消息作为上下文
                var recent = _conversationHistory
                    .Where(m => m.Role == "user")
                    .Reverse()
                    .Take(3)
                    .Reverse()
                    .Select(m => m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content);
                contextSummary = string.Join(" | ", recent);
            }

            string contextLine = string.IsNullOrWhiteSpace(contextSummary)
                ? $"用户问题：{userQuery}"
                : $"对话上下文：{contextSummary}\n用户问题：{userQuery}";

            string optimizationPrompt = AiPrompts.BuildSearchOptimizationPrompt(contextLine, isBaiduSearch);
            string systemPrompt = AiPrompts.GetSearchOptimizationSystemPrompt(isBaiduSearch);

            try
            {
                var optimizationMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = systemPrompt },
                    new ChatApiMessage { Role = "user", Content = optimizationPrompt },
                };

                Logger.Info($"开始 AI 搜索词优化 ({(isBaiduSearch ? "百度" : "DuckDuckGo")})，原始查询: \"{userQuery}\"");
                var rawResponse = await _apiService.CompleteAsync(optimizationMessages, ct);
                Logger.Info($"AI 搜索词优化原始响应: {rawResponse}");

                if (isBaiduSearch)
                {
                    // ── 百度：校验 JSON ──
                    return ParseAndValidateSearchOptimization(rawResponse, userQuery);
                }
                else
                {
                    // ── DuckDuckGo：纯文本关键词 ──
                    string keyword = rawResponse?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(keyword) ||
                        keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SearchQueryOptimization
                        {
                            SearchQuery = userQuery,
                            NeedSearch = keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase) ? false : true,
                        };
                    }
                    // 清理可能的多余内容（AI 偶尔会返回带引号或前缀的文字）
                    keyword = keyword.Trim('"', '\'', '`');
                    if (keyword.Length > 72)
                        keyword = keyword.Substring(0, 72);
                    return new SearchQueryOptimization
                    {
                        SearchQuery = keyword,
                        NeedSearch = true,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("搜索词优化已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"搜索词优化异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 解析并校验 AI 返回的搜索优化 JSON。
        /// 若 JSON 不合法或关键字段缺失，回退到原始查询。
        /// </summary>
        private static SearchQueryOptimization ParseAndValidateSearchOptimization(string rawResponse, string fallbackQuery)
        {
            try
            {
                // 尝试提取 JSON 部分（AI 可能在 JSON 前后附加了文字）
                string jsonStr = rawResponse.Trim();

                // 去掉可能的 markdown 代码块标记
                if (jsonStr.StartsWith("```"))
                {
                    int endOfFirstLine = jsonStr.IndexOf('\n');
                    if (endOfFirstLine > 0)
                        jsonStr = jsonStr.Substring(endOfFirstLine + 1);
                    if (jsonStr.EndsWith("```"))
                        jsonStr = jsonStr.Substring(0, jsonStr.Length - 3);
                    jsonStr = jsonStr.Trim();
                }

                var result = System.Text.Json.JsonSerializer.Deserialize<SearchQueryOptimization>(jsonStr,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });

                // ── 校验 ──
                if (result == null)
                    throw new InvalidOperationException("JSON 解析结果为 null");

                if (string.IsNullOrWhiteSpace(result.SearchQuery))
                {
                    Logger.Info("AI 优化搜索词为空，使用原始查询");
                    return new SearchQueryOptimization
                    {
                        SearchQuery = fallbackQuery,
                        NeedSearch = result.NeedSearch,
                    };
                }

                // 校验 recency 值
                var validRecencies = new HashSet<string> { "week", "month", "semiyear", "year" };
                if (result.SearchRecency != null && !validRecencies.Contains(result.SearchRecency))
                {
                    Logger.Info($"无效的 search_recency 值: {result.SearchRecency}，已忽略");
                    result.SearchRecency = null;
                }

                Logger.Info($"搜索词优化成功: \"{fallbackQuery}\" → \"{result.SearchQuery}\"");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Info($"搜索优化 JSON 解析失败: {ex.Message}，使用原始查询 \"{fallbackQuery}\"");
                return new SearchQueryOptimization
                {
                    SearchQuery = fallbackQuery,
                    NeedSearch = true,
                };
            }
        }

        /// <summary>
        /// 将用户输入中的时间词语替换为具体日期。
        /// 例如："今天" → "2026-05-06"，"本周" → "2026-05-04 至 2026-05-10"。
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

            // 精确匹配（长词优先，避免"今天"匹配到"今天天气"中的一部分）
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
            {
                result = result.Replace(kvp.Key, kvp.Value);
            }

            if (result != query)
                Logger.Info($"时间词语解析: \"{query}\" → \"{result}\"");

            return result;
        }

        /// <summary>
        /// 异步抓取搜索结果中前几条 URL 的网页内容，用于增强搜索上下文。
        /// 这是"尽力而为"的后台操作，失败不影响主流程。
        /// </summary>
        private async Task EnrichSearchContextAsync(List<WebSearchResult> results, CancellationToken ct)
        {
            if (_webSearchService == null || results.Count == 0) return;

            try
            {
                // 只抓取前6条结果的网页内容
                int fetchCount = Math.Min(6, results.Count);
                for (int i = 0; i < fetchCount; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        string? pageContent = await _webSearchService.FetchWebPageContentAsync(results[i].Url, ct);
                        if (!string.IsNullOrWhiteSpace(pageContent))
                        {
                            // 将提取的网页内容追加到结果的 Snippet 中
                            string enriched = results[i].Snippet +
                                $"\n[网页内容摘要: {TruncateText(pageContent!, 300)}]";
                            results[i].Snippet = TruncateText(enriched, 800);
                            Logger.Info($"网页内容抓取成功: {results[i].Url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"网页内容抓取跳过 ({results[i].Url}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"网页内容增强失败: {ex.Message}", ex);
            }
        }

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 构建发送给 API 的消息列表。
        /// 当启用联网搜索时，将搜索结果作为系统消息注入到对话历史之前。
        /// 保留工具调用消息（tool_calls / tool_call_id），确保多轮工具调用正常工作。
        /// </summary>
        /// <param name="searchContext">联网搜索的结果上下文，为空则不注入。</param>
        private List<ChatApiMessage> BuildRequestMessages(string searchContext = "")
        {
            var messages = new List<ChatApiMessage>();

            // ── 系统提示词 ──
            string systemPrompt = _options?.SystemPrompt ?? string.Empty;

            // ── 注入 AskAgent 系统提示词（当走问答路径时）──
            if (_agentDispatcher != null)
            {
                string askAgentPrompt = _agentDispatcher.AskAgent.Definition.SystemPrompt;
                if (!string.IsNullOrWhiteSpace(askAgentPrompt))
                {
                    systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                        ? askAgentPrompt
                        : systemPrompt + "\n\n" + askAgentPrompt;
                }
            }

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                // ── 注入 Skill 发现上下文 ──
                string skillContext = string.Empty;
                try
                {
                    if (_skillDiscoveryResult == null)
                    {
                        // 首次使用时快速同步发现（已有缓存）
                        _skillDiscoveryResult = SkillService.Instance.DiscoverSkillsAsync(_solutionPath).Result;
                    }
                    skillContext = SkillService.Instance.GenerateSkillsDiscoveryContext(_skillDiscoveryResult);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Skill] 构建技能上下文失败: {ex.Message}");
                }

                string finalSystemPrompt = systemPrompt;
                if (!string.IsNullOrWhiteSpace(skillContext))
                {
                    finalSystemPrompt = systemPrompt + "\n\n" + skillContext;
                    // ── 日志：记录注入的 Skill 发现上下文 ──
                    if (_skillDiscoveryResult != null)
                    {
                        var skillNames = string.Join(", ", _skillDiscoveryResult.AutoLoadableSkills.ConvertAll(s => s.Name));
                        Logger.Info($"[Skill] 系统提示注入可用技能列表: {_skillDiscoveryResult.AutoLoadableSkills.Count} 个 → {skillNames}");
                    }
                }

                messages.Add(new ChatApiMessage { Role = "system", Content = finalSystemPrompt });
            }

            // ── 注入联网搜索结果作为系统消息 ──
            if (!string.IsNullOrWhiteSpace(searchContext))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = searchContext });
            }

            // ── 对话历史（保留 tool_calls / tool_call_id 字段） ──
            foreach (var m in _conversationHistory)
            {
                var apiMsg = new ChatApiMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                };

                // 保留工具调用相关字段
                if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    apiMsg.ToolCalls = m.ToolCalls;

                if (!string.IsNullOrEmpty(m.ToolCallId))
                    apiMsg.ToolCallId = m.ToolCallId;

                if (!string.IsNullOrEmpty(m.Name))
                    apiMsg.Name = m.Name;

                messages.Add(apiMsg);
            }

            return messages;
        }

        private void StopGeneration()
        {
            try
            {
                lock (_lock)
                {
                    _currentStreamingCts?.Cancel();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"StopGeneration 异常: {ex.Message}", ex);
            }
        }

#pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件处理，符合 WPF 模式
        private async void ClearConversation()
        {
            try
            {
                lock (_lock)
                {
                    if (_isGenerating)
                    {
                        _currentStreamingCts?.Cancel();
                        _isGenerating = false;
                    }
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
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"清空失败: {ex.Message}";
                }
                catch { }
            }
        }
#pragma warning restore VSTHRD100

        #endregion

        #region Private Methods - Helpers

        /// <summary>
        /// 处理斜杠命令。如果用户输入以 / 开头，尝试匹配已注册的技能。
        /// 匹配成功时返回技能的完整指令文本，匹配失败时显示错误并返回 null。
        /// 非斜杠命令（不以 / 开头）返回 string.Empty 表示正常发送。
        /// </summary>
        /// <param name="userText">用户输入的原始文本（已 Trim）。</param>
        /// <returns>
        /// - null: 斜杠命令格式但未匹配到技能（已显示错误提示）
        /// - string.Empty: 非斜杠命令，正常流程
        /// - 非空字符串: 匹配到的技能完整指令
        /// </returns>
        private async Task<string?> ResolveSlashCommandAsync(string userText)
        {
            // 不以 / 开头，不是斜杠命令
            if (string.IsNullOrEmpty(userText) || !userText.StartsWith("/"))
                return string.Empty;

            // 提取命令名（去掉 / 和后续参数）
            var parts = userText.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                // 只有 /，提示可用技能
                return null; // 将由下面的逻辑处理
            }

            var commandName = parts[0].ToLowerInvariant();

            // ── 内置命令：/help — 列出所有可用技能 ──
            if (commandName == "help")
            {
                Logger.Info($"[Skill] 调用元命令: /help (用户输入: \"{userText}\")");
                await ShowSkillsHelpAsync();
                return null;
            }

            // ── 刷新技能缓存 ──
            if (commandName == "refresh-skills")
            {
                Logger.Info($"[Skill] 调用元命令: /refresh-skills (用户输入: \"{userText}\")");
                await RefreshSkillsAsync();
                return null;
            }

            // ── 创建技能 ──
            if (commandName == "create-skill")
            {
                var skillNameArg = parts.Length > 1 ? parts[1] : null;
                Logger.Info($"[Skill] 调用元命令: /create-skill (参数: {skillNameArg ?? "(无)"}, 用户输入: \"{userText}\")");
                await CreateSkillAsync(skillNameArg);
                return null;
            }

            // ── 查找匹配的技能 ──
            try
            {
                if (_skillDiscoveryResult == null)
                {
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                }

                var skill = SkillService.Instance.FindSkill(commandName, _skillDiscoveryResult);
                if (skill != null)
                {
                    // ── 详细日志：记录技能调用信息 ──
                    Logger.Info($"[Skill] ═══ 用户显式调用技能 ═══");
                    Logger.Info($"[Skill]   技能名称: {skill.Name}");
                    Logger.Info($"[Skill]   调用方式: 斜杠命令 /{commandName}");
                    Logger.Info($"[Skill]   用户输入: \"{userText}\"");
                    Logger.Info($"[Skill]   技能来源: {skill.Source}");
                    Logger.Info($"[Skill]   技能描述: {skill.Description}");
                    Logger.Info($"[Skill]   文件路径: {skill.FilePath}");
                    Logger.Info($"[Skill]   资源文件数: {skill.ResourceFiles.Count}");
                    Logger.Info($"[Skill] ══════════════════════════");

                    // 在 UI 显示技能调用提示
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"🎯 已加载技能: {skill.Name}";

                    // 返回完整技能指令
                    var instructions = skill.GetFullInstructions();
                    var userInstructions = $"用户通过 /{commandName} 调用了技能 \"{skill.Name}\"。请按以下技能指令执行：\n\n{instructions}";
                    return userInstructions;
                }
                else
                {
                    // 未匹配，显示错误提示
                    Logger.Warn($"[Skill] 未知斜杠命令: /{commandName}");

                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var availableSkills = _skillDiscoveryResult?.UserInvocableSkills ?? new List<SkillDefinition>();
                    var skillListStr = availableSkills.Count > 0
                        ? string.Join("\n", availableSkills.ConvertAll(s => $"  • `/{s.Name}` — {s.Description}"))
                        : "  （暂无可用技能）";

                    var errorMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"⚠️ **未知命令**: `/{commandName}`\n\n" +
                                  $"可用的技能命令：\n{skillListStr}\n\n" +
                                  $"输入 `/help` 查看完整帮助。",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(errorMsg);
                    AddMessagesHtml("assistant", errorMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"⚠️ 未知命令: /{commandName}";

                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 斜杠命令处理失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 显示所有可用技能的帮助信息。
        /// </summary>
        private async Task ShowSkillsHelpAsync()
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                if (_skillDiscoveryResult == null)
                {
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("## 📋 Skill 系统");
                sb.AppendLine();

                // ── 内置命令 ──
                sb.AppendLine("### 🛠️ 内置命令");
                sb.AppendLine("| 命令 | 说明 |");
                sb.AppendLine("|------|------|");
                sb.AppendLine("| `/help` | 显示此帮助信息 |");
                sb.AppendLine("| `/create-skill <名称>` | 创建新的自定义 Skill 模板 |");
                sb.AppendLine("| `/refresh-skills` | 强制刷新技能缓存 |");
                sb.AppendLine();

                var allSkills = _skillDiscoveryResult?.Skills ?? new List<SkillDefinition>();
                if (allSkills.Count == 0)
                {
                    sb.AppendLine("### 📦 自定义技能");
                    sb.AppendLine("暂无可用技能。");
                    sb.AppendLine();
                    sb.AppendLine("**快速创建技能：**");
                    sb.AppendLine("输入 `/create-skill 技能名` 一键生成模板。");
                    sb.AppendLine();
                    sb.AppendLine("**手动创建：**");
                    sb.AppendLine("1. 在项目根目录创建 `.github/skills/<技能名>/SKILL.md`");
                    sb.AppendLine("2. 或在用户目录创建 `~/.copilot/skills/<技能名>/SKILL.md`");
                    sb.AppendLine();
                    sb.AppendLine("**SKILL.md 格式：**");
                    sb.AppendLine("```yaml");
                    sb.AppendLine("---");
                    sb.AppendLine("name: my-skill");
                    sb.AppendLine("description: '技能描述（含触发关键词）。Use when: ...'");
                    sb.AppendLine("argument-hint: '[可选参数]'");
                    sb.AppendLine("user-invocable: true");
                    sb.AppendLine("---");
                    sb.AppendLine("# 技能标题");
                    sb.AppendLine("## 何时使用");
                    sb.AppendLine("- 场景一");
                    sb.AppendLine("## 流程");
                    sb.AppendLine("1. 步骤一");
                    sb.AppendLine("2. 步骤二");
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine("### 📦 自定义技能");
                    sb.AppendLine("| 命令 | 来源 | 类型 | 说明 |");
                    sb.AppendLine("|------|------|------|------|");
                    foreach (var skill in allSkills)
                    {
                        var sourceLabel = skill.Source switch
                        {
                            SkillSource.Project => "📁 项目",
                            SkillSource.User => "👤 用户",
                            SkillSource.BuiltIn => "📦 内置",
                            _ => "❓"
                        };
                        var typeLabel = skill.UserInvocable ? "✅ 可调用" : "🤖 自动";
                        var desc = TruncateText(skill.Description, 60);
                        sb.AppendLine($"| `/{skill.Name}` | {sourceLabel} | {typeLabel} | {desc} |");
                    }
                    sb.AppendLine();
                    sb.AppendLine($"💡 输入 `/create-skill 新技能名` 创建更多技能。");
                }

                // ── 调用时机说明 ──
                sb.AppendLine();
                sb.AppendLine("### ⏱️ 技能何时被调用？");
                sb.AppendLine("| 方式 | 触发条件 | 说明 |");
                sb.AppendLine("|------|----------|------|");
                sb.AppendLine("| 🗣️ **用户显式调用** | 输入 `/技能名` | 用户主动通过斜杠命令调用 |");
                sb.AppendLine("| 🧠 **AI 语义匹配** | AI 分析用户意图 | AI 自动识别匹配的技能并加载 |");
                sb.AppendLine("| 📍 **上下文推断** | 多轮对话积累 | AI 根据对话历史主动建议技能 |");

                var helpMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = sb.ToString(),
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(helpMsg);
                AddMessagesHtml("assistant", helpMsg.Content);
                UpdateBrowser();
                StatusLabel.Text = $"共 {allSkills.Count} 个技能可用";
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 显示技能帮助失败: {ex.Message}");
                StatusLabel.Text = "显示技能帮助失败";
            }
        }

        /// <summary>
        /// 强制刷新技能缓存。
        /// </summary>
        private async Task RefreshSkillsAsync()
        {
            try
            {
                _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath, forceRefresh: true);

                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var count = _skillDiscoveryResult?.TotalCount ?? 0;
                StatusLabel.Text = $"✅ 技能已刷新: 共 {count} 个技能";
                Logger.Info($"[Skill] 手动刷新完成: {count} 个技能");

                // 同时显示简短的刷新结果
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"✅ **技能已刷新**: 共发现 {count} 个技能");
                if (_skillDiscoveryResult != null && _skillDiscoveryResult.UserInvocableSkills.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("可用命令：");
                    foreach (var s in _skillDiscoveryResult.UserInvocableSkills)
                        sb.AppendLine($"- `/{s.Name}` — {s.Description}");
                }

                var msg = new ChatMessage
                {
                    Role = "assistant",
                    Content = sb.ToString(),
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(msg);
                AddMessagesHtml("assistant", msg.Content);
                UpdateBrowser();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 刷新技能失败: {ex.Message}");
                StatusLabel.Text = "刷新技能失败";
            }
        }

        /// <summary>
        /// 创建新的自定义 Skill 模板。
        /// </summary>
        /// <param name="skillName">可选的技能名称（/create-skill &lt;name&gt;）</param>
        private async Task CreateSkillAsync(string? skillName)
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // 确定创建位置：优先项目级，其次用户级
                string? targetDir = null;
                string locationLabel;

                if (!string.IsNullOrEmpty(_solutionPath))
                {
                    var solutionDir = System.IO.Path.GetDirectoryName(_solutionPath);
                    // 向上查找 .sln 所在目录作为项目根
                    var current = solutionDir;
                    while (!string.IsNullOrEmpty(current))
                    {
                        if (System.IO.Directory.GetFiles(current, "*.sln").Length > 0)
                        {
                            solutionDir = current;
                            break;
                        }
                        var parent = System.IO.Directory.GetParent(current);
                        if (parent == null) break;
                        current = parent.FullName;
                    }

                    if (solutionDir != null)
                    {
                        targetDir = System.IO.Path.Combine(solutionDir, ".github", "skills");
                        locationLabel = $"项目目录: {targetDir}";
                    }
                    else
                    {
                        targetDir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".copilot", "skills");
                        locationLabel = $"用户目录: {targetDir}";
                    }
                }
                else
                {
                    targetDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".copilot", "skills");
                    locationLabel = $"用户目录: {targetDir}";
                }

                // 如果没有指定名称，使用对话框获取
                if (string.IsNullOrWhiteSpace(skillName))
                {
                    // 提示用户通过对话提供名称
                    var promptMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = "## 🛠️ 创建新技能\n\n" +
                                  "请告诉我新技能的名称（小写字母+数字+连字符），例如：\n" +
                                  "```\n/create-skill my-test-helper\n```\n\n" +
                                  $"技能将创建在: `{locationLabel}`\n\n" +
                                  "技能名称规范：\n" +
                                  "- 1-64 个字符\n" +
                                  "- 仅限小写字母、数字和连字符 (-)\n" +
                                  "- 示例: `code-review`, `api-doc-gen`, `deploy-check`",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(promptMsg);
                    AddMessagesHtml("assistant", promptMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = "请输入技能名称";
                    return;
                }

                // 验证技能名称格式
                if (!System.Text.RegularExpressions.Regex.IsMatch(skillName, @"^[a-z0-9][a-z0-9-]{0,63}$"))
                {
                    var errorMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"⚠️ **无效的技能名称**: `{skillName}`\n\n" +
                                  "技能名称必须：\n" +
                                  "- 1-64 个字符\n" +
                                  "- 仅限小写字母、数字和连字符 (-)\n" +
                                  "- 以字母或数字开头\n\n" +
                                  "有效示例: `code-review`, `api-doc-gen`, `deploy-check`",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(errorMsg);
                    AddMessagesHtml("assistant", errorMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"⚠️ 无效的技能名称: {skillName}";
                    return;
                }

                var skillDir = System.IO.Path.Combine(targetDir!, skillName);
                if (System.IO.Directory.Exists(skillDir))
                {
                    var existsMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"⚠️ 技能 `{skillName}` 已存在于 `{skillDir}`。\n\n" +
                                  $"输入 `/refresh-skills` 刷新后即可使用。",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(existsMsg);
                    AddMessagesHtml("assistant", existsMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"技能已存在: {skillName}";
                    return;
                }

                // 创建目录
                System.IO.Directory.CreateDirectory(skillDir);

                // 生成 SKILL.md 模板
                var skillContent = $@"---
name: {skillName}
description: '[请填写技能描述，包含触发关键词。Use when: ...]'
argument-hint: '[可选参数提示]'
user-invocable: true
---

# {FormatSkillTitle(skillName)}

## 何时使用
- [描述触发此技能的用户场景]
- [例如：用户请求 XXX 操作时]

## 流程
1. [步骤一：描述第一步操作]
2. [步骤二：描述第二步操作]
3. [步骤三：描述第三步操作]

## 输出格式
- [描述期望的输出格式]
- [例如：使用 Markdown 表格、代码块等]

## 注意事项
- [列出需要注意的边界条件、限制等]
";

                var skillFilePath = System.IO.Path.Combine(skillDir, "SKILL.md");
                System.IO.File.WriteAllText(skillFilePath, skillContent, System.Text.Encoding.UTF8);

                // 同时创建 scripts、references、assets 子目录的 .gitkeep
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(skillDir, "scripts"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(skillDir, "scripts", ".gitkeep"), string.Empty);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(skillDir, "references"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(skillDir, "references", ".gitkeep"), string.Empty);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(skillDir, "assets"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(skillDir, "assets", ".gitkeep"), string.Empty);

                Logger.Info($"[Skill] 创建技能 '{skillName}' 于: {skillDir}");

                // 刷新技能缓存
                _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath, forceRefresh: true);

                var successMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = $"## ✅ 技能创建成功!\n\n" +
                              $"**技能名称**: `{skillName}`\n" +
                              $"**位置**: `{skillDir}`\n\n" +
                              $"### 文件结构\n" +
                              $"```\n" +
                              $"{skillName}/\n" +
                              $"├── SKILL.md          ← 技能定义（请编辑 description）\n" +
                              $"├── scripts/          ← 可执行脚本\n" +
                              $"├── references/       ← 参考文档\n" +
                              $"└── assets/           ← 模板资源\n" +
                              $"```\n\n" +
                              $"### 下一步\n" +
                              $"1. ✏️ 编辑 `SKILL.md`，填写 **description**（这是 AI 发现技能的关键）\n" +
                              $"2. 📝 完善「何时使用」和「流程」部分\n" +
                              $"3. 🔄 输入 `/refresh-skills` 刷新缓存后即可使用\n\n" +
                              $"现在输入 `/{skillName}` 即可调用（模板内容需先完善）。",
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(successMsg);
                AddMessagesHtml("assistant", successMsg.Content);
                UpdateBrowser();
                StatusLabel.Text = $"✅ 技能已创建: {skillName}";
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] 创建技能失败: {ex.Message}");
                StatusLabel.Text = "创建技能失败";
            }
        }

        /// <summary>
        /// 将技能名称格式化为标题（如 "my-test-skill" → "My Test Skill"）。
        /// </summary>
        private static string FormatSkillTitle(string skillName)
        {
            if (string.IsNullOrEmpty(skillName)) return skillName;
            var parts = skillName.Split('-');
            return string.Join(" ", System.Array.ConvertAll(parts,
                p => p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : p));
        }

        /// <summary>
        /// AI 技能路由：根据用户问题和可用技能总结，判断是否应自动调用某个技能。
        /// 发送轻量级 AI 查询，解析返回的 JSON 判断结果。
        /// </summary>
        /// <param name="fullUserContent">完整用户消息内容（含文件上下文）</param>
        /// <returns>匹配到的技能指令，null 表示不需要调用技能</returns>
        private async Task<string?> RouteSkillAsync(string fullUserContent)
        {
            try
            {
                // ── 获取技能总结 ──
                string? skillsSummary = SkillService.Instance.GetSkillsSummary();
                if (string.IsNullOrEmpty(skillsSummary))
                {
                    // 尝试首次发现
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                    skillsSummary = SkillService.Instance.GetSkillsSummary();
                }

                if (string.IsNullOrEmpty(skillsSummary) || _apiService == null)
                    return null;

                // ── 截断用户内容（路由判断不需要完整长文本，安全截断避免破坏代理对） ──
                string truncatedContent;
                if (fullUserContent.Length > 1500)
                {
                    int cutPoint = 1500;
                    if (cutPoint < fullUserContent.Length
                        && char.IsHighSurrogate(fullUserContent[cutPoint - 1])
                        && char.IsLowSurrogate(fullUserContent[cutPoint]))
                    {
                        cutPoint--;
                    }
                    truncatedContent = fullUserContent.Substring(0, cutPoint) + "...";
                }
                else
                {
                    truncatedContent = fullUserContent;
                }

                // ── 构建路由请求 ──
                string routingUserPrompt = string.Format(
                    AiPrompts.SkillRoutingUserPrompt,
                    skillsSummary,
                    truncatedContent);

                var routingMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = AiPrompts.SkillRoutingSystemPrompt },
                    new ChatApiMessage { Role = "user", Content = routingUserPrompt }
                };

                // ── 调用 AI 进行路由判断（非流式，轻量级，禁用思考） ──
                StatusLabel.Text = "AI 正在匹配技能…";
                Logger.Info($"[SkillRoute] 开始技能路由判断 (用户输入 {fullUserContent.Length} 字符)");

                string? routingResponse = null;
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    routingResponse = await _apiService.CompleteAsync(routingMessages, cts.Token);
                    routingResponse = routingResponse?.Trim();
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn("[SkillRoute] 路由判断超时，跳过技能匹配");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SkillRoute] 路由判断失败: {ex.Message}");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                if (string.IsNullOrEmpty(routingResponse))
                {
                    Logger.Info("[SkillRoute] 路由判断返回空，跳过技能匹配");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                // ── 解析路由结果 JSON ──
                SkillRoutingResult? routingResult = null;
                try
                {
                    // 清理可能的 markdown 代码块包装
                    string cleanJson = routingResponse;
                    if (cleanJson.StartsWith("```"))
                    {
                        int startIdx = cleanJson.IndexOf('\n');
                        int endIdx = cleanJson.LastIndexOf("```");
                        if (startIdx >= 0 && endIdx > startIdx)
                            cleanJson = cleanJson.Substring(startIdx + 1, endIdx - startIdx - 1).Trim();
                    }
                    routingResult = System.Text.Json.JsonSerializer.Deserialize<SkillRoutingResult>(cleanJson);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[SkillRoute] 解析路由结果 JSON 失败: {ex.Message}, 原始响应: {routingResponse}");
                    return null;
                }

                if (routingResult == null || !routingResult.HasSkill)
                {
                    Logger.Info($"[SkillRoute] AI 判断无需调用技能" +
                        (routingResult?.Reason != null ? $": {routingResult.Reason}" : ""));
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                // ── 匹配到技能，查找并加载 ──
                string skillName = routingResult.Skill!;
                Logger.Info($"[SkillRoute] ═══ AI 自动匹配技能 ═══");
                Logger.Info($"[SkillRoute]   技能名称: {skillName}");
                Logger.Info($"[SkillRoute]   置信度: {routingResult.Confidence}");
                Logger.Info($"[SkillRoute]   匹配理由: {routingResult.Reason}");
                Logger.Info($"[SkillRoute] ══════════════════════════");

                // 确保技能列表是最新的
                if (_skillDiscoveryResult == null)
                {
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                }

                var matchedSkill = SkillService.Instance.FindSkill(skillName, _skillDiscoveryResult);
                if (matchedSkill == null)
                {
                    Logger.Warn($"[SkillRoute] AI 返回的技能 '{skillName}' 在可用列表中未找到");
                    StatusLabel.Text = "DeepSeek 思考中…";
                    return null;
                }

                // ── 更新 UI 状态 ──
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = $"🎯 AI 自动加载技能: {skillName}";

                // ── 返回技能指令（与斜杠命令格式一致） ──
                var instructions = matchedSkill.GetFullInstructions();
                Logger.Info($"[SkillRoute] 技能指令长度: {instructions.Length} 字符");

                return $"AI 自动匹配到技能 \"{matchedSkill.Name}\"（置信度: {routingResult.Confidence}，理由: {routingResult.Reason}）。请按以下技能指令执行：\n\n{instructions}";
            }
            catch (Exception ex)
            {
                Logger.Error($"[SkillRoute] 技能路由异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 预处理 OCR 相关工具调用参数。
        /// 
        /// 问题：AI 模型可能会传递裸文件名（如 "clipboard_xxx.png"）而非 base64 或完整路径，
        /// 导致 MCP 服务器端 OCR 工具找不到文件。此方法检测并自动将文件名转换为 base64 数据。
        /// 
        /// 解决策略：
        /// 1. 检测工具名是否为 OCR 工具（含 ocr/recognize_text/paddle_ocr 等关键词）
        /// 2. 解析参数 JSON
        /// 3. 检查图像参数是否看起来像裸文件名（非 base64，非完整路径）
        /// 4. 在已知的临时目录中搜索该文件
        /// 5. 找到后读取并转为 base64，替换原参数
        /// 6. 找不到则返回友好错误，跳过实际 MCP 调用
        /// </summary>
        private string SanitizeOcrToolArguments(string toolName, string argumentsJson)
        {
            // ── 只处理 OCR 相关工具 ──
            var ocrKeywords = new[] { "ocr", "recognize_text", "paddle_ocr", "ocr_image", "image_to_text", "read_text" };
            bool isOcrTool = ocrKeywords.Any(k => toolName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!isOcrTool)
                return argumentsJson;

            try
            {
                var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(argumentsJson);
                if (args == null || args.Count == 0)
                    return argumentsJson;

                // ── 查找图像数据参数 ──
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

                // ── 判断是否需要转换 ──
                // base64 通常以较长的字母数字开头，不含常见文件扩展名
                // 文件名通常含 .png/.jpg 等扩展名
                bool looksLikeFileName = imageValue.IndexOf('.') >= 0
                    && imageValue.Length < 500  // base64 通常很长
                    && !imageValue.StartsWith("/")  // 不是绝对路径
                    && !imageValue.StartsWith("data:")  // 不是 data URI
                    && !imageValue.Contains("::");  // 不是 Windows 路径（含盘符）
                                                    // 尝试检测 base64（不含空格的长字符串）
                                                    // 注意：这里的 base64 匹配很宽松，主要靠长度来判断

                // 更精确的判断：如果看起来像文件名或短字符串（不是 base64）
                bool isProbablyBase64 = imageValue.Length > 200
                    && !imageValue.Contains(' ')
                    && !imageValue.Contains('\n')
                    && imageValue.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');

                if (isProbablyBase64)
                {
                    Logger.Info($"[OCR-Sanitize] 参数已是 base64 ({imageValue.Length} 字符)，无需转换");
                    return argumentsJson;
                }

                // 检查是否是完整路径
                if (System.IO.File.Exists(imageValue))
                {
                    Logger.Info($"[OCR-Sanitize] 参数已是有效文件路径: {imageValue}");
                    // 虽然路径有效，但 MCP 服务器端可能无法访问，转为 base64 更安全
                    try
                    {
                        byte[] fileBytes = System.IO.File.ReadAllBytes(imageValue);
                        string base64 = Convert.ToBase64String(fileBytes);
                        args[imageKey] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                            System.Text.Json.JsonSerializer.Serialize(base64));
                        Logger.Info($"[OCR-Sanitize] 已将本地文件转为 base64 ({base64.Length} 字符): {imageValue}");
                        return System.Text.Json.JsonSerializer.Serialize(args);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[OCR-Sanitize] 读取本地文件失败: {ex.Message}");
                        return argumentsJson;
                    }
                }

                // ── 看起来像裸文件名，尝试在已知目录中解析 ──
                string fileName = System.IO.Path.GetFileName(imageValue);
                Logger.Info($"[OCR-Sanitize] 检测到 AI 传入裸文件名: '{imageValue}'，尝试解析为 base64...");

                // 搜索目录优先级：
                var searchDirs = new List<string>();

                // 1. 剪贴板临时目录
                string clipboardDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "clipboard");
                if (System.IO.Directory.Exists(clipboardDir))
                    searchDirs.Add(clipboardDir);

                // 2. 上下文临时目录
                string contextDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "context");
                if (System.IO.Directory.Exists(contextDir))
                    searchDirs.Add(contextDir);

                // 3. 附件文件路径
                lock (_lock)
                {
                    if (_attachedFilePaths.Count > 0)
                    {
                        foreach (var attachedPath in _attachedFilePaths)
                        {
                            if (System.IO.Path.GetFileName(attachedPath)
                                .Equals(fileName, StringComparison.OrdinalIgnoreCase))
                            {
                                searchDirs.Insert(0, System.IO.Path.GetDirectoryName(attachedPath)!);
                                break;
                            }
                        }
                    }
                }

                // ── 在目录中搜索文件 ──
                string? resolvedPath = null;
                foreach (var dir in searchDirs.Distinct())
                {
                    string candidate = System.IO.Path.Combine(dir, fileName);
                    if (System.IO.File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        break;
                    }

                    // 也尝试不带路径的模糊搜索
                    try
                    {
                        var matches = System.IO.Directory.GetFiles(dir, fileName, System.IO.SearchOption.TopDirectoryOnly);
                        if (matches.Length > 0)
                        {
                            resolvedPath = matches[0];
                            break;
                        }
                    }
                    catch { }
                }

                if (resolvedPath != null)
                {
                    byte[] fileBytes = System.IO.File.ReadAllBytes(resolvedPath);
                    string base64 = Convert.ToBase64String(fileBytes);
                    args[imageKey] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                        System.Text.Json.JsonSerializer.Serialize(base64));
                    Logger.Info($"[OCR-Sanitize] ✅ 已解析文件并转为 base64 ({base64.Length} 字符): {resolvedPath}");
                    return System.Text.Json.JsonSerializer.Serialize(args);
                }

                // ── 找不到文件，返回错误提示（会被 AI 看到并理解） ──
                Logger.Warn($"[OCR-Sanitize] ❌ 无法解析文件 '{imageValue}'，在以下目录中搜索失败: {string.Join(", ", searchDirs)}");

                // 直接返回错误 JSON 作为工具结果，避免浪费 MCP 调用
                // 注意：这里返回的不是 arguments，而是直接构造错误结果
                // 但因为调用者期望 arguments，我们改为让调用继续但标记错误
                // 策略：将参数改为明确的错误标记，MCP 调用会快速失败
                return argumentsJson; // 让原始调用进行，MCP 端会返回错误
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OCR-Sanitize] 参数预处理异常: {ex.Message}");
                return argumentsJson; // 失败时保持原参数
            }
        }

        private void UpdateButtonsState()
        {
            SendButton.IsEnabled = !_isGenerating;
            StopButton.Visibility = _isGenerating ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Visibility = _isGenerating ? Visibility.Collapsed : Visibility.Visible;
            InputTextBox.IsReadOnly = _isGenerating;
        }

        #endregion

        #region Coding Agent Workflow

        /// <summary>
        /// Agent 工作流主入口：
        /// 1. 分解任务 → 2. 显示步骤计划 → 3. 逐步执行 → 4. 显示变更摘要
        /// 注意：此方法在后台线程中调用（来自 Task.Run），访问 UI 前必须切换到主线程。
        /// </summary>
        private async Task RunAgentWorkflowAsync(string userText, string fileContext = "",
            AgentRoutingResult? routing = null)
        {
            if (_agentDispatcher == null) return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = "🤖 Agent 正在分析任务...";

                // ── 构建执行上下文 ──
                var context = new AgentContext
                {
                    SolutionPath = _solutionPath,
                    FileContext = fileContext,
                    ConversationHistory = new List<ChatApiMessage>(_conversationHistory),
                    ReadFileAsync = async (path) =>
                    {
                        if (File.Exists(path))
                            return await Task.Run(() => File.ReadAllText(path));
                        return null;
                    },
                };

                // ── 步骤 1: 通过 AgentDispatcher 执行（Plan → Edit 全流程）──
                await TaskScheduler.Default;

                // 订阅 EditAgent 的进度更新
                var editAgent = _agentDispatcher.EditAgent;
                editAgent.PlanUpdated += OnAgentPlanUpdated;
                _agentDispatcher.PlanUpdated += OnAgentDispatcherPlanUpdated;

                AgentResult agentResult;
                try
                {
                    agentResult = await _agentDispatcher.ExecuteAsync(userText, context, routing);
                }
                finally
                {
                    editAgent.PlanUpdated -= OnAgentPlanUpdated;
                    _agentDispatcher.PlanUpdated -= OnAgentDispatcherPlanUpdated;
                }

                // ── 步骤 2: 处理 Handoff（如果 Plan Agent 产出计划后需要切换到 Edit）──
                if (agentResult.Handoff != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"🤖 切换到 {agentResult.Handoff.TargetAgent} Agent...";

                    await TaskScheduler.Default;
                    agentResult = await _agentDispatcher.ExecuteHandoffAsync(agentResult.Handoff, context);
                }

                // ── 步骤 3: 切换到主线程显示结果 ──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (agentResult.Success && agentResult.Plan != null)
                {
                    var plan = agentResult.Plan;

                    // 计划已在 OnAgentDispatcherPlanUpdated 事件回调中通过增量渲染显示，
                    // 此处仅渲染最终的任务完成摘要（变更摘要），避免重复显示相同内容的对话框。

                    // 使用 JS 做一次最终步骤状态同步（确保进度 UI 与最终状态一致）
                    if (plan.Steps.Count > 0)
                    {
                        try
                        {
                            string finalProgressJs = ChatHtmlService.BuildAgentProgressUpdateJs(plan);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalProgressJs);
                        }
                        catch { /* 非关键：步骤进度同步失败不影响主流程 */ }
                    }

                    // 显示变更摘要（如果 Edit Agent 产生了文件变更）
                    if (plan.ChangedFiles.Count > 0)
                    {
                        string summaryHtml = ChatHtmlService.BuildAgentSummaryHtml(plan);
                        var summaryMsg = new ChatMessage
                        {
                            Role = "assistant",
                            Content = summaryHtml,
                            Timestamp = DateTime.Now,
                            IsRendered = true,
                            IsHtml = true,
                        };
                        lock (_lock) { _messages.Add(summaryMsg); }
                        AddMessagesHtml("assistant", summaryHtml, isHtml: true);
                        UpdateBrowser();
                    }

                    StatusLabel.Text = plan.IsCancelled
                        ? "⚠️ Agent 任务已取消"
                        : plan.ChangedFiles.Count > 0
                            ? $"✅ Agent 任务完成 ({plan.ChangedFiles.Count} 个文件变更)"
                            : $"✅ Agent 计划完成 ({plan.Steps.Count} 个步骤)";
                }
                else if (!agentResult.Success)
                {
                    var errorMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"❌ Agent 执行失败: {agentResult.ErrorMessage}",
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    lock (_lock) { _messages.Add(errorMsg); }
                    AddMessagesHtml("assistant", errorMsg.Content);
                    UpdateBrowser();
                    StatusLabel.Text = $"❌ Agent 错误: {agentResult.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AgentDispatcher] 工作流异常: {ex.Message}", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = $"❌ Agent 错误: {ex.Message}";
            }
            finally
            {
                _agentDispatcher.ActivePlan = null;
            }
        }

        /// <summary>
        /// AgentDispatcher 层面的 PlanUpdated 回调（Plan Agent 产出计划时 / EditAgent 进度更新时触发）。
        /// 首次触发时添加计划消息到 UI，后续触发时仅通过 JS 更新步骤进度，避免重复渲染相同内容的对话框。
        /// </summary>
        private void OnAgentDispatcherPlanUpdated(AgentTaskPlan plan)
        {
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;
                try
                {
                    // ── 检查计划是否已渲染到 DOM ──
                    string checkJs = "document.getElementById('agent-step-0') !== null";
                    string checkResult = await ChatWebView.CoreWebView2.ExecuteScriptAsync(checkJs);
                    bool planExists = checkResult == "true";

                    if (!planExists)
                    {
                        // 首次：添加完整计划 HTML 消息
                        string planHtml = ChatHtmlService.BuildAgentPlanHtml(plan);
                        var planMsg = new ChatMessage
                        {
                            Role = "assistant",
                            Content = planHtml,
                            Timestamp = DateTime.Now,
                            IsRendered = true,
                            IsHtml = true,
                        };
                        lock (_lock) { _messages.Add(planMsg); }
                        AddMessagesHtml("assistant", planHtml, isHtml: true);
                        UpdateBrowser();
                    }
                    else
                    {
                        // 后续更新：仅通过 JS 增量更新步骤状态，不添加新消息
                        string js = ChatHtmlService.BuildAgentProgressUpdateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                    }

                    StatusLabel.Text = $"🤖 Plan Agent: {plan.Steps.Count} 个步骤已规划";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AgentDispatcher] Plan UI 更新失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Agent 步骤状态变更回调：更新 WebView 中的步骤进度。
        /// </summary>
        private void OnAgentPlanUpdated(AgentTaskPlan plan)
        {
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string js = ChatHtmlService.BuildAgentProgressUpdateJs(plan);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                    StatusLabel.Text = $"🤖 Agent: 步骤 {plan.CurrentStepIndex}/{plan.Steps.Count}";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] UI 更新失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Agent 权限请求回调：在 WebView 中注入确认/拒绝按钮。
        /// </summary>
        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string js = ChatHtmlService.BuildPermissionRequestJs(request);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                    StatusLabel.Text = $"🔐 等待确认: {request.Title}";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] 权限 UI 注入失败: {ex.Message}");
                    // 超时自动拒绝
                    _agentDispatcher?.RespondToPermission(request.RequestId, false);
                }
            });
        }

        #endregion

        #region Retry / Edit / Version Navigation

        /// <summary>
        /// 重试某个助手消息：找到其对应的用户消息，重新发送请求。
        /// 旧助手消息保存到版本历史中，新回复覆盖当前位置。
        /// </summary>
        /// <param name="assistantMsgIndex">助手消息在 _messages 中的索引</param>
        private async Task RetryMessageAsync(int assistantMsgIndex)
        {
            lock (_lock)
            {
                if (_isGenerating) return;
                if (assistantMsgIndex < 0 || assistantMsgIndex >= _messages.Count) return;
                var assistantMsg = _messages[assistantMsgIndex];
                if (assistantMsg.Role != "assistant") return;
            }

            try
            {
                // ── 找到对应的用户消息（向前搜索最近的一条用户消息） ──
                int userMsgIndex = -1;
                ChatMessage? userMsg = null;
                lock (_lock)
                {
                    for (int i = assistantMsgIndex - 1; i >= 0; i--)
                    {
                        if (_messages[i].Role == "user")
                        {
                            userMsgIndex = i;
                            userMsg = _messages[i];
                            break;
                        }
                    }
                }

                if (userMsg == null) return;

                // ── 保存当前助手消息到版本历史 ──
                ChatMessage oldAssistantMsg;
                lock (_lock)
                {
                    oldAssistantMsg = _messages[assistantMsgIndex];

                    // 如果这是首次重试，初始化版本历史
                    if (!_assistantVersionHistory.ContainsKey(userMsgIndex))
                    {
                        _assistantVersionHistory[userMsgIndex] = new List<ChatMessage>();
                        _activeVersionIndex[userMsgIndex] = 0; // 当前活跃的是原版（版本0）
                    }

                    // 保存当前活跃版本到历史
                    var history = _assistantVersionHistory[userMsgIndex];
                    int activeIdx = _activeVersionIndex.TryGetValue(userMsgIndex, out var idx) ? idx : 0;

                    // 在历史列表中替换或追加当前版本
                    if (activeIdx < history.Count)
                        history[activeIdx] = oldAssistantMsg;
                    else
                        history.Add(oldAssistantMsg);

                    // ── 回退对话历史：移除当前助手消息及其后的所有消息 ──
                    // 从 _conversationHistory 中移除对应的 assistant 条目
                    RemoveConversationHistoryAfter(userMsgIndex);
                }

                // ── 重新发送用户消息 ──
                await ResendUserMessageAsync(userMsgIndex, userMsg);
            }
            catch (Exception ex)
            {
                Logger.Error($"RetryMessageAsync 异常: {ex.Message}", ex);
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
            }
        }

        /// <summary>
        /// 编辑某条用户消息后重新发送。
        /// 将旧消息回填到输入框，让用户修改后按 Enter 重新发送（避免弹窗卡死 UI）。
        /// </summary>
        /// <param name="userMsgIndex">用户消息在 _messages 中的索引</param>
        private async Task EditMessageAsync(int userMsgIndex)
        {
            lock (_lock)
            {
                if (_isGenerating) return;
                if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;
                var msg = _messages[userMsgIndex];
                if (msg.Role != "user") return;
            }

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // ── 将旧消息内容回填到输入框 ──
            string? originalContent = null;
            ChatMessage? userMsg = null;
            lock (_lock)
            {
                userMsg = _messages[userMsgIndex];
                originalContent = userMsg.OriginalContent
                    ?? userMsg.Content;
            }

            InputTextBox.Text = originalContent ?? string.Empty;
            InputTextBox.Focus();
            InputTextBox.SelectAll();

            // ── 恢复附加文件列表（从消息的 AttachedFiles 中提取文件路径）──
            _attachedFilePaths.Clear();
            if (userMsg != null && userMsg.AttachedFiles.Count > 0)
            {
                foreach (var file in userMsg.AttachedFiles)
                {
                    if (!string.IsNullOrEmpty(file.FilePath) && System.IO.File.Exists(file.FilePath))
                    {
                        _attachedFilePaths.Add(file.FilePath);
                    }
                }
            }
            // 如果 AttachedFiles 中没有路径信息，回退使用 AttachedFileNames
            if (_attachedFilePaths.Count == 0 && userMsg != null && userMsg.AttachedFileNames.Count > 0)
            {
                Logger.Info($"[Edit] AttachedFiles 路径不可用，已恢复 {userMsg.AttachedFileNames.Count} 个文件名到 UI（文件需重新上传)");
            }
            RefreshAttachedFilesUI();

            // ── 标记此消息将被编辑替换（下次 SendMessage 时识别）──
            _pendingEditMsgIndex = userMsgIndex;

            StatusLabel.Text = $"✏️ 编辑消息（按 Enter 发送，Esc 取消）";
        }

        /// <summary>
        /// 待编辑的用户消息索引，-1 表示无。
        /// SendMessage 中检查此值，如果 >= 0 则走编辑+重发流程。
        /// </summary>
        private int _pendingEditMsgIndex = -1;

        /// <summary>
        /// 处理编辑后重新发送：保存版本历史、回退上下文、重新发送。
        /// </summary>
        private async Task HandleEditResendAsync(int userMsgIndex, string newContent)
        {
            int editIndex = _pendingEditMsgIndex;
            _pendingEditMsgIndex = -1;

            lock (_lock) { _isGenerating = true; }
            UpdateButtonsState();
            InputTextBox.Text = string.Empty;
            StatusLabel.Text = "正在重新生成…";

            try
            {
                ChatMessage? userMsg = null;
                lock (_lock)
                {
                    if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;
                    userMsg = _messages[userMsgIndex];
                    if (userMsg.Role != "user") return;

                    // 查找对应的助手消息并保存到版本历史
                    int assistantMsgIndex = -1;
                    if (userMsgIndex + 1 < _messages.Count && _messages[userMsgIndex + 1].Role == "assistant")
                        assistantMsgIndex = userMsgIndex + 1;

                    if (assistantMsgIndex >= 0)
                    {
                        var oldAssistant = _messages[assistantMsgIndex];
                        if (!_assistantVersionHistory.ContainsKey(userMsgIndex))
                        {
                            _assistantVersionHistory[userMsgIndex] = new List<ChatMessage>();
                            _activeVersionIndex[userMsgIndex] = 0;
                        }
                        var history = _assistantVersionHistory[userMsgIndex];
                        int activeIdx = _activeVersionIndex.TryGetValue(userMsgIndex, out var aidx) ? aidx : 0;
                        if (activeIdx < history.Count)
                            history[activeIdx] = oldAssistant;
                        else
                            history.Add(oldAssistant);
                    }

                    if (string.IsNullOrEmpty(userMsg.OriginalContent))
                        userMsg.OriginalContent = userMsg.Content;

                    userMsg.Content = newContent;

                    // 回退对话历史
                    RemoveConversationHistoryAfter(userMsgIndex);
                    _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = newContent });
                }

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                if (userMsg != null)
                    await ResendUserMessageAsync(userMsgIndex, userMsg);
            }
            catch (Exception ex)
            {
                Logger.Error($"HandleEditResendAsync 异常: {ex.Message}", ex);
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                StatusLabel.Text = "就绪";
            }
        }

        /// <summary>
        /// 导航到某个助手消息的不同版本。
        /// </summary>
        /// <param name="assistantMsgIndex">助手消息在 _messages 中的索引</param>
        /// <param name="direction">-1 表示上一个版本，1 表示下一个版本</param>
        private async Task NavigateVersionAsync(int assistantMsgIndex, int direction)
        {
            try
            {
                // ── 找到对应的用户消息索引 ──
                int userMsgIndex = -1;
                lock (_lock)
                {
                    if (assistantMsgIndex < 0 || assistantMsgIndex >= _messages.Count) return;
                    if (_messages[assistantMsgIndex].Role != "assistant") return;

                    for (int i = assistantMsgIndex - 1; i >= 0; i--)
                    {
                        if (_messages[i].Role == "user")
                        {
                            userMsgIndex = i;
                            break;
                        }
                    }
                }

                if (userMsgIndex < 0) return;

                lock (_lock)
                {
                    if (!_assistantVersionHistory.TryGetValue(userMsgIndex, out var history) || history.Count == 0)
                        return;

                    int currentActive = _activeVersionIndex.TryGetValue(userMsgIndex, out var cidx) ? cidx : 0;
                    int newActive = currentActive + (direction > 0 ? 1 : -1);

                    if (newActive < 0) newActive = history.Count - 1;
                    if (newActive >= history.Count) newActive = 0;

                    if (newActive == currentActive) return; // 没变

                    // ── 保存当前显示的版本到历史 ──
                    var currentMsg = _messages[assistantMsgIndex];
                    if (currentActive < history.Count)
                        history[currentActive] = currentMsg;
                    else
                        history.Add(currentMsg);

                    // ── 替换为导航目标版本 ──
                    var targetVersion = history[newActive];
                    targetVersion.VersionIndex = newActive + 1;
                    targetVersion.TotalVersions = history.Count;
                    targetVersion.MessageGroupId = $"ver_{userMsgIndex}";

                    _messages[assistantMsgIndex] = targetVersion;
                    _activeVersionIndex[userMsgIndex] = newActive;
                }

                // ── 重新渲染 ──
                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Error($"NavigateVersionAsync 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从 _conversationHistory 中移除 userMsgIndex 对应的用户消息之后的所有条目。
        /// 用于回退对话上下文。通过查找最后一个 user 角色消息来定位。
        /// </summary>
        private void RemoveConversationHistoryAfter(int userMsgIndex)
        {
            lock (_lock)
            {
                if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;

                var userMsg = _messages[userMsgIndex];
                if (userMsg.Role != "user") return;

                // 在 _conversationHistory 中找到该用户消息对应的条目
                // 策略：从末尾向前搜索，找到的用户消息条目就是最近添加的，
                // 它应当对应 _messages[userMsgIndex]
                int historyIdx = -1;

                // 首先尝试：从末尾向前找第一个 user 角色的消息
                for (int i = _conversationHistory.Count - 1; i >= 0; i--)
                {
                    if (_conversationHistory[i].Role == "user")
                    {
                        // 验证内容是否匹配（使用 Contains 宽松匹配，因为 history 可能包含文件上下文）
                        string historyContent = _conversationHistory[i].Content ?? string.Empty;
                        string searchContent = userMsg.OriginalContent ?? userMsg.Content ?? string.Empty;

                        if (historyContent.Contains(searchContent) ||
                            searchContent.Contains(historyContent) ||
                            string.Equals(historyContent.Trim(), searchContent.Trim(), StringComparison.Ordinal))
                        {
                            historyIdx = i;
                            break;
                        }
                    }
                }

                // 回退策略：如果内容不匹配，使用最后一个 user 消息
                if (historyIdx < 0)
                {
                    for (int i = _conversationHistory.Count - 1; i >= 0; i--)
                    {
                        if (_conversationHistory[i].Role == "user")
                        {
                            historyIdx = i;
                            Logger.Warn($"[Retry/Edit] 内容匹配失败，使用最后一个 user 消息 (索引 {i}) 作为回退");
                            break;
                        }
                    }
                }

                if (historyIdx >= 0)
                {
                    int removeCount = _conversationHistory.Count - historyIdx;
                    _conversationHistory.RemoveRange(historyIdx, removeCount);
                    Logger.Info($"[Retry/Edit] 已从对话历史移除 {removeCount} 条消息 (从索引 {historyIdx})");
                }
            }
        }

        /// <summary>
        /// 重新发送用户消息的核心逻辑。
        /// 创建新的助手消息占位并调用 API。
        /// </summary>
        private async Task ResendUserMessageAsync(int userMsgIndex, ChatMessage userMsg)
        {
            // ── 确保 API 服务可用 ──
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
                StatusLabel.Text = "⚠️ 请先配置 API 密钥";
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

            lock (_lock)
            {
                _isGenerating = true;
            }

            UpdateButtonsState();

            // ── 截断消息列表：移除用户消息之后的所有消息 ──
            lock (_lock)
            {
                int removeFrom = userMsgIndex + 1;
                if (removeFrom < _messages.Count)
                {
                    int removedCount = _messages.Count - removeFrom;
                    _messages.RemoveRange(removeFrom, removedCount);
                    Logger.Info($"[Retry/Edit] 已从消息列表移除 {removedCount} 条后续消息 (从索引 {removeFrom})");
                }
            }

            // ── 创建新的助手消息占位 ──
            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ReasoningContent = string.Empty,
                Timestamp = DateTime.Now,
                IsStreaming = true,
                IsRendered = false,
            };
            int newAssistantIdx;
            lock (_lock)
            {
                _messages.Add(assistantMsg);
                newAssistantIdx = _messages.Count - 1;
            }

            // ── 重建 HTML 并刷新 ──
            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            // ── 设置流式取消令牌 ──
            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

            try
            {
                // ── 确保用户消息在对话历史中（如果已被 RemoveConversationHistoryAfter 移除则重新添加） ──
                lock (_lock)
                {
                    bool userExistsInHistory = false;
                    string searchContent = userMsg.Content ?? string.Empty;
                    foreach (var m in _conversationHistory)
                    {
                        if (m.Role == "user" &&
                            ((m.Content?.Contains(searchContent) == true) ||
                             (searchContent.Contains(m.Content ?? string.Empty)) ||
                             string.Equals((m.Content ?? string.Empty).Trim(), searchContent.Trim(), StringComparison.Ordinal)))
                        {
                            userExistsInHistory = true;
                            break;
                        }
                    }

                    if (!userExistsInHistory)
                    {
                        string fullUserContent = searchContent;
                        if (userMsg.AttachedFiles.Count > 0)
                        {
                            string fileContext = FileParserService.FormatParseResultsForContext(userMsg.AttachedFiles);
                            if (!string.IsNullOrEmpty(fileContext))
                                fullUserContent = fileContext + "\n" + fullUserContent;
                        }
                        _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = fullUserContent });
                        Logger.Info("[Retry] 已将用户消息重新加入对话历史");
                    }
                }

                StatusLabel.Text = "DeepSeek 思考中…";

                // ── 多 Agent 路由：重试时也重新判别意图 ──
                string userContent = userMsg.Content ?? string.Empty;
                if (_agentDispatcher != null && !string.IsNullOrEmpty(userContent) && !userContent.StartsWith("/"))
                {
                    var routing = await _agentDispatcher.RouteAsync(userContent);
                    bool needsAgent = routing.TargetAgent == AgentType.Plan
                        || routing.TargetAgent == AgentType.Edit
                        || routing.NeedsPlanning;

                    if (needsAgent)
                    {
                        Logger.Info($"[Retry] 重新路由到 Agent: {routing.TargetAgent}");
                        // 构建文件上下文（从附加文件恢复）
                        string fileContext = string.Empty;
                        if (userMsg.AttachedFiles.Count > 0)
                            fileContext = FileParserService.FormatParseResultsForContext(userMsg.AttachedFiles);
                        await RunAgentWorkflowAsync(userContent, fileContext, routing);
                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        StatusLabel.Text = "就绪";
                        return;
                    }
                }

                var requestMessages = BuildRequestMessages();
                var apiService = _apiService!;

                var reasoningBuffer = new System.Text.StringBuilder();
                var contentBuffer = new System.Text.StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, null, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = "DeepSeek 深度思考中…";

                        if (reasoningBuffer.Length - lastReasoningLength >= 80)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            await UpdateStreamingMessageAsync(newAssistantIdx,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
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
                        StatusLabel.Text = "DeepSeek 回复中...";

                        if (streamRenderTick >= StreamRenderInterval)
                        {
                            streamRenderTick = 0;
                            assistantMsg.Content = contentBuffer.ToString();
                            await UpdateStreamingMessageAsync(newAssistantIdx,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                }

                // ── 流式完成 ──
                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Retry] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    newAssistantIdx,
                    contentBuffer.ToString(),
                    reasoningBuffer.ToString());
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

                _conversationHistory.Add(new ChatApiMessage { Role = "assistant", Content = contentBuffer.ToString() });

                // ── 更新版本信息：将新回复也加入版本历史 ──
                lock (_lock)
                {
                    if (_assistantVersionHistory.TryGetValue(userMsgIndex, out var history))
                    {
                        // 将新生成的回复加入历史
                        history.Add(assistantMsg);
                        assistantMsg.VersionIndex = history.Count;
                        assistantMsg.TotalVersions = history.Count;
                        assistantMsg.MessageGroupId = $"ver_{userMsgIndex}";
                        _activeVersionIndex[userMsgIndex] = history.Count - 1; // 指向最新版本
                        Logger.Info($"[Retry] 版本历史: 共 {history.Count} 个版本, 当前版本 {assistantMsg.VersionIndex}");
                    }
                }

                // ── 在消息下面显示版本导航 ──
                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                // 后台持久化
                var capturedMsg = assistantMsg;
                _ = Task.Run(() =>
                {
                    capturedMsg.HtmlContent = "rendered";
                    capturedMsg.IsRendered = true;
                    SaveCurrentSession();
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[Retry] 用户停止生成");
                assistantMsg.Content += "\n\n*[已停止]*";
                assistantMsg.IsStreaming = false;
                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    newAssistantIdx,
                    assistantMsg.Content,
                    assistantMsg.ReasoningContent);
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Retry] API 出错", ex);
                assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(newAssistantIdx,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            finally
            {
                assistantMsg.IsStreaming = false;
                lock (_lock)
                {
                    _isGenerating = false;
                }
                StatusLabel.Text = string.Empty;
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
            }
        }

        #endregion
    }

    /// <summary>
    /// 流式工具调用增量累积器。
    /// 用于将 DeepSeek 流式返回的 tool_calls 增量片段合并为完整的工具调用。
    /// </summary>
    internal class ToolCallAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string? Type { get; set; }
        public string? FunctionName { get; set; }
        public StringBuilder ArgumentsBuilder { get; } = new StringBuilder();
    }
}
