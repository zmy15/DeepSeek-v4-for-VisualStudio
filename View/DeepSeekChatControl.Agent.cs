using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Agent 工作流 + 重试/编辑/版本导航。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Coding Agent Workflow

        /// <summary>
        /// 获取最近几轮对话文本作为 Discover 的附加上下文，帮助 ExploreAgent 生成更精准的关键词。
        /// </summary>
        [RagSource("conversation-history", "获取对话历史作为 Discover 附加上下文")]
        private string GetConversationContextForDiscovery()
        {
            try
            {
                var history = _contextManager?.GetConversationHistory();
                if (history == null || history.Count == 0)
                    return string.Empty;

                var recentTurns = new List<string>();
                int turnCount = 0;
                for (int i = history.Count - 1; i >= 0 && turnCount < 3; i--)
                {
                    var entry = history[i];
                    if (entry.Role == "user" || entry.Role == "assistant")
                    {
                        string text = entry.Content ?? string.Empty;
                        // RAG-MARK: no-truncate — 不再截断对话上下文，完整传递给 ExploreAgent
                        recentTurns.Insert(0, $"[{entry.Role}]: {text}");
                        turnCount++;
                    }
                }
                return recentTurns.Count > 0
                    ? "近期对话:\n" + string.Join("\n", recentTurns)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 构建重试/编辑时的富上下文内容。
        /// </summary>
        private string BuildRetryEnrichedContent(ChatMessage userMsg, string currentContent)
        {
            var sb = new StringBuilder();

            string conversationCtx = GetConversationContextForRetry();
            if (!string.IsNullOrEmpty(conversationCtx))
            {
                sb.AppendLine("【对话历史】");
                sb.AppendLine(conversationCtx);
                sb.AppendLine();
            }

            sb.AppendLine("【当前修改后的问题】");
            sb.AppendLine(currentContent);

            return sb.ToString();
        }

        /// <summary>
        /// 获取用于重试/编辑的对话历史上下文摘要（比 Discovery 版本更详细）。
        /// </summary>
        [RagSource("conversation-history", "获取对话历史作为重试/编辑上下文")]
        private string GetConversationContextForRetry()
        {
            try
            {
                var history = _contextManager?.GetConversationHistory();
                if (history == null || history.Count == 0)
                    return string.Empty;

                var recentTurns = new List<string>();
                int turnCount = 0;
                for (int i = history.Count - 1; i >= 0 && turnCount < 5; i--)
                {
                    var entry = history[i];
                    if (entry.Role == "user" || entry.Role == "assistant")
                    {
                        string text = entry.Content ?? string.Empty;
                        string prefix = entry.Role == "user" ? "用户" : "AI";
                        // RAG-MARK: no-truncate — 不再截断对话上下文，完整传递给重试流程
                        recentTurns.Insert(0, $"[{prefix}]: {text}");
                        turnCount++;
                    }
                }
                return recentTurns.Count > 0
                    ? string.Join("\n\n", recentTurns)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Agent 工作流主入口：分解任务 → 显示步骤计划 → 逐步执行 → 显示变更摘要。
        /// 注意：此方法在后台线程中调用，访问 UI 前必须切换到主线程。
        /// </summary>
        private async Task RunAgentWorkflowAsync(string userText, string fileContext = "",
            AgentRoutingResult? routing = null)
        {
            if (_agentDispatcher == null) return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = LocalizationService.Instance["agent.status.analyzing"];

                // ── 清理上一轮 Agent 执行的追踪状态 ──
                lock (_lock)
                {
                    _createdPlanIds.Clear();
                    _pendingLogEntries.Clear();
                    _agentThinkingContent.Clear();
                }
                _agentStreamingMsgIndex = -1;
                _lastReportedStepIndex = 0;
                _lastReportedStepStatus = string.Empty;
                _pendingHandoff = null;

                var context = new AgentContext
                {
                    SolutionPath = _solutionPath,
                    FileContext = fileContext,
                    ConversationHistory = _contextManager.GetConversationHistory(),
                    ContextManager = _contextManager,
                    IsPlanningMode = routing?.NeedsPlanning == true || routing?.TargetAgent == AgentType.Plan,
                    CancellationToken = GetStreamingToken(),
                    ReadFileAsync = async (path) =>
                    {
                        // RAG-SOURCE: file-read Agent 读取文件内容（Ask/Edit/Explore/Plan Agent 上下文）
                        if (File.Exists(path))
                            return await Task.Run(() => File.ReadAllText(path));
                        return null;
                    },
                };

                // ── 记录 Token 用量日志 ──
                var stats = _contextManager.GetStats();
                Logger.Info($"[TokenUsage] 当前对话 Token: {stats.EstimatedTokens:N0}/{stats.TokenBudget:N0} ({stats.UsagePercent:F1}%) | 轮次: {stats.TurnCount} | 消息: {stats.MessageCount}");

                await TaskScheduler.Default;

                // ── 创建实时思考气泡（AI 回答流式输出）──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _agentThinkingContent.Clear();

                // ── 检查是否已有 retry fork 占位，有则复用，避免产生多余气泡 ──
                bool reusedPlaceholder = false;
                lock (_lock)
                {
                    if (_tree != null)
                    {
                        var treePath = _tree.GetActivePath();
                        var lastNode = treePath.Count > 0 ? treePath[treePath.Count - 1] : null;
                        if (lastNode?.Message != null
                            && lastNode.Message.Role == "assistant"
                            && lastNode.Message.IsStreaming
                            && string.IsNullOrEmpty(lastNode.Message.Content))
                        {
                            // 复用占位，更新内容为 Agent 分析状态
                            lastNode.Message.Content = LocalizationService.Instance["agent.status.analyzing"];
                            _agentStreamingMsgIndex = _messages.Count - 1;
                            reusedPlaceholder = true;
                            Logger.Info($"[Agent] 复用 retry fork 占位 (idx={_agentStreamingMsgIndex})");
                        }
                    }
                }

                if (!reusedPlaceholder)
                {
                    var thinkingMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = LocalizationService.Instance["agent.status.analyzing"],
                        ReasoningContent = string.Empty,
                        Timestamp = DateTime.Now,
                        IsStreaming = true,
                        IsRendered = false,
                        AgentType = routing?.TargetAgent ?? _agentDispatcher.ActiveAgentType,
                    };
                    lock (_lock)
                    {
                        _messages.Add(thinkingMsg);
                        _agentStreamingMsgIndex = _messages.Count - 1;
                    }
                    AddMessagesHtml("assistant", thinkingMsg.Content);
                }
                else
                {
                    AddMessagesHtml("assistant", LocalizationService.Instance["agent.status.analyzing"]);
                }
                _currentStreamingMsgIndex = _agentStreamingMsgIndex;
                UpdateBrowser();
                await TaskScheduler.Default;

                var editAgent = _agentDispatcher.EditAgent;
                editAgent.PlanUpdated += OnAgentPlanUpdated;
                _agentDispatcher.PlanUpdated += OnAgentDispatcherPlanUpdated;
                _agentDispatcher.LogEntryAdded += OnAgentLogEntryAdded;
                _agentDispatcher.FileChangeNotified += OnAgentFileChangeNotified;

                AgentResult agentResult;
                try
                {
                    agentResult = await _agentDispatcher.ExecuteAsync(userText, context, routing);
                }
                finally
                {
                    editAgent.PlanUpdated -= OnAgentPlanUpdated;
                    _agentDispatcher.PlanUpdated -= OnAgentDispatcherPlanUpdated;
                    _agentDispatcher.LogEntryAdded -= OnAgentLogEntryAdded;
                    _agentDispatcher.FileChangeNotified -= OnAgentFileChangeNotified;
                }

                // 仅在 AutoSend 为 true 时自动执行 Handoff；否则由用户通过 UI 按钮显式触发
                if (agentResult.Handoff != null && agentResult.Handoff.AutoSend)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentSwitched"], agentResult.Handoff.TargetAgent);

                    await TaskScheduler.Default;

                    editAgent.PlanUpdated += OnAgentPlanUpdated;
                    _agentDispatcher.PlanUpdated += OnAgentDispatcherPlanUpdated;
                    _agentDispatcher.LogEntryAdded += OnAgentLogEntryAdded;
                    _agentDispatcher.FileChangeNotified += OnAgentFileChangeNotified;
                    try
                    {
                        agentResult = await _agentDispatcher.ExecuteHandoffAsync(agentResult.Handoff, context);
                    }
                    finally
                    {
                        editAgent.PlanUpdated -= OnAgentPlanUpdated;
                        _agentDispatcher.PlanUpdated -= OnAgentDispatcherPlanUpdated;
                        _agentDispatcher.LogEntryAdded -= OnAgentLogEntryAdded;
                        _agentDispatcher.FileChangeNotified -= OnAgentFileChangeNotified;
                    }
                }
                else if (agentResult.Handoff != null && agentResult.Handoff.ShowContinueOn)
                {
                    // ── 非自动 Handoff：保存引用，稍后在渲染完成后注入"开始实现"按钮 ──
                    _pendingHandoff = agentResult.Handoff;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (agentResult.Success && agentResult.Plan != null)
                {
                    var plan = agentResult.Plan;

                    // ── 更新任务面板为完成状态 ──
                    if (plan.Steps.Count > 0)
                    {
                        try
                        {
                            string completeJs = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(completeJs);
                        }
                        catch { }
                    }

                    // ── 构建最终摘要并更新思考气泡为完成状态 ──
                    var summaryBuilder = new StringBuilder();

                    if (!string.IsNullOrWhiteSpace(agentResult.Content))
                    {
                        summaryBuilder.Append(agentResult.Content);
                    }
                    else
                    {
                        int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                        int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                        summaryBuilder.AppendLine(plan.IsCancelled
                            ? LocalizationService.Instance["agent.result.taskCancelled"]
                            : failed > 0
                                ? string.Format(LocalizationService.Instance["agent.result.taskCompletedPartial"], completed, plan.Steps.Count, failed)
                                : string.Format(LocalizationService.Instance["agent.result.taskCompletedSuccess"], completed, plan.Steps.Count));
                        summaryBuilder.AppendLine();

                        if (plan.ChangedFiles.Count > 0)
                        {
                            summaryBuilder.AppendLine($"**{LocalizationService.Instance["agent.panel.fileChanges"]}**: {plan.ChangedFiles.Count} 个文件");
                            foreach (var f in plan.ChangedFiles)
                            {
                                string fname = System.IO.Path.GetFileName(f.FilePath);
                                summaryBuilder.AppendLine($"- `{fname}` (+{f.LinesAdded} -{f.LinesRemoved})");
                            }
                            summaryBuilder.AppendLine();
                        }
                    }

                    // 追加思考过程到摘要后面 —— 作为独立 HTML 注入，默认展开显示
                    string thinkingText;
                    lock (_lock) { thinkingText = _agentThinkingContent.ToString(); }
                    string thinkingDetailsHtml = string.Empty;
                    if (!string.IsNullOrWhiteSpace(thinkingText))
                    {
                        // 将思考内容渲染为纯文本 HTML（保留换行）
                        string escapedThinking = System.Net.WebUtility.HtmlEncode(thinkingText)
                            .Replace("\n", "<br>");
                        thinkingDetailsHtml =
                            "<details class='reasoning-panel' style='margin-top:12px' open='true'>" +
                            "<summary>" + LocalizationService.Instance["agent.panel.executionProcess"] + "</summary>" +
                            "<div class='reasoning-content'>" + escapedThinking + "</div>" +
                            "</details>";
                    }

                    string finalContent = summaryBuilder.ToString().TrimEnd();

                    // ── 更新现有的流式思考气泡为最终内容 ──
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = finalContent;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                            // ── 持久化任务计划 JSON，重启后可重建任务面板 ──
                            try { msg.PlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            // ── 持久化 Handoff JSON，会话切换后可重建"开始执行"按钮 ──
                            if (_pendingHandoff != null)
                            {
                                try { msg.HandoffJson = JsonSerializer.Serialize(_pendingHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            }
                        }
                    }
                    // ── 追加 Cache 命中率统计（使用累计值，非单轮）──
                    string cacheFooter = string.Empty;
                    try
                    {
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                    }
                    catch { }

                    // ── 同步最终内容并强制刷新，确保增量内容已推送 ──
                    BatchStreamingUpdate(_agentStreamingMsgIndex, finalContent, string.Empty, isComplete: true);

                    // ── 使用非阻塞 PostWebMessageAsString 发送最终渲染（含 Markdown HTML + 执行过程）──
                    string combinedFooter = thinkingDetailsHtml + cacheFooter;
                    PostStreamEnd(_agentStreamingMsgIndex, finalContent, string.Empty, combinedFooter);

                    StatusLabel.Text = plan.IsCancelled
                        ? "⚠️ Agent 任务已取消"
                        : plan.ChangedFiles.Count > 0
                            ? $"✅ Agent 任务完成 ({plan.ChangedFiles.Count} 个文件变更)"
                            : $"✅ Agent 计划完成 ({plan.Steps.Count} 个步骤)";

                    // ── 如果有待处理的 Handoff（如 Plan→Edit），注入"开始实现"按钮 ──
                    if (_pendingHandoff != null && _agentStreamingMsgIndex >= 0)
                    {
                        try
                        {
                            string targetAgentStr = _pendingHandoff.TargetAgent.ToString();
                            string handoffBtnJs = ChatHtmlService.BuildHandoffButtonJs(
                                _agentStreamingMsgIndex, targetAgentStr, _pendingHandoff.Label);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(handoffBtnJs);
                        }
                        catch { }
                    }

                    if (plan.ChangedFiles.Count > 0)
                    {
                        _pendingAgentFileChanges = new List<FileChangeSummary>(plan.ChangedFiles);
                    }
                }
                else if (agentResult.Success && !string.IsNullOrWhiteSpace(agentResult.Content))
                {
                    // 将思考气泡更新为最终内容
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = agentResult.Content;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                            // ── 持久化 Handoff JSON，会话切换后可重建"开始执行"按钮 ──
                            if (_pendingHandoff != null)
                            {
                                try { msg.HandoffJson = JsonSerializer.Serialize(_pendingHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            }
                        }
                    }
                    // ── 计算 Cache 命中率 ──
                    string cacheFooter = string.Empty;
                    try
                    {
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                    }
                    catch { }

                    // ── 同步最终内容并强制刷新，确保增量内容已推送 ──
                    BatchStreamingUpdate(_agentStreamingMsgIndex, agentResult.Content, string.Empty, isComplete: true);

                    // ── 使用非阻塞 PostWebMessageAsString 发送最终渲染 ──
                    PostStreamEnd(_agentStreamingMsgIndex, agentResult.Content, string.Empty, cacheFooter);
                    StatusLabel.Text = LocalizationService.Instance["status.ready"];

                    // ── 如果有待处理的 Handoff，注入按钮 ──
                    if (_pendingHandoff != null && _agentStreamingMsgIndex >= 0)
                    {
                        try
                        {
                            string targetAgentStr = _pendingHandoff.TargetAgent.ToString();
                            string handoffBtnJs = ChatHtmlService.BuildHandoffButtonJs(
                                _agentStreamingMsgIndex, targetAgentStr, _pendingHandoff.Label);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(handoffBtnJs);
                        }
                        catch { }
                    }
                }
                else if (!agentResult.Success)
                {
                    string errorContent = $"❌ Agent 执行失败: {agentResult.ErrorMessage}";
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = errorContent;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                        }
                    }
                    // ── 计算 Cache 命中率 ──
                    string cacheFooter = string.Empty;
                    try
                    {
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                    }
                    catch { }

                    // ── 同步最终内容并强制刷新，确保增量内容已推送 ──
                    BatchStreamingUpdate(_agentStreamingMsgIndex, errorContent, string.Empty, isComplete: true);

                    // ── 使用非阻塞 PostWebMessageAsString 发送最终渲染 ──
                    PostStreamEnd(_agentStreamingMsgIndex, errorContent, string.Empty, cacheFooter);
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentError"], agentResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AgentDispatcher] 工作流异常: {ex.Message}", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentError"], ex.Message);
            }
            finally
            {
                _agentDispatcher.ActivePlan = null;
            }

            // ── 将 Agent 响应同步到树和上下文管理器（修复上下文丢失问题）──
            await SyncAgentResponseToTreeAndContextAsync();

            // ── AI 自动生成会话标题（Agent 工作流完成后触发）──
            if (_pendingAiTitle && !string.IsNullOrWhiteSpace(_firstUserMessageForTitle))
            {
                var capturedFirstUserMsg = _firstUserMessageForTitle;
                // 从 Agent 响应消息中获取第一条助手回复作为标题生成的上下文
                string firstAssistantReply = string.Empty;
                lock (_lock)
                {
                    if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                    {
                        firstAssistantReply = _messages[_agentStreamingMsgIndex].Content ?? string.Empty;
                    }
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await GenerateAiTitleAsync(capturedFirstUserMsg, firstAssistantReply);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[AI标题] Agent 工作流标题生成异常: {ex.Message}");
                    }
                });
            }

            // ── 记录 Cache 命中率 ──
            LogCacheHitRate();
        }

        /// <summary>
        /// 将 Agent 的最终响应消息同步到对话树和上下文管理器。
        /// 修复 Agent 执行后上下文丢失的问题：此前 Agent 响应仅写入 _messages 列表，
        /// 导致树结构缺少该节点、apiHistory 不完整，重新加载会话后上下文断裂。
        /// </summary>
        private async Task SyncAgentResponseToTreeAndContextAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                lock (_lock)
                {
                    if (_agentStreamingMsgIndex < 0 || _agentStreamingMsgIndex >= _messages.Count)
                    {
                        Logger.Warn($"[Agent→Tree] 无效的 _agentStreamingMsgIndex={_agentStreamingMsgIndex}, _messages.Count={_messages.Count}");
                        return;
                    }

                    var agentResponseMsg = _messages[_agentStreamingMsgIndex];

                    // 确保消息状态正确
                    agentResponseMsg.IsStreaming = false;
                    agentResponseMsg.IsRendered = true;

                    // ── 添加到对话树（如果尚未在树中）──
                    if (_tree != null && string.IsNullOrEmpty(agentResponseMsg.NodeId))
                    {
                        _tree.AddChildMessage(agentResponseMsg);
                        Logger.Info($"[Agent→Tree] Agent 响应已添加到树 (nodeId={agentResponseMsg.NodeId}, role={agentResponseMsg.Role})");
                    }
                    else if (_tree == null)
                    {
                        Logger.Warn("[Agent→Tree] _tree 为 null，无法将 Agent 响应添加到树");
                    }
                    else if (!string.IsNullOrEmpty(agentResponseMsg.NodeId))
                    {
                        Logger.Info($"[Agent→Tree] Agent 响应已在树中 (nodeId={agentResponseMsg.NodeId})，跳过重复添加");
                    }

                    // ── 添加到上下文管理器（供后续 API 调用使用）──
                    string content = agentResponseMsg.Content ?? string.Empty;
                    string reasoning = agentResponseMsg.ReasoningContent;
                    if (!string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(reasoning))
                    {
                        _contextManager.AddAssistantMessage(
                            string.IsNullOrWhiteSpace(content) ? null : content,
                            string.IsNullOrWhiteSpace(reasoning) ? null : reasoning);
                        Logger.Info($"[Agent→Context] Agent 响应已添加到上下文 (contentLen={content.Length}, reasoningLen={reasoning?.Length ?? 0})");
                    }
                }

                // ── 持久化保存 ──
                SaveCurrentSession();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Agent] 同步响应到树/上下文失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// AgentDispatcher 层面的 PlanUpdated 回调。
        /// 仅对 Plan Agent 产出的计划创建/更新底部任务流程面板。
        /// Edit Agent 内部的单步计划（IsFromPlanAgent=false）不创建面板。
        /// </summary>
        private void OnAgentDispatcherPlanUpdated(AgentTaskPlan plan)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;
                try
                {
                    // ── 仅处理 Plan Agent 产出的计划 ──
                    if (!plan.IsFromPlanAgent) return;

                    string pid = plan.PlanId;

                    // ── C# 层面防重：已创建过面板的，只做进度更新 ──
                    bool alreadyCreated;
                    lock (_lock) { alreadyCreated = _createdPlanIds.Contains(pid); }

                    if (!alreadyCreated)
                    {
                        lock (_lock) { _createdPlanIds.Add(pid); }
                        // 创建底部任务面板
                        string createJs = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(createJs);

                        // ── 输出规划信息到思考气泡（摘要即可，步骤详情由任务面板展示）──
                        AppendAgentThinking($"📋 **规划完成**: {plan.Title}，共 {plan.Steps.Count} 个步骤");
                    }
                    else
                    {
                        // 更新任务面板进度
                        string updateJs = ChatHtmlService.BuildAgentTaskPanelUpdateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(updateJs);
                    }

                    StatusLabel.Text = string.Format(LocalizationService.Instance["agent.status.planStepsPlanned"], plan.Steps.Count);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AgentDispatcher] Plan UI 更新失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Agent 步骤状态变更回调：更新 WebView 中的步骤进度。
        /// 仅更新已存在的计划面板（由 OnAgentDispatcherPlanUpdated 创建）。
        /// 无 Plan 路由的独立 Edit 不创建下方面板，只输出步骤状态到思考气泡。
        /// </summary>
        private void OnAgentPlanUpdated(AgentTaskPlan plan)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string pid = plan.PlanId;

                    // ── 仅更新 Plan Agent 产出的计划面板（由 OnAgentDispatcherPlanUpdated 创建）──
                    // 独立 Edit（无 Plan 路由）不创建/更新下方面板
                    if (plan.IsFromPlanAgent)
                    {
                        bool alreadyCreated;
                        lock (_lock) { alreadyCreated = _createdPlanIds.Contains(pid); }

                        if (alreadyCreated)
                        {
                            // 更新任务面板进度
                            string updateJs = ChatHtmlService.BuildAgentTaskPanelUpdateJs(plan);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(updateJs);
                        }
                    }

                    // ── 输出步骤状态变更到思考气泡 ──
                    if (plan.CurrentStepIndex > 0 && plan.CurrentStepIndex <= plan.Steps.Count)
                    {
                        var step = plan.Steps[plan.CurrentStepIndex - 1];
                        string statusKey = $"{step.Index}:{step.Status}";
                        if (step.Index != _lastReportedStepIndex || statusKey != _lastReportedStepStatus)
                        {
                            _lastReportedStepIndex = step.Index;
                            _lastReportedStepStatus = statusKey;
                            if (step.Status == AgentStepStatus.Completed)
                                AppendAgentThinking($"✅ 步骤 {step.Index} 完成: {step.Title}");
                            else if (step.Status == AgentStepStatus.Failed)
                                AppendAgentThinking($"❌ 步骤 {step.Index} 失败: {step.ResultSummary ?? step.Title}");
                            else if (step.Status == AgentStepStatus.InProgress)
                                AppendAgentThinking($"🔄 步骤 {step.Index}: {step.Title}");
                        }
                    }

                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentStepProgress"], plan.CurrentStepIndex, plan.Steps.Count);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] UI 更新失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 从活跃会话恢复累计 Cache 统计到 ApiService。
        /// 重启 VS 后保留之前的命中率数据。
        /// </summary>
        private void RestoreCacheStatsFromSession()
        {
            if (_apiService == null || _activeSession == null) return;

            try
            {
                _apiService.RestoreAccumulatedStats(
                    _activeSession.CumulativeCacheHitTokens,
                    _activeSession.CumulativeCacheMissTokens,
                    _activeSession.CumulativePromptTokens,
                    _activeSession.CumulativeCompletionTokens);
                Logger.Info($"[Cache] 从会话恢复累计统计: hit={_activeSession.CumulativeCacheHitTokens}, miss={_activeSession.CumulativeCacheMissTokens}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 恢复统计失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重启后遍历消息中的持久化计划 JSON，重建任务面板。
        /// 已完成的计划渲染为完成状态面板。
        /// 同时重建待处理的 Handoff 按钮（如 Plan→Edit 的"开始执行"按钮）。
        /// </summary>
        private async Task RebuildPersistedTaskPanelsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

                // ── 从锁中收集需要重建的计划（async 操作不能在锁内执行）──
                var plansToRebuild = new List<(AgentTaskPlan Plan, bool NeedsTwoPass)>();
                var handoffsToRebuild = new List<(int MessageIndex, AgentHandoff Handoff)>();

                lock (_lock)
                {
                    for (int i = 0; i < _messages.Count; i++)
                    {
                        var msg = _messages[i];

                        // ── 重建任务面板 ──
                        if (!string.IsNullOrEmpty(msg.PlanJson))
                        {
                            try
                            {
                                var plan = JsonSerializer.Deserialize<AgentTaskPlan>(msg.PlanJson, opts);
                                if (plan != null && plan.IsFromPlanAgent && plan.Steps.Count > 0)
                                {
                                    string pid = plan.PlanId;
                                    if (!_createdPlanIds.Contains(pid))
                                    {
                                        _createdPlanIds.Add(pid);

                                        bool needsTwoPass = !plan.IsCompleted
                                            && !plan.IsCancelled
                                            && !plan.Steps.All(s => s.Status == AgentStepStatus.Completed || s.Status == AgentStepStatus.Skipped)
                                            && plan.CurrentStepIndex > 0;

                                        plansToRebuild.Add((plan, needsTwoPass));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[Panel] 反序列化 PlanJson 失败: {ex.Message}");
                            }
                        }

                        // ── 重建 Handoff 按钮 ──
                        if (!string.IsNullOrEmpty(msg.HandoffJson))
                        {
                            try
                            {
                                var handoff = JsonSerializer.Deserialize<AgentHandoff>(msg.HandoffJson, opts);
                                if (handoff != null && handoff.ShowContinueOn)
                                {
                                    handoffsToRebuild.Add((i, handoff));
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[Panel] 反序列化 HandoffJson 失败: {ex.Message}");
                            }
                        }
                    }
                }

                // ── 在锁外执行 JS 注入：任务面板 ──
                foreach (var (plan, needsTwoPass) in plansToRebuild)
                {
                    string pid = plan.PlanId;
                    string js;
                    if (plan.IsCompleted || plan.Steps.All(s => s.Status == AgentStepStatus.Completed || s.Status == AgentStepStatus.Skipped))
                    {
                        js = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                    }
                    else if (plan.IsCancelled)
                    {
                        js = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                    }
                    else
                    {
                        js = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);
                        if (needsTwoPass)
                        {
                            _ = ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                            await Task.Delay(100);
                            js = ChatHtmlService.BuildAgentTaskPanelUpdateJs(plan);
                        }
                    }

                    _ = ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                    Logger.Info($"[Panel] 重建持久化任务面板: {plan.Title} (PlanId={pid}, Steps={plan.Steps.Count})");
                }

                // ── 在锁外执行 JS 注入：Handoff 按钮 ──
                foreach (var (msgIndex, handoff) in handoffsToRebuild)
                {
                    try
                    {
                        string targetAgentStr = handoff.TargetAgent.ToString();
                        string handoffBtnJs = ChatHtmlService.BuildHandoffButtonJs(
                            msgIndex, targetAgentStr, handoff.Label);
                        _ = ChatWebView.CoreWebView2.ExecuteScriptAsync(handoffBtnJs);
                        Logger.Info($"[Panel] 重建 Handoff 按钮: msgIdx={msgIndex}, target={targetAgentStr}, label={handoff.Label}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Panel] 重建 Handoff 按钮失败: {ex.Message}");
                    }
                }

                // ── 恢复 _pendingHandoff，使重启后点击按钮不会因 null 而直接返回 ──
                if (handoffsToRebuild.Count > 0)
                {
                    _pendingHandoff = handoffsToRebuild[handoffsToRebuild.Count - 1].Handoff;
                    Logger.Info($"[Panel] 恢复 _pendingHandoff: target={_pendingHandoff.TargetAgent}, label={_pendingHandoff.Label}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Panel] 重建持久化任务面板异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 等待 WebView 页面就绪后重建持久化任务面板（最多等待 10 秒）。
        /// </summary>
        private async Task RebuildPanelsWhenPageReadyAsync()
        {
            try
            {
                // 等待 _pageReady 标志（最多 10 秒）
                for (int i = 0; i < 100; i++)
                {
                    if (_pageReady) break;
                    await Task.Delay(100);
                }
                if (_pageReady)
                {
                    await RebuildPersistedTaskPanelsAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Panel] RebuildPanelsWhenPageReady 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 向实时思考气泡追加一行内容（Markdown 格式），并更新 DOM。
        /// </summary>
        private void AppendAgentThinking(string line)
        {
            lock (_lock)
            {
                if (_agentStreamingMsgIndex < 0) return;
                if (_agentThinkingContent.Length > 0)
                    _agentThinkingContent.AppendLine();
                _agentThinkingContent.Append(line);
            }
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null || _agentStreamingMsgIndex < 0) return;
                try
                {
                    string content;
                    lock (_lock) { content = _agentThinkingContent.ToString(); }
                    // 使用批处理：仅在内容显著变化时推送，避免频繁 WebView2 通信
                    BatchStreamingUpdate(_agentStreamingMsgIndex, content, string.Empty);
                }
                catch { }
            });
        }

        /// <summary>
        /// 将日志条目格式化为思考气泡中的可读行。
        /// 过滤掉过于技术性的日志，保留用户关心的信息。
        /// 支持中英文双语匹配，确保两种 locale 下都能正确展示进度。
        /// </summary>
        private static string FormatLogForThinking(AgentLogEntry entry)
        {
            string msg = entry.Message ?? string.Empty;

            // ── 过滤纯内部日志（中英文双语匹配）──
            if (msg.StartsWith("[TokenUsage]") || msg.StartsWith("[Retry") || msg.StartsWith("[AgentDispatcher]"))
                return string.Empty;
            if (msg.Contains("上下文已累积") || msg.Contains("Planning 模式")
                || msg.Contains("context accumulated") || msg.Contains("Planning mode"))
                return string.Empty;

            // ── 格式化为可读的思考内容 ──

            // Emoji 前缀（locale-independent）：状态指示、文件操作、工具调用
            if (msg.StartsWith("📄") || msg.StartsWith("📖") || msg.Contains("已读取") || msg.Contains("read file"))
                return msg;
            if (msg.StartsWith("✅") || msg.StartsWith("❌") || msg.StartsWith("⚠️"))
                return msg;
            if (msg.StartsWith("🔨") || msg.StartsWith("🔧"))
                return msg;
            // 工具调用 emoji：编辑、创建、终端、搜索、目录等
            if (msg.StartsWith("✏️") || msg.StartsWith("📝") || msg.StartsWith("💻") || msg.StartsWith("📋")
                || msg.StartsWith("📂") || msg.StartsWith("🔍") || msg.StartsWith("🔎") || msg.StartsWith("🌐")
                || msg.StartsWith("🗑️"))
                return msg;

            // Phase 进度指示（中英文通用：包含 "/3:" 的模式）
            if (msg.StartsWith("阶段") || msg.StartsWith("Phase") || msg.Contains("/3:"))
                return $"🔍 {msg}";

            // Plan Agent 开始（中英文）
            if (msg.StartsWith("Plan Agent 开始规划") || msg.StartsWith("Plan Agent started planning"))
                return "🔍 开始分析任务，探索项目结构…";

            // Plan Agent 完成 / 探索路由 / 发现完成（中英文）
            if (msg.StartsWith("计划创建完成") || msg.StartsWith("Plan created")
                || msg.StartsWith("探索路由") || msg.StartsWith("Explore routing")
                || msg.StartsWith("发现阶段完成") || msg.StartsWith("Discovery phase complete"))
                return msg;

            // 无计划 / 单步（中英文）
            if (msg.StartsWith("无计划") || msg.StartsWith("No plan"))
                return "📋 单步任务，直接执行代码修改…";

            // 编译结果（中英文）
            if (msg.Contains("编译通过") || msg.Contains("build passed") || msg.Contains("Build succeeded"))
                return "✅ 编译验证通过";
            if ((msg.Contains("编译") || msg.Contains("build") || msg.Contains("Build"))
                && (msg.Contains("失败") || msg.Contains("错误") || msg.Contains("failed") || msg.Contains("error")))
                return $"⚠️ {msg}";

            // Edit Agent 步骤前缀（使用 i18n）
            var L = LocalizationService.Instance;
            if (msg.StartsWith(L["agent.log.editStepPrefix"]) || msg.Contains(L["agent.log.planStepsPlannedSuffix"]))
                return msg;

            // ── ExploreAgent 委托和发现日志 ──
            if (msg.StartsWith("[EditAgent] 委托 ExploreAgent") || msg.StartsWith("[EditAgent] delegated ExploreAgent"))
                return $"🔍 {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[EditAgent] ExploreAgent 返回") || msg.StartsWith("[EditAgent] ExploreAgent returned"))
                return $"📁 {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[EditAgent]"))
                return $"📝 {msg.Replace("[EditAgent] ", "")}";

            // ── Plan Agent 转发的 Explore 日志 ──
            if (msg.StartsWith("[Explore] [Discover]"))
                return string.Empty; // Explore 内部发现日志不展示
            if (msg.StartsWith("[Explore]"))
                return $"🔍 {msg.Replace("[Explore] ", "")}";

            // ── Plan Agent 自身进度日志（[Plan] 前缀）──
            if (msg.StartsWith("[Plan]"))
                return $"📋 {msg.Replace("[Plan] ", "")}";

            // ── Plan Agent 关键日志（中英文通用匹配）──
            // 匹配模式: "Phase X/Y:", "步骤 X/Y:", "step X/Y:", "📄 plan.md"
            if (msg.Contains(" plan.md") || msg.Contains(": 成功") || msg.Contains(": succeeded"))
                return msg;

            // 其他日志：以 ERROR/WARN 级别展示简要信息
            if (entry.Level == "ERROR")
                return $"❌ {msg}";
            if (entry.Level == "WARN")
                return $"⚠️ {msg}";

            // ── Plan/Explore Agent 的 INFO 日志：展示进度给用户 ──
            // 包含这些关键词的 INFO 日志对用户有意义，不应过滤
            if (entry.Level == "INFO")
            {
                if (msg.Contains("/3:") || msg.Contains("Phase") || msg.Contains("阶段")
                    || msg.StartsWith("Plan Agent") || msg.StartsWith("Plan created")
                    || msg.StartsWith("计划创建完成") || msg.StartsWith("探索路由")
                    || msg.StartsWith("Explore routing") || msg.StartsWith("发现阶段完成")
                    || msg.StartsWith("Discovery phase"))
                    return msg;
            }

            return string.Empty; // 其余 INFO 级别默认不展示，避免刷屏
        }

        /// <summary>
        /// Agent 日志条目回调：仅更新实时思考气泡。
        /// </summary>
        private void OnAgentLogEntryAdded(AgentLogEntry entry)
        {
            // ── 更新实时思考气泡 ──
            string thinkingLine = FormatLogForThinking(entry);
            if (!string.IsNullOrEmpty(thinkingLine))
                AppendAgentThinking(thinkingLine);
        }

        /// <summary>
        /// Agent 权限请求回调：在 WebView 中注入确认/拒绝按钮。
        /// 针对不同 ActionType 渲染不同的 UI：
        /// - "file_delete" → 文件删除确认卡片（含文件列表、确认/取消按钮）
        /// - "terminal_command" → 终端命令审批卡片（含命令详情、允许/跳过按钮）
        /// - 其他 → 通用权限确认弹窗
        /// </summary>
        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string js;
                    if (request.ActionType == "file_delete")
                    {
                        js = ChatHtmlService.BuildFileDeleteConfirmationJs(request);
                        StatusLabel.Text = string.Format(LocalizationService.Instance["status.deleteWaitingConfirm"], request.Title);
                    }
                    else if (request.ActionType == "terminal_command")
                    {
                        js = ChatHtmlService.BuildTerminalApprovalJs(request);
                        StatusLabel.Text = string.Format(LocalizationService.Instance["status.terminalWaitingApproval"], request.Command);
                    }
                    else
                    {
                        js = ChatHtmlService.BuildPermissionRequestJs(request);
                        StatusLabel.Text = string.Format(LocalizationService.Instance["status.waitingConfirm"], request.Title);
                    }

                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] 权限 UI 注入失败: {ex.Message}");
                    _agentDispatcher?.RespondToPermission(request.RequestId, false);
                }
            });
        }

        /// <summary>
        /// Agent 向用户提问回调（VisualStudio_askQuestions 工具）。
        /// 在 WebView 中注入问题 UI，等待用户回答后回调 AgentDispatcher。
        /// </summary>
        private void OnAgentQuestionsRequested(AgentQuestionRequest request)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string js = ChatHtmlService.BuildAskQuestionsJs(request);
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.questionsWaiting"],
                        request.Questions.Count);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] 问题 UI 注入失败: {ex.Message}");
                    _agentDispatcher?.RespondToQuestions(request.RequestId, "{}");
                }
            });
        }

        /// <summary>
        /// Agent 文件变更实时通知回调：仅更新实时思考气泡。
        /// </summary>
        private void OnAgentFileChangeNotified(AgentFileChangeEventArgs args)
        {
            // ── 更新实时思考气泡 ──
            string icon = args.ChangeType.ToLowerInvariant() switch
            {
                "create" => "📄 新建",
                "delete" => "🗑️ 删除",
                _ => "✏️ 修改",
            };
            string fileName = System.IO.Path.GetFileName(args.FilePath);
            AppendAgentThinking($"{icon} `{fileName}` ({args.Detail})");
        }

        #endregion

        #region Plan Contextual Intent Detection

        /// <summary>
        /// 上下文感知意图覆盖：当存在待处理计划（_pendingHandoff）时，
        /// 识别用户输入的执行/修改意图，覆盖 AI 路由结果。
        /// 
        /// 场景：
        /// - "开始执行" / "执行计划" → 直接路由到 Edit Agent 执行计划（效果等同点击按钮）
        /// - "修改第三步..." / "换个方案" → 路由到 Plan Agent 重新规划
        /// </summary>
        private AgentRoutingResult OverrideRoutingForPlanContext(string userText, AgentRoutingResult originalRouting)
        {
            // 没有待处理计划时，尝试从持久化的 HandoffJson 恢复
            if (_pendingHandoff == null || !_pendingHandoff.ShowContinueOn)
            {
                _pendingHandoff = TryRestorePendingHandoffFromMessages();
            }

            // 仍未找到待处理计划，不覆盖路由
            if (_pendingHandoff == null || !_pendingHandoff.ShowContinueOn)
                return originalRouting;

            // ── 执行意图：直接开始执行计划 ──
            if (IsPlanExecutionIntent(userText))
            {
                Logger.Info($"[PlanContext] 检测到执行意图，路由到 Edit Agent: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Edit,
                    Confidence = "high",
                    Reason = "检测到执行意图，且存在待处理计划",
                    NeedsPlanning = false,
                    IsExplicit = true,
                };
            }

            // ── 修改计划意图：重新规划 ──
            if (IsPlanModificationIntent(userText))
            {
                Logger.Info($"[PlanContext] 检测到修改计划意图，路由到 Plan Agent: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Plan,
                    Confidence = "high",
                    Reason = "检测到修改计划意图，重新规划",
                    NeedsPlanning = true,
                    IsExplicit = true,
                };
            }

            // ── 问答意图：用户可能在询问计划相关问题 ──
            if (IsQuestionAboutPlan(userText))
            {
                Logger.Info($"[PlanContext] 检测到计划相关提问，路由到 Ask Agent: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Ask,
                    Confidence = "high",
                    Reason = "存在待处理计划，用户消息为计划相关提问",
                    NeedsPlanning = false,
                    IsExplicit = true,
                };
            }

            // ── 原路由为 Edit 但置信度不高，且无明确修改关键词 → 保守路由到 Ask ──
            // 场景：AI 路由失败时启发式可能误判"实现"等中性别关键词为 Edit，
            // 存在待处理计划时应保守处理，避免直接执行修改
            if (originalRouting.TargetAgent == AgentType.Edit
                && (originalRouting.Confidence == "medium" || originalRouting.Confidence == "low")
                && !HasExplicitEditIntent(userText))
            {
                Logger.Info($"[PlanContext] Edit 路由低置信度且无明确修改意图，改为 Ask: \"{userText.Truncate(50)}\"");
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Ask,
                    Confidence = "medium",
                    Reason = "存在待处理计划，低置信度 Edit 路由保守回退到 Ask",
                    NeedsPlanning = false,
                    IsExplicit = true,
                };
            }

            // ── 原路由为 Ask 且置信度不高时，检查是否在讨论计划 ──
            if (originalRouting.TargetAgent == AgentType.Ask
                && (originalRouting.Confidence == "medium" || originalRouting.Confidence == "low"))
            {
                var planDiscussionKeywords = new[]
                {
                    "步骤", "第", "step", "计划", "方案", "plan",
                    "不满意", "调整", "修改", "改成", "换成", "换个",
                    "第三步", "第二步", "第一步", "第四步", "第五步",
                };
                if (planDiscussionKeywords.Any(k =>
                    userText.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Info($"[PlanContext] Ask 路由低置信度 + 计划关键词，改为 Plan Agent: \"{userText.Truncate(50)}\"");
                    return new AgentRoutingResult
                    {
                        TargetAgent = AgentType.Plan,
                        Confidence = "medium",
                        Reason = "存在待处理计划，用户消息涉及计划讨论",
                        NeedsPlanning = true,
                        IsExplicit = true,
                    };
                }
            }

            return originalRouting;
        }

        /// <summary>
        /// 判断用户输入是否为"执行计划"意图。
        /// </summary>
        private static bool IsPlanExecutionIntent(string userText)
        {
            // 精确匹配：开始执行、执行计划 等
            var exactMatches = new[]
            {
                "开始执行", "执行计划", "开始实现", "确认执行",
                "开始吧", "执行吧", "开始实施", "开始干活",
                "go", "execute", "run it", "start",
            };

            if (exactMatches.Any(k => string.Equals(userText.Trim(), k, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 短文本包含执行关键词
            if (userText.Length <= 10)
            {
                var shortExecutionKeywords = new[]
                {
                    "执行", "开始", "实现", "实施", "启动",
                    "execute", "start", "run", "go ahead", "do it",
                };
                if (shortExecutionKeywords.Any(k =>
                    userText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判断用户输入是否为"修改计划"意图。
        /// </summary>
        private static bool IsPlanModificationIntent(string userText)
        {
            var modifyKeywords = new[]
            {
                "修改计划", "调整方案", "改一下计划", "重新规划",
                "换个方案", "不满意", "修改第", "调整第",
                "改第", "步骤.*改", "方案.*调整",
                "改成", "换成", "前面.*改",
            };

            return modifyKeywords.Any(k =>
            {
                if (k.Contains(".*"))
                {
                    // 简单模式匹配：检查两个部分是否都存在
                    var parts = k.Split(new[] { ".*" }, StringSplitOptions.None);
                    return parts.All(p =>
                        userText.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                return userText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        /// <summary>
        /// 判断用户输入是否是在询问计划相关问题（非修改、非执行）。
        /// 例如："第三步是什么意思"、"这个方案可行吗"、"为什么需要这样做"
        /// </summary>
        private static bool IsQuestionAboutPlan(string userText)
        {
            // 疑问句式
            var questionPatterns = new[]
            {
                "是什么", "为什么", "怎么做", "如何", "怎么样",
                "可行吗", "可以吗", "对吗", "好不好",
                "什么意思", "能否", "是否", "能不能",
                "what", "why", "how", "explain",
                "?", "？",
            };

            // 计划相关上下文词（结合疑问才算计划提问）
            var planContextWords = new[]
            {
                "步骤", "第", "计划", "方案", "step", "plan",
                "这个", "这样", "那种", "方案",
            };

            bool hasQuestion = questionPatterns.Any(p =>
                userText.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasPlanContext = planContextWords.Any(p =>
                userText.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

            return hasQuestion && hasPlanContext;
        }

        /// <summary>
        /// 判断用户输入是否包含明确的代码修改意图（如"修复"/"添加"/"删除"等强动作词）。
        /// 用于区分"讨论计划"和"执行修改"。
        /// </summary>
        private static bool HasExplicitEditIntent(string userText)
        {
            var strongEditKeywords = new[]
            {
                "修复", "fix", "添加", "add", "删除", "delete", "remove",
                "创建", "create", "修改", "change", "update",
                "重构", "refactor", "实现", "implement",
                "写一个", "编写", "write", "生成", "generate",
                "改一下", "修改一下",
            };

            return strongEditKeywords.Any(k =>
                userText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 从消息列表的 HandoffJson 中恢复 _pendingHandoff（用于会话切换后的上下文感知路由）。
        /// </summary>
        private AgentHandoff? TryRestorePendingHandoffFromMessages()
        {
            lock (_lock)
            {
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    var msg = _messages[i];
                    if (!string.IsNullOrEmpty(msg.HandoffJson))
                    {
                        try
                        {
                            var handoff = JsonSerializer.Deserialize<AgentHandoff>(
                                msg.HandoffJson,
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                            if (handoff != null && handoff.ShowContinueOn)
                            {
                                Logger.Info("[PlanContext] 从 HandoffJson 恢复 _pendingHandoff");
                                return handoff;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[PlanContext] 反序列化 HandoffJson 失败: {ex.Message}");
                        }
                    }
                }
            }
            return null;
        }

        #endregion

        #region Retry / Edit / Version Navigation

        /// <summary>
        /// 记录一轮对话中的文件变更，用于后续重试/编辑时的回退判断。
        /// </summary>
        internal void RecordFileChangesForTurn(int userMsgIndex, List<FileChangeSummary> changedFiles)
        {
            if (changedFiles == null || changedFiles.Count == 0) return;

            lock (_lock)
            {
                var merged = changedFiles
                    .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList();

                _fileChangeHistory[userMsgIndex] = merged;
                Logger.Info($"[FileHistory] 记录第 {userMsgIndex} 轮文件变更: {merged.Count} 个文件");
            }
        }

        /// <summary>
        /// 从 _pendingAgentFileChanges 中消费并记录最近一次 Agent 的文件变更。
        /// </summary>
        private void RecordAgentFileChanges(int userMsgIndex)
        {
            List<FileChangeSummary>? changes = _pendingAgentFileChanges;
            _pendingAgentFileChanges = null;
            if (changes != null && changes.Count > 0)
            {
                RecordFileChangesForTurn(userMsgIndex, changes);
            }
        }

        /// <summary>
        /// 执行 Agent Handoff（从 Plan → Edit）。
        /// 由 WebView 中的"▶ 开始实现"按钮触发。
        /// </summary>
        /// <param name="targetAgent">目标 Agent 类型字符串（如 "Edit"）</param>
        /// <param name="label">按钮标签（用于日志）</param>
        private async Task ExecuteAgentHandoffAsync(string targetAgent, string label)
        {
            if (_agentDispatcher == null) return;

            // ── 重启恢复: _pendingHandoff 在 VS 重启/会话切换后为 null，
            // 但 HandoffJson 已持久化到 ChatMessage 中，需从此恢复。 ──
            if (_pendingHandoff == null)
            {
                lock (_lock)
                {
                    for (int i = _messages.Count - 1; i >= 0; i--)
                    {
                        var msg = _messages[i];
                        if (!string.IsNullOrEmpty(msg.HandoffJson))
                        {
                            try
                            {
                                _pendingHandoff = JsonSerializer.Deserialize<AgentHandoff>(
                                    msg.HandoffJson,
                                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                if (_pendingHandoff != null)
                                {
                                    Logger.Info("[AgentHandoff] 从 HandoffJson 恢复了待处理 Handoff");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[AgentHandoff] 反序列化 HandoffJson 失败: {ex.Message}");
                            }
                        }
                    }
                }
                if (_pendingHandoff == null) return;
            }

            // ── 切换按钮状态为停止 ──
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            lock (_lock) { _isGenerating = true; }
            UpdateButtonsState();

            try
            {
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentHandoff"], targetAgent);

                // ── 隐藏 handoff 按钮（防止重复点击）──
                if (ChatWebView.CoreWebView2 != null)
                {
                    try
                    {
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                            "var btns=document.querySelectorAll('.handoff-btn');btns.forEach(function(b){b.disabled=true;b.textContent='⏳ 执行中...';b.style.opacity='0.6';});");
                    }
                    catch { }
                }

                // ── 重置思考内容，为 Edit 阶段准备新的实时气泡 ──
                lock (_lock) { _agentThinkingContent.Clear(); }
                _lastReportedStepIndex = 0;
                _lastReportedStepStatus = string.Empty;

                // ── 创建新的流式思考气泡（重启后 _agentStreamingMsgIndex 为 -1）──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var thinkingMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = $"🔨 {LocalizationService.Instance["agent.status.analyzing"]}",
                    ReasoningContent = string.Empty,
                    Timestamp = DateTime.Now,
                    IsStreaming = true,
                    IsRendered = false,
                    AgentType = AgentType.Edit,
                };
                lock (_lock)
                {
                    _messages.Add(thinkingMsg);
                    _agentStreamingMsgIndex = _messages.Count - 1;
                }
                AddMessagesHtml("assistant", thinkingMsg.Content);
                _currentStreamingMsgIndex = _agentStreamingMsgIndex;
                UpdateBrowser();

                await TaskScheduler.Default;

                // ── 构建包含 _pendingHandoff 上下文的 AgentContext ──
                var context = new AgentContext
                {
                    SolutionPath = _solutionPath,
                    ContextManager = _contextManager,
                    IsPlanningMode = true,
                    ReadFileAsync = async (path) =>
                    {
                        // RAG-SOURCE: file-read EditAgent 读取文件内容（Handoff 执行上下文）
                        if (File.Exists(path))
                            return await Task.Run(() => File.ReadAllText(path));
                        return null;
                    },
                };

                // ── 恢复 Plan: 从持久化的 PlanJson 中重建 ActivePlan ──
                // PlanAgent 执行完毕后 _agentDispatcher.ActivePlan 已在 RunAgentWorkflowAsync 的
                // finally 块中被清空，但计划 JSON 已持久化到 ChatMessage.PlanJson 中。
                // 必须在 Handoff 前恢复，否则 EditAgent 将回退到单步计划。
                AgentTaskPlan? restoredPlan = null;
                lock (_lock)
                {
                    // 查找最近一条包含 PlanJson 的消息（通常为 PlanAgent 的响应消息）
                    for (int i = _messages.Count - 1; i >= 0; i--)
                    {
                        var msg = _messages[i];
                        if (!string.IsNullOrEmpty(msg.PlanJson))
                        {
                            try
                            {
                                restoredPlan = JsonSerializer.Deserialize<AgentTaskPlan>(
                                    msg.PlanJson,
                                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                if (restoredPlan != null && restoredPlan.Steps.Count > 0)
                                    break;
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[AgentHandoff] 反序列化 PlanJson 失败: {ex.Message}");
                            }
                        }
                    }
                }

                if (restoredPlan != null)
                {
                    _agentDispatcher.ActivePlan = restoredPlan;

                    // 重建 PlanFilePath（用于 ExecuteHandoffAsync 加载 plan.md）
                    // 与 PlanAgent.SavePlanMarkdownAsync 保持相同的路径计算逻辑
                    string baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeepSeekVS", "plans");
                    string subDir;
                    if (!string.IsNullOrEmpty(_solutionPath))
                    {
                        using (var sha256 = SHA256.Create())
                        {
                            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(_solutionPath));
                            var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
                            subDir = Path.Combine(baseDir, $"proj_{hash}");
                        }
                    }
                    else
                    {
                        subDir = Path.Combine(baseDir, "_unsaved");
                    }
                    context.PlanFilePath = Path.Combine(subDir, "plan.md");

                    Logger.Info($"[AgentHandoff] 从 PlanJson 恢复了计划: {restoredPlan.Steps.Count} 个步骤");
                }

                var editAgent = _agentDispatcher.EditAgent;
                editAgent.PlanUpdated += OnAgentPlanUpdated;
                _agentDispatcher.PlanUpdated += OnAgentDispatcherPlanUpdated;
                _agentDispatcher.LogEntryAdded += OnAgentLogEntryAdded;
                _agentDispatcher.FileChangeNotified += OnAgentFileChangeNotified;

                AgentResult agentResult;
                try
                {
                    agentResult = await _agentDispatcher.ExecuteHandoffAsync(_pendingHandoff, context);
                }
                finally
                {
                    editAgent.PlanUpdated -= OnAgentPlanUpdated;
                    _agentDispatcher.PlanUpdated -= OnAgentDispatcherPlanUpdated;
                    _agentDispatcher.LogEntryAdded -= OnAgentLogEntryAdded;
                    _agentDispatcher.FileChangeNotified -= OnAgentFileChangeNotified;
                }

                _pendingHandoff = null; // 消费后清空

                // ── 清除消息中的 HandoffJson，避免会话切换后重复显示按钮 ──
                lock (_lock)
                {
                    if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                    {
                        _messages[_agentStreamingMsgIndex].HandoffJson = null;
                    }
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (agentResult.Success && agentResult.Plan != null)
                {
                    var plan = agentResult.Plan;
                    if (plan.Steps.Count > 0)
                    {
                        try
                        {
                            string completeJs = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(completeJs);
                        }
                        catch { }
                    }

                    // ── 将 Edit 阶段的思考过程追加到最终输出 ──
                    string thinkingText;
                    lock (_lock) { thinkingText = _agentThinkingContent.ToString(); }
                    string thinkingDetailsHtml = string.Empty;
                    if (!string.IsNullOrWhiteSpace(thinkingText))
                    {
                        string escapedThinking = System.Net.WebUtility.HtmlEncode(thinkingText)
                            .Replace("\n", "<br>");
                        thinkingDetailsHtml =
                            "<details class='reasoning-panel' style='margin-top:12px' open='true'>" +
                            "<summary>🔨 " + LocalizationService.Instance["agent.panel.executionProcess"] + "</summary>" +
                            "<div class='reasoning-content'>" + escapedThinking + "</div>" +
                            "</details>";
                    }

                    // ── 最终内容仅包含 Markdown 总结，不混入 HTML（避免 double-escape）──
                    string finalContent = agentResult.Content
                        ?? string.Format(LocalizationService.Instance["agent.result.taskCompletedSuccess"],
                            plan.Steps.Count(s => s.Status == AgentStepStatus.Completed), plan.Steps.Count);

                    // ── 更新现有的流式思考气泡为最终内容 ──
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = finalContent;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                            // ── 持久化任务计划 JSON，重启后可重建任务面板 ──
                            try { msg.PlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                        }
                    }

                    // ── 追加 Cache 命中率统计（使用累计值，非单轮）──
                    string cacheFooter = string.Empty;
                    try
                    {
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                    }
                    catch { }

                    // ── 强制刷新 DOM 显示最终结果 ──
                    BatchStreamingUpdate(_agentStreamingMsgIndex, finalContent, string.Empty, isComplete: true);

                    // ── 发送最终渲染：extraFooter 中注入执行过程 HTML + 缓存统计（纯 HTML，不经过 Markdown 转义）──
                    string combinedFooter = thinkingDetailsHtml + cacheFooter;
                    PostStreamEnd(_agentStreamingMsgIndex, finalContent, string.Empty, combinedFooter);

                    StatusLabel.Text = plan.ChangedFiles.Count > 0
                        ? string.Format(LocalizationService.Instance["agent.result.completed"], plan.ChangedFiles.Count)
                        : LocalizationService.Instance["agent.result.planCompleted"];

                    if (plan.ChangedFiles.Count > 0)
                        _pendingAgentFileChanges = new List<FileChangeSummary>(plan.ChangedFiles);
                }
                else
                {
                    StatusLabel.Text = LocalizationService.Instance["status.ready"];
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AgentHandoff] 执行失败: {ex.Message}", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = string.Format(LocalizationService.Instance["agent.status.handoffError"], ex.Message);
            }
            finally
            {
                // ── 恢复按钮状态：移除已失效的 handoff 按钮 ──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    if (ChatWebView.CoreWebView2 != null)
                    {
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                            "var btns=document.querySelectorAll('.handoff-btn');btns.forEach(function(b){if(b.parentNode)b.parentNode.removeChild(b);});");
                    }
                }
                catch { }
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
            }
        }

        /// <summary>
        /// 检查指定轮次是否有文件变更，如果有则询问用户是否回退。
        /// </summary>
        /// <param name="userMsgIndex">用户消息索引</param>
        /// <returns>true 表示可以继续；false 表示用户取消</returns>
        private async Task<bool> CheckAndRevertFileChangesAsync(int userMsgIndex)
        {
            List<FileChangeSummary>? changes;
            lock (_lock)
            {
                if (!_fileChangeHistory.TryGetValue(userMsgIndex, out changes) || changes.Count == 0)
                    return true;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var fileList = string.Join("\n", changes.Select(c =>
                $"  • {Path.GetFileName(c.FilePath)} (+{c.LinesAdded} -{c.LinesRemoved})"));

            var L = LocalizationService.Instance;
            var result = MessageBox.Show(
                string.Format(L["agent.revert.message"], changes.Count, fileList),
                L["agent.revert.title"],
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                StatusLabel.Text = L["agent.revert.cancelled"];
                return false;
            }

            if (result == MessageBoxResult.Yes)
            {
                StatusLabel.Text = L["agent.revert.reverting"];
                int revertedCount = 0;
                int failedCount = 0;

                foreach (var change in changes)
                {
                    try
                    {
                        // ── 情况1：有原始内容 → 写回原始内容 ──
                        if (!string.IsNullOrEmpty(change.OriginalContent))
                        {
                            // 优先通过 VS SDK 回退（若文件在编辑器中打开则纳入 Undo 栈）
                            bool revertedViaVS = await TryRevertViaVSSdkAsync(change.FilePath, change.OriginalContent);
                            if (!revertedViaVS)
                            {
                                // 回退：文件未在编辑器中打开，直接写磁盘
                                string? dir = Path.GetDirectoryName(change.FilePath);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);
                                await Task.Run(() =>
                                    File.WriteAllText(change.FilePath, change.OriginalContent, Encoding.UTF8));
                            }
                            revertedCount++;
                            Logger.Info($"[FileHistory] ✅ 已回退: {Path.GetFileName(change.FilePath)}");
                        }
                        // ── 情况2：纯新建文件 → 删除文件 ──
                        else if (change.LinesAdded > 0 && change.LinesRemoved == 0)
                        {
                            if (File.Exists(change.FilePath))
                            {
                                await Task.Run(() => File.Delete(change.FilePath));
                                revertedCount++;
                                Logger.Info($"[FileHistory] ✅ 已删除新建文件: {Path.GetFileName(change.FilePath)}");
                            }
                        }
                        // ── 情况3：文件被删除 → 从 OriginalContent 恢复（若已捕获）──
                        else if (change.LinesRemoved < 0)
                        {
                            if (!string.IsNullOrEmpty(change.OriginalContent))
                            {
                                string? dir = Path.GetDirectoryName(change.FilePath);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);
                                await Task.Run(() =>
                                    File.WriteAllText(change.FilePath, change.OriginalContent, Encoding.UTF8));
                                revertedCount++;
                                Logger.Info($"[FileHistory] ✅ 已恢复删除的文件: {Path.GetFileName(change.FilePath)}");
                            }
                            else
                            {
                                Logger.Warn($"[FileHistory] 无法恢复删除的文件（缺少原始内容）: {Path.GetFileName(change.FilePath)}");
                                failedCount++;
                            }
                        }
                        else
                        {
                            Logger.Warn($"[FileHistory] 无法回退（缺少原始内容）: {Path.GetFileName(change.FilePath)}");
                            failedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[FileHistory] 回退失败: {change.FilePath} - {ex.Message}", ex);
                        failedCount++;
                    }
                }

                lock (_lock) { _fileChangeHistory.Remove(userMsgIndex); }

                StatusLabel.Text = revertedCount > 0
                    ? $"✅ 已回退 {revertedCount} 个文件" + (failedCount > 0 ? $"，{failedCount} 个失败" : "")
                    : "未回退任何文件";
                Logger.Info($"[FileHistory] 回退完成: {revertedCount} 成功, {failedCount} 失败");
            }
            else
            {
                // ── 用户选择了「否」：保留文件，注入提示让 AI 以磁盘最新文件为准 ──
                lock (_lock) { _fileChangeHistory.Remove(userMsgIndex); }

                // 注入系统提示：告知 AI 对话历史中的代码可能已过时，应以磁盘最新文件为准
                _contextManager.AddCustomMessage("system", LocalizationService.Instance["agent.revert.staleCodeHint"]);

                StatusLabel.Text = L["agent.revert.keepChanges"];
                Logger.Info($"[FileHistory] {L["agent.revert.keepChanges"]}");
            }

            return true;
        }

        /// <summary>
        /// 尝试通过 VS SDK 的 ITextBuffer API 回退文件内容。
        /// 若文件当前在 VS 编辑器中打开，则使用 ITextEdit 操作（纳入 VS Undo 栈）；
        /// 若未打开，则返回 false，由调用方回退到磁盘写入。
        /// </summary>
        /// <returns>true 表示已通过 VS SDK 成功回退；false 表示文件未打开，需磁盘回退。</returns>
        private static async Task<bool> TryRevertViaVSSdkAsync(string filePath, string originalContent)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;
                if (editorAdapter == null) return false;

                // ── 通过 RDT 查找文件是否在编辑器中打开 ──
                var rdt = (IVsRunningDocumentTable?)ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable));
                if (rdt == null) return false;

                IEnumRunningDocuments? enumDocs;
                if (rdt.GetRunningDocumentsEnum(out enumDocs) != VSConstants.S_OK || enumDocs == null)
                    return false;

                uint[] cookieArray = new uint[1];
                uint fetched;
                while (enumDocs.Next(1, cookieArray, out fetched) == VSConstants.S_OK && fetched == 1)
                {
                    uint cookie = cookieArray[0];
                    uint flags; uint readLocks; uint editLocks;
                    string? docPath; IVsHierarchy? hierarchy; uint itemId; IntPtr docDataPtr;

                    if (rdt.GetDocumentInfo(cookie, out flags, out readLocks, out editLocks,
                        out docPath, out hierarchy, out itemId, out docDataPtr) != VSConstants.S_OK)
                        continue;

                    if (docPath == null || !string.Equals(docPath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (docDataPtr == IntPtr.Zero) continue;

                    // ── 找到匹配的文档 → 通过 ITextBuffer 回退 ──
                    var vsTextBuffer = Marshal.GetObjectForIUnknown(docDataPtr) as IVsTextBuffer;
                    if (vsTextBuffer == null) continue;

                    var textBuffer = editorAdapter.GetDataBuffer(vsTextBuffer);
                    if (textBuffer == null) continue;

                    using (var edit = textBuffer.CreateEdit())
                    {
                        var snapshot = textBuffer.CurrentSnapshot;
                        if (snapshot.Length > 0)
                            edit.Replace(0, snapshot.Length, originalContent);
                        else
                            edit.Insert(0, originalContent);
                        edit.Apply();
                    }

                    // ── 保存文件 ──
                    if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                    {
                        textDoc.Save();
                    }

                    Logger.Info($"[FileHistory] ✅ 通过 VS SDK 回退已打开文件: {Path.GetFileName(filePath)}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[FileHistory] VS SDK 回退失败，回退到磁盘写入: {Path.GetFileName(filePath)} - {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// 重试某个助手消息：EditAgent 原地替换，其他 Agent 产生分叉。
        /// </summary>
        private async Task RetryMessageAsync(int assistantMsgIndex)
        {
            lock (_lock)
            {
                if (_isGenerating)
                {
                    _ = Task.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        StatusLabel.Text = LocalizationService.Instance["agent.status.busyRetry"];
                    });
                    return;
                }
                if (assistantMsgIndex < 0 || assistantMsgIndex >= _messages.Count) return;
                var assistantMsg = _messages[assistantMsgIndex];
                if (assistantMsg.Role != "assistant") return;
            }

            try
            {
                // ── 通过消息索引找到对应的助手 ConvNode ──
                ConvNode? assistantNode = GetConvNodeByMessageIndex(assistantMsgIndex);
                if (assistantNode == null || !assistantNode.IsAssistantMessage) return;

                // ── 找到对应的用户消息索引（用于文件回退检查）──
                int userMsgIndex = -1;
                lock (_lock)
                {
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

                bool canProceed = await CheckAndRevertFileChangesAsync(userMsgIndex);
                if (!canProceed) return;

                // ── 判断是否为 EditAgent：是则移除旧节点后重新生成，否则树状分叉 ──
                ChatMessage? assistantMsg;
                lock (_lock) { assistantMsg = _messages[assistantMsgIndex]; }
                bool isEditAgent = assistantMsg?.AgentType == AgentType.Edit;

                var tree = EnsureTree();

                if (isEditAgent)
                {
                    // ── EditAgent：移除旧助手节点及其后代，重置到用户节点，重新生成 ──
                    tree.RemoveNodeFromTree(assistantNode);
                    Logger.Info($"[EditAgent] 重试消息：移除旧助手节点 (nodeId={assistantNode.Id})，不产生分支");
                }
                else
                {
                    // ── 其他 Agent：树状分叉 ──
                    var newAssistantMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.Empty,
                        ReasoningContent = string.Empty,
                        Timestamp = DateTime.Now,
                        IsStreaming = true,
                        IsRendered = false,
                        ForkReason = "retry",
                        AgentType = assistantMsg?.AgentType,
                    };
                    tree.ForkAt(assistantNode, newAssistantMsg, "retry");
                    Logger.Info($"[Tree] 重试消息：树状分叉 (nodeId={assistantNode.Id})");
                }

                // ── 同步消息列表并重建上下文 ──
                RebuildFromTree();
                RebuildContextFromTree();

                // ── 重新发送用户消息生成新的助手回复 ──
                ChatMessage? userMsg = null;
                int newUserMsgIndex = -1;
                lock (_lock)
                {
                    // 找到新分支中 fork 点之前的用户消息
                    for (int i = _messages.Count - 1; i >= 0; i--)
                    {
                        if (_messages[i].Role == "user")
                        {
                            userMsg = _messages[i];
                            newUserMsgIndex = i;
                            break;
                        }
                    }
                }

                if (userMsg != null)
                    await ResendUserMessageAsync(newUserMsgIndex, userMsg);
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
        /// </summary>
        private async Task EditMessageAsync(int userMsgIndex)
        {
            lock (_lock)
            {
                if (_isGenerating)
                {
                    _ = Task.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        StatusLabel.Text = LocalizationService.Instance["agent.status.busyEdit"];
                    });
                    return;
                }
                if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;
                var msg = _messages[userMsgIndex];
                if (msg.Role != "user") return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 确保输入框可编辑
            UpdateButtonsState();

            string? originalContent = null;
            lock (_lock)
            {
                var uMsg = _messages[userMsgIndex];
                originalContent = uMsg.Content;
            }

            // ── 在用户气泡处显示内联编辑区 ──
            string inlineEditJs = ChatHtmlService.BuildInlineEditJs(userMsgIndex, originalContent ?? string.Empty);
            try
            {
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(inlineEditJs);
            }
            catch { }

            _pendingEditMsgIndex = userMsgIndex;

            // ── 将原始文本填入输入框，方便用户在输入框中编辑 ──
            InputTextBox.Text = originalContent ?? string.Empty;
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            InputTextBox.Focus();

            StatusLabel.Text = LocalizationService.Instance["status.editMessageHint"];
        }

        /// <summary>
        /// 待编辑的用户消息索引，-1 表示无。
        /// </summary>
        private int _pendingEditMsgIndex = -1;

        /// <summary>
        /// 处理编辑后重新发送：在用户节点处产生分叉（新 User），切换到新分支后重新生成。
        /// </summary>
        private async Task HandleEditResendAsync(int userMsgIndex, string newContent)
        {
            _pendingEditMsgIndex = -1;

            lock (_lock) { _isGenerating = true; }
            UpdateButtonsState();
            InputTextBox.Text = string.Empty;
            StatusLabel.Text = LocalizationService.Instance["agent.status.regenerating"];

            bool canProceed = await CheckAndRevertFileChangesAsync(userMsgIndex);
            if (!canProceed)
            {
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                StatusLabel.Text = LocalizationService.Instance["status.ready"];
                return;
            }

            try
            {
                // ── 通过消息索引找到对应的用户 ConvNode ──
                ConvNode? userNode = GetConvNodeByMessageIndex(userMsgIndex);
                if (userNode == null || !userNode.IsUserMessage)
                {
                    lock (_lock) { _isGenerating = false; }
                    UpdateButtonsState();
                    StatusLabel.Text = LocalizationService.Instance["status.ready"];
                    return;
                }

                ChatMessage? originalUserMsg = null;
                lock (_lock)
                {
                    if (userMsgIndex >= 0 && userMsgIndex < _messages.Count)
                        originalUserMsg = _messages[userMsgIndex];
                }

                // ── 判断是否为 EditAgent：是则原地替换，否则树状分叉 ──
                bool isEditAgent = originalUserMsg?.AgentType == AgentType.Edit;

                var tree = EnsureTree();
                var editedUserMsg = new ChatMessage
                {
                    Role = "user",
                    Content = newContent,
                    AttachedFileNames = originalUserMsg?.AttachedFileNames ?? new List<string>(),
                    AttachedFiles = originalUserMsg?.AttachedFiles ?? new List<FileParseResult>(),
                    Timestamp = DateTime.Now,
                    AgentType = originalUserMsg?.AgentType, // 保留原 Agent 类型
                };

                if (isEditAgent)
                {
                    // ── EditAgent：原地修改，不产生分支，不显示 <> ──
                    tree.ReplaceInPlace(userNode, editedUserMsg);
                    Logger.Info($"[EditAgent] 编辑消息：原地替换 (nodeId={userNode.Id})，不产生分支");
                }
                else
                {
                    // ── 其他 Agent：树状分叉 ──
                    editedUserMsg.ForkReason = "edit";
                    tree.ForkAt(userNode, editedUserMsg, "edit");
                    Logger.Info($"[Tree] 编辑消息：树状分叉 (nodeId={userNode.Id})");
                }

                // ── 同步消息列表并重建上下文 ──
                RebuildFromTree();
                RebuildContextFromTree();

                // ── 重新发送 ──
                int newUserMsgIndex = -1;
                ChatMessage? newUserMsg = null;
                lock (_lock)
                {
                    for (int i = _messages.Count - 1; i >= 0; i--)
                    {
                        if (_messages[i].Role == "user")
                        {
                            newUserMsg = _messages[i];
                            newUserMsgIndex = i;
                            break;
                        }
                    }
                }

                if (newUserMsg != null)
                    await ResendUserMessageAsync(newUserMsgIndex, newUserMsg);
            }
            catch (Exception ex)
            {
                Logger.Error($"HandleEditResendAsync 异常: {ex.Message}", ex);
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                StatusLabel.Text = LocalizationService.Instance["status.ready"];
            }
        }

        /// <summary>
        /// 在兄弟分支间导航（树状分叉切换）。
        /// </summary>
        private async Task NavigateBranchAsync(string nodeId, int direction)
        {
            try
            {
                var tree = EnsureTree();
                var node = tree.FindNode(nodeId);
                if (node == null) return;

                var newLeaf = tree.NavigateSibling(node, direction);
                if (newLeaf == null) return; // 边界，无法切换

                // ── 同步消息列表并重建上下文 ──
                RebuildFromTree();
                RebuildContextFromTree();

                // ── 更新浏览器 ──
                UpdateBrowser();

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Error($"NavigateBranchAsync 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从树活跃路径重建上下文管理器（用于分支切换后同步，或作为 RestoreFullContext 的回退路径）。
        /// 树节点不包含 tool_calls / tool / system 角色，这些信息从 ApiHistory 补充。
        /// 注意：此方法仅在 ApiHistory 为空或 RestoreFullContext 失败时作为回退使用；
        /// 正常情况下优先使用 RestoreFullContext（完整数据源）。
        /// </summary>
        private void RebuildContextFromTree()
        {
            if (_tree == null) return;

            lock (_lock)
            {
                _contextManager.Clear();
                var path = _tree.GetActivePath();
                foreach (var node in path)
                {
                    if (node.Message == null) continue;
                    var msg = node.Message;
                    string content = msg.Content ?? string.Empty;

                    if (msg.Role == "user")
                    {
                        // 用户消息：附加文件内容（如有）
                        if (msg.AttachedFiles.Count > 0)
                        {
                            string fileContext = FileParserService.FormatParseResultsForContext(msg.AttachedFiles);
                            if (!string.IsNullOrEmpty(fileContext))
                                content = fileContext + "\n" + content;
                        }
                        _contextManager.AddUserMessage(content);
                    }
                    else if (msg.Role == "assistant")
                    {
                        // 树节点的 ChatMessage 不含 ToolCalls，此处仅恢复 content + reasoning
                        _contextManager.AddAssistantMessage(content, msg.ReasoningContent);
                    }
                }

                // ── 从 ApiHistory 补充 tool / system / 含tool_calls的assistant 消息 ──
                if (_activeSession?.ApiHistory != null && _activeSession.ApiHistory.Count > 0)
                {
                    foreach (var apiMsg in _activeSession.ApiHistory)
                    {
                        if (apiMsg.Role == "tool" && !string.IsNullOrEmpty(apiMsg.ToolCallId))
                        {
                            _contextManager.AddToolResult(apiMsg.ToolCallId,
                                apiMsg.Name ?? "unknown", apiMsg.Content ?? string.Empty);
                        }
                        else if (apiMsg.Role == "system")
                        {
                            _contextManager.AddCustomMessage("system", apiMsg.Content ?? string.Empty);
                        }
                        else if (apiMsg.Role == "assistant" && apiMsg.ToolCalls != null && apiMsg.ToolCalls.Count > 0)
                        {
                            // 补充含 tool_calls 的 assistant 消息（树节点不包含此字段）
                            _contextManager.AddAssistantMessage(apiMsg.Content, apiMsg.ReasoningContent, apiMsg.ToolCalls);
                        }
                    }
                }

                Logger.Info($"[Tree→Context] 已从 {path.Count} 个节点重建上下文"
                    + (_activeSession?.ApiHistory?.Count > 0 ? $" (+ {_activeSession.ApiHistory.Count} 条 ApiHistory 补充)" : ""));
            }
        }

        /// <summary>
        /// 重新发送用户消息的核心逻辑。
        /// </summary>
        private async Task ResendUserMessageAsync(int userMsgIndex, ChatMessage userMsg)
        {
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
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

            lock (_lock) { _isGenerating = true; }
            UpdateButtonsState();

            // ── 记录 Token 用量日志 ──
            var tokenStats = _contextManager.GetStats();
            Logger.Info($"[TokenUsage] 当前对话 Token: {tokenStats.EstimatedTokens:N0}/{tokenStats.TokenBudget:N0} ({tokenStats.UsagePercent:F1}%) | 轮次: {tokenStats.TurnCount} | 消息: {tokenStats.MessageCount}");

            // ── 树状结构：不再需要裁剪 _messages（分支切换时已由 SyncMessagesFromTree 处理）──
            // ── 树状结构：上下文已由 RebuildContextFromTree 重建，无需手动检查 userExistsInHistory ──

            var retryCts = CreateNewStreamingCts();

            ChatMessage? assistantMsg = null;
            int newAssistantIdx = -1;

            try
            {
                StatusLabel.Text = LocalizationService.Instance["status.thinking"];

                string userContent = userMsg.Content ?? string.Empty;
                string enrichedContent = BuildRetryEnrichedContent(userMsg, userContent);

                if (_agentDispatcher != null && !string.IsNullOrEmpty(userContent) && !userContent.StartsWith("/"))
                {
                    // ── 保留原始 @agent 显式路由，避免重试时重新判断 ──
                    AgentRoutingResult routing;
                    if (userMsg.AgentType != null && userMsg.AgentType != AgentType.Ask)
                    {
                        routing = new AgentRoutingResult
                        {
                            TargetAgent = userMsg.AgentType.Value,
                            Confidence = "high",
                            Reason = "重试保留原始 @agent 路由",
                            NeedsPlanning = userMsg.AgentType == AgentType.Plan,
                            IsExplicit = true,
                        };
                        Logger.Info($"[Retry] 使用原始 AgentType: {userMsg.AgentType.Value}，跳过重新路由");
                    }
                    else
                    {
                        routing = await _agentDispatcher.RouteAsync(enrichedContent);
                    }

                    bool needsAgent = routing.TargetAgent == AgentType.Plan
                        || routing.TargetAgent == AgentType.Edit
                        || routing.NeedsPlanning;

                    if (needsAgent)
                    {
                        Logger.Info($"[Retry] 重新路由到 Agent: {routing.TargetAgent}" +
                            $", NeedsPlanning={routing.NeedsPlanning}");

                        string fileContext = string.Empty;
                        if (userMsg.AttachedFiles.Count > 0)
                            fileContext = FileParserService.FormatParseResultsForContext(userMsg.AttachedFiles);

                        string conversationContext = GetConversationContextForRetry();
                        if (!string.IsNullOrEmpty(conversationContext))
                        {
                            fileContext = string.IsNullOrEmpty(fileContext)
                                ? conversationContext
                                : conversationContext + "\n\n" + fileContext;
                        }

                        // ── 检查是否已有 retry fork 占位 assistant，避免创建多余气泡 ──
                        bool hasPlaceholder = TryReuseRetryPlaceholder(out assistantMsg, out newAssistantIdx);

                        // ── 重置累计 Cache 统计（与主流程一致）──
                        _apiService?.ResetAccumulatedStats();

                        await RunAgentWorkflowAsync(enrichedContent, fileContext, routing);
                        RecordAgentFileChanges(userMsgIndex);

                        // ── RunAgentWorkflowAsync 已处理所有渲染（任务面板 + Handoff 按钮），
                        // 无需再次全量重建页面（会清除动态注入的 JS 元素）──

                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        StatusLabel.Text = LocalizationService.Instance["status.ready"];
                        return;
                    }
                }

                // ── 检查是否已有 retry fork 占位 assistant（非 Agent 路径），避免创建多余气泡 ──
                bool reusedPlaceholder = TryReuseRetryPlaceholder(out assistantMsg, out newAssistantIdx);

                if (!reusedPlaceholder)
                {
                    assistantMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.Empty,
                        ReasoningContent = string.Empty,
                        Timestamp = DateTime.Now,
                        IsStreaming = true,
                        IsRendered = false,
                    };
                    lock (_lock)
                    {
                        // ── 树状结构：通过 AddChildMessage 添加到活跃分支 ──
                        if (_tree != null)
                        {
                            _tree.AddChildMessage(assistantMsg);
                            SyncMessagesFromTree();
                            newAssistantIdx = _messages.Count - 1;
                        }
                        else
                        {
                            _messages.Add(assistantMsg);
                            newAssistantIdx = _messages.Count - 1;
                        }
                    }
                }

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                // ── 重置累计 Cache 统计（与主流程 SendMessageCoreAsync 一致）──
                _apiService?.ResetAccumulatedStats();

                var requestMessages = await BuildRequestMessagesAsync();
                var apiService = _apiService!;

                var reasoningBuffer = new StringBuilder();
                var contentBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, null, retryCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = LocalizationService.Instance["status.deepThinking"];

                        if (reasoningBuffer.Length - lastReasoningLength >= 200)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            BatchStreamingUpdate(newAssistantIdx,
                                contentBuffer.ToString(), reasoningBuffer.ToString());
                        }
                    }
                    else if (chunk.StartsWith("[TOOL_CALL]"))
                    {
                        // Retry 场景不使用工具调用，忽略
                    }
                    else if (chunk.StartsWith("[CACHE]"))
                    {
                        // ── Cache 统计信息 ── 日志在流结束后统一记录
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
                        StatusLabel.Text = LocalizationService.Instance["status.replying"];

                        if (streamRenderTick >= StreamRenderInterval)
                        {
                            streamRenderTick = 0;
                            assistantMsg.Content = contentBuffer.ToString();
                            BatchStreamingUpdate(newAssistantIdx,
                                contentBuffer.ToString(), reasoningBuffer.ToString());
                        }
                    }
                }

                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Retry] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                // ── 记录 Cache 命中率 ──
                LogCacheHitRate();

                // ── 构建 Cache 命中率统计卡片 HTML（使用累计值，与主流程一致）──
                string cacheFooterHtml = string.Empty;
                {
                    long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                    long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                    long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                    long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                    if (totalHit + totalMiss > 0)
                    {
                        cacheFooterHtml = ChatHtmlService.BuildCacheHitFooterHtml(
                            totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                    }
                }

                // ── 同步最终内容并强制刷新，确保增量内容已推送 ──
                BatchStreamingUpdate(newAssistantIdx, contentBuffer.ToString(), reasoningBuffer.ToString(), isComplete: true);

                PostStreamEnd(newAssistantIdx, contentBuffer.ToString(), reasoningBuffer.ToString(), cacheFooterHtml);

                _contextManager.AddAssistantMessage(
                    contentBuffer.ToString(),
                    reasoningBuffer.Length > 0 ? reasoningBuffer.ToString() : null);

                // ── 树状结构：不再需要版本历史字典 ──

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

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
                if (assistantMsg != null)
                {
                    assistantMsg.Content += "\n\n*[已停止]*";
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(newAssistantIdx, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(newAssistantIdx, assistantMsg.Content, assistantMsg.ReasoningContent);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Retry] API 出错", ex);
                if (assistantMsg != null)
                {
                    assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                    assistantMsg.IsStreaming = false;
                    BatchStreamingUpdate(newAssistantIdx, assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    PostStreamEnd(newAssistantIdx,
                        assistantMsg.Content, assistantMsg.ReasoningContent);
                }
            }
            finally
            {
                if (assistantMsg != null)
                    assistantMsg.IsStreaming = false;
                lock (_lock) { _isGenerating = false; }
                StatusLabel.Text = string.Empty;
                DisposeStreamingCts();
                UpdateButtonsState();
            }
        }

        /// <summary>
        /// 检查树活跃路径末尾是否已有 retry fork 产生的空 streaming assistant 占位。
        /// 有则复用（避免重试时出现两个"思考中…"气泡），返回 true；
        /// 无则返回 false，调用方需自行创建新 assistant。
        /// </summary>
        private bool TryReuseRetryPlaceholder(out ChatMessage? assistantMsg, out int newAssistantIdx)
        {
            assistantMsg = null;
            newAssistantIdx = -1;

            lock (_lock)
            {
                if (_tree != null)
                {
                    var path = _tree.GetActivePath();
                    var lastNode = path.Count > 0 ? path[path.Count - 1] : null;
                    if (lastNode?.Message != null
                        && lastNode.Message.Role == "assistant"
                        && lastNode.Message.IsStreaming
                        && string.IsNullOrEmpty(lastNode.Message.Content))
                    {
                        assistantMsg = lastNode.Message;
                        newAssistantIdx = _messages.Count - 1;
                        // 更新占位文本，准备接收流式内容
                        assistantMsg.Content = string.Empty;
                        assistantMsg.ReasoningContent = string.Empty;
                        Logger.Info($"[Retry] 复用 retry fork 占位 assistant (idx={newAssistantIdx}), nodeId={lastNode.Id}");
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 内联编辑确认：接收用户在气泡中编辑后的新文本，处理重新发送。
        /// </summary>
        private async Task HandleEditConfirmAsync(int userMsgIndex, string newContent)
        {
            lock (_lock)
            {
                if (_isGenerating)
                {
                    _ = Task.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        StatusLabel.Text = LocalizationService.Instance["agent.status.busyConfirm"];
                    });
                    return;
                }
                if (_pendingEditMsgIndex < 0) return;
            }

            _pendingEditMsgIndex = -1;

            // 移除内联编辑区 UI
            try
            {
                if (ChatWebView.CoreWebView2 != null)
                {
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                        $"var el=document.getElementById('inline-edit-{userMsgIndex}');if(el)el.remove();" +
                        $"var eb=document.getElementById('edit-btn-{userMsgIndex}');if(eb)eb.style.display='';");
                }
            }
            catch { }

            await HandleEditResendAsync(userMsgIndex, newContent);
        }

        /// <summary>
        /// 内联编辑取消：恢复用户消息原样，清除编辑状态。
        /// </summary>
        private async Task HandleEditCancelAsync(int userMsgIndex)
        {
            _pendingEditMsgIndex = -1;

            // ── 清空输入框 ──
            InputTextBox.Text = string.Empty;

            // 恢复消息正文为原始内容
            string? originalText = null;
            lock (_lock)
            {
                if (userMsgIndex >= 0 && userMsgIndex < _messages.Count)
                {
                    var msg = _messages[userMsgIndex];
                    if (msg.Role == "user")
                        originalText = msg.Content;
                }
            }

            try
            {
                if (ChatWebView.CoreWebView2 != null)
                {
                    string restoreJs = ChatHtmlService.BuildRestoreMessageJs(userMsgIndex, originalText ?? string.Empty);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(restoreJs);
                }
            }
            catch { }

            StatusLabel.Text = LocalizationService.Instance["status.ready"];
        }

        #endregion
    }
}
