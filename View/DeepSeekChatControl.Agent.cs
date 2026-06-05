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
                        string prefix = entry.Role == "user" ? LocalizationService.Instance["chat.role.user"] : LocalizationService.Instance["chat.role.ai"];
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
        /// 恢复系统级上下文（system prompt / memory / skill / RAG）。
        /// 在 RebuildContextFromTree() 后调用，因为 Clear() 会清空这些字段。
        /// 确保 Agent（Plan/Edit/Explore）在编辑/重试后仍能获取完整的系统上下文。
        /// </summary>
        private async Task RestoreSystemContextAsync()
        {
            try
            {
                // ── 惰性解析：确保 _solutionPath 已就绪 ──
                if (_solutionPath == null)
                {
                    await ResolveSolutionPathAsync();
                }

                // ── 系统提示词 ──
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

                string workspaceRoot = _solutionPath ?? string.Empty;
                if (!string.IsNullOrEmpty(workspaceRoot))
                {
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

                // ── Skill 上下文 ──
                try
                {
                    if (_skillDiscoveryResult == null)
                        _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);
                    string skillContext = SkillService.Instance.GenerateSkillsDiscoveryContext(_skillDiscoveryResult);
                    _contextManager.SetSkillContext(string.IsNullOrWhiteSpace(skillContext) ? null : skillContext);
                }
                catch (Exception ex) { Logger.Warn($"[Skill] RestoreSystemContext 失败: {ex.Message}"); }

                // ── 冻结不可变前缀（v1.1.9 缓存优化）──
                //     将 system prompt + skill context 冻结为 messages[0]，
                //     确保整个会话期间前缀不变，DeepSeek V4 自动前缀缓存可持续命中。
                _contextManager.FreezeSystemPrompt();

                // ── 记忆上下文 ──
                try
                {
                    if (_memoryService != null)
                    {
                        string userMemory = _memoryService.GetMemoryContext(MemoryScope.User);
                        string repoMemory = _memoryService.GetMemoryContext(MemoryScope.Repo, solutionPath: _solutionPath);
                        var memoryContext = new StringBuilder();
                        if (!string.IsNullOrWhiteSpace(userMemory))
                            memoryContext.AppendLine(userMemory);
                        if (!string.IsNullOrWhiteSpace(repoMemory))
                            memoryContext.AppendLine(repoMemory);
                        string combined = memoryContext.ToString().Trim();
                        _contextManager.SetMemoryContext(string.IsNullOrWhiteSpace(combined) ? null : combined);
                    }
                }
                catch (Exception ex) { Logger.Warn($"[Memory] RestoreSystemContext 失败: {ex.Message}"); }

                Logger.Info("[SystemContext] 系统级上下文已恢复 (systemPrompt + skill + memory)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SystemContext] RestoreSystemContextAsync 异常: {ex.Message}");
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

                // ── 上下文感知执行：路由到 Edit 且存在待处理计划时，恢复 ActivePlan ──
                // 场景：用户输入"开始执行"后 OverrideRoutingForPlanContext 路由到 Edit，
                // 但 ActivePlan 未被恢复，EditAgent 会回退到单步执行。
                if (routing?.TargetAgent == AgentType.Edit
                    && !(routing?.NeedsPlanning == true))
                {
                    RestoreActivePlanIfNeeded(context);
                }

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

                    // ── 更新任务面板为完成状态（仅当步骤已实际执行过）──
                    bool anyStepExecuted = plan.Steps.Any(s => s.Status != AgentStepStatus.Pending);
                    if (plan.Steps.Count > 0 && anyStepExecuted)
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
                        ? LocalizationService.Instance["agent.taskCancelled"]
                        : plan.ChangedFiles.Count > 0
                            ? string.Format(LocalizationService.Instance["agent.taskCompletedFiles"], plan.ChangedFiles.Count)
                            : string.Format(LocalizationService.Instance["agent.planCompletedSteps"], plan.Steps.Count);

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
                    string errorContent = string.Format(LocalizationService.Instance["agent.executionFailed"], agentResult.ErrorMessage);
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
                return LocalizationService.Instance["status.analyzing"];

            // Plan Agent 完成 / 探索路由 / 发现完成（中英文）
            if (msg.StartsWith("计划创建完成") || msg.StartsWith("Plan created")
                || msg.StartsWith("探索路由") || msg.StartsWith("Explore routing")
                || msg.StartsWith("发现阶段完成") || msg.StartsWith("Discovery phase complete"))
                return msg;

            // 无计划 / 单步（中英文）
            if (msg.StartsWith("无计划") || msg.StartsWith("No plan"))
                return LocalizationService.Instance["status.singleStepTask"];

            // 编译结果（中英文）
            if (msg.Contains("编译通过") || msg.Contains("build passed") || msg.Contains("Build succeeded"))
                return LocalizationService.Instance["status.buildVerified"];
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
        /// 根据当前审批模式决定是否直接放行：
        /// - AllowAll：全部自动放行
        /// - BlockAll：全部拦截询问
        /// - SmartBlock：检测危险命令，仅拦截危险操作
        /// </summary>
        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null)
                {
                    Logger.Warn($"[Agent] CoreWebView2 未就绪，自动拒绝权限请求: {request.Title}");
                    _agentDispatcher?.RespondToPermission(request.RequestId, false);
                    return;
                }

                // ── 审批模式检查：全部放行 → 自动批准 ──
                var approvalMode = GetCurrentApprovalMode();
                if (approvalMode == Models.ApprovalMode.AllowAll)
                {
                    Logger.Info($"[Agent] 审批模式=全部放行，自动批准: {request.Title}");
                    _agentDispatcher?.RespondToPermission(request.RequestId, true);
                    return;
                }

                // ── 审批模式检查：智能拦截 → 仅危险命令需要审批 ──
                if (approvalMode == Models.ApprovalMode.SmartBlock)
                {
                    bool isDangerous = IsDangerousCommand(request.Command, request.ActionType);
                    if (!isDangerous)
                    {
                        Logger.Info($"[Agent] 审批模式=智能拦截，安全命令自动放行: {request.Title}");
                        _agentDispatcher?.RespondToPermission(request.RequestId, true);
                        return;
                    }
                    Logger.Info($"[Agent] 审批模式=智能拦截，检测到危险命令，需要审批: {request.Command}");
                }

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
    }
}
