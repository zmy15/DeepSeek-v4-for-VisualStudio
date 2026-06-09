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
    /// 重试/编辑/版本导航：消息重试、内联编辑、分支导航、文件变更回退。
    /// </summary>
    public partial class DeepSeekChatControl
    {
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
            if (_activeAgent == null || _agentFactory == null) return;

            // ── 单轮 Cache 统计快照：本次 Handoff 执行开始时的累计值 ──
            _apiService?.TakeCacheSnapshot();

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
                            $"var btns=document.querySelectorAll('.handoff-btn');btns.forEach(function(b){{b.disabled=true;b.textContent='{LocalizationService.Instance["chat.html.executing"]}';b.style.opacity='0.6';}});");
                    }
                    catch { }
                }

                // ── 重置思考内容，为 Edit 阶段准备新的实时气泡 ──
                lock (_lock) { _agentThinkingContent.Clear(); _streamingReasoning.Clear(); }
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

                // ── 设置实时推理流回调 ──
                var capturedRetryMsgIdx = _agentStreamingMsgIndex;
                context.OnThinkingChunk = (chunk) =>
                {
                    lock (_lock) { _streamingReasoning.Append(chunk); }
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (ChatWebView.CoreWebView2 == null || capturedRetryMsgIdx < 0) return;
                        try
                        {
                            string reasoning;
                            string content;
                            lock (_lock)
                            {
                                reasoning = _streamingReasoning.ToString();
                                var msg = capturedRetryMsgIdx < _messages.Count ? _messages[capturedRetryMsgIdx] : null;
                                content = msg?.Content ?? string.Empty;
                            }
                            BatchStreamingUpdate(capturedRetryMsgIdx, content, reasoning);
                        }
                        catch { }
                    });
                };

                // ── 恢复 Plan: 从持久化的 PlanJson 中重建 ActivePlan ──
                // PlanAgent 执行完毕后 _activePlan 已在 RunAgentWorkflowAsync 的
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
                    _activePlan = restoredPlan;
                    Logger.Info($"[AgentHandoff] 从 PlanJson 恢复了计划: {restoredPlan.Steps.Count} 个步骤 (PlanId={restoredPlan.PlanId}, IsFromPlanAgent={restoredPlan.IsFromPlanAgent})");

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

                    Logger.Info($"[AgentHandoff] 从 PlanJson 恢复了计划: {restoredPlan.Steps.Count} 个步骤 (PlanId={restoredPlan.PlanId}, Source={restoredPlan.Source})");
                }
                else
                {
                    Logger.Warn("[AgentHandoff] 未找到有效的 PlanJson，EditAgent 将回退到单步执行");
                }

                // ── 切换到目标 Agent 并绑定事件 ──
                var handoffTargetAgent = _agentFactory.GetAgent(_pendingHandoff.TargetAgent);
                SwitchActiveAgent(handoffTargetAgent, context);
                if (handoffTargetAgent is EditAgent targetEditAgent)
                    targetEditAgent.PlanUpdated += OnAgentPlanUpdated;

                AgentResult agentResult;
                try
                {
                    agentResult = await _activeAgent.ExecuteHandoffAsync(_pendingHandoff, context, _activePlan, _agentFactory);
                }
                finally
                {
                    if (handoffTargetAgent is EditAgent ea)
                        ea.PlanUpdated -= OnAgentPlanUpdated;
                }

                _pendingHandoff = null; // 消费后清空原始 Handoff（Plan→Edit）

                // ── AutoSend 链式处理：EditAgent 返回的 Handoff（如 Edit→Build、Edit→Ask）
                //    需要自动跟进执行。RunAgentWorkflowAsync 有同样的逻辑处理 Plan→Edit 链，
                //    此处补充 Handoff 场景下的多层 AutoSend 链。 ──
                int chainDepth = 0;
                const int maxChainDepth = 10;
                while (agentResult.Handoff != null && agentResult.Handoff.AutoSend)
                {
                    chainDepth++;
                    if (chainDepth > maxChainDepth)
                    {
                        Logger.Warn($"[AgentHandoff] AutoSend 链达到最大深度 {maxChainDepth}，强制终止");
                        break;
                    }
                    var nextHandoff = agentResult.Handoff;
                    Logger.Info($"[AgentHandoff] AutoSend 链式跟进: → {nextHandoff.TargetAgent} ({nextHandoff.Label})");

                    // ── 保存当前推理内容，防止链式 Handoff 覆盖 ──
                    string chainPreReasoning = agentResult.ReasoningContent ?? string.Empty;

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentSwitched"], nextHandoff.TargetAgent);

                    await TaskScheduler.Default;

                    // ── 切换并执行链式 Handoff ──
                    var chainAgent = _agentFactory.GetAgent(nextHandoff.TargetAgent);
                    SwitchActiveAgent(chainAgent, context);
                    try
                    {
                        agentResult = await _activeAgent.ExecuteHandoffAsync(nextHandoff, context, _activePlan, _agentFactory);
                    }
                    finally
                    {
                        // 事件已在 SwitchActiveAgent 中解绑旧 Agent
                    }

                    // ── 合并链式 Handoff 前后的推理内容 ──
                    if (!string.IsNullOrEmpty(chainPreReasoning))
                    {
                        agentResult.ReasoningContent = string.IsNullOrEmpty(agentResult.ReasoningContent)
                            ? chainPreReasoning
                            : chainPreReasoning + "\n\n" + agentResult.ReasoningContent;
                    }
                }

                // ── 链式处理完毕后，如有非自动 Handoff，保存供 UI 按钮触发 ──
                if (agentResult.Handoff != null && agentResult.Handoff.ShowContinueOn)
                {
                    _pendingHandoff = agentResult.Handoff;
                }

                // ── 清除所有消息中的 HandoffJson，避免后续消息误触发 Plan 上下文覆盖 ──
                // 此前仅清除 _agentStreamingMsgIndex（当前 Edit 响应消息），但 HandoffJson
                // 实际保存在 Plan Agent 的原始响应消息上。Edit Handoff 消费后必须清除全部
                // 残留的 HandoffJson，否则下一轮对话中 TryRestorePendingHandoffFromMessages()
                // 会重新恢复旧的 Handoff 并触发 OverrideRoutingForPlanContext 覆盖路由，
                // 导致 Plan 面板再次出现。
                //
                // 同时清除同一 PlanId 的旧 PlanJson（PlanAgent 原始响应的未完成版本），
                // 防止 RestoreActivePlanIfNeeded 在后续 @edit 调用时恢复已执行完毕的计划。
                string? completedPlanId = agentResult.Plan?.PlanId;
                lock (_lock)
                {
                    foreach (var msg in _messages)
                    {
                        if (!string.IsNullOrEmpty(msg.HandoffJson))
                        {
                            msg.HandoffJson = null;
                        }

                        // 清除与已完成计划同 PlanId 的旧 PlanJson（IsCompleted=false 的残留版本）
                        if (!string.IsNullOrEmpty(completedPlanId) && !string.IsNullOrEmpty(msg.PlanJson))
                        {
                            try
                            {
                                var oldPlan = JsonSerializer.Deserialize<AgentTaskPlan>(
                                    msg.PlanJson,
                                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                if (oldPlan != null && oldPlan.PlanId == completedPlanId && !oldPlan.IsCompleted)
                                {
                                    msg.PlanJson = null;
                                    Logger.Info($"[AgentHandoff] 清除残留的旧 PlanJson: PlanId={completedPlanId}");
                                }
                            }
                            catch { }
                        }
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
                            bool anyStepExecuted = plan.Steps.Any(s => s.Status != AgentStepStatus.Pending);
                            if (anyStepExecuted)
                            {
                                // 步骤已执行 → 更新面板为完成/进度状态
                                string completeJs = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                                await ChatWebView.CoreWebView2.ExecuteScriptAsync(completeJs);
                            }
                            else if (plan.Source != PlanSource.None)
                            {
                                // 新创建的计划（PlanAgent 产出）→ 创建任务面板
                                string createJs = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);
                                await ChatWebView.CoreWebView2.ExecuteScriptAsync(createJs);
                                lock (_lock) { _createdPlanIds.Add(plan.PlanId); }
                            }
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

                    // ── 追加 Cache 命中率统计（本次 Handoff 增量，非 Session 累计）──
                    string cacheFooter = string.Empty;
                    try
                    {
                        var delta = _apiService?.GetCacheDelta() ?? (0, 0, 0, 0);
                        if (delta.Hit + delta.Miss > 0)
                        {
                            cacheFooter = ChatHtmlService.BuildCacheHitFooterHtml(
                                delta.Hit, delta.Miss, delta.Prompt, delta.Completion, roundCount: 1);
                        }
                    }
                    catch { }

                    // ── 将 Cache 统计 HTML 追加到最终内容末尾，确保持久化后可恢复显示 ──
                    // 注意：cacheFooter 是原始 HTML，不应嵌入 msg.Content（Markdown 渲染会转义），
                    //       而是通过 PostStreamEnd 的 extraFooterHtml 参数发送。
                    string persistedContent = finalContent;

                    // ── 更新现有的流式思考气泡为最终内容 ──
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = persistedContent;
                            msg.ReasoningContent = _streamingReasoning.ToString();
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                            // ── 持久化任务计划 JSON，重启后可重建任务面板 ──
                            try { msg.PlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            // ── 持久化 Handoff JSON，会话切换后可重建"开始执行"按钮 ──
                            if (_pendingHandoff != null)
                            {
                                try { msg.HandoffJson = JsonSerializer.Serialize(_pendingHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            }
                            // ── 持久化缓存统计 HTML，重启后可恢复显示 ──
                            if (!string.IsNullOrEmpty(cacheFooter))
                                msg.CacheFooterHtml = cacheFooter;
                        }
                    }

                    // ── 强制刷新 DOM 显示最终结果 ──
                    string reasoningForRender;
                    lock (_lock) { reasoningForRender = _streamingReasoning.ToString(); }
                    BatchStreamingUpdate(_agentStreamingMsgIndex, persistedContent, reasoningForRender, isComplete: true);

                    // ── 发送最终渲染：extraFooter 中注入执行过程 HTML + 缓存统计（纯 HTML，不经过 Markdown 转义）──
                    string combinedFooter = thinkingDetailsHtml + cacheFooter;
                    PostStreamEnd(_agentStreamingMsgIndex, finalContent, reasoningForRender, combinedFooter);

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

                // ── 同步 Agent 响应到树和上下文管理器，持久化保存 ──
                // 修复 Plan→Edit 切换后变更总结和 Token 统计丢失的问题。
                // RunAgentWorkflowAsync 有相同的调用，ExecuteAgentHandoffAsync 此前缺失。
                await SyncAgentResponseToTreeAndContextAsync();

                // ── 刷新右下角余额/Token 显示 ──
                RefreshConsumptionDisplay();
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

            // ── 在 finally 清理完毕后，如有新 Handoff 则注入按钮 ──
            //    注意：finally 块会先移除旧的 handoff 按钮，新按钮在清理完后注入
            if (_pendingHandoff != null && _agentStreamingMsgIndex >= 0)
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ChatWebView.CoreWebView2 != null)
                    {
                        string targetAgentStr = _pendingHandoff.TargetAgent.ToString();
                        string handoffBtnJs = ChatHtmlService.BuildHandoffButtonJs(
                            _agentStreamingMsgIndex, targetAgentStr, _pendingHandoff.Label);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(handoffBtnJs);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 关闭任务面板：清除对应消息的 PlanJson，防止重启后重新显示已关闭的面板。
        /// 通过 planId 匹配消息中的 PlanJson。
        /// </summary>
        private void DismissTaskPanel(string planId)
        {
            int clearedCount = 0;
            lock (_lock)
            {
                var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    var msg = _messages[i];
                    if (string.IsNullOrEmpty(msg.PlanJson)) continue;

                    try
                    {
                        var plan = JsonSerializer.Deserialize<AgentTaskPlan>(msg.PlanJson, opts);
                        if (plan != null && plan.PlanId == planId)
                        {
                            msg.PlanJson = null;
                            clearedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Panel] DismissTaskPanel 反序列化 PlanJson 失败: {ex.Message}");
                    }
                }
                if (clearedCount > 0)
                    _createdPlanIds.Remove(planId);
            }
            if (clearedCount > 0)
                Logger.Info($"[Panel] 任务面板已关闭并清除持久化数据: PlanId={planId}, 清除 {clearedCount} 条");
            else
                Logger.Info($"[Panel] DismissTaskPanel: 未找到 PlanId={planId} 的消息");
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

                // ── 恢复系统级上下文（Clear() 会清空 system prompt / memory / skill）──
                await RestoreSystemContextAsync();

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

                // ── 恢复系统级上下文（Clear() 会清空 system prompt / memory / skill）──
                await RestoreSystemContextAsync();

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

                // ── 恢复系统级上下文（Clear() 会清空 system prompt / memory / skill）──
                await RestoreSystemContextAsync();

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

                if (_activeAgent != null && _agentFactory != null && !string.IsNullOrEmpty(userContent) && !userContent.StartsWith("/"))
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
                        routing = new AgentRoutingResult { TargetAgent = AgentType.Ask, Confidence = "high", Reason = "重试默认 AskAgent", NeedsPlanning = false };
                    }

                    bool needsAgent = routing.TargetAgent != AgentType.Ask
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

                // ── 构建 Cache 命中率统计卡片 HTML（本次重试增量）──
                string cacheFooterHtml = string.Empty;
                {
                    var delta = _apiService?.GetCacheDelta() ?? (0, 0, 0, 0);
                    if (delta.Hit + delta.Miss > 0)
                    {
                        cacheFooterHtml = ChatHtmlService.BuildCacheHitFooterHtml(
                            delta.Hit, delta.Miss, delta.Prompt, delta.Completion, roundCount: 1);
                        // ── 持久化到 ChatMessage，重启后可恢复显示 ──
                        lock (_lock) { if (newAssistantIdx >= 0 && newAssistantIdx < _messages.Count) _messages[newAssistantIdx].CacheFooterHtml = cacheFooterHtml; }
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
            catch (ObjectDisposedException) when (retryCts?.IsCancellationRequested == true)
            {
                // 用户点击停止导致 SslStream 释放，兜底处理为停止
                Logger.Info("[Retry] 用户停止生成 (ObjectDisposed)");
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

            // 移除内联编辑区 UI（以编辑按钮为锚点，与 BuildInlineEditJs 保持一致）
            try
            {
                if (ChatWebView.CoreWebView2 != null)
                {
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                        $"(function(){{" +
                        $"var eb=document.getElementById('edit-btn-{userMsgIndex}');if(!eb)return;" +
                        $"var bubble=eb.closest('.msg-bubble');if(!bubble)return;" +
                        $"var editArea=bubble.querySelector('.inline-edit-area');if(editArea)editArea.remove();" +
                        $"eb.style.display='';" +
                        $"}})();");
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
