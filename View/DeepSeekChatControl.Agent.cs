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
using System.Text;
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
                        if (text.Length > 500)
                            text = text.Substring(0, 500);
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
                        if (text.Length > 800)
                            text = text.Substring(0, 800) + "…";
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
                    CancellationToken = _currentStreamingCts?.Token ?? CancellationToken.None,
                    ReadFileAsync = async (path) =>
                    {
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
                        }
                    }
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, finalContent, string.Empty, isComplete: true);
                    // ── 最终渲染用 Markdown → HTML（执行过程作为独立 HTML 注入，不经过 Markdown）──
                    try
                    {
                        // 追加 Cache 命中率统计（使用累计值，非单轮）
                        string cacheFooter = string.Empty;
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                        string combinedFooter = thinkingDetailsHtml + cacheFooter;
                        string finalRenderJs = ChatHtmlService.BuildFinalRenderJs(
                            _agentStreamingMsgIndex, finalContent, string.Empty, combinedFooter);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalRenderJs);
                    }
                    catch { }

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
                        }
                    }
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, agentResult.Content, string.Empty, isComplete: true);
                    try
                    {
                        string cacheFooter = string.Empty;
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                        string frJs = ChatHtmlService.BuildFinalRenderJs(
                            _agentStreamingMsgIndex, agentResult.Content, string.Empty, cacheFooter);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(frJs);
                    }
                    catch { }
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
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, errorContent, string.Empty, isComplete: true);
                    try
                    {
                        string cacheFooter = string.Empty;
                        long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                        long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                        long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                        long totalComp = _apiService?.TotalCompletionTokens ?? 0;
                        if (totalHit + totalMiss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                totalHit, totalMiss, totalPrompt, totalComp, roundCount: 1);
                        }
                        string frJs = ChatHtmlService.BuildFinalRenderJs(
                            _agentStreamingMsgIndex, errorContent, string.Empty, cacheFooter);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(frJs);
                    }
                    catch { }
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

                        // ── 输出规划信息到思考气泡 ──
                        AppendAgentThinking($"📋 **规划完成**: {plan.Title}");
                        AppendAgentThinking($"   共 {plan.Steps.Count} 个步骤");
                        foreach (var s in plan.Steps)
                            AppendAgentThinking($"   {s.Index}. {s.Title}");
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
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, content, string.Empty, isComplete: false);
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

            // Emoji 前缀（locale-independent）
            if (msg.StartsWith("📄") || msg.StartsWith("📖") || msg.Contains("已读取") || msg.Contains("read file"))
                return msg;
            if (msg.StartsWith("✅") || msg.StartsWith("❌") || msg.StartsWith("⚠️"))
                return msg;
            if (msg.StartsWith("🔨") || msg.StartsWith("🔧"))
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
            if (_agentDispatcher == null || _pendingHandoff == null) return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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

                await TaskScheduler.Default;

                // ── 构建包含 _pendingHandoff 上下文的 AgentContext ──
                var context = new AgentContext
                {
                    SolutionPath = _solutionPath,
                    ContextManager = _contextManager,
                    IsPlanningMode = true,
                    ReadFileAsync = async (path) =>
                    {
                        if (File.Exists(path))
                            return await Task.Run(() => File.ReadAllText(path));
                        return null;
                    },
                };

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

                    string finalContent = agentResult.Content ?? string.Format(LocalizationService.Instance["agent.result.taskCompletedSuccess"], plan.Steps.Count(s => s.Status == AgentStepStatus.Completed), plan.Steps.Count);
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

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

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

                        await RunAgentWorkflowAsync(enrichedContent, fileContext, routing);
                        RecordAgentFileChanges(userMsgIndex);

                        RebuildMessagesHtml();
                        _browserInitialized = false;
                        UpdateBrowser();

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

                var requestMessages = await BuildRequestMessagesAsync();
                var apiService = _apiService!;

                var reasoningBuffer = new StringBuilder();
                var contentBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, null, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = LocalizationService.Instance["status.deepThinking"];

                        if (reasoningBuffer.Length - lastReasoningLength >= 80)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            await UpdateStreamingMessageAsync(newAssistantIdx,
                                contentBuffer.ToString(), reasoningBuffer.ToString(), isComplete: false);
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
                            await UpdateStreamingMessageAsync(newAssistantIdx,
                                contentBuffer.ToString(), reasoningBuffer.ToString(), isComplete: false);
                        }
                    }
                }

                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Retry] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                // ── 记录 Cache 命中率 ──
                LogCacheHitRate();

                // ── 构建 Cache 命中率统计卡片 HTML ──
                string cacheFooterHtml = string.Empty;
                {
                    var usage = _apiService?.LastUsage;
                    if (usage != null)
                    {
                        cacheFooterHtml = ChatHtmlService.BuildCacheHitFooterHtml(
                            usage.PromptCacheHitTokens, usage.PromptCacheMissTokens,
                            usage.PromptTokens, usage.CompletionTokens, roundCount: 1);
                    }
                }

                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    newAssistantIdx, contentBuffer.ToString(), reasoningBuffer.ToString(), cacheFooterHtml);
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

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
                    string finalJs = ChatHtmlService.BuildFinalRenderJs(
                        newAssistantIdx, assistantMsg.Content, assistantMsg.ReasoningContent);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Retry] API 出错", ex);
                if (assistantMsg != null)
                {
                    assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                    assistantMsg.IsStreaming = false;
                    await UpdateStreamingMessageAsync(newAssistantIdx,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                }
            }
            finally
            {
                if (assistantMsg != null)
                    assistantMsg.IsStreaming = false;
                lock (_lock) { _isGenerating = false; }
                StatusLabel.Text = string.Empty;
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
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
