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
        /// 确保系统提示词已初始化（幂等）。
        /// 新会话首次 Agent 调用时 _fixedSystemPrompt 尚未冻结，此处补做初始化。
        /// Session 恢复和 Retry 流程中 RestoreSystemContextAsync 已处理，此方法直接返回。
        /// </summary>
        private async Task EnsureSystemPromptInitializedAsync()
        {
            if (!string.IsNullOrEmpty(_contextManager?.GetFixedSystemPrompt()))
                return;

            await RestoreSystemContextAsync();
        }

        /// <summary>
        /// 共享系统上下文初始化：组装 system prompt、发现 skill、冻结前缀、注入记忆。
        /// 新会话首次进入 Agent 工作流和会话恢复/重试共用此逻辑。
        /// RAG 和搜索上下文属于易变上下文，需要在具体发送路径中单独更新。
        /// </summary>
        private async Task InitializeSystemContextAsync()
        {
            if (_solutionPath == null)
                await ResolveSolutionPathAsync();

            string systemPrompt = _options?.GetEffectiveSystemPrompt() ?? string.Empty;
            if (_agentFactory != null)
                systemPrompt += "\n\n" + AiPrompts.MultiAgentSystemPromptFragment;
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
                systemPrompt += string.Format(LocalizationService.Instance["system.workspaceInfo"], workspaceRoot);
            }

            _contextManager.SetSystemPrompt(string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt);

            try
            {
                if (_skillDiscoveryResult == null)
                    _skillDiscoveryResult = await SkillService.Instance.DiscoverSkillsAsync(_solutionPath);

                // 技能发现结果仅用于 /skill 解析和 UI 列表；不在系统提示中注入
                // available_skills 清单，避免未显式调用技能时污染上下文。
                _contextManager.SetSkillContext(null);

                // 注入始终激活的技能完整指令（每次对话均加载）
                string alwaysInjectContext = SkillService.Instance.GenerateAlwaysInjectSkillsContext(_skillDiscoveryResult);
                _contextManager.SetAlwaysInjectSkillsContext(string.IsNullOrWhiteSpace(alwaysInjectContext) ? null : alwaysInjectContext);

                if (_skillDiscoveryResult != null)
                {
                    var skillNames = string.Join(", ", _skillDiscoveryResult.AutoLoadableSkills.ConvertAll(s => s.Name));
                    Logger.Info($"[Skill] 发现: {_skillDiscoveryResult.AutoLoadableSkills.Count} 个可选(不注入清单) + {_skillDiscoveryResult.AlwaysInjectSkills.Count} 个始终激活 → 可选: {skillNames}");
                    if (_skillDiscoveryResult.AlwaysInjectSkills.Count > 0)
                    {
                        var alwaysNames = string.Join(", ", _skillDiscoveryResult.AlwaysInjectSkills.ConvertAll(s => s.Name));
                        Logger.Info($"[Skill] 始终激活技能: {alwaysNames}");
                    }
                }
            }
            catch (Exception ex) { Logger.Warn($"[Skill] 上下文初始化失败: {ex.Message}"); }

            _contextManager.FreezeSystemPrompt();

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
            catch (Exception ex) { Logger.Warn($"[Memory] 上下文初始化失败: {ex.Message}"); }
        }

        /// <summary>
        /// 恢复系统级上下文（system prompt / memory / skill）。
        /// 在 RebuildContextFromTree() 后调用，因为 Clear() 会清空这些字段。
        /// </summary>
        private async Task RestoreSystemContextAsync()
        {
            try
            {
                await InitializeSystemContextAsync();
                Logger.Info("[SystemContext] 系统级上下文已恢复 (systemPrompt + skill + memory)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SystemContext] RestoreSystemContextAsync 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset the active agent back to Ask for a new conversation.
        /// </summary>
        private void ResetActiveAgentToAsk()
        {
            if (_agentFactory == null) return;

            if (_activeAgent != null && _activeAgent.Definition.Type == AgentType.Ask)
            {
                UpdateAgentModeBadge();
                return;
            }

            if (_activeAgent != null)
                UnbindAgentEvents(_activeAgent);

            _activeAgent = _agentFactory.AskAgent;
            _activeAgent.PermissionRequested += OnAgentPermissionRequested;
            _activeAgent.QuestionsRequested += OnAgentQuestionsRequested;
            UpdateAgentModeBadge();
            Logger.Info("[Session] active agent reset to AskAgent");
        }

        /// <summary>
        /// Bind the active agent's events to the UI handlers.
        /// </summary>
        private void BindAgentEvents(BaseAgent agent)
        {
            if (agent == null) return;
            agent.LogEntryAdded += OnAgentLogEntryAdded;
            agent.FileChangeNotified += OnAgentFileChangeNotified;
            agent.PermissionRequested += OnAgentPermissionRequested;
            agent.QuestionsRequested += OnAgentQuestionsRequested;
            Logger.Info($"[Agent] 事件已绑定 → {agent.Definition.Type} (QuestionsRequested 订阅数: {agent.QuestionsRequestedHandlerCount})");
        }

        /// <summary>
        /// 解绑活跃 Agent 的事件。
        /// </summary>
        private void UnbindAgentEvents(BaseAgent agent)
        {
            if (agent == null) return;
            agent.LogEntryAdded -= OnAgentLogEntryAdded;
            agent.FileChangeNotified -= OnAgentFileChangeNotified;
            agent.PermissionRequested -= OnAgentPermissionRequested;
            agent.QuestionsRequested -= OnAgentQuestionsRequested;
        }

        /// <summary>
        /// 切换到新的活跃 Agent：解绑旧 Agent 事件，绑定新 Agent 事件，更新上下文。
        /// </summary>
        private void SwitchActiveAgent(BaseAgent newAgent, AgentContext context)
        {
            var oldType = _activeAgent?.Definition.Type.ToString() ?? "null";
            if (_activeAgent != null)
                UnbindAgentEvents(_activeAgent);

            _activeAgent = newAgent;
            _activeAgent.Context = context;

            BindAgentEvents(_activeAgent);
            Logger.Info($"[Agent] 切换活跃 Agent: {oldType} → {_activeAgent.Definition.Type} (QuestionsRequested 订阅数: {_activeAgent.QuestionsRequestedHandlerCount})");
        }

        /// <summary>
        /// Agent 工作流主入口：分解任务 → 显示步骤计划 → 逐步执行 → 显示变更摘要。
        /// 注意：此方法在后台线程中调用，访问 UI 前必须切换到主线程。
        /// </summary>
        /// <summary>IDE 实时态追踪器（P1-A，惰性创建；仅 UI 线程使用）</summary>
        private Services.IdeContext.IdeContextTracker? _ideContextTracker;

        /// <summary>
        /// 构建会话开始时的上下文构成快照（P2 Context Debugger 数据面）。
        /// 随遥测 JSON 导出，用于失败复盘时回答"模型当时看到了什么、为什么"。
        /// </summary>
        private string? BuildContextDebugJson()
        {
            try
            {
                var stats = _contextManager.GetStats();
                var ide = _ideContextTracker?.Current;

                var payload = new
                {
                    tokens = new
                    {
                        estimated = stats.EstimatedTokens,
                        budget = stats.TokenBudget,
                        percent = Math.Round(stats.UsagePercent, 1),
                    },
                    turns = stats.TurnCount,
                    messages = stats.MessageCount,
                    toolCalls = stats.ToolCallCount,
                    compressedTurns = stats.CompressedTurns,
                    injected = new
                    {
                        ide = ide != null,
                        ideChars = _contextManager.IdeContextChars,
                        search = _contextManager.HasSearchContext,
                        rag = _contextManager.HasRagContext,
                    },
                    workingSet = _contextManager.GetWorkingSetTopPaths(6),
                    ideSnapshot = ide == null ? null : new
                    {
                        file = ide.FilePath,
                        hasSelection = ide.HasSelection,
                        selectionStartLine = ide.SelectionStartLine,
                        selectionEndLine = ide.SelectionEndLine,
                        cursorLine = ide.CursorLine,
                        symbol = ide.SymbolAtCursor,
                        errors = ide.ErrorCount,
                        warnings = ide.WarningCount,
                    },
                    capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                return JsonSerializer.Serialize(payload,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ContextDebug] 构建快照失败: {ex.Message}");
                return null;
            }
        }

        private async Task RunAgentWorkflowAsync(
            string userText,
            string fileContext = "",
            AgentRoutingResult? routing = null,
            List<ChatContentPart>? visionContent = null,
            string? currentUserContent = null)
        {
            if (_activeAgent == null || _agentFactory == null) return;

            // ── 单轮 Cache 统计快照：本次问答开始时的累计值 ──
            _apiService?.TakeCacheSnapshot();

            // ── P0 Telemetry：会话指标采集器（设置开关控制，创建失败静默降级）──
            Services.Telemetry.AgentMetricsCollector? telemetry = null;
            if (_options?.EnableTelemetryExport != false)
            {
                try { telemetry = new Services.Telemetry.AgentMetricsCollector(); }
                catch (Exception tex) { Logger.Warn($"[Telemetry] 采集器创建失败: {tex.Message}"); }
            }

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // Start each ordinary user turn back at Ask instead of keeping the
                // Plan/Edit agent left over from the previous turn's workflow.
                if (routing == null || (!routing.IsExplicit && routing.TargetAgent == AgentType.Ask))
                {
                    ResetActiveAgentToAsk();
                }

                StatusLabel.Text = LocalizationService.Instance["agent.status.analyzing"];

                // ── P1-A IDE Context：捕获编辑器实时态并注入 volatile 块（设置开关控制）──
                //    在 UI 线程上单次捕获；Agent 每轮只读快照，不重复扫描 VS。
                try
                {
                    _contextManager.SetSolutionPath(_solutionPath);
                    if (_options?.EnableIdeContextInjection == true)
                    {
                        _ideContextTracker ??= new Services.IdeContext.IdeContextTracker();
                        _ideContextTracker.CaptureFromActiveView();
                        _contextManager.SetIdeContext(
                            _ideContextTracker.Current?.ToPromptBlock(_solutionPath));
                        if (_ideContextTracker.Current != null)
                            Logger.Info($"[IdeContext] 已注入: {_ideContextTracker.Current.FilePath} " +
                                        $"(选区={_ideContextTracker.Current.HasSelection}, " +
                                        $"诊断={_ideContextTracker.Current.Diagnostics.Count})");
                    }
                    else
                    {
                        _contextManager.SetIdeContext(null);
                    }
                }
                catch (Exception ideEx)
                {
                    Logger.Warn($"[IdeContext] 注入失败: {ideEx.Message}");
                }

                // ── P2 Context Debugger：一行日志 + 聊天面板推送（设置开关复用 ShowContextStats）──
                if (_options?.ShowContextStats == true)
                {
                    try
                    {
                        var dbgStats = _contextManager.GetStats();
                        var ideCur = _ideContextTracker?.Current;
                        Logger.Info($"[ContextDebug] IDE={(ideCur != null ? ideCur.FilePath : "off")}" +
                            $"(sel={ideCur?.HasSelection == true}, diag={ideCur?.ErrorCount ?? 0}e/{ideCur?.WarningCount ?? 0}w) " +
                            $"Search={_contextManager.HasSearchContext} RAG={_contextManager.HasRagContext} " +
                            $"WS={_contextManager.GetWorkingSetTopPaths(6).Count} " +
                            $"Tokens={dbgStats.EstimatedTokens:N0}/{dbgStats.TokenBudget:N0}");

                        // 推送到聊天窗口右上角抽屉（JS 懒创建，折叠展示）
                        string? ctxJson = BuildContextDebugJson();
                        if (!string.IsNullOrEmpty(ctxJson))
                            ChatWebView.CoreWebView2?.PostWebMessageAsString(
                                "{\"type\":\"contextDebug\",\"d\":" + ctxJson + "}");
                    }
                    catch { }
                }

                // ── 清理上一轮 Agent 执行的追踪状态 ──
                lock (_lock)
                {
                    _createdPlanIds.Clear();
                    _pendingLogEntries.Clear();
                    _agentThinkingContent.Clear();
                    _streamingReasoning.Clear();
                }
                _agentStreamingMsgIndex = -1;
                _lastReportedStepIndex = 0;
                _lastReportedStepStatus = string.Empty;
                _pendingHandoff = null;

                var context = new AgentContext
                {
                    SolutionPath = _solutionPath,
                    FileContext = fileContext,
                    VisionContent = visionContent,
                    CurrentUserContent = currentUserContent,
                    ConversationHistory = _contextManager.GetConversationHistory(),
                    ContextManager = _contextManager,
                    IsPlanningMode = routing?.NeedsPlanning == true || routing?.TargetAgent == AgentType.Plan,
                    PreClassifiedTaskSize = routing?.TaskSize ?? TaskSize.Small,
                    IsExplicitRoute = routing?.IsExplicit == true,
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
                        AgentType = routing?.TargetAgent ?? _activeAgent?.Definition.Type ?? AgentType.Ask,
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

                // ── 设置实时推理流回调：每个 thinking chunk 立即推送到 WebView2 思考面板 ──
                var capturedMsgIdx = _agentStreamingMsgIndex;
                // ── P3: 流式增量累积的节流局部状态。与 BatchStreamingUpdate 内部 60ms 节流对齐，
                //    将"每 chunk 一次 O(n) 全量拷贝/ToString/线程切换"降为每 60ms 一次，
                //    长输出时整体字符搬运从 O(n²) 降为近似 O(n)；最终完整性由收尾的
                //    FinalizeAgentMessage(isComplete) 全量推送兜底 ──
                const long StreamFlushSyncIntervalTicks = 60 * TimeSpan.TicksPerMillisecond;
                long lastContentFlushTicks = DateTime.UtcNow.Ticks;
                long lastThinkingFlushTicks = DateTime.UtcNow.Ticks;
                var streamingContentSb = new StringBuilder();

                context.OnThinkingChunk = (chunk) =>
                {
                    // 锁内增量累积（StringBuilder.Append 均摊 O(1)）；按 60ms 节流后才全量读取推送，
                    // 避免长思考输出时每个 chunk 都执行 O(n) ToString 与跨线程切换
                    bool syncDue = false;
                    lock (_lock)
                    {
                        _streamingReasoning.Append(chunk);
                        long nowTicks = DateTime.UtcNow.Ticks;
                        syncDue = nowTicks - lastThinkingFlushTicks >= StreamFlushSyncIntervalTicks;
                        if (!syncDue) return;
                        lastThinkingFlushTicks = nowTicks;
                    }

                    string reasoning;
                    string content;
                    lock (_lock)
                    {
                        reasoning = _streamingReasoning.ToString();
                        var msg = capturedMsgIdx < _messages.Count ? _messages[capturedMsgIdx] : null;
                        content = msg?.Content ?? string.Empty;
                    }
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (ChatWebView.CoreWebView2 == null || capturedMsgIdx < 0) return;
                        try
                        {
                            BatchStreamingUpdate(capturedMsgIdx, content, reasoning);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Agent] OnThinkingChunk BatchStreamingUpdate 异常: {ex.Message}");
                        }
                    });
                };

                // ── 设置实时内容流回调：每个 content chunk 立即推送到 WebView2 消息正文 ──
                context.OnContentChunk = (chunk) =>
                {
                    // 锁内单次临界区：StringBuilder 增量累积（均摊 O(1)），替代逐 chunk 的
                    // string += 全量复制；按 60ms 节流把最新完整内容同步到消息并触发批处理推送
                    bool syncDue = false;
                    lock (_lock)
                    {
                        if (capturedMsgIdx < 0 || capturedMsgIdx >= _messages.Count) return;
                        streamingContentSb.Append(chunk);

                        long nowTicks = DateTime.UtcNow.Ticks;
                        syncDue = nowTicks - lastContentFlushTicks >= StreamFlushSyncIntervalTicks;
                        if (!syncDue) return;

                        lastContentFlushTicks = nowTicks;
                        _messages[capturedMsgIdx].Content = streamingContentSb.ToString();
                    }
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (ChatWebView.CoreWebView2 == null || capturedMsgIdx < 0) return;
                        try
                        {
                            string reasoning;
                            string content;
                            lock (_lock)
                            {
                                reasoning = _streamingReasoning.ToString();
                                content = capturedMsgIdx >= 0 && capturedMsgIdx < _messages.Count
                                    ? (_messages[capturedMsgIdx]?.Content ?? "") : "";
                            }
                            BatchStreamingUpdate(capturedMsgIdx, content, reasoning);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Agent] OnContentChunk BatchStreamingUpdate 异常: {ex.Message}");
                        }
                    });
                };

                // ── 显式路由 Agent 切换：@agent 时切换到目标 Agent ──
                //    修复：此前仅当 targetAgent != Ask 时才切换，导致 @ask 无法从
                //         PlanAgent 切回 AskAgent。现在任意 @agent 均正确切换。
                if (routing != null && routing.IsExplicit
                    && _activeAgent.Definition.Type != routing.TargetAgent)
                {
                    var targetAgent = _agentFactory.GetAgent(routing.TargetAgent);
                    SwitchActiveAgent(targetAgent, context);
                }
                else
                {
                    // ── 绑定事件到活跃 Agent，并同步 Context（显式路由拦截依赖此值）──
                    _activeAgent.Context = context;
                    BindAgentEvents(_activeAgent);
                }
                if (_agentFactory.EditAgent is EditAgent editAgent)
                    editAgent.PlanUpdated += OnAgentPlanUpdated;

                // ── 显式路由时注入系统消息：告知 AI 用户已显式指定 Agent，不要移交 ──
                if (routing?.IsExplicit == true)
                {
                    string doNotHandoffMsg = string.Format(
                        LocalizationService.Instance["agent.explicitRoute.doNotHandoff"],
                        routing.TargetAgent);
                    _contextManager.AddCustomMessage("system", doNotHandoffMsg);
                    Logger.Info($"[Agent] 显式路由 @{routing.TargetAgent}: 已注入禁止移交指令");
                }

                // ── 联网搜索：在 Agent 执行前进行搜索，注入上下文和结果卡片 ──
                List<WebSearchResult>? searchResults = null;
                string searchContext = string.Empty;
                if (_webSearchEngine != "Off" && _webSearchService != null && !string.IsNullOrWhiteSpace(userText))
                {
                    try
                    {
                        bool isBaidu = _webSearchEngine == "Baidu";
                        string providerName = _webSearchEngine switch
                        {
                            "Baidu" => "百度搜索",
                            "Bing" => "Bing",
                            _ => "DuckDuckGo",
                        };
                        var searchCt = CancellationToken.None; // 搜索阶段不依赖外部取消令牌
                        var L = LocalizationService.Instance;

                        // Step 1: AI 优化搜索关键词
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        StatusLabel.Text = L["status.search.optimizing"];
                        await TaskScheduler.Default;

                        var optimization = await OptimizeSearchQueryAsync(userText, searchCt, isBaidu);
                        string searchQuery = optimization?.SearchQuery ?? userText;
                        if (optimization?.NeedSearch == false)
                        {
                            Logger.Info("[Search] AI 判断无需搜索，跳过");
                        }
                        else
                        {
                            // Step 2: 执行搜索
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            StatusLabel.Text = string.Format(L["status.search.searching"], providerName);
                            await TaskScheduler.Default;

                            searchResults = await _webSearchService.SearchAsync(searchQuery, searchCt,
                                searchRecency: optimization?.SearchRecency);

                            if (searchResults.Count > 0)
                            {
                                // Step 3: 抓取网页内容增强结果
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                StatusLabel.Text = L["status.search.enriching"];
                                await TaskScheduler.Default;

                                await EnrichSearchContextAsync(searchResults, searchCt);

                                // Step 4: 构建搜索上下文注入 AI
                                searchContext = BuildSearchContextFromResults(searchResults);
                                _contextManager.SetSearchContext(searchContext);
                                Logger.Info($"[Search] {providerName} 返回 {searchResults.Count} 条结果，已注入上下文 ({searchContext.Length} 字符)");

                                // Step 5: 在 UI 中展示搜索结果卡片
                                string searchCardJs = ChatHtmlService.BuildSearchResultsInjectionJs(
                                    _agentStreamingMsgIndex, searchResults, providerName);
                                await PostSearchResultsJsAsync(searchCardJs);
                            }
                            else
                            {
                                Logger.Info($"[Search] {providerName} 返回 0 条结果");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Search] 搜索流程异常: {ex.Message}");
                        _contextManager.SetSearchContext(null);
                    }
                }

                AgentResult agentResult;
                try
                {
                    // ── P0 Telemetry：路由确定后挂载采集器并标记会话开始（附上下文构成快照）──
                    if (telemetry != null)
                    {
                        context.Metrics = telemetry;
                        telemetry.BeginSession(_options?.SelectedModel,
                            _activeAgent.Definition.Type.ToString(), userText,
                            BuildContextDebugJson());
                    }

                    agentResult = await _activeAgent.ExecuteAsync(userText, context);
                }
                finally
                {
                    UnbindAgentEvents(_activeAgent);
                    if (_agentFactory.EditAgent is EditAgent ea)
                        ea.PlanUpdated -= OnAgentPlanUpdated;
                }

                // ── Handoff 链式执行：自动跟进 AutoSend 移交，最多 10 层防止无限循环 ──
                // ShowContinueOn 移交保存到 _pendingHandoff，稍后渲染时注入按钮等待用户点击。
                const int maxHandoffChainDepth = 10;
                int handoffChainDepth = 0;
                bool handoffChainCompleted = false;
                string mergedReasoning = agentResult.ReasoningContent ?? string.Empty;

                while (agentResult.Handoff != null && !handoffChainCompleted)
                {
                    handoffChainDepth++;
                    if (agentResult.Handoff.ForwardedMessages == null)
                    {
                        agentResult.Handoff.ForwardedMessages = _activeAgent?.SnapshotHandoffCacheMessages();
                    }

                    if (handoffChainDepth > maxHandoffChainDepth)
                    {
                        Logger.Warn($"[Agent] Handoff 链达到最大深度 {maxHandoffChainDepth}，强制终止");
                        break;
                    }

                    if (agentResult.Handoff.ShowContinueOn)
                    {
                        // ── 需要用户确认：保存引用，注入按钮，中断链 ──
                        _pendingHandoff = agentResult.Handoff;
                        Logger.Info($"[Agent] Handoff 链中断 (ShowContinueOn)，等待用户点击 → {agentResult.Handoff.TargetAgent}");
                        handoffChainCompleted = true;
                        break;
                    }

                    if (!agentResult.Handoff.AutoSend)
                    {
                        // ── 既非 AutoSend 也非 ShowContinueOn：静默终止 ──
                        break;
                    }

                    // ── AutoSend：自动链式移交 ──
                    Logger.Info($"[Agent] Handoff 链 #{handoffChainDepth}: → {agentResult.Handoff.TargetAgent} ({agentResult.Handoff.Label})");

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentSwitched"], agentResult.Handoff.TargetAgent);
                    await TaskScheduler.Default;

                    var nextAgent = _agentFactory.GetAgent(agentResult.Handoff.TargetAgent);
                    SwitchActiveAgent(nextAgent, context);
                    context.Metrics?.SwitchAgent(agentResult.Handoff.TargetAgent.ToString());
                    if (_agentFactory.EditAgent is EditAgent ea2)
                        ea2.PlanUpdated += OnAgentPlanUpdated;
                    try
                    {
                        agentResult = await _activeAgent.ExecuteHandoffAsync(agentResult.Handoff, context, _activePlan, _agentFactory);
                    }
                    finally
                    {
                        if (_agentFactory.EditAgent is EditAgent ea3)
                            ea3.PlanUpdated -= OnAgentPlanUpdated;
                    }

                    // ── 合并推理内容 ──
                    if (!string.IsNullOrEmpty(agentResult.ReasoningContent))
                        mergedReasoning = mergedReasoning + "\n\n" + agentResult.ReasoningContent;
                }

                agentResult.ReasoningContent = mergedReasoning;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (agentResult.Success && agentResult.Plan != null)
                {
                    var plan = agentResult.Plan;

                    // ── 创建或更新任务面板（仅本分支独有逻辑）──
                    bool anyStepExecuted = plan.Steps.Any(s => s.Status != AgentStepStatus.Pending);
                    if (plan.Steps.Count > 0)
                    {
                        try
                        {
                            if (anyStepExecuted)
                            {
                                // 步骤已执行 → 更新面板为完成/进度状态
                                string completeJs = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                                await ChatWebView.CoreWebView2.ExecuteScriptAsync(completeJs);
                            }
                            else if (plan.Source != PlanSource.None)
                            {
                                // 新创建的计划（PlanAgent 或 EditAgent 产出，所有步骤待执行）→ 创建任务面板
                                string createJs = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);
                                await ChatWebView.CoreWebView2.ExecuteScriptAsync(createJs);
                                lock (_lock) { _createdPlanIds.Add(plan.PlanId); }
                                Logger.Info($"[Agent] 任务面板已创建: PlanId={plan.PlanId}, Source={plan.Source}, Steps={plan.Steps.Count}");
                            }
                            else
                            {
                                // 无计划来源也无已执行步骤 → 不创建面板，但记录日志
                                Logger.Info($"[Agent] 跳过面板创建: Source={plan.Source}, anyStepExecuted={anyStepExecuted}, Steps={plan.Steps.Count}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Agent] 任务面板创建失败: {ex.Message}", ex);
                        }
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

                    // ── 持久化任务计划 / Handoff JSON（重启后可重建任务面板与"开始执行"按钮）──
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            try { msg.PlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            if (_pendingHandoff != null)
                            {
                                try { msg.HandoffJson = JsonSerializer.Serialize(_pendingHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            }
                        }
                    }

                    // ── 收尾（共用 helper）：Cache footer + 最终化消息推送（含执行过程 HTML）──
                    string reasoningForRender;
                    lock (_lock) { reasoningForRender = _streamingReasoning.ToString(); }
                    string cacheFooter = BuildCacheFooterAndPersist(_agentStreamingMsgIndex);
                    FinalizeAgentMessage(_agentStreamingMsgIndex, finalContent, reasoningForRender, cacheFooter, thinkingDetailsHtml);

                    StatusLabel.Text = plan.IsCancelled
                        ? LocalizationService.Instance["agent.taskCancelled"]
                        : plan.ChangedFiles.Count > 0
                            ? string.Format(LocalizationService.Instance["agent.taskCompletedFiles"], plan.ChangedFiles.Count)
                            : string.Format(LocalizationService.Instance["agent.planCompletedSteps"], plan.Steps.Count);

                    // ── P0 Telemetry：计划执行完成（取消视为 Cancelled）──
                    if (plan.IsCancelled || context.CancellationToken.IsCancellationRequested)
                        telemetry?.CompleteCancelled();
                    else
                        telemetry?.CompleteSuccess();

                    // ── 如果有待处理的 Handoff（如 Plan→Edit），注入"开始实现"按钮 ──
                    await InjectPendingHandoffButtonAsync();

                    if (plan.ChangedFiles.Count > 0)
                    {
                        _pendingAgentFileChanges = new List<FileChangeSummary>(plan.ChangedFiles);
                    }
                }
                else if (agentResult.Success && !string.IsNullOrWhiteSpace(agentResult.Content))
                {
                    // ── 持久化 Handoff JSON，会话切换后可重建"开始执行"按钮 ──
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            if (_pendingHandoff != null)
                            {
                                try { _messages[_agentStreamingMsgIndex].HandoffJson = JsonSerializer.Serialize(_pendingHandoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); } catch { }
                            }
                        }
                    }

                    // ── 收尾（共用 helper）：Cache footer + 最终化消息推送 ──
                    string reasoningForRender;
                    lock (_lock) { reasoningForRender = _streamingReasoning.ToString(); }
                    string cacheFooter = BuildCacheFooterAndPersist(_agentStreamingMsgIndex);
                    FinalizeAgentMessage(_agentStreamingMsgIndex, agentResult.Content, reasoningForRender, cacheFooter);
                    StatusLabel.Text = LocalizationService.Instance["status.ready"];

                    // ── P0 Telemetry：问答完成（流中取消视为 Cancelled）──
                    if (context.CancellationToken.IsCancellationRequested)
                        telemetry?.CompleteCancelled();
                    else
                        telemetry?.CompleteSuccess();

                    // ── 如果有待处理的 Handoff，注入按钮 ──
                    await InjectPendingHandoffButtonAsync();
                }
                else if (!agentResult.Success)
                {
                    string errorContent = string.Format(LocalizationService.Instance["agent.executionFailed"], agentResult.ErrorMessage);

                    // ── 收尾（共用 helper）：Cache footer + 最终化错误消息推送 ──
                    string cacheFooter = BuildCacheFooterAndPersist(_agentStreamingMsgIndex);
                    FinalizeAgentMessage(_agentStreamingMsgIndex, errorContent, string.Empty, cacheFooter);
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentError"], agentResult.ErrorMessage);

                    // ── P0 Telemetry：会话失败（取消 → Cancelled；其余 category 留待人工标注 Model/Context/Host）──
                    if (context.CancellationToken.IsCancellationRequested)
                        telemetry?.CompleteCancelled();
                    else
                        telemetry?.CompleteFailure(AgentFailureCategory.None, agentResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AgentFlow] 工作流异常: {ex.Message}", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.agentError"], ex.Message);

                // ── P0 Telemetry：未捕获异常归类为 System 故障 ──
                try { telemetry?.CompleteFailure(AgentFailureCategory.System, ex.ToString()); } catch { }
            }
            finally
            {
                _activePlan = null;

                // ── P1-A：会话结束后清除 IDE 快照，避免过期上下文泄漏到非 Agent 的聊天轮次 ──
                try { _contextManager.SetIdeContext(null); }
                catch { }

                // ── 追踪器内部快照同步清空（含未截断的原始 SelectionText，避免跨会话常驻内存）──
                try { _ideContextTracker?.Clear(); }
                catch { }
            }

            // ── 将 Agent 响应同步到树和上下文管理器（修复上下文丢失问题）──
            try
            {
                await SyncAgentResponseToTreeAndContextAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"[AgentFlow] SyncAgentResponseToTreeAndContextAsync 失败: {ex.Message}", ex);
            }

            // ── AI 自动生成会话标题（Agent 工作流完成后触发，独立 try 确保同步失败也不影响标题生成）──
            try
            {
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
                            // 最终兜底：直接使用用户消息作为标题
                            if (_activeSession != null && _activeSession.Title == LocalizationService.Instance["session.new"])
                            {
                                FallbackAutoTitle(capturedFirstUserMsg);
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AI标题] 标题生成初始化失败: {ex.Message}", ex);
            }

            // ── 记录 Cache 命中率 ──
            LogCacheHitRate();

            // ── 一次回答结束后根据需要自动记录 memory ──
            _ = Task.Run(async () =>
            {
                try
                {
                    string? lastUserMsg = null;
                    string? lastAssistantMsg = null;
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            lastAssistantMsg = _messages[_agentStreamingMsgIndex].Content;
                        }
                        // 向前查找最近的用户消息
                        for (int i = _agentStreamingMsgIndex - 1; i >= 0; i--)
                        {
                            if (_messages[i].Role == "user" && !string.IsNullOrEmpty(_messages[i].Content))
                            {
                                lastUserMsg = _messages[i].Content;
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(lastUserMsg) && !string.IsNullOrEmpty(lastAssistantMsg))
                    {
                        await AutoRecordMemoryAsync(lastUserMsg, lastAssistantMsg);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Memory] 自动记忆记录异常: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 计算本次问答的 Cache 命中率增量 footer（非 Session 累计），并持久化到消息。
        /// 无增量数据时返回空字符串。Agent 结果三分支收尾共用，避免重复实现。
        /// </summary>
        /// <param name="msgIndex">当前流式消息下标。</param>
        /// <returns>Cache footer HTML；无数据时为空字符串。</returns>
        private string BuildCacheFooterAndPersist(int msgIndex)
        {
            try
            {
                var delta = _apiService?.GetCacheDelta() ?? (0, 0, 0, 0);
                if (delta.Hit + delta.Miss <= 0) return string.Empty;

                string footer = ChatHtmlService.BuildCacheHitFooterHtml(
                    delta.Hit, delta.Miss, delta.Prompt, delta.Completion, roundCount: 1);
                lock (_lock)
                {
                    if (msgIndex >= 0 && msgIndex < _messages.Count)
                        _messages[msgIndex].CacheFooterHtml = footer;
                }
                return footer;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 最终化一条 Agent 流式消息（结果三分支收尾共用）：
        /// 锁内更新消息为完成态 → 同步批处理缓冲并强制刷新 → PostWebMessage 发送最终渲染。
        /// Plan 分支的"执行过程"HTML 通过 extraFooterHtml 传入，拼接在 Cache footer 之前。
        /// </summary>
        /// <param name="msgIndex">当前流式消息下标。</param>
        /// <param name="content">最终正文。</param>
        /// <param name="reasoning">最终推理内容。</param>
        /// <param name="footerHtml">Cache footer HTML（可为空字符串）。</param>
        /// <param name="extraFooterHtml">额外页脚 HTML（如执行过程详情），拼在 footer 前（可为空）。</param>
        private void FinalizeAgentMessage(int msgIndex, string content, string reasoning,
            string footerHtml, string? extraFooterHtml = null)
        {
            // ── 更新消息状态为最终完成态（下标无效时跳过更新，与旧分支行为一致）──
            lock (_lock)
            {
                if (msgIndex >= 0 && msgIndex < _messages.Count)
                {
                    var msg = _messages[msgIndex];
                    msg.Content = content;
                    msg.ReasoningContent = reasoning;
                    msg.IsStreaming = false;
                    msg.IsRendered = true;
                }
            }

            // ── 同步批处理缓冲并强制刷新，确保增量内容已推送 ──
            string combinedFooter = (extraFooterHtml ?? string.Empty) + footerHtml;
            BatchStreamingUpdate(msgIndex, content, reasoning, isComplete: true);

            // ── 使用非阻塞 PostWebMessageAsString 发送最终渲染 ──
            PostStreamEnd(msgIndex, content, reasoning, combinedFooter);
        }

        /// <summary>
        /// 若存在等待用户确认的 Handoff（ShowContinueOn），在其消息气泡下注入"开始执行"按钮。
        /// Agent 结果分支收尾共用。
        /// </summary>
        private async Task InjectPendingHandoffButtonAsync()
        {
            if (_pendingHandoff == null || _agentStreamingMsgIndex < 0) return;
            try
            {
                string targetAgentStr = _pendingHandoff.TargetAgent.ToString();
                string handoffBtnJs = ChatHtmlService.BuildHandoffButtonJs(
                    _agentStreamingMsgIndex, targetAgentStr, _pendingHandoff.Label);
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(handoffBtnJs);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 从 AI 原始响应中提取 JSON 数组。
        /// DeepSeek JSON Output 模式下仍可能包裹在 markdown 代码块、标题或其他文本中。
        /// </summary>
        private static string ExtractJsonArray(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse)) return string.Empty;

            string text = rawResponse.Trim();

            // 1) 去掉 markdown 代码块包裹 (```json ... ``` 或 ``` ... ```)
            var codeBlockMatch = System.Text.RegularExpressions.Regex.Match(
                text, @"```(?:json)?\s*\n?([\s\S]*?)\n?```",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (codeBlockMatch.Success)
            {
                text = codeBlockMatch.Groups[1].Value.Trim();
            }

            // 2) 尝试直接找到 JSON 数组（以 [ 开头，匹配到配对的 ]）
            if (text.StartsWith("["))
            {
                int depth = 0;
                int endIdx = -1;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '[') depth++;
                    else if (text[i] == ']') { depth--; if (depth == 0) { endIdx = i; break; } }
                }
                if (endIdx > 0)
                    return text.Substring(0, endIdx + 1);
            }

            // 3) 搜索文本中第一个出现的 JSON 数组
            var arrayMatch = System.Text.RegularExpressions.Regex.Match(
                text, @"\[\s*\{[\s\S]*?\}\s*\]|\[\s*\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (arrayMatch.Success)
                return arrayMatch.Value;

            // 4) 回退：去掉常见的非 JSON 前缀（# 标题、- 列表、普通文本行）
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^(?:#+?\s+.*?\n|(?:\w[\w\s]*?[：:]\s*?\n))*", "",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            text = text.Trim();
            if (text.StartsWith("["))
            {
                int depth = 0, endIdx = -1;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '[') depth++;
                    else if (text[i] == ']') { depth--; if (depth == 0) { endIdx = i; break; } }
                }
                if (endIdx > 0) return text.Substring(0, endIdx + 1);
            }

            return string.Empty;
        }

        /// <summary>
        /// 兼容 json_object 模式下模型偶发返回单对象 {…}（而非协议要求的 JSON 数组）的情况：
        /// 定位文本中第一个 JSON 对象的起止（括号深度配对，跳过字符串字面量），包装为单元素数组。
        /// 仅在 ExtractJsonArray 解析失败时调用，不影响正常数组响应路径。
        /// </summary>
        /// <param name="rawResponse">AI 原始响应文本。</param>
        /// <param name="jsonArray">输出：包装后的单元素 JSON 数组；失败时为空字符串。</param>
        /// <returns>是否成功提取并包装为数组。</returns>
        private static bool TryWrapSingleMemoryObjectAsArray(string rawResponse, out string jsonArray)
        {
            jsonArray = string.Empty;
            if (string.IsNullOrWhiteSpace(rawResponse)) return false;

            // 与 ExtractJsonArray 一致：先剥离 markdown 代码块包裹
            string text = rawResponse.Trim();
            var codeBlockMatch = System.Text.RegularExpressions.Regex.Match(
                text, @"```(?:json)?\s*\n?([\s\S]*?)\n?```",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (codeBlockMatch.Success)
                text = codeBlockMatch.Groups[1].Value.Trim();

            int startIdx = text.IndexOf('{');
            if (startIdx < 0) return false;

            // 括号深度配对：跳过字符串字面量内的 { }，避免 content 字段含花括号时配对错乱
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = startIdx; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        jsonArray = "[" + text.Substring(startIdx, i - startIdx + 1) + "]";
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 在一次问答结束后，自动判断是否需要将关键信息记录到持久化记忆。
        /// 使用轻量级非流式 API 调用，解析 AI 返回的记忆操作指令并执行。
        /// </summary>
        private async Task AutoRecordMemoryAsync(string userMessage, string assistantResponse)
        {
            if (_activeAgent == null || _memoryService == null) return;

            try
            {
                var systemPrompt = AiPrompts.MemoryAutoRecordSystemPrompt;

                var userPrompt = string.Format(AiPrompts.MemoryAutoRecordUserPrompt,
                    userMessage.Truncate(2000), assistantResponse.Truncate(2000));

                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = systemPrompt },
                    new ChatApiMessage { Role = "user", Content = userPrompt },
                };

                var rawResponse = await _activeAgent.CallAiWithMessagesAsync(
                    messages, CancellationToken.None, responseFormat: "json_object", temperature: 0.0);

                if (string.IsNullOrWhiteSpace(rawResponse))
                {
                    Logger.Info("[Memory] 自动记忆判断：AI 无响应，跳过");
                    return;
                }

                // ── 解析 JSON（DeepSeek JSON Output 模式下仍可能包裹 markdown 或前缀文本）──
                // 先按协议要求解析数组；json_object 模式下模型偶发返回单对象 {…}，
                // 需兼容包装为单元素数组，否则会被误判为"无需记录"而静默丢失用户偏好数据。
                string json = ExtractJsonArray(rawResponse);
                if (string.IsNullOrWhiteSpace(json)
                    && TryWrapSingleMemoryObjectAsArray(rawResponse, out var wrappedJson))
                {
                    json = wrappedJson;
                }
                if (string.IsNullOrWhiteSpace(json))
                {
                    // 格式异常（既非数组也非对象）意味着可能丢数据，升级 Warn 以便追踪模型输出问题
                    Logger.Warn($"[Memory] 自动记忆判断：响应格式异常无法解析（可能丢数据），原文: {rawResponse.Truncate(200)}");
                    return;
                }

                if (json == "[]")
                {
                    Logger.Info("[Memory] 自动记忆判断：无需记录");
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Logger.Warn($"[Memory] 自动记忆判断：AI 返回了非数组格式: {json.Truncate(200)}");
                    return;
                }

                string? sessionId = _activeSession?.Id;
                string? solutionPath = _solutionPath;

                int recordedCount = 0;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        string? scopeStr = item.TryGetProperty("scope", out var s) ? s.GetString() : null;
                        string? path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                        string? content = item.TryGetProperty("content", out var c) ? c.GetString() : null;

                        if (string.IsNullOrWhiteSpace(scopeStr) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(content))
                            continue;

                        var scope = scopeStr.ToLowerInvariant() switch
                        {
                            "session" => MemoryScope.Session,
                            "repo" => MemoryScope.Repo,
                            _ => MemoryScope.User,
                        };

                        // 确保路径以 .md 结尾
                        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                            path += ".md";

                        // 构建完整日志路径
                        string scopeLabel = scope switch
                        {
                            MemoryScope.Session => "/memories/session/",
                            MemoryScope.Repo => "/memories/repo/",
                            _ => "/memories/",
                        };
                        string fullPath = scopeLabel + path.TrimStart('/');

                        // 检查是否已有同名文件，避免重复记录完全重复的内容
                        string? existingContent = null;
                        try
                        {
                            var viewResult = await _memoryService.ViewAsync(scope, path, sessionId, solutionPath);
                            existingContent = viewResult?.Content;
                        }
                        catch
                        {
                            // 文件不存在或读取失败，视为无已有内容
                        }

                        if (!string.IsNullOrWhiteSpace(existingContent))
                        {
                            if (existingContent.Contains(content.Trim()))
                            {
                                Logger.Info($"[Memory] 跳过重复内容: {fullPath}");
                                continue;
                            }

                            // ── 追加到已有文件末尾 ──
                            await _memoryService.InsertAsync(scope, path, int.MaxValue, "\n\n" + content.Trim(), sessionId, solutionPath);
                        }
                        else
                        {
                            // ── 创建新文件 ──
                            await _memoryService.CreateAsync(scope, path, content, sessionId, solutionPath);
                        }

                        recordedCount++;
                        Logger.Info($"[Memory] 自动记录: {fullPath} ({content.Length} 字符)");
                    }
                    catch (Exception itemEx)
                    {
                        Logger.Warn($"[Memory] 单条记忆记录失败: {itemEx.Message}");
                    }
                }

                if (recordedCount > 0)
                    Logger.Info($"[Memory] 自动记忆记录完成: 共 {recordedCount} 条");
            }
            catch (JsonException jex)
            {
                Logger.Warn($"[Memory] 自动记忆判断 JSON 解析失败: {jex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Memory] 自动记忆判断异常: {ex.Message}");
            }
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
        /// Agent 步骤状态变更回调：更新 WebView 中的步骤进度。
        /// 仅更新已存在的计划面板（由 RunAgentWorkflowAsync / ExecuteAgentHandoffAsync 创建）。
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

                    // ── 仅更新有来源的计划面板（PlanAgent 或 EditAgent 产出）──
                    // 独立 Edit（无拆分计划）不创建/更新下方面板
                    if (plan.Source != PlanSource.None)
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
                                AppendAgentThinking(string.Format(LocalizationService.Instance["agent.step.completedWithTitle"], step.Index, step.Title));
                            else if (step.Status == AgentStepStatus.Failed)
                                AppendAgentThinking(string.Format(LocalizationService.Instance["agent.step.failedWithTitle"], step.Index, step.ResultSummary ?? step.Title));
                            else if (step.Status == AgentStepStatus.InProgress)
                                AppendAgentThinking(string.Format(LocalizationService.Instance["agent.step.inProgressWithTitle"], step.Index, step.Title));
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
                    _activeSession.CumulativeCompletionTokens,
                    _activeSession.CumulativeCostYuan,
                    _activeSession.CumulativeCostUsd);
                Logger.Info($"[Cache] 从会话恢复累计统计: hit={_activeSession.CumulativeCacheHitTokens}, miss={_activeSession.CumulativeCacheMissTokens}, cost=¥{_activeSession.CumulativeCostYuan:F4}/${_activeSession.CumulativeCostUsd:F4}");
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

            // 工具调用摘要由 BaseAgent 显式标记，始终作为用户可见的过程输出展示。
            if (entry.Level == "TOOL")
                return msg;

            // ── 过滤纯内部日志（中英文双语匹配）──
            if (msg.StartsWith("[TokenUsage]") || msg.StartsWith("[Retry") || msg.StartsWith("[AgentFlow]"))
                return string.Empty;
            if (msg.Contains("上下文已累积") || msg.Contains("Planning 模式")
                || msg.Contains("context accumulated") || msg.Contains("Planning mode"))
                return string.Empty;

            // ── 格式化为可读的思考内容 ──

            // 结果标记行（文本前缀，locale 无关）与文件读取状态原样保留
            if (msg.StartsWith("Error: ") || msg.StartsWith("Timeout: ") || msg.StartsWith("[BLOCKED] "))
                return msg;
            if (msg.Contains("已读取") || msg.Contains("read file"))
                return msg;

            // Phase 进度指示（中英文通用：包含 "/3:" 的模式）
            if (msg.StartsWith("阶段") || msg.StartsWith("Phase") || msg.Contains("/3:"))
                return $" {msg}";

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
                return $" {msg}";

            // Edit Agent 步骤前缀（使用 i18n）
            var L = LocalizationService.Instance;
            if (msg.StartsWith(L["agent.log.editStepPrefix"]) || msg.Contains(L["agent.log.planStepsPlannedSuffix"]))
                return msg;

            // ── ExploreAgent 委托和发现日志 ──
            if (msg.StartsWith("[EditAgent] 委托 ExploreAgent") || msg.StartsWith("[EditAgent] delegated ExploreAgent"))
                return $" {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[EditAgent] ExploreAgent 返回") || msg.StartsWith("[EditAgent] ExploreAgent returned"))
                return $" {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[EditAgent]"))
                return $" {msg.Replace("[EditAgent] ", "")}";

            // ── Plan Agent 转发的 Explore 日志 ──
            if (msg.StartsWith("[Explore] [Discover]"))
                return string.Empty; // Explore 内部发现日志不展示
            if (msg.StartsWith("[Explore]"))
                return $" {msg.Replace("[Explore] ", "")}";

            // ── Plan Agent 自身进度日志（[Plan] 前缀）──
            if (msg.StartsWith("[Plan]"))
                return $" {msg.Replace("[Plan] ", "")}";

            // ── Plan Agent 关键日志（中英文通用匹配）──
            // 匹配模式: "Phase X/Y:", "步骤 X/Y:", "step X/Y:", " plan.md"
            if (msg.Contains(" plan.md") || msg.Contains(": 成功") || msg.Contains(": succeeded"))
                return msg;

            // 其他日志：以 ERROR/WARN 级别展示简要信息
            if (entry.Level == "ERROR")
                return $"Error: {msg}";
            if (entry.Level == "WARN")
                return $" {msg}";

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

        /// <summary>
        /// 发送 Toast 通知，提醒用户 VS 中有需要操作的事项（审批/回答问题）。
        /// </summary>
        private static void NotifyUserActionRequired(string title, string detail)
        {
            try
            {
                var toastService = CompositionRoot.GetServiceOrDefault<ToastNotificationService>();
                if (toastService == null) return;

                string message = string.IsNullOrWhiteSpace(detail) ? "请切换到 Visual Studio 处理" : detail;
                toastService.Show(title, message);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Agent] 发送操作提醒 Toast 失败: {ex.Message}");
            }
        }

        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            // ── 后台线程快速路径：全部放行不需要等 UI 线程，避免 UI 繁忙时审批卡住 ──
            if (_cachedApprovalMode == Models.ApprovalMode.AllowAll)
            {
                Logger.Info($"[Agent] 审批模式=全部放行（后台快速批准）: {request.Title}");
                var permAgent = _agentFactory?.FindAgentWithPendingPermission(request.RequestId) ?? _activeAgent;
                permAgent?.RespondToPermission(request.RequestId, true);
                return;
            }

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null)
                {
                    Logger.Warn($"[Agent] CoreWebView2 未就绪，自动拒绝权限请求: {request.Title}");
                    var permAgent = _agentFactory?.FindAgentWithPendingPermission(request.RequestId) ?? _activeAgent;
                    permAgent?.RespondToPermission(request.RequestId, false);
                    return;
                }

                // ── 审批模式检查：全部放行 → 自动批准 ──
                var approvalMode = GetCurrentApprovalMode();
                if (approvalMode == Models.ApprovalMode.AllowAll)
                {
                    Logger.Info($"[Agent] 审批模式=全部放行，自动批准: {request.Title}");
                    var permAgent = _agentFactory?.FindAgentWithPendingPermission(request.RequestId) ?? _activeAgent;
                    permAgent?.RespondToPermission(request.RequestId, true);
                    return;
                }

                // ── 审批模式检查：智能拦截 → 仅危险命令需要审批 ──
                if (approvalMode == Models.ApprovalMode.SmartBlock)
                {
                    bool isDangerous = IsDangerousCommand(request.Command, request.ActionType);
                    if (!isDangerous)
                    {
                        Logger.Info($"[Agent] 审批模式=智能拦截，安全命令自动放行: {request.Title}");
                        var permAgent = _agentFactory?.FindAgentWithPendingPermission(request.RequestId) ?? _activeAgent;
                        permAgent?.RespondToPermission(request.RequestId, true);
                        return;
                    }
                    Logger.Info($"[Agent] 审批模式=智能拦截，检测到危险命令，需要审批: {request.Command}");
                }

                try
                {
                    // ── v1.1.10: Toast 通知用户需要审批 ──
                    NotifyUserActionRequired(request.Title, request.Command);

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
                    var permAgent = _agentFactory?.FindAgentWithPendingPermission(request.RequestId) ?? _activeAgent;
                    permAgent?.RespondToPermission(request.RequestId, false);
                }
            });
        }

        /// <summary>
        /// Agent 向用户提问回调（VisualStudio_askQuestions 工具）。
        /// 在 WebView 中注入问题 UI，等待用户回答后回调 AgentFlow。
        /// </summary>
        private void OnAgentQuestionsRequested(AgentQuestionRequest request)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                try
                {
                    if (ChatWebView.CoreWebView2 == null)
                    {
                        Logger.Warn($"[Agent] CoreWebView2 未就绪，无法注入问题 UI (共 {request.Questions.Count} 个问题)，自动跳过");
                        var questionAgent = _agentFactory?.FindAgentWithPendingQuestion(request.RequestId) ?? _activeAgent;
                        questionAgent?.RespondToQuestions(request.RequestId, "[]");
                        return;
                    }

                    // ── 诊断：记录问题详情 ──
                    try
                    {
                        var questionHeaders = request.Questions.Select(q => q.Header.Truncate(50));
                        Logger.Info($"[Agent] 准备注入问题 UI: RequestId={request.RequestId}, 问题数={request.Questions.Count}, 标题=[{string.Join(", ", questionHeaders)}]");
                        Logger.Info($"[Agent] 问题 JSON 长度: {System.Text.Json.JsonSerializer.Serialize(request.Questions).Length} 字符");
                    }
                    catch { }

                    // ── v1.1.10: Toast 通知用户需要回答问题 ──
                    string questionSummary = request.Questions.Count == 1
                        ? request.Questions[0].Header.Truncate(80)
                        : $"{request.Questions.Count} 个问题需要回答";
                    NotifyUserActionRequired("AI 需要你的回答", questionSummary);

                    string js = ChatHtmlService.BuildAskQuestionsJs(request);
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.questionsWaiting"],
                        request.Questions.Count);

                    string result = await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                    Logger.Info($"[Agent] 问题 UI JS 执行完成, 返回: {result?.Truncate(200) ?? "(null)"}");

                    // ── 验证：检查 DOM 中是否真的创建了问题元素 ──
                    string verifyJs = "document.getElementById('agent-questions') ? 'EXISTS' : 'MISSING';";
                    string verifyResult = await ChatWebView.CoreWebView2.ExecuteScriptAsync(verifyJs);
                    if (verifyResult?.Contains("MISSING") == true)
                    {
                        Logger.Warn("[Agent]  问题 UI 注入后验证失败: DOM 中未找到 #agent-questions 元素!");
                        // 检查可能的原因
                        string containerCheck = await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                            "var c=document.getElementById('chat-container');" +
                            "var f=typeof window.__insertBeforeTaskPanel;" +
                            "JSON.stringify({containerExists:!!c, insertFnType:f});");
                        Logger.Info($"[Agent] DOM 诊断: {containerCheck}");
                    }
                    else
                    {
                        Logger.Info($"[Agent]  问题 UI 已成功注入 DOM (verify={verifyResult})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] 问题 UI 注入失败: {ex.Message}\n{ex.StackTrace}");
                    var questionAgent = _agentFactory?.FindAgentWithPendingQuestion(request.RequestId) ?? _activeAgent;
                    questionAgent?.RespondToQuestions(request.RequestId, "{}");
                }
            });
        }

        /// <summary>
        /// Agent 文件变更实时通知回调：仅更新实时思考气泡。
        /// </summary>
        private void OnAgentFileChangeNotified(AgentFileChangeEventArgs args)
        {
            // ── 更新实时思考气泡（标签随 i18n；此前硬编码中文在英文界面泄漏）──
            string icon = args.ChangeType.ToLowerInvariant() switch
            {
                "create" => LocalizationService.Instance["agent.fileChange.create"],
                "delete" => LocalizationService.Instance["agent.fileChange.delete"],
                _ => LocalizationService.Instance["agent.fileChange.modify"],
            };
            string fileName = System.IO.Path.GetFileName(args.FilePath);
            AppendAgentThinking($"{icon} `{fileName}` ({args.Detail})");
        }

        #endregion
    }
}
