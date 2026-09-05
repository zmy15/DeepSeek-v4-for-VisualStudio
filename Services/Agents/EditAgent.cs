using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Edit Agent — 代码修改执行代理。
    /// 
    /// 职责：
    /// - 按计划逐步执行代码修改
    /// - 输出 ```file: 格式的代码变更
    /// - 支持构建/运行验证步骤
    /// - 请求用户权限确认
    /// - 追踪文件变更
    /// 
    /// 限制策略（v1.1.10）：
    /// - 每次编辑 ≤ 3 个文件
    /// - 每次编辑 ≤ 500 行代码变更
    /// - 文件修改后再次编辑前强制重新读取
    /// </summary>
    public partial class EditAgent : BaseAgent
    {
        // ── 编辑限制常量 ──
        internal const int MaxFilesPerEdit = 3;
        internal const int MaxLinesPerEdit = 500;

        private CancellationTokenSource? _agentCts;
        private ExploreAgent? _exploreAgent;

        // ── 累积累推理/思考内容（跨步骤收集，供 UI 渲染思考面板）──
        private string? _accumulatedReasoning;

        // ── 用户原始消息（用于检测跳过构建的意图）──
        private string? _lastUserMessage;

        // ── 本轮已修改文件追踪（用于步骤间重读提示）──
        private readonly HashSet<string> _lastModifiedFiles = new(StringComparer.OrdinalIgnoreCase);

        // ── 编辑工具（懒加载，由 EnsureEditTools 初始化）──
        private ApplyPatchTool? _applyPatchTool;
        private InsertEditTool? _insertEditTool;
        private ReplaceStringTool? _replaceStringTool;
        private MultiReplaceStringTool? _multiReplaceStringTool;

        // ── Agent 多步编辑 Workspace ──
        private Editing.StagedEditWorkspace? _stagedWorkspace;

        /// <summary>
        /// ExploreAgent 引用，由 AgentFactory 注入。
        /// 用于在执行代码修改前智能发现相关文件。
        /// 设置时自动转发 ExploreAgent 的日志和文件变更事件。
        /// </summary>
        public new ExploreAgent? ExploreAgent
        {
            get => _exploreAgent;
            set
            {
                RegisterExploreAgent(value, ref _exploreAgent);
                base.ExploreAgent = value; //  同步到基类属性，确保 ExecuteToolAsync 可见
            }
        }

        /// <summary>当前正在执行的任务计划</summary>
        public AgentTaskPlan? CurrentPlan { get; set; }

        /// <summary>计划/步骤状态变更事件（UI 订阅）</summary>
        public event Action<AgentTaskPlan>? PlanUpdated;

        public EditAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Edit) { }

        #region Agent Definition

        /// <summary>
        /// Edit Agent 工具集 — 编辑/终端/构建能力。
        /// 代码库探索（搜索、列表、grep）通过 runSubagent 委派给 ExploreAgent。
        /// read_file 保留用于编辑前确认文件内容（利用 ExploreAgent 预热缓存）。
        /// </summary>
        public static readonly string[] EditTools = new[]
        {
            // 编辑工具
            "create_file",
            "delete_file",
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "apply_patch",
            "create_directory",
            // 编辑必需：读取文件（利用缓存命中）
            "read_file",
            "capture_window",      // 视觉模型直接查看窗口截图
            "get_errors",
            // 终端与构建
            "run_in_terminal",
            "get_terminal_output",
            "build_solution",
            // Git 版本控制
            "git",
            // 子代理委派与移交
            "runSubagent",
            "request_handoff",
            // 记忆
            "memory",
            // 用户交互
            "VisualStudio_askQuestions",  // 向用户提问澄清
        };

        /// <summary>
        /// Edit Agent 代码步骤完整工具集（v1.1.10）。
        /// 包含探索工具 + 编辑工具，允许 AI 在步骤内执行增量编辑：
        /// 探索 → 编辑 → 读取结果 → 继续编辑 → ...，而非强制一次性输出所有变更。
        /// 不包含 build_solution / request_handoff：编译和后续移交由系统统一触发，避免代码步骤内抢占流程。
        /// 循环检测机制（BaseAgent.CallAiWithToolLoopAsync）防止死循环。
        /// </summary>
        private static readonly string[] CodeStepTools = new[]
        {
            // 探索工具
            "read_file",
            "capture_window",
            "file_search",
            "grep_search",
            "symbol_search",
            "list_dir",
            "get_errors",
            "runSubagent",
            "git",
            "run_in_terminal",
            // 编辑工具 — 允许步骤内增量编辑
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "delete_file",
            "apply_patch",
            "create_directory",
            // 记忆工具 — 允许步骤内读写持久记忆
            "memory",
            "VisualStudio_askQuestions",  // 向用户提问澄清
        };

        /// <summary>
        /// Edit Agent 编译验证阶段工具清单（build + 只读 + 编辑 + 记忆，不含探索/子代理工具）。
        /// </summary>
        private static readonly string[] VerifyPhaseTools = new[]
        {
            "build_solution",
            "read_file",
            "capture_window",
            "get_errors",
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "apply_patch",
            "delete_file",
            "create_directory",
            "run_in_terminal",
            "get_terminal_output",
            "memory",
            "git",                   // 解决冲突后重试推送等 git 操作
        };

        /// <summary>
        /// 只读执行阶段工具：允许读取、搜索、运行终端命令和 git 操作，但不允许代码文件写入。
        /// </summary>
        private static readonly string[] ReadOnlyExecutionTools = new[]
        {
            "read_file",
            "capture_window",
            "file_search",
            "grep_search",
            "symbol_search",
            "list_dir",
            "run_in_terminal",
            "get_terminal_output",
            "VisualStudio_askQuestions",
            "git",
        };
        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Edit,
                Name = "Edit",
                Description = LocalizationService.Instance["agent.edit.description"],
                ArgumentHint = LocalizationService.Instance["agent.edit.argumentHint"],
                UserInvocable = true,
                AllowedTools = new List<string>(EditTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>
                {
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.edit.handoffAskLabel"],
                        TargetAgent = AgentType.Ask,
                        Prompt = LocalizationService.Instance["agent.edit.handoffAskPrompt"],
                        AutoSend = true,
                        ShowContinueOn = false,
                    },
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.edit.handoffBuildLabel"],
                        TargetAgent = AgentType.Build,
                        Prompt = LocalizationService.Instance["agent.edit.handoffBuildPrompt"],
                        AutoSend = true,
                        ShowContinueOn = false,
                    },
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.edit.handoffPlanLabel"],
                        TargetAgent = AgentType.Plan,
                        Prompt = LocalizationService.Instance["agent.edit.handoffPlanPrompt"],
                        AutoSend = true,
                        ShowContinueOn = false,
                    },
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return LocalizationService.Instance["system.agent.editPromptFragment"]
                + LocalizationService.Instance["agent.edit.mcpSystemPrompt"]
                + LocalizationService.Instance["system.agent.editBuildTrustRule"]
                + LocalizationService.Instance["system.agent.editPhaseToolOverride"];
        }

        #endregion

        #region Execute

        /// <summary>
        /// Edit Agent 执行入口。
        /// 接收计划并逐步执行代码修改。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清空上次执行的日志、推理内容和移交状态 ──
            _logs.Clear();
            _accumulatedReasoning = null;
            PendingHandoffRequest = null;
            _lastUserMessage = userMessage;

            var result = new AgentResult
            {
                AgentType = AgentType.Edit,
                Success = true,
            };

            // ── 如果有 ActivePlan 且未完成，执行计划 ──
            // 如果计划已完成（如上一轮 plan→edit 已执行完毕），则视为新任务重新路由
            AgentTaskPlan plan;
            if (context.ActivePlan != null && context.ActivePlan.Steps.Count > 0
                && !context.ActivePlan.IsCompleted)
            {
                plan = context.ActivePlan;
                await ExecutePlanAsync(plan, context);
            }
            else
            {
                // ── 使用首次路由时预分类的 TaskSize，避免对 handoff 长消息重复分类 ──
                var taskSize = context.PreClassifiedTaskSize;
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editTaskSize"], taskSize));

                // ── 用户 @edit 显式指定时尊重用户意图，跳过自动移交 ──
                if (taskSize == TaskSize.Large && context.IsExplicitRoute)
                {
                    AddLog("INFO", LocalizationService.Instance["agent.log.editExplicitRouteSkipHandoff"]);
                    taskSize = TaskSize.Medium;
                }

                if (taskSize == TaskSize.Large)
                {
                    // ── Large 任务：移交 Plan Agent 进行深入规划 ──
                    AddLog("INFO", LocalizationService.Instance["agent.log.editLargeTaskHandoff"]);
                    return BuildLargeTaskHandoffResult(userMessage);
                }
                else if (taskSize == TaskSize.Medium)
                {
                    // ── Medium 任务：AI 自主拆分步骤 ──
                    AddLog("INFO", LocalizationService.Instance["agent.log.editAutoSplit"]);
                    plan = await CreateAutoSplitPlanAsync(userMessage, context);
                }
                else
                {
                    // ── Small 任务：单步执行 ──
                    AddLog("INFO", LocalizationService.Instance["agent.log.editNoPlan"]);
                    plan = CreateSingleStepPlan(userMessage);
                }
                plan.Source = PlanSource.EditAgent;
                context.ActivePlan = plan;
                await ExecutePlanAsync(plan, context);
            }

            result.Plan = plan;
            result.FileChanges = plan.ChangedFiles;

            // ── 确定 Handoff 目标（AI 动态移交优先于程序化移交）──
            result.Handoff = ResolveHandoff(plan);

            // ── 传递累积累的推理内容供 UI 渲染思考面板 ──
            if (!string.IsNullOrEmpty(_accumulatedReasoning))
                result.ReasoningContent = _accumulatedReasoning;

            // ── 构建最终回复内容（Content）──
            // 纯只读/终端任务无文件变更时直接沿用 AI 的最终回复（例如“输出代码内容”），
            // 避免后续被“变更总结”形式的 Handoff 覆盖；有文件变更时仍是执行结果摘要。
            result.Content = BuildFinalContent(result.Plan, result.Handoff == null);

            result.Logs.AddRange(_logs);
            return result;
        }

        #endregion

        #region Plan Execution

        /// <summary>
        /// 执行任务计划中的所有步骤。
        /// </summary>
        public async Task ExecutePlanAsync(
            AgentTaskPlan plan,
            AgentContext context)
        {
            CurrentPlan = plan;
            _agentCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

            // ── P0-6: 新计划开始，重置跨计划状态（防止前一个计划的 CodeMemory/AccumulatedContext 泄漏）──
            context.CodeMemory = null;
            context.AccumulatedContext = null;

            // ── v1.1.11: 清理上一次计划的步骤摘要记忆文件，防止新旧摘要混在一起 ──
            await ClearPreviousPlanMemoryAsync(context);

            // ═══════════════════════════════════════════════════════════════
            // 缓存策略：将 BuiltInToolService 已读取的文件同步到 AgentContext
            // 全局缓存，避免后续步骤重复 read_file（以后会被 RAG 替代）
            // ═══════════════════════════════════════════════════════════════
            if (context.FileReadCache.Count == 0 && BuiltInTools != null)
            {
                var builtInCache = BuiltInTools.GetFileReadCacheSnapshot();
                if (builtInCache.Count > 0)
                {
                    foreach (var kvp in builtInCache)
                        context.FileReadCache[kvp.Key] = kvp.Value;
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.editCachedFiles", builtInCache.Count));
                }
            }

            // ── P0-1: 预填充 CodeMemory，让步骤1也能受益于探索阶段已读取的文件 ──
            if (string.IsNullOrEmpty(context.CodeMemory) && BuiltInTools != null)
            {
                var initialModifiedPaths = new HashSet<string>(
                    plan.ChangedFiles.Select(c => NormalizePath(c.FilePath)),
                    StringComparer.OrdinalIgnoreCase);
                RefreshCodeMemory(context, initialModifiedPaths);
            }

            // ── 防重守卫：如果计划已完成，跳过重复执行 ──
            if (plan.IsCompleted)
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.editPlanDone"]);
                return;
            }

            try
            {
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    if (_agentCts.IsCancellationRequested)
                    {
                        plan.IsCancelled = true;
                        break;
                    }

                    var step = plan.Steps[i];

                    // ── 跳过已完成的步骤（防止计划被恢复后重复执行）──
                    if (step.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped)
                    {
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editStepSkipped", step.Index, step.Title));
                        continue;
                    }

                    plan.CurrentStepIndex = i + 1;
                    step.Status = AgentStepStatus.InProgress;
                    NotifyPlanUpdated();

                    // ── 记录本步骤开始前的累积思考长度，用于提取本步骤思考增量（供完成声明检测）──
                    int reasoningBaseLength = _accumulatedReasoning?.Length ?? 0;

                    var L = LocalizationService.Instance;
                    AddLog("INFO", string.Format(L["agent.log.editStepExec"], step.Index, plan.Steps.Count, step.Title));

                    try
                    {
                        await ExecuteStepAsync(step, plan, context);
                        step.Status = AgentStepStatus.Completed;
                        AddLog("INFO", string.Format(L["agent.log.editStepDone"], step.Index, step.ResultSummary ?? "OK"));
                    }
                    catch (OperationCanceledException)
                    {
                        step.Status = AgentStepStatus.Skipped;
                        AddLog("WARN", string.Format(L["agent.log.editStepCancelled"], step.Index));
                        plan.IsCancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        step.Status = AgentStepStatus.Failed;
                        step.ResultSummary = ex.Message;
                        AddLog("ERROR", string.Format(L["agent.log.editStepFailed"], step.Index, ex.Message));
                    }

                    NotifyPlanUpdated();

                    // ── 继承上下文：将刚完成的步骤结果累积（所有模式通用）──
                    if (step.Status == AgentStepStatus.Completed)
                    {
                        string stepResult = string.IsNullOrEmpty(step.ResultSummary)
                            ? string.Format(L["agent.log.editStepContextCompleted"], step.Index, step.Title)
                            : string.Format(L["agent.log.editStepContextWithResult"], step.Index, step.Title, step.ResultSummary);
                        context.AccumulatedContext = (context.AccumulatedContext ?? "") + "\n" + stepResult;
                        if (!string.IsNullOrEmpty(step.AiResponse) && step.AiResponse!.Length < 3000)
                            context.AccumulatedContext += "\n" + step.AiResponse;

                        // ── 截断：保留最近 8000 字符，防止无限增长导致 token 爆炸 ──
                        const int maxAccumulatedChars = 8000;
                        if (context.AccumulatedContext.Length > maxAccumulatedChars)
                        {
                            context.AccumulatedContext = "...(早期上下文已截断)\n"
                                + context.AccumulatedContext.Substring(
                                    context.AccumulatedContext.Length - maxAccumulatedChars);
                        }
                        AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.contextAccumulated"], context.AccumulatedContext.Length));

                        // ── 更新代码记忆：从文件读取缓存中提取关键文件内容 ──
                        UpdateCodeMemory(context, plan);

                        // ── 将步骤摘要写入会话记忆（供 Ask Agent 最终汇总使用）──
                        await SaveStepSummaryToMemoryAsync(step, plan, context);

                        // ── v1.1.10: 检测 AI 输出中声明的后续步骤完成情况（含思考内容）──
                        string stepThinking = _accumulatedReasoning != null && _accumulatedReasoning.Length > reasoningBaseLength
                            ? _accumulatedReasoning.Substring(reasoningBaseLength)
                            : "";
                        DetectAndAutoCompleteLaterSteps(step, plan, stepThinking);
                    }
                }

                plan.IsCompleted = plan.Steps.All(s =>
                    s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped);

                // ── 计划完成后，将聚合摘要写入会话记忆 ──
                if (plan.IsCompleted && !plan.IsCancelled)
                {
                    await SaveFinalPlanSummaryToMemoryAsync(plan, context);
                }

                // ── 诊断日志：记录步骤完成情况 ──
                int completedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int skippedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);
                int failedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int pendingCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Pending);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editPlanProgress"],
                    plan.Steps.Count, completedCount, skippedCount, failedCount, pendingCount));

                // ── v1.1.10: Plan 级别变更追踪 — 对比计划预期与实际修改 ──
                PerformPlanChangeTracking(plan, context);

                // ── Planning 模式：所有步骤完成后统一编译验证一次 ──
                // 必须 plan.IsCompleted 才触发最终构建（防止 JSON 回退单步计划误触发）
                // v1.1.12: 检查 ShouldSkipAutoBuild() 以尊重用户设置和提示中的意图
                if (context.IsPlanningMode && plan.IsCompleted
                    && plan.ChangedFiles.Count > 0
                    && !plan.IsCancelled && !_agentCts!.IsCancellationRequested)
                {
                    if (ShouldSkipAutoBuild())
                    {
                        AddLog("INFO", LocalizationService.Instance["agent.edit.autoBuildDisabledSkip"]);
                    }
                    else
                    {
                        AddLog("INFO", LocalizationService.Instance["agent.log.editFinalBuild"]);
                        NotifyPlanUpdated();
                        try
                        {
                            string finalBuildResult = await ExecuteDirectBuildAsync(
                                LocalizationService.Instance["agent.log.editFinalBuildStepTitle"],
                                context.SolutionPath,
                                _agentCts?.Token ?? context.CancellationToken);
                            LogDirectBuildResult(finalBuildResult);

                            // ── 最终构建通过后回写步骤状态，避免旧编译失败污染总结 ──
                            if (!HasBuildFailure(finalBuildResult))
                            {
                                int reconciled = PlanBuildOutcomeReconciler.ReconcileAfterBuildSuccess(
                                    plan,
                                    finalBuildResult,
                                    LocalizationService.Instance["agent.log.buildReconciledStepResult"]);
                                if (reconciled > 0)
                                {
                                    AddLog("INFO", string.Format(
                                        LocalizationService.Instance["agent.log.buildReconciledSteps"],
                                        reconciled));
                                }

                                // 状态回写后计划可能恢复为已完成，刷新最终摘要记忆
                                if (plan.IsCompleted && !plan.IsCancelled)
                                    await SaveFinalPlanSummaryToMemoryAsync(plan, context);
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.finalBuildException"], ex.Message));
                        }
                    }
                }
            }
            finally
            {
                NotifyPlanUpdated();

                // ── Toast 通知：任务完成或中断 ──
                NotifyPlanCompletionViaToast(plan);

                // ── 清理 Plan Agent 生成?plan.md ──
                await CleanupPlanMarkdownAsync(plan, context);

                // ── 结束本轮编辑会话：清空备份会话目录（空目录回收）──
                // 此前无调用点，会话目录随进程存活持续累积（见还原点分析报告 P1-1）。
                BackupService.EndSession();
            }
        }

        /// <summary>
        /// 删除 Plan Agent 生成的 plan.md 文件（Edit Agent 执行完毕后清理）。
        /// </summary>
        private async Task CleanupPlanMarkdownAsync(AgentTaskPlan plan, AgentContext context)
        {
            string? planFilePath = plan.PlanFilePath ?? context.PlanFilePath;
            if (string.IsNullOrEmpty(planFilePath))
                return;

            try
            {
                await Task.Run(() =>
                {
                    if (File.Exists(planFilePath))
                    {
                        File.Delete(planFilePath);
                        Logger.Info($"[EditAgent] 已清理 plan.md: {planFilePath}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 清理 plan.md 失败（非致命）: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行单个步骤。
        /// </summary>
        private async Task ExecuteStepAsync(
            AgentStep step, AgentTaskPlan plan, AgentContext context)
        {
            var ct = _agentCts?.Token ?? context.CancellationToken;

            // ── 权限确认 ──
            if (step.RequiresApproval && !string.IsNullOrEmpty(step.PendingCommand))
            {
                step.Status = AgentStepStatus.WaitingApproval;
                NotifyPlanUpdated();

                bool approved = await RequestPermissionAsync(step.Title, step.PendingCommand!, "command");
                if (!approved)
                {
                    step.Status = AgentStepStatus.Skipped;
                    step.ResultSummary = LocalizationService.Instance["agent.log.editStepPermissionDenied"];
                    return;
                }

                step.Status = AgentStepStatus.InProgress;
                NotifyPlanUpdated();
            }

            // ── 判断步骤类型 ──
            bool isBuildStep = IsBuildVerificationStep(step.Title);
            bool isReadOnlyExecutionStep = plan.Intent == AgentIntent.QandA
                && IsReadOnlyExecutionRequest(step.Description);
            bool isCodeStep = !isReadOnlyExecutionStep && IsCodeWritingStep(step.Title);

            // ── 构建 AI prompt ──
            string stepPrompt = BuildStepPrompt(step, plan, context, isCodeStep);

            if (isBuildStep)
            {
                // ── 直接构建：计划中的构建/运行/测试步骤统一走 build_solution ──
                string buildResult = await ExecuteDirectBuildAsync(
                    step.Title, context.SolutionPath, ct);
                step.AiResponse = buildResult;
                step.ResultSummary = buildResult;

                // ── 记录构建结果到日志，使 HasBuildWarningsInLogs() 能检测到步骤级构建失败 ──
                LogDirectBuildResult(buildResult);
            }
            else if (isCodeStep)
            {
                await ExecuteCodeStepAsync(step, plan, context, stepPrompt, ct);
            }
            else if (isReadOnlyExecutionStep)
            {
                await ExecuteReadOnlyExecutionStepAsync(step, context, stepPrompt, ct);
            }
            else
            {
                // ── 非代码/构建步骤：同样收集思考内容（供完成声明检测与 UI 渲染）──
                var stepThinking = new System.Text.StringBuilder();
                string result = await CallAiLongAsync(Definition.SystemPrompt, stepPrompt, ct, maxTokens: 4096,
                    onThinking: thinking =>
                    {
                        stepThinking.Append(thinking);
                        context.OnThinkingChunk?.Invoke(thinking);
                    });
                step.AiResponse = result;
                step.ResultSummary = result;
                if (stepThinking.Length > 0)
                {
                    if (!string.IsNullOrEmpty(_accumulatedReasoning))
                        _accumulatedReasoning += "\n\n";
                    _accumulatedReasoning += stepThinking.ToString();
                }
            }
        }

        /// <summary>
        /// 执行代码编写步骤（支持工具调用探索 + 三种编辑格式 + healing）。
        /// AI 先使用只读工具探索项目结构和现有代码，再选择最佳编辑格式输出变更。
        /// 
        /// 三种编辑格式：
        /// 1. apply_patch — *** Begin Patch / *** End Patch（首选，局部修改）
        /// 2. insert_edit_into_file — ```insert_edit_into_file: 代码块（多处修改）
        /// 3. create_file — ```file: 代码块（新建文件，已有支持）
        /// 
        /// 编辑应用流程：
        /// 1. AI 选择工具并生成编辑内容
        /// 2. 后端 4 级字符串匹配（精确 → 空白弹性 → 模糊 → Levenshtein）
        /// 3. 匹配失败时启动 healing 机制（降级模型修正）
        /// 4. 匹配成功后通过 VS 文本缓冲区应用
        /// 5. 检查新引入的诊断错误
        /// </summary>
        private async Task ExecuteCodeStepAsync(
            AgentStep step, AgentTaskPlan plan, AgentContext context,
            string stepPrompt, CancellationToken ct)
        {
            const int maxFormatRetries = 2;
            string result = string.Empty;
            List<FileChangeSummary> changes = new();

            // ── 解析工作区根目录 ──
            string workspaceRoot = context.SolutionPath ?? string.Empty;
            if (!string.IsNullOrEmpty(workspaceRoot) && System.IO.File.Exists(workspaceRoot))
                workspaceRoot = System.IO.Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;

            // ── 创建 / 重置 StagedEditWorkspace（须在 AI 工具循环之前！）──
            // 工具循环中的 create_file / apply_patch 等会通过 WriteFile 登记 Baseline，
            // 供 diff 预览和逐块撤销使用。若在此之后才初始化，工具编辑将走 BackupService 直接落盘，
            // 不登记 hunks，导致 diff 无数据可显示。
            _stagedWorkspace ??= new Editing.StagedEditWorkspace();

            // ── 注入已打开文档写入器：已打开文档通过 buffer+编辑器 Save 写入 ──
            // 避免 File.WriteAllText 裸写盘在 dirty buffer 场景触发 VS「文件已在磁盘上修改」弹窗；
            // 未打开的文件 writer 返回 false，自动回退裸写盘。
            _stagedWorkspace.OpenDocumentWriter = EditBufferApplier.TryWriteOpenDocument;
            _stagedWorkspace.OpenDocumentContentProvider = EditBufferApplier.TryGetOpenDocumentContent;

            _stagedWorkspace.Discard(); // 清空上一轮残留

            EnsureEditTools(workspaceRoot);
            if (BuiltInTools != null)
                BuiltInTools.Workspace = _stagedWorkspace;

            // ── AI 调用循环（支持格式重试）──
            // messages 在循环外声明，重试时复用前一次的完整对话上下文（含工具调用结果），
            // 避免重复读取文件、重复搜索目录等浪费。
            var retryOutputs = new List<string>();
            List<ChatApiMessage>? messages = null;
            int stepPromptIndex = 0; // 步骤 prompt 在消息列表中的位置（重试时插入点）
            int stepToolLoopStart = 0; // 当前步骤工具循环新增消息的起点（排除转发/历史消息）

            for (int retry = 0; retry <= maxFormatRetries; retry++)
            {
                if (ct.IsCancellationRequested) return;

                if (retry == 0)
                {
                    // 首次尝试：创建全新的消息列表
                    messages = BuildContextAwareMessages(Definition.SystemPrompt, stepPrompt);
                    stepPromptIndex = messages.Count - 1; // 步骤 prompt 始终是最后一条
                    // 工具循环会把 assistant/tool 消息插入到末尾 agent 提示与用户消息之前。
                    // 因此基线必须指向 agent 提示之前，而不是消息列表末尾。
                    stepToolLoopStart = Math.Max(0, messages.Count - 2);
                }
                else
                {
                    // 重试：在步骤 prompt 之后、工具消息之前插入格式修正指令
                    // 这样 sys→history→step 前缀保持完整，DeepSeek 可缓存命中的 KV 不变
                    messages!.Insert(stepPromptIndex + 1, new ChatApiMessage
                    {
                        Role = "assistant",
                        Content = result // 上次的（格式错误）输出，作为对话上下文
                    });
                    messages.Insert(stepPromptIndex + 2, new ChatApiMessage
                    {
                        Role = "user",
                        Content = AiPrompts.EditFormatRecoveryPrompt
                    });
                }

                // ── 使用工具调用循环：AI 可以先探索再修改（v1.1.10：支持步骤内增量编辑）──
                // 编译统一交给步骤结束后的验证阶段（非 Planning）或计划完成后的最终构建，
                // 代码步骤内不提供 build_solution，避免同一轮出现两次构建。
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.callingAiToolLoop"], retry));
                var thinkingBuilder = new StringBuilder();
                var stepToolWhitelist = new List<string>(CodeStepTools);
                result = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 8192,
                    toolWhitelist: stepToolWhitelist,
                    onThinking: (thinking) =>
                    {
                        thinkingBuilder.Append(thinking);
                        context.OnThinkingChunk?.Invoke(thinking);
                    },
                    onContent: (content) =>
                    {
                        context.OnContentChunk?.Invoke(content);
                    },
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("TOOL", toolSummary);
                    });

                // ── 累积推理内容（累加所有步骤和重试轮次的思考过程）──
                if (thinkingBuilder.Length > 0)
                {
                    if (!string.IsNullOrEmpty(_accumulatedReasoning))
                        _accumulatedReasoning += "\n\n";
                    _accumulatedReasoning += thinkingBuilder.ToString();
                }

                retryOutputs.Add(result);

                // ── 检测 AI 是否明确表示没有要更改的内容 ──
                if (IsNoChangesResponse(result))
                {
                    // ── 但如果本轮有工具调用完成了编辑，则不视为空响应 ──
                    if (!HasToolMadeEdits(GetStepToolLoopMessages(messages!, stepToolLoopStart)))
                    {
                        AddLog("INFO", LocalizationService.Instance["agent.log.editEmptyResponse"]);
                        result = string.Empty; // 统一置空，后续流程据此跳过编辑
                        break;
                    }
                    AddLog("INFO", "[EditAgent] 文本回复为空但检测到工具编辑，继续处理");
                }

                // ── v1.1.10: 检测本轮是否通过工具完成了文件编辑 ──
                // 如果 AI 已在工具循环中直接修改了文件，则无需格式重试，
                // 文本回复视为操作总结而非编辑格式输出。
                bool hasToolEditsThisRound = HasToolMadeEdits(GetStepToolLoopMessages(messages!, stepToolLoopStart));
                if (hasToolEditsThisRound)
                {
                    AddLog("INFO", "[EditAgent] 检测到步骤内工具编辑，跳过编辑格式校验");
                    break;
                }

                // ── 纯 Git/终端操作跳过格式校验 ──
                // 如果本轮所有工具调用都是 git/终端/构建（无代码读取/编辑），
                // AI 的文本回复是操作总结而非编辑输出，无需格式重试。
                if (IsGitOrTerminalOnlyResult(GetStepToolLoopMessages(messages!, stepToolLoopStart)))
                {
                    AddLog("INFO", "[EditAgent] 纯 Git/终端操作，跳过编辑格式校验");
                    break;
                }

                // ── 检测编辑格式并解析（仅当 AI 未通过工具编辑时走此路径）──
                bool hasValidEdit = HasAnyValidEditFormat(result);
                if (hasValidEdit) break;

                if (retry < maxFormatRetries)
                    AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.invalidEditFormat"], retry + 1));
                else
                    AddLog("WARN", LocalizationService.Instance["agent.log.retriesExhausted"]);
            }

            // ── 保留所有重试输出，方便用户查看完整 AI 交互过程 ──
            if (retryOutputs.Count > 1)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < retryOutputs.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine(string.Format(LocalizationService.Instance["agent.log.editFormatRetryNotice"], i + 1));
                        sb.AppendLine();
                    }
                    sb.Append(retryOutputs[i]);
                }
                step.AiResponse = sb.ToString();
            }
            else
            {
                step.AiResponse = retryOutputs.FirstOrDefault() ?? "";
            }

            // ── v1.1.10: 提取工具循环中通过工具完成的文件编辑 ──
            // AI 可以在步骤内使用 replace_string_in_file / create_file 等工具增量编辑，
            // 而非强制一次性输出所有变更。此处从消息历史中提取编辑记录。
            var toolMadeEdits = ExtractToolMadeEdits(GetStepToolLoopMessages(messages!, stepToolLoopStart));
            bool hasToolEdits = toolMadeEdits.Count > 0;
            if (hasToolEdits)
            {
                AddLog("INFO", $"[EditAgent] 检测到步骤内 {toolMadeEdits.Count} 个工具编辑: {string.Join(", ", toolMadeEdits.Select(e => Path.GetFileName(e.FilePath)).Distinct())}");
            }

            // ── AI 明确表示没有要更改的内容 且 工具也未编辑文件 → 跳过编辑执行 ──
            if (string.IsNullOrWhiteSpace(result) && !hasToolEdits)
            {
                step.ResultSummary = LocalizationService.Instance["agent.log.editNoChangesConfirmed"];
                AddLog("INFO", LocalizationService.Instance["agent.log.editNoChange"]);
                return;
            }

            // ── 初始化编辑工具（懒加载，使用当前 workspaceRoot）──
            EnsureEditTools(workspaceRoot);

            // ── 保存原始文件内容（用于最终 diff 比较）──
            var originalContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var appliedResults = new List<EditApplyResult>();

            // ── operationType 提前声明（goto 路径需要可见）──
            var operationType = EditOperationType.ApplyPatch;

            // ── v1.1.10: 路径A — 工具编辑（AI 在工具循环中直接修改了文件）──
            // 工具编辑后需在 originalContents 中记录"原始"状态，防止文本路径重复处理时 diff 归零。
            var toolHandledFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hasToolEdits)
            {
                await CollectToolMadeEditsAsync(toolMadeEdits, plan, context, workspaceRoot,
                    originalContents, appliedResults, ct, toolHandledFiles);

                // ── 如果文本回复中也有编辑格式，作为补充处理（但排除已通过工具编辑的文件）──
                if (!string.IsNullOrWhiteSpace(result) && HasAnyValidEditFormat(result))
                {
                    AddLog("INFO", "[EditAgent] 工具编辑之外还检测到文本编辑格式，作为补充处理");
                    // 继续走下面的文本格式解析（合并模式），但跳过 toolHandledFiles 中的文件
                }
                else
                {
                    // 纯工具编辑：文本回复作为摘要，直接跳到变更收集阶段
                    result = string.Empty; // 清空，避免重复解析
                    goto SkipTextFormatParsing;
                }
            }

            // ── 路径B — 文本格式编辑（AI 通过文本输出编辑块）──
            if (!string.IsNullOrWhiteSpace(result))
            {
                // ── 如果工具路径已处理过某些文件，跳过文本路径的重复处理 ──
                if (toolHandledFiles.Count > 0)
                {
                    AddLog("INFO", $"[EditAgent] 跳过 {toolHandledFiles.Count} 个已由工具编辑的文件: {string.Join(", ", toolHandledFiles.Select(Path.GetFileName))}");
                }

                // ── 检测编辑操作类型 ──
                operationType = DetectOperationType(result);

                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editTypeDetected"], operationType));

                switch (operationType)
                {
                    case EditOperationType.ApplyPatch:
                        // ── 方法1：apply_patch ──
                        await ExecutePatchEditsAsync(result, plan, context, workspaceRoot,
                            originalContents, appliedResults, ct, toolHandledFiles);
                        break;

                    case EditOperationType.InsertEditIntoFile:
                        // ── 方法2：insert_edit_into_file ──
                        await ExecuteInsertEditsAsync(result, plan, context, workspaceRoot,
                            originalContents, appliedResults, ct, toolHandledFiles);
                        break;

                    case EditOperationType.CreateFile:
                    default:
                        // ── 方法3：create_file（原有逻辑）──
                        await ExecuteCreateFileEditsAsync(result, plan, context, workspaceRoot,
                            originalContents, appliedResults, ct, toolHandledFiles);
                        break;
                }

                // ── 处理文件删除（delete: 格式，原有逻辑）──
                await ProcessFileDeletionsAsync(result, plan, context, ct);
            }

        SkipTextFormatParsing:

            // ── 收集所有变更到 changes 列表（使用真实行数差异而非编辑块数量）──
            changes = appliedResults
                .Where(r => r.Success)
                .Select(r =>
                {
                    // 从 originalContents 计算真实行数变化（使用 diff 算法）
                    int realAdded = 0;
                    int realRemoved = 0;
                    if (originalContents.TryGetValue(r.FilePath, out string? original))
                    {
                        // RAG-SOURCE: file-read 读取最终文件内容（计算变更统计）
                        string final = File.Exists(r.FilePath)
                            ? File.ReadAllText(r.FilePath)
                            : (r.FinalContent ?? string.Empty);
                        CountDiffLines(original, final, out realAdded, out realRemoved);
                    }
                    else
                    {
                        // 新文件（未在 originalContents 中）：读取实际文件内容计算行数
                        if (File.Exists(r.FilePath))
                        {
                            string content = File.ReadAllText(r.FilePath);
                            realAdded = CountLines(content);
                        }
                        else if (!string.IsNullOrEmpty(r.FinalContent))
                        {
                            realAdded = CountLines(r.FinalContent!);
                        }
                        else
                        {
                            realAdded = r.AppliedEdits.Count > 0 ? r.AppliedEdits.Count : 1;
                        }
                    }

                    return new FileChangeSummary
                    {
                        FilePath = r.FilePath,
                        LinesAdded = realAdded,
                        LinesRemoved = realRemoved,
                        BriefDescription = $"{Path.GetFileName(r.FilePath)} ({r.OperationType})",
                    };
                })
                .Concat(plan.ChangedFiles)
                .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(c => c.LinesAdded + c.LinesRemoved).First())
                .ToList();

            var L = LocalizationService.Instance;

            // ── 汇总操作类型描述 ──
            // 优先使用工具编辑标签（当工具编辑已发生时，文本格式检测可能为误报）
            string operationTypeLabel;
            if (hasToolEdits)
            {
                operationTypeLabel = "tool_edit";
            }
            else if (!string.IsNullOrWhiteSpace(result))
            {
                operationTypeLabel = operationType.ToString();
            }
            else
            {
                operationTypeLabel = "unknown";
            }

            // ── 使用实际变更文件数（而非仅 appliedResults 中的成功计数）──
            int actualChangedFileCount = changes.Count > 0
                ? changes.Count
                : plan.ChangedFiles.Count;

            step.ResultSummary = actualChangedFileCount > 0
                ? string.Format(L["agent.log.editFilesModified"], actualChangedFileCount, operationTypeLabel)
                : string.Format(L["agent.log.editNoFilesChanged"], operationTypeLabel);

            // ── 编辑后健全性检查：检测括号不匹配等常见问题 ──
            string? sanityWarnings = null;
            if (changes.Count > 0)
            {
                var warnings = new List<string>();
                foreach (var ch in changes)
                {
                    if (!File.Exists(ch.FilePath)) continue;
                    // RAG-SOURCE: file-read 读取变更文件内容（括号匹配检查）
                    string content = await Task.Run(() => File.ReadAllText(ch.FilePath), ct);
                    int openBraces = content.Count(c => c == '{');
                    int closeBraces = content.Count(c => c == '}');
                    int openParens = content.Count(c => c == '(');
                    int closeParens = content.Count(c => c == ')');
                    if (openBraces != closeBraces)
                        warnings.Add($"`{Path.GetFileName(ch.FilePath)}`: {{ {openBraces} vs }} {closeBraces} (差 {openBraces - closeBraces})");
                    if (openParens != closeParens)
                        warnings.Add($"`{Path.GetFileName(ch.FilePath)}`: ( {openParens} vs ) {closeParens} (差 {openParens - closeParens})");
                }
                if (warnings.Count > 0)
                {
                    sanityWarnings = string.Join("; ", warnings);
                    AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.braceParenMismatch"], sanityWarnings));

                    // ── 注入 step.AiResponse 确保警告即使跳过验证阶段也不会丢失 ──
                    step.AiResponse = (step.AiResponse ?? "") +
                        string.Format(LocalizationService.Instance["agent.log.editBraceParenWarningHeader"], sanityWarnings) +
                        LocalizationService.Instance["agent.log.editBraceParenWarningHint"];
                }
            }

            // ── 编译修复阶段：构建失败时由 AI 读取错误并直接修复 ──
            // v1.1.12: 当用户关闭自动编译或在提示中要求跳过时，跳过整个验证阶段
            if (changes.Count > 0 && !ct.IsCancellationRequested && !context.IsPlanningMode
                && !ShouldSkipAutoBuild())
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.verifyPhaseStarted"]);

                // ── 验证阶段专用 system prompt（从 i18n 加载，支持中英切换）──
                //  缓存优化：作为 extraSystemMessages 注入而非替换 messages[0]，
                //    保持 Definition.SystemPrompt 在 messages[0] 不变，
                //    使 DeepSeek Prompt Cache 能命中编辑阶段已缓存的前缀。
                string verifySystemPrompt = LocalizationService.Instance.Format(
                    "system.agent.verifyPromptFragment",
                    workspaceRoot,
                    string.Join("\n", changes.Select(c => $"- `{c.FilePath}`")));

                // ── 验证阶段专用工具白名单：build + 只读 + 编辑工具（不含探索工具）──
                var verifyToolWhitelist = new List<string>(VerifyPhaseTools);

                // ── 将验证专用指令作为额外 system 消息注入，保持 messages[0] 不变 ──
                var verifyExtraSystemMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = verifySystemPrompt }
                };

                // ── 构建验证阶段的探索上下文摘要（注入已读取的文件信息，避免重复探索）──
                string verifyExploreContext = "";
                if (BuiltInTools != null)
                {
                    var verifyFileCache = BuiltInTools.GetFileReadCacheSnapshot();
                    if (verifyFileCache.Count > 0)
                    {
                        var sbCtx = new StringBuilder();
                        sbCtx.AppendLine("\n## 探索阶段已读取的文件（已缓存，可直接使用）");
                        sbCtx.AppendLine("> 以下文件已在编辑前被读取并缓存。验证阶段可直接引用，无需重复 read_file。");
                        int count = 0;
                        foreach (var kvp in verifyFileCache.Take(10))
                        {
                            sbCtx.AppendLine($"- `{kvp.Key}` ({kvp.Value.Length} 字符)");
                            count++;
                        }
                        if (verifyFileCache.Count > 10)
                            sbCtx.AppendLine($"> ... 还有 {verifyFileCache.Count - 10} 个已缓存文件");
                        verifyExploreContext = sbCtx.ToString();
                    }
                }

                string verifyUserMessage = string.Format(
                    AiPrompts.EditVerifyUserMessage,
                    changes.Count,
                    string.Join(", ", changes.Select(c => Path.GetFileName(c.FilePath))),
                    verifyExploreContext,
                    sanityWarnings != null
                        ? $" **Sanity check found potential issues**: {sanityWarnings}\nPlease pay special attention to bracket/parenthesis matching; use read_file to check near file ends if needed.\n\n"
                        : "\n");

                // ── 使用 Definition.SystemPrompt 保持缓存前缀，验证指令通过 extraSystemMessages 注入 ──
                var verifyMessages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    verifyUserMessage,
                    verifyExtraSystemMessages);

                string verifyResult = await CallAiWithToolLoopAsync(
                    verifyMessages,
                    workspaceRoot,
                    ct,
                    maxTokens: 8192,
                    toolWhitelist: verifyToolWhitelist,
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("TOOL", toolSummary);
                    });

                if (!string.IsNullOrWhiteSpace(verifyResult))
                {
                    step.AiResponse = (step.AiResponse ?? "") + LocalizationService.Instance["agent.log.editVerifyHeader"] + verifyResult;
                    AddLog("INFO", LocalizationService.Instance["agent.log.verifyPhaseComplete"]);

                    // ── 追踪验证阶段的文件变更到 plan.ChangedFiles ──
                    TrackVerifyPhaseChanges(verifyMessages, plan);

                    // ── 智能检测编译是否真的失败 ──
                    // 避免因 AI 回复中的否定表述（如"没有错误"）误报警告
                    if (HasBuildFailure(verifyResult))
                    {
                        AddLog("WARN", LocalizationService.Instance["agent.log.editBuildWarning"]);
                    }
                }
            }
            else if (changes.Count > 0 && context.IsPlanningMode)
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.planningSkipVerify"]);
            }

            // ── 编辑后诊断检查 ──
            if (appliedResults.Count > 0)
            {
                foreach (var editResult in appliedResults.Where(r => r.Success))
                {
                    var newDiags = await EditPatchService.CheckNewDiagnosticsAsync(editResult.FilePath);
                    if (newDiags.Count > 0)
                    {
                        editResult.NewDiagnostics = newDiags;
                        AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.newDiagnostics"],
                            Path.GetFileName(editResult.FilePath), newDiags.Count,
                            string.Join("; ", newDiags.Take(5))));
                    }
                }
            }

            // ── 使已修改文件的读取缓存失效，确保后续步骤读取到最新内容 ──
            if (BuiltInTools != null && appliedResults.Count > 0)
            {
                var modifiedPaths = appliedResults
                    .Where(r => r.Success)
                    .Select(r => r.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                BuiltInTools.InvalidateFileReadCache(modifiedPaths);
            }

            // ── 恢复 diff 预览，从 Workspace 生成 Batch 并创建 Session ──
            var batch = _stagedWorkspace!.ToPreparedChangeBatch();

            if (batch.Changes.Count > 0)
            {
                foreach (var change in batch.Changes)
                {
                    // RAG-SOURCE: file-read 读取最终文件内容（diff 预览对比）
                    string finalContent = change.ProposedText;
                    string oldContent = change.BaselineText;
                    if (oldContent != finalContent)
                    {
                        // 写穿模式：已落盘，撤销时通过 _stagedWorkspace 恢复磁盘 Baseline
                        await TerminalWindowHelper.ShowFinalDiffAsync(
                            oldContent, finalContent, change.FilePath, _stagedWorkspace);
                    }
                }
            }
            else
            {
                // 无 Workspace 变更 → 保持旧版路径兼容
                foreach (var kvp in originalContents)
                {
                    string finalContent = File.Exists(kvp.Key)
                        ? await Task.Run(() => File.ReadAllText(kvp.Key), ct)
                        : string.Empty;
                    if (kvp.Value != finalContent)
                    {
                        await TerminalWindowHelper.ShowFinalDiffAsync(kvp.Value, finalContent, kvp.Key);
                    }
                }
            }

            // ── v1.1.10: 步骤完成自检 — 对比步骤描述中的文件名与实际修改 ──
            // 如果步骤描述明确提到了某文件但未在本次编辑中修改，记录提示。
            PerformStepCompletenessCheck(step, appliedResults, workspaceRoot);
        }

        /// <summary>
        /// 步骤完整性自检（v1.1.10）。
        /// 对比步骤描述中引用的文件与实际修改的文件，发现遗漏时记录警告。
        /// 不阻断执行，仅作为日志提示供用户参考。
        /// </summary>
        private void PerformStepCompletenessCheck(
            AgentStep step, List<EditApplyResult> appliedResults, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(step.Description) || appliedResults.Count == 0)
                return;

            // 从步骤描述中提取文件引用（匹配常见代码文件扩展名）
            var filePattern = @"\b(\w+\.(?:cs|ts|js|py|java|cpp|h|hpp|xml|json|yaml|yml|md|csproj|sln|vb|fs|cshtml|razor|css|scss|html|xaml|config|props|targets))\b";
            var matches = System.Text.RegularExpressions.Regex.Matches(step.Description, filePattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (matches.Count == 0) return;

            var mentionedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                mentionedFiles.Add(m.Groups[1].Value);
            }

            // 收集实际修改的文件名
            var actuallyModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in appliedResults.Where(r => r.Success))
            {
                actuallyModified.Add(Path.GetFileName(r.FilePath));
            }

            // 找出步骤描述中提到但未修改的文件
            var untouched = mentionedFiles
                .Where(f => !actuallyModified.Contains(f))
                .ToList();

            if (untouched.Count > 0)
            {
                AddLog("WARN", string.Format(
 "[EditAgent]  步骤自检：步骤描述中提到了 {0} 个文件，但以下文件未被本次编辑修改: {1}。" +
                    "如果这些修改在后续步骤中完成则可忽略，否则可能是遗漏。",
                    mentionedFiles.Count,
                    string.Join(", ", untouched)));
            }
        }

        /// <summary>
        /// 从 AI 响应中检测是否声明了后续步骤也已完成（v1.1.10）。
        /// 解析 AI 输出中的步骤完成声明（如"步骤2和3也完成了"/"also completed step 2"），
        /// 自动将对应步骤标记为 Completed。比文件级启发式更可靠，尤其适用于同文件多步骤场景。
        /// v1.1.12: 支持范围式声明（"步骤1-6 已完成"），并合并扫描思考内容（thinkingContent），
        /// 因为 AI 可能在思考过程中声明步骤完成而正式输出未提及。
        /// </summary>
        private void DetectAndAutoCompleteLaterSteps(AgentStep completedStep, AgentTaskPlan plan, string? thinkingContent = null)
        {
            if (string.IsNullOrWhiteSpace(completedStep.AiResponse) && string.IsNullOrWhiteSpace(thinkingContent)) return;

            // 检测范围：正式输出 + 思考内容（合并后同一套正则均生效）
            string response = (completedStep.AiResponse ?? "") + "\n[思考内容]\n" + (thinkingContent ?? "");
            var autoCompletedIndices = new HashSet<int>();

            // 模式1: 中文 "步骤2、3也完成了" / "步骤2和3已完成" / "也完成了步骤2,3"
            var cnPatterns = new[]
            {
                @"步骤\s*(\d+(?:[、,，\s]+(?:和|及|与)?\s*\d+)*)\s*(?:也|已|同样|一并|同时)?\s*(?:完成|做完|搞定)",
                @"(?:也|已|同样|一并|同时)\s*(?:完成|做完|搞定)了?\s*步骤\s*(\d+(?:[、,，\s]+(?:和|及|与)?\s*\d+)*)",
            };
            foreach (var pattern in cnPatterns)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(response, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    ParseStepNumbers(m.Groups[1].Value, autoCompletedIndices);
                }
            }

            // 模式2: 英文 "also completed step 2,3" / "steps 2 and 3 are done"
            var enPatterns = new[]
            {
                @"(?:also\s+)?(?:completed?|finished?|done)\s+steps?\s*(\d+(?:[,\s]+(?:and\s+)?\d+)*)",
                @"steps?\s*(\d+(?:[,\s]+(?:and\s+)?\d+)*)\s*(?:are\s+)?(?:also\s+)?(?:done|completed?|finished?)",
            };
            foreach (var pattern in enPatterns)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(response, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    ParseStepNumbers(m.Groups[1].Value, autoCompletedIndices);
                }
            }

            // 模式3（原"勾号+步骤"）：勾号字形已随 emoji 清理移除，
            // 无完成词的裸步骤列表不再作为自动完成依据，避免误标

            // 模式4: 范围式声明 "步骤1-6 已完成" / "步骤1~6" / "步骤1到6" / "步骤1至6"
            // "steps 1 through 6 done" / "steps 1-6 completed"
            var rangePatterns = new[]
            {
                // "步骤2-4 也随之完成" / "步骤1~6 已完成" / "步骤1-6做完"
                @"步骤\s*(\d+)\s*[-~—–]\s*(\d+)\s*[也已均都随之一同顺并]*\s*(?:完成|做完|搞定)",
                @"步骤\s*(\d+)\s*(?:到|至|一直到)\s*(\d+)\s*[也已均都随之一同顺并]*\s*(?:完成|做完|搞定)",
                @"(?:也|已|同样|一并|同时)\s*完成(?:了)?\s*步骤\s*(\d+)\s*[-~—–到至]\s*(\d+)",
                @"steps?\s*(\d+)\s*(?:through|to|-|~|&amp;)\s*(\d+)\s*(?:are\s+)?(?:also\s+)?(?:done|completed|finished)",
            };
            foreach (var pattern in rangePatterns)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(response, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out int start) && int.TryParse(m.Groups[2].Value, out int end))
                    {
                        if (start > 0 && end >= start && end - start <= 50)
                        {
                            for (int n = start; n <= end; n++)
                                autoCompletedIndices.Add(n);
                        }
                    }
                }
            }

            // 过滤：仅处理当前步骤之后的 Pending 步骤
            var toAutoComplete = autoCompletedIndices
                .Where(idx => idx > completedStep.Index && idx <= plan.Steps.Count)
                .Select(idx => plan.Steps[idx - 1])
                .Where(s => s.Status == AgentStepStatus.Pending)
                .ToList();

            foreach (var s in toAutoComplete)
            {
                s.Status = AgentStepStatus.Completed;
                s.ResultSummary = $"(由步骤{completedStep.Index}的 AI 输出自动标记完成)";
                AddLog("INFO", $"[EditAgent]  步骤{s.Index}「{s.Title}」由 AI 声明完成，自动标记");
            }

            if (toAutoComplete.Count > 0)
            {
                // 推进 CurrentStepIndex 到最后一个已完成/跳过步骤，
                // 使 UI 顶栏进度(如 "6/10")随之同步
                // （步骤2..n 由 AI 声明完成时若仍停在旧索引，状态栏会一直显示旧计数）。
                int advanced = completedStep.Index;
                for (int idx = completedStep.Index + 1; idx <= plan.Steps.Count; idx++)
                {
                    var s = plan.Steps[idx - 1];
                    if (s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped)
                        advanced = idx;
                    else
                        break;
                }
                plan.CurrentStepIndex = advanced;
                NotifyPlanUpdated();
            }
        }

        /// <summary>
        /// 解析步骤编号字符串（如 "2、3、5" 或 "2,3,5" 或 "2 and 3"）并加入集合。
        /// </summary>
        private static void ParseStepNumbers(string text, HashSet<int> result)
        {
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(text, @"\d+"))
            {
                if (int.TryParse(m.Value, out int num) && num > 0)
                    result.Add(num);
            }
        }

        /// <summary>
        /// Plan 级别变更追踪（v1.1.10）。
        /// 对比所有步骤描述中引用的文件与实际修改的文件，
        /// 发现遗漏时在日志中提示，帮助用户判断计划是否执行完整。
        /// </summary>
        private void PerformPlanChangeTracking(AgentTaskPlan plan, AgentContext context)
        {
            if (plan.Steps.Count == 0 || plan.ChangedFiles.Count == 0)
                return;

            // 从所有步骤描述中提取文件引用
            var filePattern = @"\b(\w+\.(?:cs|ts|js|py|java|cpp|h|hpp|xml|json|yaml|yml|md|csproj|sln|vb|fs|cshtml|razor|css|scss|html|xaml|config|props|targets))\b";
            var allMentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in plan.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Description)) continue;
                var matches = System.Text.RegularExpressions.Regex.Matches(step.Description, filePattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    allMentioned.Add(m.Groups[1].Value);
                }
            }

            if (allMentioned.Count == 0) return;

            // 收集所有实际修改的文件名
            var allModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in plan.ChangedFiles)
            {
                allModified.Add(Path.GetFileName(ch.FilePath));
            }

            // 计划中提到但完全没被修改的文件
            var neverTouched = allMentioned
                .Where(f => !allModified.Contains(f))
                .ToList();

            // 实际修改但计划中未提到的文件（额外修改，可能是好事也可能是跑偏）
            var extraModified = allModified
                .Where(f => !allMentioned.Contains(f))
                .ToList();

            if (neverTouched.Count > 0)
            {
                AddLog("WARN", string.Format(
 "[EditAgent]  Plan 追踪：计划步骤中引用了 {0} 个文件，其中 {1} 个文件在所有步骤中均未被修改: {2}。" +
                    "请确认这些文件是否确实无需修改，或是否存在遗漏。",
                    allMentioned.Count, neverTouched.Count,
                    string.Join(", ", neverTouched.Take(10))));
            }

            if (extraModified.Count > 0)
            {
                AddLog("INFO", string.Format(
 "[EditAgent]  Plan 追踪：实际修改了 {0} 个计划中未明确列出的文件: {1}。" +
                    "这可能是合理的关联修改，也可能是范围蔓延。",
                    extraModified.Count,
                    string.Join(", ", extraModified.Take(10))));
            }

            if (neverTouched.Count == 0 && extraModified.Count == 0)
            {
                AddLog("INFO", $"[EditAgent]  Plan 追踪：计划中引用的 {allMentioned.Count} 个文件与实际修改一致 ");
            }
        }

        #region Sub-methods for each edit format

        /// <summary>
        /// 执行 apply_patch 格式的编辑（使用 ApplyPatchTool）。
        /// </summary>
        private async Task ExecutePatchEditsAsync(
            string aiResult, AgentTaskPlan plan, AgentContext context,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct,
            HashSet<string>? toolHandledFiles = null)
        {
            if (_applyPatchTool == null)
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.patchServiceMissing"]);
                return;
            }

            var patches = ApplyPatchTool.ParsePatches(aiResult);
            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.parsedPatches"], patches.Count));

            // ── v1.1.11: 检测并告警与工具编辑重叠的文件，但不跳过（支持同一文件多次编辑）──
            if (toolHandledFiles != null && toolHandledFiles.Count > 0)
            {
                var overlapFiles = patches
                    .Select(p => EditPatchService.ResolvePath(p.FilePath, workspaceRoot))
                    .Where(p => toolHandledFiles.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (overlapFiles.Count > 0)
                {
                    AddLog("WARN", $"[EditAgent] ApplyPatch 目标中有 {overlapFiles.Count} 个文件已被工具编辑过（将基于工具编辑后的内容应用补丁）: {string.Join(", ", overlapFiles.Select(Path.GetFileName))}");
                }
            }

            // ── 项目文件审批：在执行前检查所有 patch 目标，对项目文件请求用户确认 ──
            var approvedPatches = new List<PatchOperation>();
            foreach (var patch in patches)
            {
                string resolvedPath = EditPatchService.ResolvePath(patch.FilePath, workspaceRoot);
                if (IsProjectFile(resolvedPath))
                {
                    string fileName = Path.GetFileName(resolvedPath);
                    string patchPreview = patch.Hunks != null && patch.Hunks.Count > 0
                        ? string.Join("\n", patch.Hunks.Select(h =>
                            h.RawText.TrimEnd('\n', '\r')))
                        : "(无 hunk 详情)";
                    bool confirmed = await EnsureProjectFileWriteConfirmedAsync(
                        resolvedPath,
                        $"Patch 修改项目文件: {fileName}",
                        "",
                        $"向 `{fileName}` 应用代码补丁以完成项目配置修改\n\n补丁预览:\n{patchPreview}");
                    if (!confirmed)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.editProjectPatchSkipped", fileName));
                        appliedResults.Add(new EditApplyResult
                        {
                            FilePath = resolvedPath,
                            Success = false,
                            OperationType = EditOperationType.ApplyPatch,
                            ErrorMessage = LocalizationService.Instance["agent.log.editPermissionDeniedGeneric"],
                        });
                        continue;
                    }
                }
                approvedPatches.Add(patch);
            }

            // ── 保存原始内容（执行前读取，确保 diff 计算准确）──
            foreach (var patch in approvedPatches)
            {
                string resolvedPath = EditPatchService.ResolvePath(patch.FilePath, workspaceRoot);
                if (!originalContents.ContainsKey(resolvedPath))
                {
                    string original = File.Exists(resolvedPath)
                        ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                        : string.Empty;
                    originalContents[resolvedPath] = original;
                }
            }

            // ── 使用 ApplyPatchTool 批量执行（内置 Healing + 原子性）──
            var results = await _applyPatchTool.ExecutePatchesAsync(approvedPatches, ct);

            foreach (var applyResult in results)
            {
                string resolvedPath = applyResult.FilePath;

                if (applyResult.Success)
                {
                    AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.patchApplied"], resolvedPath, applyResult.AppliedEdits.Count));

                    // ── 新文件处理 ──
                    var patch = approvedPatches.FirstOrDefault(p =>
                        string.Equals(EditPatchService.ResolvePath(p.FilePath, workspaceRoot), resolvedPath, StringComparison.OrdinalIgnoreCase));
                    bool isNewFile = patch?.Action == PatchFileAction.Add;

                    if (isNewFile)
                    {
                        bool writeAllowed = await EnsureProjectFileWriteConfirmedAsync(
                            resolvedPath, $"Patch 新建文件", applyResult.FinalContent ?? string.Empty);
                        if (writeAllowed && File.Exists(resolvedPath))
                        {
                            await AddFileToProjectAsync(resolvedPath, ct);
                        }
                    }

                    NotifyFileChange(plan.PlanId,
                        isNewFile ? "create" : "modify",
                        resolvedPath,
                        string.Format(LocalizationService.Instance["agent.log.patchEditPoints"], applyResult.AppliedEdits.Count));

                    // ── 更新 plan.ChangedFiles ──
                    if (!plan.ChangedFiles.Any(c => string.Equals(c.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        int added = 0, removed = 0;
                        if (originalContents.TryGetValue(resolvedPath, out string? orig))
                        {
                            string final = applyResult.FinalContent ?? orig;
                            CountDiffLines(orig, final, out added, out removed);
                        }
                        else { added = applyResult.AppliedEdits.Count; }

                        plan.ChangedFiles.Add(new FileChangeSummary
                        {
                            FilePath = resolvedPath,
                            LinesAdded = added,
                            LinesRemoved = removed,
                            BriefDescription = $"{Path.GetFileName(resolvedPath)} (patch)",
                        });
                    }
                }
                else
                {
                    AddLog("ERROR", LocalizationService.Instance.Format("agent.log.editPatchFailed", resolvedPath, applyResult.ErrorMessage));
                }

                appliedResults.Add(applyResult);
            }
        }

        /// <summary>
        /// 执行 insert_edit_into_file 格式的编辑（使用 InsertEditTool）。
        /// </summary>
        private async Task ExecuteInsertEditsAsync(
            string aiResult, AgentTaskPlan plan, AgentContext context,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct,
            HashSet<string>? toolHandledFiles = null)
        {
            if (_insertEditTool == null)
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.editNoInsertEditTool"]);
                return;
            }

            var insertEdits = InsertEditTool.ParseInsertEdits(aiResult);
            AddLog("INFO", LocalizationService.Instance.Format("agent.log.editInsertEditsParsed", insertEdits.Count));

            // ── v1.1.11: 检测并告警与工具编辑重叠的文件，但不跳过（支持同一文件多次编辑）──
            if (toolHandledFiles != null && toolHandledFiles.Count > 0)
            {
                var overlapFiles = insertEdits
                    .Select(e => EditPatchService.ResolvePath(e.FilePath, workspaceRoot))
                    .Where(p => toolHandledFiles.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (overlapFiles.Count > 0)
                {
                    AddLog("WARN", $"[EditAgent] InsertEdit 目标中有 {overlapFiles.Count} 个文件已被工具编辑过（将基于工具编辑后的内容应用编辑）: {string.Join(", ", overlapFiles.Select(Path.GetFileName))}");
                }
            }

            // ── 排序：项目配置优先，构建定义文件最后 ──
            var sortedEdits = insertEdits
                .OrderBy(e => GetEditPriority(EditPatchService.ResolvePath(e.FilePath, workspaceRoot)))
                .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ── 项目文件审批：在执行前检查所有 InsertEdit 目标，对项目文件请求用户确认 ──
            var approvedEdits = new List<InsertEditOperation>();
            foreach (var edit in sortedEdits)
            {
                string resolvedPath = EditPatchService.ResolvePath(edit.FilePath, workspaceRoot);
                if (IsProjectFile(resolvedPath))
                {
                    bool confirmed = await EnsureProjectFileWriteConfirmedAsync(
                        resolvedPath,
                        string.Format(LocalizationService.Instance["agent.edit.insertEditModifyProject"], Path.GetFileName(resolvedPath)),
                        "",
                        string.Format(LocalizationService.Instance["agent.edit.projectConfigChange"], Path.GetFileName(resolvedPath)));
                    if (!confirmed)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.editInsertEditSkipped", Path.GetFileName(resolvedPath)));
                        appliedResults.Add(new EditApplyResult
                        {
                            FilePath = resolvedPath,
                            Success = false,
                            OperationType = EditOperationType.InsertEditIntoFile,
                            ErrorMessage = LocalizationService.Instance["agent.log.editPermissionDeniedGeneric"],
                        });
                        continue;
                    }
                }
                approvedEdits.Add(edit);
            }

            // ── 保存原始内容（执行前读取，确保 diff 计算准确）──
            foreach (var edit in approvedEdits)
            {
                string resolvedPath = EditPatchService.ResolvePath(edit.FilePath, workspaceRoot);
                if (!originalContents.ContainsKey(resolvedPath))
                {
                    string original = File.Exists(resolvedPath)
                        ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                        : string.Empty;
                    originalContents[resolvedPath] = original;
                }
            }

            // ── 使用 InsertEditTool 批量执行（内置 Healing + create_file 兜底）──
            var results = await _insertEditTool.ExecuteInsertEditsAsync(approvedEdits, ct);

            foreach (var applyResult in results)
            {
                string resolvedPath = applyResult.FilePath;

                if (applyResult.Success)
                {
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.editInsertEditApplied", resolvedPath, applyResult.AppliedEdits.Count));

                    // ── 项目文件拦截 ──
                    if (!string.IsNullOrEmpty(applyResult.FinalContent))
                    {
                        bool writeAllowed = await EnsureProjectFileWriteConfirmedAsync(
                            resolvedPath, string.Format(LocalizationService.Instance["agent.edit.editPoints"], applyResult.AppliedEdits.Count), applyResult.FinalContent!);
                        if (!writeAllowed)
                        {
                            AddLog("WARN", LocalizationService.Instance.Format("agent.log.editWriteSkipped", Path.GetFileName(resolvedPath)));
                            applyResult.Success = false;
                            applyResult.ErrorMessage = LocalizationService.Instance["agent.log.editPermissionDeniedGeneric"];
                        }
                    }

                    if (applyResult.Success)
                    {
                        NotifyFileChange(plan.PlanId, "modify", resolvedPath,
                            string.Format(LocalizationService.Instance["agent.log.patchEditPoints"], applyResult.AppliedEdits.Count));

                        if (!plan.ChangedFiles.Any(c => string.Equals(c.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            int added = 0, removed = 0;
                            if (originalContents.TryGetValue(resolvedPath, out string? orig))
                            {
                                string final = applyResult.FinalContent ?? orig;
                                CountDiffLines(orig, final, out added, out removed);
                            }
                            else { added = applyResult.AppliedEdits.Count; }

                            plan.ChangedFiles.Add(new FileChangeSummary
                            {
                                FilePath = resolvedPath,
                                LinesAdded = added,
                                LinesRemoved = removed,
                                BriefDescription = $"{Path.GetFileName(resolvedPath)} (InsertEdit)",
                            });
                        }
                    }
                }
                else
                {
                    AddLog("ERROR", LocalizationService.Instance.Format("agent.log.editInsertEditFailed", resolvedPath, applyResult.ErrorMessage));
                }

                appliedResults.Add(applyResult);
            }
        }

        /// <summary>
        /// 执行 create_file 格式的编辑（原有 ```file: 逻辑）。
        /// </summary>
        private async Task ExecuteCreateFileEditsAsync(
            string aiResult, AgentTaskPlan plan, AgentContext context,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct,
            HashSet<string>? toolHandledFiles = null)
        {
            var changes = ParseCodeChangesFromResult(aiResult);

            // ── v1.1.11: 检测并告警与工具编辑重叠的文件，但不跳过（支持同一文件多次编辑）──
            if (toolHandledFiles != null && toolHandledFiles.Count > 0)
            {
                var overlapFiles = changes
                    .Select(c => ResolveFilePath(c.FilePath, context.SolutionPath))
                    .Where(p => toolHandledFiles.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (overlapFiles.Count > 0)
                {
                    AddLog("WARN", $"[EditAgent] CreateFile 目标中有 {overlapFiles.Count} 个文件已被工具编辑过（将基于工具编辑后的内容写入）: {string.Join(", ", overlapFiles.Select(Path.GetFileName))}");
                }
            }

            // ── 排序：项目配置优先（避免 VS 冲突对话框），构建定义文件最后（CMakeLists.txt 必须在源文件后写入）──
            var sortedChanges = changes
                .OrderBy(c => GetEditPriority(ResolveFilePath(c.FilePath, context.SolutionPath)))
                .ThenBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var change in sortedChanges)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string resolvedPath = ResolveFilePath(change.FilePath, context.SolutionPath);
                    change.FilePath = resolvedPath;

                    // 保存原始内容
                    if (!originalContents.ContainsKey(resolvedPath))
                    {
                        // RAG-SOURCE: file-read 读取文件原始内容（CreateFile 前保存）
                        string original = File.Exists(resolvedPath)
                            ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                            : string.Empty;
                        originalContents[resolvedPath] = original;
                        change.OriginalContent = original;
                    }
                    else
                    {
                        change.OriginalContent = originalContents[resolvedPath];
                    }

                    bool isNewFile = !File.Exists(resolvedPath);
                    if (isNewFile && _stagedWorkspace == null)
                    {
                        // ── 仅直接写盘模式：预创建空文件并加入项目 ──
                        string? dir = Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        // 已在编辑器打开（如同名文档）→ buffer+编辑器 Save；否则裸写盘
                        bool createdViaBuffer = await EditBufferApplier.TryWriteOpenDocumentAsync(
                            resolvedPath, string.Empty);
                        if (!createdViaBuffer)
                            await Task.Run(() => File.WriteAllText(resolvedPath, string.Empty, System.Text.Encoding.UTF8), ct);

                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editPreCreateFile", Path.GetFileName(resolvedPath)));
                        await AddFileToProjectAsync(resolvedPath, ct);
                    }
                    else if (isNewFile && _stagedWorkspace != null)
                    {
                        AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editStagedNewFile"],
                            Path.GetFileName(resolvedPath)));
                    }

                    // ── 项目文件拦截：新建/修改 .vcxproj/.sln 等前请求用户确认 ──
                    string createOpDesc = isNewFile
                        ? string.Format(LocalizationService.Instance["agent.edit.newProjectFile"], Path.GetFileName(resolvedPath))
                        : string.Format(LocalizationService.Instance["agent.edit.modifyFile"], Path.GetFileName(resolvedPath), change.LinesAdded, change.LinesRemoved);
                    bool createWriteAllowed = await EnsureProjectFileWriteConfirmedAsync(resolvedPath, createOpDesc, change.NewContent ?? string.Empty);
                    if (!createWriteAllowed)
                    {
                        AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.editProjectFileWriteSkipped"], Path.GetFileName(resolvedPath)));
                        appliedResults.Add(new EditApplyResult
                        {
                            FilePath = resolvedPath,
                            Success = false,
                            OperationType = EditOperationType.CreateFile,
                            ErrorMessage = LocalizationService.Instance["agent.log.editPermissionDeniedGeneric"],
                        });
                        continue;
                    }

                    string? error = null;

                    if (_stagedWorkspace != null)
                    {
                        // ── Workspace 模式：暂存到 Workspace，不写盘（由 Agent 结束统一提交）──
                        _stagedWorkspace.WriteFile(resolvedPath, change.NewContent ?? string.Empty);
                        AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.fileStaged"],
                            resolvedPath, change.LinesAdded, change.LinesRemoved));
                    }
                    else
                    {
                        // ── 直接写盘模式（旧版兼容 / 无 Workspace 场景）──
                        error = await TerminalWindowHelper.WriteCodeToFileAsync(
                            resolvedPath, change.NewContent ?? string.Empty);
                    }

                    if (error == null)
                    {
                        AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.fileWritten"],
                            resolvedPath, change.LinesAdded, change.LinesRemoved));
                        plan.ChangedFiles.Add(change);

                        string changeType = isNewFile ? "create" : "modify";
                        string detail = $"+{change.LinesAdded} -{change.LinesRemoved}";
                        NotifyFileChange(plan.PlanId, changeType, resolvedPath, detail);

                        appliedResults.Add(new EditApplyResult
                        {
                            FilePath = resolvedPath,
                            Success = true,
                            OperationType = EditOperationType.CreateFile,
                        });
                    }
                    else
                    {
                        AddLog("ERROR", LocalizationService.Instance.Format("agent.log.editWriteFailed", resolvedPath, error));
                        appliedResults.Add(new EditApplyResult
                        {
                            FilePath = resolvedPath,
                            Success = false,
                            OperationType = EditOperationType.CreateFile,
                            ErrorMessage = error,
                        });
                    }
                }
                catch (Exception ex)
                {
                    AddLog("ERROR", LocalizationService.Instance.Format("agent.log.editWriteError", change.FilePath, ex.Message));
                }
            }
        }

        /// <summary>
        /// 处理 delete: / delete_file: 格式的文件删除。
        /// </summary>
        private async Task ProcessFileDeletionsAsync(
            string aiResult, AgentTaskPlan plan, AgentContext context, CancellationToken ct)
        {
            var deletions = ParseFileDeletionsFromResult(aiResult);
            if (deletions.Count == 0 || ct.IsCancellationRequested) return;

            var resolvedDeletions = deletions
                .Select(d => ResolveFilePath(d, context.SolutionPath))
                .Where(d => File.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (resolvedDeletions.Count == 0) return;

            AddLog("INFO", LocalizationService.Instance.Format("agent.log.editDeletionsDetected", resolvedDeletions.Count, string.Join(", ", resolvedDeletions.Select(Path.GetFileName))));

            var deletionOriginals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string deletedPath in resolvedDeletions)
            {
                try
                {
                    if (File.Exists(deletedPath))
                    {
                        // RAG-SOURCE: file-read 读取待删除文件原始内容（备份）
                        string original = await Task.Run(() => File.ReadAllText(deletedPath), ct);
                        deletionOriginals[deletedPath] = original;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[EditAgent] 无法读取待删除文件原始内容: {deletedPath} - {ex.Message}");
                }
            }

            string deleteReason = plan.Title ?? LocalizationService.Instance["agent.log.editDefaultDeleteReason"];
            string deletePurpose = string.Format(LocalizationService.Instance["agent.log.editDeletePurpose"], deleteReason);
            bool confirmed = await RequestFileDeleteConfirmationAsync(resolvedDeletions, deleteReason, deletePurpose);

            if (confirmed)
            {
                await AgentFactory.DeleteFilesViaEnvDTEAsync(resolvedDeletions);
                AddLog("INFO", LocalizationService.Instance.Format("agent.log.editDeletionsDone", resolvedDeletions.Count));

                foreach (string deletedPath in resolvedDeletions)
                {
                    deletionOriginals.TryGetValue(deletedPath, out string? capturedOriginal);
                    plan.ChangedFiles.Add(new FileChangeSummary
                    {
                        FilePath = deletedPath,
                        LinesAdded = 0,
                        LinesRemoved = -1,
                        BriefDescription = $"{Path.GetFileName(deletedPath)}{LocalizationService.Instance["agent.log.editFileDeletedSuffix"]}",
                        OriginalContent = capturedOriginal,
                    });
                    NotifyFileChange(plan.PlanId, "delete", deletedPath, LocalizationService.Instance["agent.log.editNotifiedDeleted"]);
                }
            }
            else
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.editDeletionsCancelled"]);
            }
        }

        /// <summary>
        /// 检测 AI 输出是否包含任何有效的编辑格式。
        /// </summary>
        /// <summary>
        /// 检测 AI 是否明确表示没有需要更改的内容（空响应、或明确说明无需修改）。
        /// 用于格式重试循环中，让 AI 可以选择"输出空"来表示该步骤已无变更。
        /// </summary>
        private static bool IsNoChangesResponse(string aiResult)
        {
            if (string.IsNullOrWhiteSpace(aiResult)) return true;

            // 去除 DSML/XML 标签后再判断
            string clean = System.Text.RegularExpressions.Regex.Replace(aiResult,
                @"<\|DSML\|[^>]*>.*?</\|DSML\|>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);

            // 去掉 markdown 代码块内容（可能包含示例代码被误判）
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                @"```[\s\S]*?```", string.Empty);

            // 去掉思考标签
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                @"</?think>", string.Empty);

            // 去掉 think 标签内容（DeepSeek 推理块）
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                @"\s*think\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (string.IsNullOrWhiteSpace(clean)) return true;

            // 检测常见的"无需修改"短语（中英文）
            var noChangesPatterns = new[]
            {
                @"^[。.！!]*\s*$",                             // 只有标点符号
                @"不需要修改|无需修改|没有需要更改|无变更|已完成",
                @"无需.*(?:修改|更改|变更|编辑)",
                @"已经.*(?:完成|好了|修改好)",
                @"all\s+changes?\s+(?:are\s+)?done",
                @"no\s+(?:further\s+)?changes?\s+(?:needed|required)",
                @"nothing\s+to\s+(?:change|modify|edit)",
                // Git 操作完成语（避免格式重试）
                @"已推送|推送成功|推送完成|push.*(?:success|done|ok)",
                @"已提交|提交成功|commit.*(?:success|done|ok)",
                @"已暂存|已添加|add.*(?:success|done|ok)|暂存.*成功",
                @"stash.*(?:success|done)",
                @"切换.*成功|已切换到|checkout.*success",
                @"(?:git\s+)?操作.*(?:完成|成功|已执行)",
                // 短回复兜底：非代码的简短完成确认（<100字符，不含代码块标记）
                @"^(?:OK|Done|完成|好了|搞定|成功|已执行|已处理)[。！!.\s]*$",
            };

            foreach (var pattern in noChangesPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(clean, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // 确保不是长篇响应中误匹配（如讨论"无需修改"但实际有编辑块）
                    if (clean.Trim().Length < 200)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 检测本轮消息中是否只有 git/终端/构建操作（无代码读取或编辑）。
        /// 如果是纯 Git 操作，AI 的文本回复是操作总结而非编辑格式，应跳过格式重试。
        /// </summary>
        private static bool IsGitOrTerminalOnlyResult(List<ChatApiMessage> messages)
        {
            // 只检查 assistant 消息中的 tool_calls
            bool hasCodeTool = false;
            bool hasGitOrTerminal = false;

            foreach (var msg in messages)
            {
                if (msg.ToolCalls == null || msg.ToolCalls.Count == 0) continue;

                foreach (var tc in msg.ToolCalls)
                {
                    string name = tc.Function?.Name ?? "";
                    if (name == "git" || name == "run_in_terminal" ||
                        name == "get_terminal_output" || name == "build_solution")
                    {
                        hasGitOrTerminal = true;
                    }
                    else if (name == "read_file" || name == "replace_string_in_file" ||
                             name == "create_file" || name == "delete_file" ||
                             name == "multi_replace_string_in_file" || name == "apply_patch")
                    {
                        hasCodeTool = true;
                    }
                }
            }

            // 至少有一次 git/终端操作，且没有任何代码操作 → 纯 Git/终端
            return hasGitOrTerminal && !hasCodeTool;
        }

        private bool HasAnyValidEditFormat(string aiResult)
        {
            if (string.IsNullOrWhiteSpace(aiResult)) return false;

            // 检测 apply_patch 格式
            if (System.Text.RegularExpressions.Regex.IsMatch(aiResult,
                @"\*\*\*\s*Begin\s*Patch", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            // 检测 insert_edit_into_file 格式
            if (System.Text.RegularExpressions.Regex.IsMatch(aiResult,
                @"```(?:insert_edit_into_file|edit)\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            // 检测 create_file 格式（原有 ```file:）
            if (System.Text.RegularExpressions.Regex.IsMatch(aiResult,
                @"```file:\s*[^\r\n]+"))
                return true;

            // 检测 delete 格式
            if (System.Text.RegularExpressions.Regex.IsMatch(aiResult,
                @"(?:^|\n)\s*(?:delete|delete_file)\s*:"))
                return true;

            return false;
        }

        #endregion

        #region Tool-Made Edit Detection (v1.1.10)

        /// <summary>
        /// 检测本轮消息中是否包含编辑类工具调用（replace_string_in_file / create_file 等）。
        /// 如果 AI 已在工具循环中直接修改了文件，则无需再通过文本格式输出编辑块。
        /// </summary>
        /// <summary>
        /// 截取当前步骤工具循环期间新增的消息（排除 Handoff/上下文中历史工具调用）。
        /// 防止历史中的 create_file 等调用被误判为本轮“工具编辑”。
        /// </summary>
        private static List<ChatApiMessage> GetStepToolLoopMessages(
            List<ChatApiMessage> messages, int startIndex)
        {
            if (messages == null || messages.Count == 0 || startIndex < 0)
                return new List<ChatApiMessage>();
            if (startIndex >= messages.Count)
                return new List<ChatApiMessage>();

            int count = messages.Count - startIndex;
            var slice = new List<ChatApiMessage>(count);
            for (int i = startIndex; i < messages.Count; i++)
            {
                slice.Add(messages[i]);
            }
            return slice;
        }

        private static bool HasToolMadeEdits(List<ChatApiMessage> messages)
        {
            foreach (var msg in messages)
            {
                if (msg.ToolCalls == null || msg.ToolCalls.Count == 0) continue;

                foreach (var tc in msg.ToolCalls)
                {
                    string name = tc.Function?.Name ?? "";
                    if (name == "replace_string_in_file" ||
                        name == "multi_replace_string_in_file" ||
                        name == "create_file" ||
                        name == "delete_file" ||
                        name == "apply_patch" ||
                        name == "create_directory")
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 从消息列表中提取通过工具调用完成的文件编辑记录。
        /// 解析 tool_calls 中的 JSON 参数，提取目标文件路径。
        /// </summary>
        /// <returns>列表元素: (解析后的绝对路径, 工具名)</returns>
        private static List<(string FilePath, string ToolName)> ExtractToolMadeEdits(
            List<ChatApiMessage> messages)
        {
            var edits = new List<(string FilePath, string ToolName)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var msg in messages)
            {
                if (msg.ToolCalls == null || msg.ToolCalls.Count == 0) continue;

                foreach (var tc in msg.ToolCalls)
                {
                    string name = tc.Function?.Name ?? "";
                    string args = tc.Function?.Arguments ?? "{}";

                    // 仅处理编辑类工具
                    if (name != "replace_string_in_file" &&
                        name != "multi_replace_string_in_file" &&
                        name != "create_file" &&
                        name != "delete_file" &&
                        name != "apply_patch")
                        continue;

                    string? filePath = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(args);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("filePath", out var fp))
                            filePath = fp.GetString();
                        else if (root.TryGetProperty("path", out var p))
                            filePath = p.GetString();
                    }
                    catch
                    {
                        // JSON 解析失败，尝试正则提取 filePath
                        var match = System.Text.RegularExpressions.Regex.Match(args,
                            @"""filePath""\s*:\s*""([^""]+)""");
                        if (match.Success)
                            filePath = match.Groups[1].Value;
                    }

                    if (!string.IsNullOrEmpty(filePath) && seen.Add(filePath!))
                    {
                        edits.Add((filePath!, name));
                    }
                }
            }
            return edits;
        }

        /// <summary>
        /// 收集工具循环中完成的文件编辑，将其转换为 EditApplyResult 并加入 appliedResults。
        /// 同时追踪变更到 plan.ChangedFiles。
        /// </summary>
        private async Task CollectToolMadeEditsAsync(
            List<(string FilePath, string ToolName)> toolEdits,
            AgentTaskPlan plan,
            AgentContext context,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct,
            HashSet<string>? toolHandledFiles = null)
        {
            foreach (var (filePath, toolName) in toolEdits)
            {
                string resolvedPath = EditPatchService.ResolvePath(filePath, workspaceRoot);

                // ── 确定操作类型 ──
                var opType = toolName switch
                {
                    "create_file" => EditOperationType.CreateFile,
                    "delete_file" => EditOperationType.DeleteFile,
                    "apply_patch" => EditOperationType.ApplyPatch,
                    _ => EditOperationType.ApplyPatch, // replace_string_in_file 等归为 Patch 类
                };

                bool isNewFile = toolName == "create_file" && !File.Exists(resolvedPath);
                bool fileExists = File.Exists(resolvedPath);

                // ── 记录已处理的文件（供文本路径去重）──
                toolHandledFiles?.Add(resolvedPath);

                // ── 为新文件设置空原始内容（防止文本路径的 diff 归零）──
                if (isNewFile)
                {
                    originalContents[resolvedPath] = string.Empty;
                }

                if (toolName == "delete_file")
                {
                    // 删除操作不在此处处理（由 ProcessFileDeletionsAsync 统一处理）
                    continue;
                }

                if (!fileExists && !isNewFile)
                {
                    AddLog("WARN", $"[EditAgent] 工具编辑目标文件不存在: {Path.GetFileName(resolvedPath)} (工具: {toolName})");
                    appliedResults.Add(new EditApplyResult
                    {
                        FilePath = resolvedPath,
                        Success = false,
                        OperationType = opType,
                        ErrorMessage = "文件不存在",
                    });
                    continue;
                }

                // ── v1.1.11: 工具编辑已在磁盘生效，此时无法获取真正的原始内容。
                //     不设置 originalContents（留待后续文本编辑路径读取当前状态作为基线），
                //     避免 diff 计算时 original==final 导致变更量归零。
                //     仅对新文件设置空原始内容。

                // ── 新文件处理：添加到项目 ──
                if (isNewFile && fileExists)
                {
                    await AddFileToProjectAsync(resolvedPath, ct);
                }

                // ── 记录编辑结果 ──
                AddLog("INFO", $"[EditAgent] 工具编辑已应用: {Path.GetFileName(resolvedPath)} (工具: {toolName})");

                appliedResults.Add(new EditApplyResult
                {
                    FilePath = resolvedPath,
                    Success = true,
                    OperationType = opType,
                });

                // ── 变更通知 ──
                string changeType = isNewFile ? "create" : "modify";
                NotifyFileChange(plan.PlanId, changeType, resolvedPath,
                    $"工具编辑 ({toolName})");

                // ── 更新 plan.ChangedFiles ──
                if (!plan.ChangedFiles.Any(c => string.Equals(c.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    int added = 0, removed = 0;
                    if (isNewFile || !fileExists)
                    {
                        added = 1; // 新文件或异常情况
                    }
                    else
                    {
                        // 工具修改了已存在的文件，无法获取原始内容做精确 diff，
                        // 使用最终文件行数作为变更量估算
                        try
                        {
                            string content = await Task.Run(() => File.ReadAllText(resolvedPath), ct);
                            added = CountLines(content);
                        }
                        catch { added = 1; }
                    }

                    plan.ChangedFiles.Add(new FileChangeSummary
                    {
                        FilePath = resolvedPath,
                        LinesAdded = added,
                        LinesRemoved = removed,
                        BriefDescription = $"{Path.GetFileName(resolvedPath)} ({toolName})",
                    });
                }
            }
        }

        #endregion

        #endregion

        #region Build Step

        /// <summary>
        /// 判断步骤是否应直接触发构建验证。
        /// “运行/测试”步骤目前没有独立测试执行器，先统一归入构建验证路径。
        /// </summary>
        private static bool IsBuildVerificationStep(string stepTitle)
        {
            if (string.IsNullOrWhiteSpace(stepTitle)) return false;
            var buildKeywords = new[] { "运行", "验证", "构建", "编译", "测试运行", "执行测试",
                "跑测试", "build", "run", "test", "集成到测试套件", "运行并验证", "构建并运行" };
            return buildKeywords.Any(k => stepTitle.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 直接执行 build_solution（支持 .sln 和 CMake/Open Folder）。
        /// 计划中的显式构建步骤和 Planning 最终构建共用此入口。
        /// </summary>
        private async Task<string> ExecuteDirectBuildAsync(
            string stepTitle, string? solutionPath, CancellationToken ct)
        {
            AddLog("INFO", LocalizationService.Instance.Format("agent.log.editStepStart", stepTitle));

            try
            {
                string? result;
                if (BuiltInTools != null)
                {
                    result = await BuiltInTools.ExecuteBuiltInToolAsync(
                        "build_solution", "{}", solutionPath, ct);
                }
                else
                {
                    var buildService = new BuildService();
                    result = await buildService.BuildAsync(solutionPath, ct);
                }

                string buildResult = result ?? LocalizationService.Instance["agent.log.editBuildToolNoResult"];
                Logger.Info($"[EditAgent] 构建完成: {(buildResult.Length > 200 ? buildResult.Substring(0, 200) + "..." : buildResult)}");
                return buildResult;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["agent.log.editBuildFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 记录直接构建结果。成功/失败标记缺失时按警告处理，避免把不可判定结果误报为成功。
        /// </summary>
        private void LogDirectBuildResult(string buildResult)
        {
            string oneLine = buildResult.Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? buildResult;

            bool success = buildResult.Contains("构建成功")
                || buildResult.Contains("构建通过")
                || buildResult.Contains("build succeeded")
                || buildResult.Contains("0 个错误")
                || buildResult.Contains("0 errors");
            if (success)
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editFinalBuildOk"], oneLine));
            else
                AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.editFinalBuildWarn"], oneLine));
        }

        #endregion

        #region Step Classification & Prompt

        /// <summary>
        /// 判断步骤是否为代码编写类。
        /// </summary>
        private static bool IsCodeWritingStep(string stepTitle)
        {
            if (string.IsNullOrWhiteSpace(stepTitle)) return false;

            var codeKeywords = new[] { "编写", "写", "修改", "创建", "添加", "生成", "实现",
                "重构", "修复", "改代码", "改", "开发", "build", "write", "code", "implement",
                "create", "add", "fix", "refactor", "modify", "change", "update" };

            bool isCode = codeKeywords.Any(k =>
                stepTitle.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

            var analysisKeywords = new[] { "确定", "分析", "查找", "了解", "理解", "定位",
                "研究", "检查", "审查", "评估", "阅读", "查看", "review", "analyze",
                "find", "check", "examine", "investigate", "understand", "identify" };

            bool isAnalysis = analysisKeywords.Any(k =>
                stepTitle.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

            if (isCode) return true;
            if (isAnalysis) return false;
            return true; // 默认按代码步骤处理
        }

        private string BuildStepPrompt(AgentStep step, AgentTaskPlan plan,
            AgentContext context, bool isCodeStep)
        {
            var sb = new StringBuilder();

            // ── 缓存优化：稳定性高的内容放前面（token 级前缀缓存可命中更多）──

            // 第1层：Plan 标题（同计划内所有步骤完全相同，最稳定）
            sb.AppendLine(string.Format(AiPrompts.EditStepPromptPrefix, plan.Title));
            sb.AppendLine();

            // 第2层：代码记忆（跨步骤持久化，未读新文件/未修改文件时不变）
            if (!string.IsNullOrEmpty(context.CodeMemory))
            {
                sb.AppendLine("##  代码记忆（前面步骤读取的关键文件内容，可直接使用，无需重复 read_file）");
                sb.AppendLine(">  以下内容来自前面步骤的 read_file 结果，这些文件在之前步骤中**未被修改**。已被修改过的文件已自动排除。");
                sb.AppendLine();
                sb.AppendLine(context.CodeMemory);
                sb.AppendLine();
            }

            // 第3层：累积上下文（每步追加，前缀稳定）
            if (!string.IsNullOrEmpty(context.AccumulatedContext))
            {
                sb.AppendLine("## 前面步骤的执行结果（请基于这些结果继续，不要重复搜索已发现的文件）");
                // RAG-MARK: no-truncate — 已在 ExecutePlanAsync 中做了 8000 字符截断
                // RAG-SOURCE: accumulated-context 之前步骤的累积执行结果
                sb.AppendLine(context.AccumulatedContext);
                sb.AppendLine();
            }

            // 第4层：当前步骤信息（每步不同，变化最大）
            sb.AppendLine(string.Format(LocalizationService.Instance["agent.step.currentStepPrompt"], step.Index, plan.Steps.Count, step.Title));
            sb.AppendLine($"步骤详情: {step.Description}");
            sb.AppendLine();

            if (isCodeStep)
            {
                sb.AppendLine("## 代码修改步骤");
                sb.AppendLine("- 按系统提示中的编辑格式和项目文件规则执行修改。");
                sb.AppendLine("- 完成修改后直接结束本步骤，系统会自动执行编译验证与移交。");
                sb.AppendLine();
            }

            // ── 注入 plan.md 概述 + 当前步骤对应章节 ──
            string? planFilePath = context.PlanFilePath ?? plan.PlanFilePath;
            if (!string.IsNullOrEmpty(planFilePath) && File.Exists(planFilePath))
            {
                try
                {
                    string planMd = File.ReadAllText(planFilePath);
                    if (planMd.Length > 0)
                    {
                        // 提取概述（详细步骤章节之前的内容，截断至 ~2000 字符）
                        string overview = ExtractPlanMdOverview(planMd);
                        if (!string.IsNullOrEmpty(overview))
                        {
                            sb.AppendLine("##  计划概述");
                            sb.AppendLine(overview);
                            sb.AppendLine();
                        }

                        // 提取当前步骤对应的章节
                        string stepSection = ExtractPlanMdStepSection(planMd, step);
                        if (!string.IsNullOrEmpty(stepSection))
                        {
                            sb.AppendLine(string.Format(LocalizationService.Instance["agent.step.planMdDetail"], step.Index));
                            sb.AppendLine(stepSection);
                            sb.AppendLine();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[EditAgent] 读取 plan.md 章节失败: {ex.Message}");
                }
            }

            // ── 注入前面步骤已读取的文件内容缓存（所有模式通用），避免重复 read_file 调用 ──
            if (BuiltInTools != null)
            {
                var fileCache = BuiltInTools.GetFileReadCacheSnapshot();
                if (fileCache.Count > 0)
                {
                    // 排除之前步骤已修改过的文件（内容可能已过时）
                    var modifiedPaths = new HashSet<string>(
                        plan.ChangedFiles.Select(c => NormalizePath(c.FilePath)),
                        StringComparer.OrdinalIgnoreCase);

                    // 过滤出与当前步骤可能相关的文件（基于步骤标题/描述中的文件名关键词）
                    var relevantFiles = FilterRelevantCachedFiles(fileCache, step);

                    // ── P1-2: 收集 CodeMemory 中已包含的文件名，避免双重注入 ──
                    var codeMemoryFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(context.CodeMemory))
                    {
                        var cmMatches = System.Text.RegularExpressions.Regex.Matches(
                            context.CodeMemory, @"###  `([^`]+)`");
                        foreach (System.Text.RegularExpressions.Match m in cmMatches)
                            codeMemoryFileNames.Add(m.Groups[1].Value);
                    }

                    var safeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in relevantFiles)
                    {
                        if (!modifiedPaths.Contains(NormalizePath(kvp.Key))
                            && !codeMemoryFileNames.Contains(System.IO.Path.GetFileName(kvp.Key)))
                            safeFiles[kvp.Key] = kvp.Value;
                    }

                    if (safeFiles.Count > 0)
                    {
                        sb.AppendLine("## 前面步骤已读取的文件内容（可直接使用，无需重复调用 read_file）");
                        sb.AppendLine(">  以下文件内容来自前面步骤的读取缓存，这些文件在之前步骤中**未被修改**，内容仍然有效。已被修改过的文件已自动排除。");
                        sb.AppendLine();

                        const int maxFilesToInclude = 10;
                        const int maxCharsPerFile = 2500; // 每个文件最多注入 2.5KB
                        int included = 0;
                        long totalChars = 0;
                        const long maxTotalChars = 20000; // 总计最多 20KB

                        foreach (var kvp in safeFiles)
                        {
                            if (included >= maxFilesToInclude || totalChars >= maxTotalChars)
                                break;

                            string filePath = kvp.Key;
                            string content = kvp.Value;
                            bool truncated = content.Length > maxCharsPerFile;
                            if (truncated)
                                content = content.Substring(0, maxCharsPerFile) + "\n... (内容已截断，如需完整内容请使用 read_file)";

                            sb.AppendLine($"###  `{filePath}`");
                            sb.AppendLine("```");
                            sb.AppendLine(content);
                            sb.AppendLine("```");
                            sb.AppendLine();

                            included++;
                            totalChars += content.Length;
                        }

                        if (included < safeFiles.Count)
                        {
                            sb.AppendLine($">  还有 {safeFiles.Count - included} 个已缓存文件未显示（超出大小限制）。如需要，请使用 read_file 读取。");
                            sb.AppendLine();
                        }

                        sb.AppendLine("**重要**: 上述文件内容已在前面步骤中通过 read_file 获取且未被修改。请直接使用这些内容进行分析和编辑，不要重复调用 read_file。");
                        sb.AppendLine();
                    }
                }
            }

            // ── 提示 AI 利用已有计划上下文，避免不必要的全项目搜索 ──
            sb.AppendLine("## 重要提示");
            sb.AppendLine("- 用户消息中已包含计划概述和各步骤详情，请根据当前步骤标题和描述执行任务");
            sb.AppendLine("- 请优先使用计划中已列出的文件路径，直接用 read_file 读取目标文件内容");
            sb.AppendLine("- 仅在需要确认额外依赖关系时才使用 file_search/grep_search 搜索");
            sb.AppendLine("- 避免全项目搜索已明确指定的文件");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"解决方案路径: {context.SolutionPath}");
                sb.AppendLine();
            }

            // ── 用户附加的文件上下文 ──
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine("## 用户上传的文件内容");
                // RAG-MARK: no-truncate — 不再截断用户上传的文件内容
                // RAG-SOURCE: file-read 用户上传的附件文件内容（EditAgent 上下文）
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            if (!isCodeStep)
            {
                sb.AppendLine("这是一个分析/验证步骤，不需要修改代码。");
                sb.AppendLine("请直接输出你的分析结论、发现或建议。");
            }

            sb.AppendLine();
            sb.AppendLine("- 如果本步骤顺带完成了后续步骤，请在响应末尾声明：\"也完成了步骤X、Y\" 或 \"also completed step X, Y\"。");

            return sb.ToString();
        }

        /// <summary>
        /// 从 plan.md 中提取概述部分（详细步骤章节之前的内容），按章节边界截断。
        /// 在"详细步骤"/"Detailed Steps"章节标题前切断，保留项目目标、结构分析等概述信息。
        /// </summary>
        private static string ExtractPlanMdOverview(string planMd)
        {
            // 找到"详细步骤"章节的起始位置（中英文两种模式）
            var stepSectionPatterns = new[]
            {
                "### 3.", "### 2.", "### 3 ", "### 2 ",
                "## 详细步骤", "## Detailed Steps",
 "## ", "## 实现步骤", "## Implementation",
                "## 步骤", "## Steps"
            };

            int cutPos = planMd.Length;
            foreach (var pattern in stepSectionPatterns)
            {
                int idx = planMd.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx > 0 && idx < cutPos)
                    cutPos = idx;
            }

            string overview = planMd.Substring(0, cutPos).TrimEnd();
            const int maxOverviewChars = 2000;
            if (overview.Length > maxOverviewChars)
            {
                // 在 maxOverviewChars 附近找最近的 \n\n 段落边界切断
                int boundary = overview.LastIndexOf("\n\n", maxOverviewChars, StringComparison.Ordinal);
                if (boundary > maxOverviewChars / 2)
                    overview = overview.Substring(0, boundary).TrimEnd() + "\n\n... (概述已截断)";
                else
                    overview = overview.Substring(0, maxOverviewChars) + "\n... (概述已截断)";
            }

            return overview;
        }

        /// <summary>
        /// 从 plan.md 中提取当前步骤对应的章节内容。
        /// 匹配策略：按步骤索引号（如 "步骤 1"、"Step 1"、"1."）定位到下一个同级/上级标题。
        /// </summary>
        private static string ExtractPlanMdStepSection(string planMd, AgentStep step)
        {
            // 构建匹配模式：支持 "步骤 N"、"Step N"、"#### N."、"#### Step N" 等多种格式
            var patterns = new[]
            {
                $"#### 步骤 {step.Index}:", $"#### 步骤 {step.Index}：",
                $"#### Step {step.Index}:", $"#### Step {step.Index}.",
                $"#### {step.Index}.", $"#### {step.Index} ",
                $"### 步骤 {step.Index}:", $"### 步骤 {step.Index}：",
                $"### Step {step.Index}:", $"### Step {step.Index}.",
                $"### {step.Index}.", $"### {step.Index} ",
            };

            int startIdx = -1;
            foreach (var pattern in patterns)
            {
                int idx = planMd.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    startIdx = idx;
                    break;
                }
            }

            if (startIdx < 0)
                return string.Empty;

            // 从该步骤标题的下一行开始提取
            int contentStart = planMd.IndexOf('\n', startIdx);
            if (contentStart < 0) return string.Empty;
            contentStart++; // 跳过换行符

            // 找到下一个 ## 或 ### 或 #### 标题作为结束边界
            int endIdx = planMd.Length;
            var headerPattern = System.Text.RegularExpressions.Regex.Match(
                planMd, @"^#{2,4}\s", System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // 使用逐行扫描找下一个标题
            int searchStart = contentStart;
            int nextHeader = planMd.IndexOf("\n##", searchStart, StringComparison.Ordinal);
            if (nextHeader < 0) nextHeader = planMd.IndexOf("\r\n##", searchStart, StringComparison.Ordinal);
            if (nextHeader >= 0) endIdx = nextHeader;
            
            // 也检查 ### 和 ####
            int nextH3 = planMd.IndexOf("\n###", searchStart, StringComparison.Ordinal);
            if (nextH3 < 0) nextH3 = planMd.IndexOf("\r\n###", searchStart, StringComparison.Ordinal);
            if (nextH3 >= 0 && nextH3 < endIdx) endIdx = nextH3;
            
            int nextH4 = planMd.IndexOf("\n####", searchStart, StringComparison.Ordinal);
            if (nextH4 < 0) nextH4 = planMd.IndexOf("\r\n####", searchStart, StringComparison.Ordinal);
            if (nextH4 >= 0 && nextH4 < endIdx) endIdx = nextH4;

            string section = planMd.Substring(contentStart, endIdx - contentStart).Trim();
            
            // 截断过长内容
            const int maxSectionChars = 3000;
            if (section.Length > maxSectionChars)
            {
                int boundary = section.LastIndexOf("\n\n", maxSectionChars, StringComparison.Ordinal);
                if (boundary > maxSectionChars / 2)
                    section = section.Substring(0, boundary).TrimEnd() + "\n\n... (章节内容已截断)";
                else
                    section = section.Substring(0, maxSectionChars) + "\n... (章节内容已截断)";
            }

            return section;
        }

        /// <summary>
        /// 从文件读取缓存中筛选与当前步骤可能相关的文件。
        /// 匹配策略：文件名或路径片段出现在步骤标题/描述中，或者步骤关键词（如 WAL、B+树、Lock）匹配文件名。
        /// </summary>
        private static Dictionary<string, string> FilterRelevantCachedFiles(
            Dictionary<string, string> fileCache, AgentStep step)
        {
            // 如果缓存文件数 ≤ 10，全部返回（无需过滤）
            if (fileCache.Count <= 10)
                return fileCache;

            var relevant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string stepText = $"{step.Title} {step.Description}".ToLowerInvariant();

            // 从步骤文本提取关键词（取长度>2的单词）
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in stepText.Split(new[] { ' ', '(', ')', '（', '）', '、', '，', '/', '\\', '_', '-', '.' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 2)
                    keywords.Add(word);
            }

            foreach (var kvp in fileCache)
            {
                string fileName = System.IO.Path.GetFileName(kvp.Key).ToLowerInvariant();
                string filePath = kvp.Key.ToLowerInvariant();

                // 文件名直接匹配步骤文本
                if (stepText.Contains(fileName) || fileName.Contains(stepText))
                {
                    relevant[kvp.Key] = kvp.Value;
                    continue;
                }

                // 关键词匹配文件名或路径
                bool keywordMatch = false;
                foreach (var kw in keywords)
                {
                    if (fileName.Contains(kw) || filePath.Contains(kw))
                    {
                        keywordMatch = true;
                        break;
                    }
                }
                if (keywordMatch)
                {
                    relevant[kvp.Key] = kvp.Value;
                    continue;
                }
            }

            // 如果没匹配到任何文件，返回全部（让 AI 自己决定）
            return relevant.Count > 0 ? relevant : fileCache;
        }

        /// <summary>
        /// 更新代码记忆 — 使用 LRU + 头文件加权淘汰算法。
        /// <summary>
        /// 更新代码记忆 — 委托给 BaseAgent.RefreshCodeMemory。
        /// 从文件读取缓存中提取未被修改的关键文件内容，供后续步骤直接使用。
        /// </summary>
        private void UpdateCodeMemory(AgentContext context, AgentTaskPlan plan)
        {
            if (BuiltInTools == null) return;

            var modifiedPaths = new HashSet<string>(
                plan.ChangedFiles.Select(c => NormalizePath(c.FilePath)),
                StringComparer.OrdinalIgnoreCase);

            // 提取计划步骤关键词用于语义加分
            var stepKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in plan.Steps)
            {
                string text = $"{step.Title} {step.Description}".ToLowerInvariant();
                foreach (var word in text.Split(new[] { ' ', '(', ')', '（', '）', '、', '，', '/', '\\', '_', '-', '.' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.Length > 2)
                        stepKeywords.Add(word);
                }
            }

            RefreshCodeMemory(context, modifiedPaths, stepKeywords);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名集合。
        /// 修改这些文件可能影响项目结构，需要用户明确许可。
        /// </summary>
        /// <summary>
        /// 构建定义文件名集合（CMakeLists.txt、Makefile 等）。
        /// 这些文件引用源文件，因此必须在源文件创建完成后才能写入，
        /// 否则构建系统会在文件还不存在时尝试编译它们。
        /// </summary>
        private static readonly HashSet<string> BuildDefinitionFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CMakeLists.txt", "Makefile", "GNUmakefile", "makefile",
        };

        /// <summary>
        /// 检查文件是否为构建定义文件（CMakeLists.txt / Makefile 等）。
        /// 构建定义文件引用源文件，必须在源文件创建完成后才能处理。
        /// </summary>
        private static bool IsBuildDefinitionFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string fileName = Path.GetFileName(filePath);
            return BuildDefinitionFileNames.Contains(fileName);
        }

        /// <summary>
        /// 获取编辑操作的排序优先级。
        /// 0 = MSBuild 项目文件最先（避免 VS 冲突对话框）
        /// 1 = 普通源文件
        /// 2 = 构建定义文件最后（CMakeLists.txt/Makefile — 必须在源文件创建后才能写入）
        /// </summary>
        private static int GetEditPriority(string filePath)
        {
            if (IsBuildDefinitionFile(filePath)) return 2;
            if (IsProjectFile(filePath)) return 0;
            return 1;
        }

        /// <summary>
        /// 将文件路径列表排序，确保项目配置文件（.csproj/.slnx等）优先写入，
        /// 构建定义文件（CMakeLists.txt/Makefile）最后写入（必须在源文件创建后才能处理）。
        /// 避免 VS 在外部修改源文件后才检测到项目文件变更而弹出"检测到冲突文件修改"对话框。
        /// </summary>
        private static List<string> SortPathsWithProjectFilesFirst(IEnumerable<string> paths)
        {
            return paths
                .OrderBy(p => GetEditPriority(p))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 在写入项目文件前请求用户确认。
        /// 非项目文件直接返回 true（放行）。
        /// </summary>
        /// <param name="filePath">目标文件绝对路径</param>
        /// <param name="operationDescription">操作描述（如"修改 leetcode.vcxproj"）</param>
        /// <param name="fileContent">可选，即将写入的文件内容（用于向用户展示变更预览，自动截断过长内容）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要修改此项目文件，如"添加新源文件到项目中"）</param>
        /// <returns>true=允许写入, false=用户拒绝</returns>
        private async Task<bool> EnsureProjectFileWriteConfirmedAsync(string filePath, string operationDescription = "", string fileContent = "", string purpose = "")
        {
            if (!IsProjectFile(filePath))
                return true; // 非项目文件，直接放行

            string fileName = Path.GetFileName(filePath);
            string desc = !string.IsNullOrEmpty(operationDescription)
                ? operationDescription
                : $"修改项目文件: {fileName}";

            // 自动推断目的（如果调用方未提供）
            string effectivePurpose = purpose;
            if (string.IsNullOrEmpty(effectivePurpose))
            {
                if (operationDescription.Contains("新建") || operationDescription.Contains("create_file"))
                    effectivePurpose = "创建新文件需要更新项目配置以将其纳入编译";
                else if (operationDescription.Contains("删除") || operationDescription.Contains("移除"))
                    effectivePurpose = "删除文件后需要从项目配置中移除对应引用";
                else
                    effectivePurpose = "代码修改涉及项目配置变更，需要更新项目文件以保持一致";
            }

            AddLog("WARN", LocalizationService.Instance.Format("agent.log.editProjectModDetected", fileName));

            // 构造内容预览（截断过长内容，保留前后各 30 行）
            string detail = "";
            if (!string.IsNullOrWhiteSpace(fileContent))
            {
                const int maxPreviewLines = 60;
                var lines = fileContent.Replace("\r\n", "\n").Split('\n');
                if (lines.Length > maxPreviewLines)
                {
                    int headLines = 30;
                    int tailLines = 30;
                    var preview = new System.Text.StringBuilder();
                    preview.AppendLine("```xml");
                    for (int i = 0; i < headLines && i < lines.Length; i++)
                        preview.AppendLine(lines[i]);
                    preview.AppendLine($"... (省略 {lines.Length - headLines - tailLines} 行) ...");
                    for (int i = Math.Max(headLines, lines.Length - tailLines); i < lines.Length; i++)
                        preview.AppendLine(lines[i]);
                    preview.Append("```");
                    detail = preview.ToString();
                }
                else
                {
                    detail = "```xml\n" + string.Join("\n", lines) + "\n```";
                }
            }

            bool approved = await RequestPermissionAsync(
                $"确认修改项目文件: {fileName}",
                $"即将修改项目配置文件 `{fileName}`\n\n路径: {filePath}\n\n{desc}\n\n 修改项目文件可能影响构建配置和项目结构。",
                "file_write",
                detail,
                effectivePurpose);

            if (!approved)
            {
                AddLog("WARN", LocalizationService.Instance.Format("agent.log.projectModDenied", fileName));
            }
            return approved;
        }

        private static AgentTaskPlan CreateSingleStepPlan(string userMessage)
        {
            bool isReadOnlyExecution = IsReadOnlyExecutionRequest(userMessage);
            string stepTitle = isReadOnlyExecution
                ? LocalizationService.Instance["agent.step.executeReadOnlyCommand"]
                : LocalizationService.Instance["agent.step.analyzeAndModify"];

            return new AgentTaskPlan
            {
                Intent = isReadOnlyExecution ? AgentIntent.QandA : AgentIntent.CodeChange,
                Title = isReadOnlyExecution
                    ? LocalizationService.Instance["agent.step.executeReadOnlyCommand"]
                    : LocalizationService.Instance["agent.step.executeCodeChange"],
                Steps = new List<AgentStep>
                {
                    new AgentStep
                    {
                        Index = 1,
                        Title = stepTitle,
                        Description = userMessage,
                        RequiresApproval = false,
                    }
                },
            };
        }

        /// <summary>
        /// 识别“运行命令以读取/输出内容”的只读执行请求。
        /// 这类任务允许执行终端命令，但禁止任何文件写入，避免把输出内容误落地为代码文件。
        /// </summary>
        private static bool IsReadOnlyExecutionRequest(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string text = message.Trim();

            bool hasExecutionIntent =
                text.Contains("运行", StringComparison.OrdinalIgnoreCase)
                || text.Contains("执行", StringComparison.OrdinalIgnoreCase)
                || text.Contains("终端命令", StringComparison.OrdinalIgnoreCase)
                || text.Contains("python", StringComparison.OrdinalIgnoreCase)
                || text.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                || text.Contains("script", StringComparison.OrdinalIgnoreCase)
                || text.Contains("run ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("execute ", StringComparison.OrdinalIgnoreCase);

            bool hasReadOrOutputIntent =
                text.Contains("读取", StringComparison.OrdinalIgnoreCase)
                || text.Contains("读出", StringComparison.OrdinalIgnoreCase)
                || text.Contains("查看", StringComparison.OrdinalIgnoreCase)
                || text.Contains("显示", StringComparison.OrdinalIgnoreCase)
                || text.Contains("输出", StringComparison.OrdinalIgnoreCase)
                || text.Contains("打印", StringComparison.OrdinalIgnoreCase)
                || text.Contains("read ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("output ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("print ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("show ", StringComparison.OrdinalIgnoreCase);

            bool hasWriteIntent =
                text.Contains("修改", StringComparison.OrdinalIgnoreCase)
                || text.Contains("更改", StringComparison.OrdinalIgnoreCase)
                || text.Contains("创建", StringComparison.OrdinalIgnoreCase)
                || text.Contains("新建", StringComparison.OrdinalIgnoreCase)
                || text.Contains("写入", StringComparison.OrdinalIgnoreCase)
                || text.Contains("保存", StringComparison.OrdinalIgnoreCase)
                || text.Contains("替换", StringComparison.OrdinalIgnoreCase)
                || text.Contains("删除", StringComparison.OrdinalIgnoreCase)
                || text.Contains("实现", StringComparison.OrdinalIgnoreCase)
                || text.Contains("修复", StringComparison.OrdinalIgnoreCase)
                || text.Contains("create", StringComparison.OrdinalIgnoreCase)
                || text.Contains("write", StringComparison.OrdinalIgnoreCase)
                || text.Contains("save", StringComparison.OrdinalIgnoreCase)
                || text.Contains("modify", StringComparison.OrdinalIgnoreCase)
                || text.Contains("change", StringComparison.OrdinalIgnoreCase)
                || text.Contains("update", StringComparison.OrdinalIgnoreCase)
                || text.Contains("delete", StringComparison.OrdinalIgnoreCase)
                || text.Contains("fix", StringComparison.OrdinalIgnoreCase);

            return hasExecutionIntent && hasReadOrOutputIntent && !hasWriteIntent;
        }

        /// <summary>
        /// 执行只读命令/读取任务：保留终端能力，但禁用所有文件编辑工具。
        /// </summary>
        private async Task ExecuteReadOnlyExecutionStepAsync(
            AgentStep step,
            AgentContext context,
            string stepPrompt,
            CancellationToken ct)
        {
            string workspaceRoot = context.SolutionPath ?? string.Empty;
            if (!string.IsNullOrEmpty(workspaceRoot) && File.Exists(workspaceRoot))
                workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;

            var promptBuilder = new StringBuilder(stepPrompt);
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("## 只读执行约束（最高优先级）");
            promptBuilder.AppendLine("- 本任务是读取或输出内容，只能使用读取、搜索、终端执行和 git 工具。");
            promptBuilder.AppendLine("- 严禁创建、修改、删除、保存代码文件；不要调用 create_file、replace_string_in_file、apply_patch、delete_file。");
            promptBuilder.AppendLine("- 可以使用 git 工具查看版本状态或执行其他 git 操作；需要审批的 git 写操作必须等用户确认。");
            promptBuilder.AppendLine("- 你可以对执行过程或元信息做简要说明，但用户明确要求输出的内容必须完整保留。");
            promptBuilder.AppendLine("- 如果用户要求输出代码或文件内容，必须包含完整原文；不得只给摘要、说明或“已输出”的状态描述。");

            var messages = BuildContextAwareMessages(Definition.SystemPrompt, promptBuilder.ToString());
            int toolLoopStart = Math.Max(0, messages.Count - 2);
            var thinkingBuilder = new StringBuilder();
            string result = await CallAiWithToolLoopAsync(
                messages,
                workspaceRoot,
                ct,
                maxTokens: 8192,
                toolWhitelist: new List<string>(ReadOnlyExecutionTools),
                onThinking: thinking =>
                {
                    thinkingBuilder.Append(thinking);
                    context.OnThinkingChunk?.Invoke(thinking);
                },
                onContent: content => context.OnContentChunk?.Invoke(content),
                onToolCall: toolSummary => AddLog("TOOL", toolSummary));

            // 只读输出任务优先返回工具原始结果，避免模型把文件内容再总结一遍。
            var rawToolOutput = GetStepToolLoopMessages(messages, toolLoopStart)
                .LastOrDefault(m =>
                    m.Role == "tool" &&
                    (string.Equals(m.Name, "run_in_terminal", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(m.Name, "read_file", StringComparison.OrdinalIgnoreCase)))
                ?.Content;

            step.AiResponse = BuildReadOnlyExecutionContent(result, rawToolOutput);
            step.ResultSummary = LocalizationService.Instance["agent.log.readOnlyExecutionCompleted"];

            if (thinkingBuilder.Length > 0)
            {
                if (!string.IsNullOrEmpty(_accumulatedReasoning))
                    _accumulatedReasoning += "\n\n";
                _accumulatedReasoning += thinkingBuilder.ToString();
            }
        }

        /// <summary>
        /// 组装只读执行结果：允许 AI 做简要加工，但保证用户要求的原始输出不缺失。
        /// </summary>
        private static string BuildReadOnlyExecutionContent(
            string? aiResult,
            string? rawToolOutput)
        {
            string summary = aiResult?.Trim() ?? string.Empty;
            string raw = rawToolOutput?.Trim() ?? string.Empty;

            if (raw.Length == 0)
                return summary;

            if (summary.Length == 0)
                return raw;

            // AI 已经完整携带原始输出时，不再重复附加。
            if (ContainsNormalized(summary, raw))
                return summary;

            return summary
                + "\n\n--- 完整终端输出 ---\n"
                + raw
                + "\n--- 完整终端输出结束 ---";
        }

        private static bool ContainsNormalized(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
                return false;

            string Normalize(string value)
            {
                return value
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Trim();
            }

            return Normalize(haystack).Contains(
                Normalize(needle),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 取消当前任务。
        /// </summary>
        public void Cancel()
        {
            _agentCts?.Cancel();
            AddLog("WARN", LocalizationService.Instance["edit.summary.cancelled"]);
        }

        private void NotifyPlanUpdated()
        {
            try { PlanUpdated?.Invoke(CurrentPlan!); } catch { }
        }

        /// <summary>
        /// 通过 Toast 通知用户计划执行结果。
        /// </summary>
        private void NotifyPlanCompletionViaToast(AgentTaskPlan plan)
        {
            try
            {
                var toastService = CompositionRoot.GetServiceOrDefault<ToastNotificationService>();
                if (toastService == null)
                    return;

                int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int total = plan.Steps.Count;

                if (plan.IsCancelled)
                {
                    toastService.Show(
                        "DeepSeek V4",
                        string.Format(LocalizationService.Instance["toast.taskCancelled"], completed, total));
                }
                else if (plan.IsCompleted && failed == 0)
                {
                    toastService.Show(
                        "DeepSeek V4",
                        string.Format(LocalizationService.Instance["toast.taskComplete"], completed, total));
                }
                else if (plan.IsCompleted && failed > 0)
                {
                    toastService.Show(
                        "DeepSeek V4",
                        string.Format(LocalizationService.Instance["toast.taskPartialComplete"], completed, total, failed));
                }
            }
            catch
            {
                // Toast 通知失败不应影响主流程
            }
        }

        /// <summary>
        /// 确定 Handoff 目标：AI 通过 request_handoff 动态移交优先，
        /// 否则默认移交 Ask Agent 生成总结，若有编译警告则移交 Build Agent。
        /// </summary>
        private AgentHandoff ResolveHandoff(AgentTaskPlan plan)
        {
            // ── AI 动态移交优先 ──
            if (PendingHandoffRequest != null)
                return ConvertHandoffRequestToHandoff(PendingHandoffRequest);

            // ── 纯只读/终端任务（未产生文件变更且无构建警告）：不再移交 Ask 生成“变更总结”──
            // 直接返回 Edit Agent 的最终回复作为结果（例如用户要求运行命令并输出内容）。
            // 这样最终回复是实际内容，而不是被「文件变更总结」覆盖。
            if (plan.ChangedFiles.Count == 0 && !HasBuildWarningsInLogs())
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.editNoChangesConfirmed"]);
                return null;
            }

            // ── 检查是否应跳过自动编译 ──
            bool skipBuild = ShouldSkipAutoBuild();

            // ── 有编译警告 → Build Agent（除非用户/设置禁用了自动编译）──
            if (HasBuildWarningsInLogs())
            {
                if (skipBuild)
                {
                    AddLog("INFO", LocalizationService.Instance["agent.edit.autoBuildDisabledByUser"]);
                    return BuildSummaryHandoff(plan);
                }
                return Definition.Handoffs.FirstOrDefault(h => h.TargetAgent == AgentType.Build)
                    ?? BuildSummaryHandoff(plan);
            }

            // ── 默认 → Ask Agent 生成总结 ──
            return BuildSummaryHandoff(plan);
        }

        /// <summary>
        /// 构建移交 Ask Agent 生成总结的 Handoff。
        /// 将文件变更统计、步骤执行情况、缓存命中率等上下文打包传递给 Ask Agent。
        /// </summary>
        private AgentHandoff BuildSummaryHandoff(AgentTaskPlan plan)
        {
            var L = LocalizationService.Instance;

            if (plan.IsCancelled)
            {
                return new AgentHandoff
                {
                    Label = L["agent.edit.handoffAskLabel"],
                    TargetAgent = AgentType.Ask,
                    Prompt = L["edit.summary.cancelled"],
                    AutoSend = true,
                    ShowContinueOn = false,
                };
            }

            // 构建包含所有统计数据的 handoff prompt
            var sb = new StringBuilder();
            sb.AppendLine(L["agent.edit.handoffAskPrompt"]);
            sb.AppendLine();
            sb.AppendLine($"**{L["edit.summary.taskLabel"]}**: {plan.Title}");
            sb.AppendLine();

            // ── 步骤执行情况（优先：描述完成了什么）──
            if (plan.Steps.Count > 0)
            {
                sb.AppendLine(L["edit.summary.stepExecutionHeader"]);
                foreach (var step in plan.Steps)
                {
                    string statusIcon = step.Status == AgentStepStatus.Completed ? "✅"
                        : step.Status == AgentStepStatus.Failed ? "❌"
                        : step.Status == AgentStepStatus.Skipped ? "⏭️"
                        : "🔄";
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary!
                        : LocalizationService.Instance["agent.step.noDetail"];
                    sb.AppendLine(L.Format("edit.summary.stepLineFormat",
                        statusIcon, step.Index, step.Title, summary));
                }
                sb.AppendLine();
            }

            // ── 文件变更统计（辅助参考）──
            if (plan.ChangedFiles.Count > 0)
            {
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                    })
                    .ToList();

                sb.AppendLine(L.Format("edit.summary.changeStats",
                    mergedFiles.Sum(c => c.LinesAdded),
                    mergedFiles.Sum(c => c.LinesRemoved),
                    mergedFiles.Count));
                sb.AppendLine();
                sb.AppendLine(L["edit.summary.modifiedFiles"]);
                foreach (var file in mergedFiles)
                {
                    sb.AppendLine($"- **{file.FileName}** (+{file.LinesAdded} -{file.LinesRemoved})");
                }
                sb.AppendLine();
            }

            // 编译警告（如果有）
            if (HasBuildWarningsInLogs())
            {
                sb.AppendLine(LocalizationService.Instance["agent.edit.handoffBuildWarningHint"]);
            }

            return new AgentHandoff
            {
                Label = L["agent.edit.handoffAskLabel"],
                TargetAgent = AgentType.Ask,
                Prompt = sb.ToString(),
                AutoSend = true,
                ShowContinueOn = false,
            };
        }

        /// <summary>
        /// 构建执行结果摘要（用于 AgentResult.Content，使 Handoff 合并时 UI 可见执行结果）。
        /// 与 BuildSummaryHandoff 不同，此摘要面向用户展示（而非作为 Agent prompt）。
        /// </summary>
        private string BuildExecutionSummary(AgentTaskPlan? plan)
        {
            if (plan == null) return string.Empty;

            var L = LocalizationService.Instance;
            var sb = new StringBuilder();

            // 步骤完成情况
            if (plan.Steps.Count > 0)
            {
                int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int skipped = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);

                sb.AppendLine(L.Format("edit.summary.executionHeader", plan.Title, 
                    $" {completed} / Error: {failed} /  {skipped}"));
            }

            // 文件变更
            if (plan.ChangedFiles.Count > 0)
            {
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                    })
                    .ToList();

                sb.AppendLine(L.Format("edit.summary.fileCountWithValue",
                    L["edit.summary.fileCount"], mergedFiles.Count.ToString()));
                foreach (var file in mergedFiles)
                {
                    sb.AppendLine($"  - `{file.FileName}` (+{file.LinesAdded} -{file.LinesRemoved})");
                }
            }
            else
            {
                sb.AppendLine(L.Format("edit.summary.fileCountWithValue",
                    L["edit.summary.fileCount"], "0"));
            }

            // 构建结果
            if (HasBuildWarningsInLogs())
            {
                sb.AppendLine(LocalizationService.Instance["agent.log.buildWarningInSummary"]);
            }
            else
            {
                sb.AppendLine(LocalizationService.Instance["agent.log.buildPassInSummary"]);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 构建最终回复内容：纯只读/终端任务（无文件变更）直接沿用 AI 的最终回复，
        /// 避免被“变更总结”形式的 Handoff 覆盖；有文件变更时仍使用执行结果摘要。
        /// </summary>
        private string BuildFinalContent(AgentTaskPlan plan, bool hasNoFileChanges)
        {
            if (!hasNoFileChanges)
                return BuildExecutionSummary(plan);

            var completedStep = plan.Steps.LastOrDefault(s => s.Status == AgentStepStatus.Completed);
            if (completedStep != null && !string.IsNullOrWhiteSpace(completedStep.AiResponse))
                return completedStep.AiResponse.Trim();

            return BuildExecutionSummary(plan);
        }

        #endregion

        #region Project Integration Helpers

        /// <summary>
        /// 收集项目文件上下文 — 委托 ExploreAgent 智能发现与当前步骤相关的文件，
        /// 而非盲目读取所有文件。提供完整的项目结构和代码风格参考给 AI。
        /// 限制总大小防止超出 token 限制。
        /// </summary>
        private async Task<string> GatherProjectFilesContextAsync(
            string? solutionPath, string userQuery)
        {
            if (string.IsNullOrEmpty(solutionPath))
                return string.Empty;

            const int maxTotalChars = 60000;
            var sb = new StringBuilder();
            int totalChars = 0;

            try
            {
                List<string> relevantFiles;

                // ═══════════════════════════════════════════════════════════
                // 缓存策略（三层优先，以后会被 RAG 替代）：
                // 第1层：ActivePlan.DiscoveredFiles（PlanAgent 已发现，最高优先级）
                // 第2层：ExploreAgent 文件列表缓存（同一次会话内已扫描）
                // 第3层：实时 DiscoverRelevantFilesAsync / DiscoverSolutionFilesAsync
                // ═══════════════════════════════════════════════════════════

                // ── 第1层：PlanAgent 传递的已发现文件列表 ──
                var discoveredFromPlan = Context?.ActivePlan?.DiscoveredFiles;
                if (discoveredFromPlan != null && discoveredFromPlan.Count > 0)
                {
                    relevantFiles = discoveredFromPlan;
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.editReusePlanFiles", relevantFiles.Count));
                }
                // ── 第2层：ExploreAgent 文件列表缓存 ──
                else if (ExploreAgent != null)
                {
                    var cached = ExploreAgent.GetCachedDiscoveredFiles(solutionPath!);
                    if (cached != null && cached.Count > 0)
                    {
                        relevantFiles = cached;
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editCacheHit", relevantFiles.Count));
                    }
                    else if (!string.IsNullOrWhiteSpace(userQuery))
                    {
                        // ── 第2.5层：智能发现相关文件（结果会自动缓存）──
                        string additionalCtx = "";
                        if (CurrentPlan != null)
                        {
                            additionalCtx = $"{LocalizationService.Instance["edit.plan.currentTask"]}: {CurrentPlan.Title}";
                            var completedSteps = CurrentPlan.Steps
                                .Where(s => s.Status == AgentStepStatus.Completed)
                                .ToList();
                            if (completedSteps.Count > 0)
                            {
                                additionalCtx += "\n" + LocalizationService.Instance["agent.log.completedStepsPrefix"] + string.Join("; ",
                                    completedSteps.Select(s => s.Title));
                            }
                        }

                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editDelegateExplore", userQuery.Truncate(80)));
                        relevantFiles = await ExploreAgent.DiscoverRelevantFilesAsync(
                            solutionPath!, userQuery, maxFiles: 30,
                            additionalContext: additionalCtx);
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editExploreDone", relevantFiles.Count));
                    }
                    else
                    {
                        // ── 第3层：回退到全量发现（结果会自动缓存）──
                        relevantFiles = await ExploreAgent.DiscoverSolutionFilesAsync(
                            solutionPath!, maxFiles: 50);
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editFullDiscovery", relevantFiles.Count));
                    }
                }
                else
                {
                    // ── 最终回退：简单的目录扫描 ──
                    relevantFiles = await FallbackFileScanAsync(solutionPath!);
                }

                // ── 向 AgentContext 共享已发现文件列表（供后续 Agent 复用）──
                if (Context != null && relevantFiles.Count > 0)
                {
                    Context.DiscoveredFiles = relevantFiles;
                }

                // ── 读取发现的文件内容（优先从缓存读取）──
                foreach (var file in relevantFiles)
                {
                    if (totalChars >= maxTotalChars) break;

                    try
                    {
                        string relativePath = GetRelativePath(solutionPath ?? "", file);

                        // ═══════════════════════════════════════════════
                        // 内容缓存策略（以后会被 RAG 替代）：
                        // 第1层：AgentContext.FileReadCache
                        // 第2层：ExploreAgent._fileContentCache
                        // 第3层：磁盘读取
                        // ═══════════════════════════════════════════════
                        string content;
                        bool fromCache = false;

                        // 第1层：AgentContext 全局缓存
                        if (Context?.FileReadCache != null &&
                            Context.FileReadCache.TryGetValue(file, out var cachedContent))
                        {
                            content = cachedContent;
                            fromCache = true;
                        }
                        // 第2层：ExploreAgent 本地文件内容缓存
                        else if (ExploreAgent != null &&
                            ExploreAgent.TryGetCachedFileContent(file, out var exploreCached) &&
                            exploreCached != null)
                        {
                            content = exploreCached;
                            fromCache = true;
                        }
                        else
                        {
                            // 第3层：磁盘读取
                            // RAG-SOURCE: file-read 项目文件内容（EditAgent 项目上下文收集）
                            content = await Task.Run(() => File.ReadAllText(file));

                            // 写入缓存（以后会被 RAG 替代）
                            ExploreAgent?.CacheFileContent(file, content);
                            if (Context?.FileReadCache != null)
                            {
                                lock (Context.FileReadCache)
                                {
                                    Context.FileReadCache[file] = content;
                                }
                            }
                        }

                        // RAG-MARK: no-truncate — 不再截断项目文件内容，完整提供给 AI

                        sb.AppendLine($"### {relativePath}{(fromCache ? " (cached)" : "")}");
                        sb.AppendLine("```");
                        sb.AppendLine(content);
                        sb.AppendLine("```");
                        sb.AppendLine();

                        totalChars += content.Length + relativePath.Length + 20;
                    }
                    catch
                    {
                        // 跳过无法读取的文件
                    }
                }

                AddLog("INFO", $"[EditAgent] 项目文件上下文: {relevantFiles.Count} 个文件, {totalChars} 字符（以后会被 RAG 替代）");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[EditAgent] 收集项目文件上下文失败: {ex.Message}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 最终回退方案：简单的目录文件扫描（当 ExploreAgent 不可用时）。
        /// </summary>
        private static async Task<List<string>> FallbackFileScanAsync(string solutionPath)
        {
            var files = new List<string>();

            try
            {
                var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".cs", ".vb", ".cpp", ".h", ".hpp", ".c",
                    ".xaml", ".xml", ".config", ".csproj", ".vbproj",
                    ".json", ".ts", ".js", ".py", ".java", ".fs", ".fsx",
                    ".sln", ".md",
                };

                var excludeDirs = SharedConstants.ExcludedDirectories;

                files = await Task.Run(() =>
                    Directory.GetFiles(solutionPath, "*.*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            string dir = Path.GetDirectoryName(f) ?? "";
                            string ext = Path.GetExtension(f);
                            foreach (var excludeDir in excludeDirs)
                                if (dir.IndexOf(excludeDir, StringComparison.OrdinalIgnoreCase) >= 0)
                                    return false;
                            return codeExtensions.Contains(ext);
                        })
                        .Take(50)
                        .ToList());
            }
            catch
            {
                // 忽略扫描失败
            }

            return files;
        }

        /// <summary>
        /// 将新建文件添加到 Visual Studio 解决方案的项目中。
        /// 如果文件已存在于项目中，则跳过。
        /// </summary>
        private static async Task AddFileToProjectAsync(string filePath, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            try
            {
                var dteService = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dteService is not EnvDTE.DTE dte || dte.Solution == null || !dte.Solution.IsOpen)
                    return;

                // 遍历所有项目，找到包含该文件路径的最佳匹配项目
                string? fileDir = Path.GetDirectoryName(filePath);
                EnvDTE.Project? bestProject = null;
                string? bestProjectDir = null;

                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    try
                    {
                        string? projectDir = Path.GetDirectoryName(project.FullName);
                        if (projectDir == null) continue;

                        // 检查文件是否已经在项目中
                        foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                        {
                            try
                            {
                                for (short i = 1; i <= item.FileCount; i++)
                                {
                                    if (string.Equals(item.get_FileNames(i), filePath,
                                        StringComparison.OrdinalIgnoreCase))
                                        return; // 文件已在项目中
                                }
                            }
                            catch { }
                        }

                        // 优先匹配目录更深的项目（更具体的项目）
                        if (fileDir != null && fileDir.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase)
                            && (bestProjectDir == null || projectDir.Length > bestProjectDir.Length))
                        {
                            bestProject = project;
                            bestProjectDir = projectDir;
                        }
                    }
                    catch { }
                }

                if (bestProject != null)
                {
                    bestProject.ProjectItems.AddFromFile(filePath);
                    Logger.Info($"[EditAgent]  已将文件加入项目: {Path.GetFileName(filePath)} → {bestProject.Name}");
                }
                else
                {
                    Logger.Warn($"[EditAgent] 未找到合适的项目来添加文件: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 添加文件到项目失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取相对路径。
        /// </summary>
        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath)) return fullPath;
            if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                string relative = fullPath.Substring(basePath.Length).TrimStart('\\', '/');
                return relative;
            }
            return fullPath;
        }

        #endregion

        #region Missing File Detection

        /// <summary>
        /// 检测 AI 回复是否表示缺少某些文件。
        /// 匹配中英文常见表达模式。
        /// </summary>
        private static bool DetectMissingFilesInResponse(string aiResponse)
        {
            if (string.IsNullOrWhiteSpace(aiResponse)) return false;

            // ── 中文模式 ──
            var cnPatterns = new[]
            {
                "需要查看", "需要读取", "需要看到", "缺少文件",
                "看不到", "无法访问", "请提供", "没有提供",
                "没有看到", "未提供", "无法确定", "需要更多信息",
                "需要了解", "需要确认", "需要参考", "需要查阅",
                "找不到", "不清楚", "不确定文件", "无法定位",
                "还需要", "缺少上下文", "需要完整代码",
            };

            // ── 英文模式 ──
            var enPatterns = new[]
            {
                "need to see", "need to read", "need to look at",
                "missing file", "missing context", "don't have access",
                "cannot see", "can't see", "please provide",
                "not provided", "not available", "unable to determine",
                "need more information", "need more context",
                "don't know", "not sure about", "would need",
                "I need the", "I would need to see",
            };

            foreach (var pattern in cnPatterns)
                if (aiResponse.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;

            foreach (var pattern in enPatterns)
                if (aiResponse.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        /// 从 AI 回复中提取请求的文件名/路径。
        /// 匹配反引号包裹的文件引用、常见路径模式等。
        /// </summary>
        private static List<string> ExtractRequestedFileNames(string aiResponse)
        {
            var files = new List<string>();

            if (string.IsNullOrWhiteSpace(aiResponse)) return files;

            // 模式 1: 反引号包裹的文件名（如 `UserService.cs`、`src/Models/User.cs`）
            var backtickMatches = System.Text.RegularExpressions.Regex.Matches(
                aiResponse, @"`([^`]+\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|java|go|rs|swift|kt|php|rb|lua|sql|xml|json|yaml|yml|md|css|html|xaml|csproj|vbproj|sln|config|razor|cshtml|ps1|psm1|proto))`");
            foreach (System.Text.RegularExpressions.Match m in backtickMatches)
            {
                string name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name.Length > 2)
                    files.Add(name);
            }

            // 模式 2: 引号包裹的文件名
            var quoteMatches = System.Text.RegularExpressions.Regex.Matches(
                aiResponse, @"[""']([^""']+\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|java|go|rs|swift|kt|php|rb|lua|sql|xml|json|yaml|yml|md|css|html|xaml|csproj|vbproj|sln))[""']");
            foreach (System.Text.RegularExpressions.Match m in quoteMatches)
            {
                string name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name.Length > 2)
                    files.Add(name);
            }

            // 模式 3: 斜体/粗体标记包裹（如 *UserService.cs*、**src/Models/User.cs**）
            var markdownMatches = System.Text.RegularExpressions.Regex.Matches(
                aiResponse, @"\*{1,2}([^*]+\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|java|go|rs|swift|kt|php|rb|lua|sql|xml|json|yaml|yml|md|css|html|xaml|csproj|vbproj|sln))\*{1,2}");
            foreach (System.Text.RegularExpressions.Match m in markdownMatches)
            {
                string name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name.Length > 2)
                    files.Add(name);
            }

            // 去重，最多返回 10 个
            return files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }

        /// <summary>
        /// 追踪验证阶段产生的文件变更，合并到 plan.ChangedFiles。
        /// 验证阶段 AI 可通过工具直接修改/创建文件，这些变更需要反映在最终总结中。
        /// </summary>
        private void TrackVerifyPhaseChanges(List<ChatApiMessage> verifyMessages, AgentTaskPlan plan)
        {
            try
            {
                for (int i = 0; i < verifyMessages.Count; i++)
                {
                    var msg = verifyMessages[i];
                    if (msg.Role != "assistant" || msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                        continue;

                    foreach (var tc in msg.ToolCalls)
                    {
                        string toolName = tc.Function?.Name ?? "";
                        if (!IsFileModifyingTool(toolName))
                            continue;

                        string? filePath = ExtractFilePathFromArgs(tc.Function?.Arguments ?? "");
                        if (string.IsNullOrWhiteSpace(filePath))
                            continue;

                        // 查找对应的 tool result 消息
                        string toolResult = "";
                        for (int j = i + 1; j < verifyMessages.Count; j++)
                        {
                            if (verifyMessages[j].Role == "tool"
                                && verifyMessages[j].ToolCallId == tc.Id)
                            {
                                toolResult = verifyMessages[j].Content ?? "";
                                break;
                            }
                        }

                        // 判断操作是否成功（以 Error:/Timeout: 标记开头表示失败）
                        if (toolResult.StartsWith("Error: ") || toolResult.StartsWith("Timeout: ")) continue;

                        // 估算行数变更（从工具结果中提取 +N -M 模式）
                        int linesAdded = 0;
                        int linesRemoved = 0;
                        var lineMatch = System.Text.RegularExpressions.Regex.Match(
                            toolResult, @"\+(\d+)\s*-(\d+)");
                        if (lineMatch.Success)
                        {
                            int.TryParse(lineMatch.Groups[1].Value, out linesAdded);
                            int.TryParse(lineMatch.Groups[2].Value, out linesRemoved);
                        }
                        else if (toolName == "create_file")
                        {
                            // 读取实际文件内容计算行数
                            if (File.Exists(filePath))
                            {
                                string content = File.ReadAllText(filePath);
                                linesAdded = CountLines(content);
                            }
                            else
                            {
                                linesAdded = 1; // 文件不存在时至少标记为有变更
                            }
                        }
                        else if (linesAdded == 0 && linesRemoved == 0)
                        {
                            // replace_string_in_file / multi_replace_string_in_file 等工具
                            // 不返回 +N -M 格式，从参数中提取 oldString/newString 计算行数
                            string? oldStr = ExtractStringArg(tc.Function?.Arguments ?? "", "oldString");
                            string? newStr = ExtractStringArg(tc.Function?.Arguments ?? "", "newString");
                            if (oldStr != null || newStr != null)
                            {
                                linesRemoved = oldStr != null ? CountLines(oldStr) : 0;
                                linesAdded = newStr != null ? CountLines(newStr) : 0;
                            }
                            else if (File.Exists(filePath))
                            {
                                // 无法提取参数时，至少标记文件被修改
                                linesAdded = 1;
                                linesRemoved = 1;
                            }
                            else
                            {
                                linesAdded = 1;
                            }
                        }

                        string fileName = System.IO.Path.GetFileName(filePath);
                        string description = toolName switch
                        {
                            "replace_string_in_file" => string.Format(LocalizationService.Instance["agent.log.toolModifyFile"], fileName),
                            "multi_replace_string_in_file" => string.Format(LocalizationService.Instance["agent.log.toolBatchModifyFile"], fileName),
                            "create_file" => string.Format(LocalizationService.Instance["agent.log.toolCreateFile"], fileName),
                            _ => string.Format(LocalizationService.Instance["agent.log.toolOperateFile"], fileName),
                        };

                        // 合并同一文件的多次变更
                        var existing = plan.ChangedFiles.FirstOrDefault(
                            c => string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.LinesAdded += linesAdded;
                            existing.LinesRemoved += linesRemoved;
                            if (!string.IsNullOrEmpty(description)
                                && !(existing.BriefDescription ?? "").Contains(description))
                            {
                                existing.BriefDescription = (existing.BriefDescription ?? "") + "; " + description;
                            }
                        }
                        else
                        {
                            plan.ChangedFiles.Add(new FileChangeSummary
                            {
                                FilePath = filePath!,
                                LinesAdded = linesAdded,
                                LinesRemoved = linesRemoved,
                                BriefDescription = description,
                            });
                        }
                    }
                }

                if (plan.ChangedFiles.Count > 0)
                {
                    Logger.Info($"[EditAgent] 验证阶段追踪到文件变更，当前 ChangedFiles 总数: {plan.ChangedFiles.Count}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 追踪验证阶段变更失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从工具参数 JSON 中提取 filePath。
        /// </summary>
        private static string? ExtractFilePathFromArgs(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("filePath", out var fpProp))
                    return fpProp.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 从工具参数 JSON 中按名称提取字符串参数。
        /// </summary>
        private static string? ExtractStringArg(string argumentsJson, string argName)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty(argName, out var prop))
                    return prop.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 判断工具名是否为文件修改类工具。
        /// </summary>
        private static bool IsFileModifyingTool(string toolName)
        {
            return toolName is "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_file"
                or "apply_patch";
        }

        /// <summary>
        /// 智能检测验证结果中是否真的存在编译/构建失败。
        /// 
        /// 与简单关键词匹配不同，此方法会排除 AI 自然语言中的否定表述
        /// <summary>
        /// 检查执行日志中是否有编译警告或失败信号。
        /// 仅匹配明确的构建失败标记（错误代码、构建摘要行），避免
        /// 因日志中包含 "build"/"Build"/"Error: " 等通用词而产生误判。
        /// 用于判断是否应建议 Handoff 到 Build Agent。
        /// </summary>
        private bool HasBuildWarningsInLogs()
        {
            foreach (var log in _logs)
            {
                // ── 检查 WARN / ERROR 级别日志，以及 INFO 级别中包含构建失败标记的日志 ──
                bool isRelevantLevel = log.Level == "WARN" || log.Level == "ERROR" || log.Level == "INFO";
                if (!isRelevantLevel) continue;

                string msg = log.Message ?? string.Empty;

                // ── 明确的构建/编译失败标记（含 Error: 前缀）──
                if (msg.Contains("Error: 构建失败") || msg.Contains("Error: 编译失败")
                    || msg.Contains("Error: build") || msg.Contains("Error: Build")
                    || msg.Contains("Error: CMake") || msg.Contains("Error: MSBuild"))
                    return true;

                // ── 编译器/MSBuild 错误代码 ──
                if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                    @"\berror\s+(CS|C|LNK|MSB|BC|FS|TS|RUST)\d+\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;

                // ── MSBuild 摘要失败模式 ──
                if (msg.Contains("Build FAILED"))
                    return true;

                // ── 本地化构建失败关键词（精确匹配，避免 "build" 误判）──
                if (msg.Contains("构建失败") || msg.Contains("编译失败")
                    || msg.Contains("build failed") || msg.Contains("Build failed"))
                    return true;

                // ── CMake 构建失败 ──
                if (msg.Contains("CMake build failed") || msg.Contains("CMake 构建失败"))
                    return true;

                // ── 非零退出码（构建进程异常退出）──
                if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                    @"exit code:\s*[1-9]\d*",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;

                // ── 最终编译验证的警告日志（中/英文 locale）──
                if (msg.Contains(" 最终编译") || msg.Contains("Final build has issues")
                    || (msg.IndexOf("final build", StringComparison.OrdinalIgnoreCase) >= 0
                        && (msg.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("issues", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 判断是否应跳过自动编译（整合设置和用户提示两个维度）。
        /// 1. 用户设置：DeepSeekOptionsPage.Instance.EnableAutoBuild 为 false
        /// 2. 用户提示：原始消息中包含"不要编译""跳过构建""don't build"等短语
        /// </summary>
        private bool ShouldSkipAutoBuild()
        {
            // 维度1：检查用户设置
            if (!(Settings.DeepSeekOptionsPage.Instance?.EnableAutoBuild ?? true))
                return true;

            // 维度2：检查用户提示中的意图
            return UserPromptSaysSkipBuild();
        }

        /// <summary>
        /// 检查用户原始消息中是否包含跳过构建的意图。
        /// 支持中/英文关键词匹配。
        /// </summary>
        private bool UserPromptSaysSkipBuild()
        {
            if (string.IsNullOrWhiteSpace(_lastUserMessage))
                return false;

            string msg = _lastUserMessage;

            // ── 中文关键词 ──
            if (msg.Contains("不要编译") || msg.Contains("不要构建")
                || msg.Contains("别编译") || msg.Contains("别构建")
                || msg.Contains("跳过编译") || msg.Contains("跳过构建")
                || msg.Contains("不编译") || msg.Contains("不构建")
                || msg.Contains("无需编译") || msg.Contains("无需构建")
                || msg.Contains("不用编译") || msg.Contains("不用构建")
                || msg.Contains("禁止编译") || msg.Contains("禁止构建")
                || msg.Contains("免编译") || msg.Contains("免构建"))
                return true;

            // ── 英文关键词 ──
            string lower = msg.ToLowerInvariant();
            if (lower.Contains("don't build") || lower.Contains("do not build")
                || lower.Contains("skip build") || lower.Contains("skip the build")
                || lower.Contains("no build") || lower.Contains("without build")
                || lower.Contains("without building") || lower.Contains("don't compile")
                || lower.Contains("do not compile") || lower.Contains("skip compile")
                || lower.Contains("no compile") || lower.Contains("without compile")
                || lower.Contains("without compiling") || lower.Contains("don't run build"))
                return true;

            return false;
        }

        #endregion

        #region Edit Tool Helpers

        /// <summary>
        /// 懒加载初始化编辑工具实例。
        /// </summary>
        private void EnsureEditTools(string workspaceRoot)
        {
            _applyPatchTool ??= new ApplyPatchTool(_apiService, workspaceRoot);
            _insertEditTool ??= new InsertEditTool(_apiService, workspaceRoot);
            _replaceStringTool ??= new ReplaceStringTool(_apiService, workspaceRoot);
            _multiReplaceStringTool ??= new MultiReplaceStringTool(_apiService, workspaceRoot);

            // ── 注入 StagedEditWorkspace ──
            if (_stagedWorkspace != null)
            {
                _applyPatchTool.Workspace = _stagedWorkspace;
                _insertEditTool.Workspace = _stagedWorkspace;
                _replaceStringTool.Workspace = _stagedWorkspace;
                _multiReplaceStringTool.Workspace = _stagedWorkspace;
            }
        }

        /// <summary>
        /// 检测 AI 输出中的编辑操作类型（不依赖 EditPatchService）。
        /// </summary>
        private static EditOperationType DetectOperationType(string aiOutput)
        {
            if (string.IsNullOrWhiteSpace(aiOutput))
                return EditOperationType.CreateFile; // 默认

            // 检测 patch 格式
            if (System.Text.RegularExpressions.Regex.IsMatch(aiOutput,
                @"\*\*\*\s*Begin\s*Patch", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return EditOperationType.ApplyPatch;

            // 检测 insert_edit_into_file 格式
            if (System.Text.RegularExpressions.Regex.IsMatch(aiOutput,
                @"```(?:insert_edit_into_file|edit)\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return EditOperationType.InsertEditIntoFile;

            // 检测 ...existing code... 标记
            if (aiOutput.Contains("...existing code..."))
                return EditOperationType.InsertEditIntoFile;

            // 检测 create_file / delete_file
            if (System.Text.RegularExpressions.Regex.IsMatch(aiOutput,
                @"```file:\s*[^\r\n]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return EditOperationType.CreateFile;

            return EditOperationType.CreateFile; // 默认
        }

        #endregion

        #region Memory — 步骤摘要写入会话记忆

        /// <summary>
        /// 将单个步骤的完成摘要写入会话记忆，供 Ask Agent 最终汇总使用。
        /// </summary>
        private async Task SaveStepSummaryToMemoryAsync(AgentStep step, AgentTaskPlan plan, AgentContext context)
        {
            if (MemoryService == null) return;

            try
            {
                string? sessionId = BuiltInTools?.CurrentSessionId;
                string stepSummary = BuildStepSummaryMarkdown(step, plan);
                string fileName = $"step-{step.Index:D2}-summary.md";

                // 先检查文件是否已存在（防止重复写入）
                try
                {
                    await MemoryService.ViewAsync(MemoryScope.Session, fileName, sessionId, context.SolutionPath);
                    // 文件已存在，先删除再重新创建（内容可能已更新）
                    await MemoryService.DeleteAsync(MemoryScope.Session, fileName, sessionId, context.SolutionPath);
                    await MemoryService.CreateAsync(MemoryScope.Session, fileName,
                        stepSummary, sessionId, context.SolutionPath);
                }
                catch (FileNotFoundException)
                {
                    // 文件不存在，创建新文件
                    await MemoryService.CreateAsync(MemoryScope.Session, fileName,
                        stepSummary, sessionId, context.SolutionPath);
                }

                AddLog("INFO", $"[Memory] 步骤 {step.Index} 摘要已写入会话记忆: {fileName}");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Memory] 步骤摘要写入失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 计划全部完成后，将聚合摘要写入会话记忆。
        /// </summary>
        private async Task SaveFinalPlanSummaryToMemoryAsync(AgentTaskPlan plan, AgentContext context)
        {
            if (MemoryService == null) return;

            try
            {
                string? sessionId = BuiltInTools?.CurrentSessionId;
                string finalSummary = BuildFinalPlanSummaryMarkdown(plan);
                string fileName = "plan-final-summary.md";

                await MemoryService.CreateAsync(MemoryScope.Session, fileName,
                    finalSummary, sessionId, context.SolutionPath);

                AddLog("INFO", $"[Memory] 最终计划摘要已写入会话记忆: {fileName}");
            }
            catch (Exception ex)
            {
                // 文件可能已存在，删除旧文件后重新创建
                try
                {
                    string? sessionId = BuiltInTools?.CurrentSessionId;
                    await MemoryService.DeleteAsync(MemoryScope.Session, "plan-final-summary.md",
                        sessionId, context.SolutionPath);
                    await MemoryService.CreateAsync(MemoryScope.Session, "plan-final-summary.md",
                        BuildFinalPlanSummaryMarkdown(plan),
                        sessionId, context.SolutionPath);
                }
                catch
                {
                    AddLog("WARN", $"[Memory] 最终计划摘要写入失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// v1.1.11: 清理上一次计划遗留的步骤摘要和最终摘要记忆文件。
        /// 在 ExecutePlanAsync 开始时调用，防止新旧计划摘要混在一起。
        /// </summary>
        private async Task ClearPreviousPlanMemoryAsync(AgentContext context)
        {
            if (MemoryService == null) return;

            try
            {
                string? sessionId = BuiltInTools?.CurrentSessionId;

                // 清理最终摘要
                try
                {
                    await MemoryService.DeleteAsync(MemoryScope.Session, "plan-final-summary.md",
                        sessionId, context.SolutionPath);
                }
                catch (FileNotFoundException) { /* 不存在，无需清理 */ }
                catch { /* 静默忽略其他错误 */ }

                // 清理步骤摘要 (step-01 ~ step-99)
                for (int i = 1; i <= 99; i++)
                {
                    string fileName = $"step-{i:D2}-summary.md";
                    try
                    {
                        await MemoryService.DeleteAsync(MemoryScope.Session, fileName,
                            sessionId, context.SolutionPath);
                    }
                    catch (FileNotFoundException)
                    {
                        // 连续两个文件不存在则停止（假设后续也没有）
                        if (i > 1)
                        {
                            string prevFile = $"step-{(i - 1):D2}-summary.md";
                            try
                            {
                                await MemoryService.ViewAsync(MemoryScope.Session, prevFile,
                                    sessionId, context.SolutionPath);
                            }
                            catch (FileNotFoundException)
                            {
                                break; // 前一个也不存在，确认没有更多旧文件
                            }
                        }
                    }
                    catch { /* 静默忽略其他错误 */ }
                }

                AddLog("INFO", "[Memory] 已清理上一次计划的步骤摘要记忆文件");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Memory] 清理计划记忆文件时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建单个步骤的 Markdown 摘要。
        /// </summary>
        private static string BuildStepSummaryMarkdown(AgentStep step, AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 步骤 {step.Index}/{plan.Steps.Count}: {step.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **状态**: {(step.Status == AgentStepStatus.Completed ? LocalizationService.Instance["agent.step.completed"] : LocalizationService.Instance["agent.step.failed"])}");
            sb.AppendLine($"- **任务**: {plan.Title}");
            if (!string.IsNullOrWhiteSpace(step.ResultSummary))
            {
                sb.AppendLine($"- **结果**: {step.ResultSummary}");
            }
            if (!string.IsNullOrWhiteSpace(step.Description))
            {
                sb.AppendLine($"- **描述**: {step.Description}");
            }
            // 展示当前所有已变更文件
            var allFiles = plan.ChangedFiles
                .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = System.IO.Path.GetFileName(g.Key), Added = g.Sum(c => c.LinesAdded), Removed = g.Sum(c => c.LinesRemoved) })
                .ToList();
            if (allFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 已修改的文件");
                foreach (var file in allFiles)
                {
                    string delta = $"{(file.Added > 0 ? $"+{file.Added}" : "")}"
                        + $"{(file.Removed > 0 ? $" -{file.Removed}" : "")}";
                    sb.AppendLine($"- `{file.Name}` {delta}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 构建最终计划聚合摘要。
        /// </summary>
        private static string BuildFinalPlanSummaryMarkdown(AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 计划完成: {plan.Title}");
            sb.AppendLine();
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int skipped = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);
            sb.AppendLine($"- **总步骤**: {plan.Steps.Count}");
            sb.AppendLine($"- **完成**: {completed} | **失败**: {failed} | **跳过**: {skipped}");
            sb.AppendLine();

            // 汇总所有步骤
            sb.AppendLine("## 步骤摘要");
            sb.AppendLine();
            foreach (var step in plan.Steps)
            {
                string icon = step.Status switch
                {
                    AgentStepStatus.Completed => "",
                    AgentStepStatus.Failed => "Error: ",
                    AgentStepStatus.Skipped => "",
                    _ => "",
                };
                string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                    ? step.ResultSummary!
                    : "(无详细结果)";
                sb.AppendLine($"- {icon} **{step.Title}**: {summary}");
            }

            // 汇总所有文件变更
            var mergedFiles = plan.ChangedFiles
                .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Path = System.IO.Path.GetFileName(g.Key),
                    Added = g.Sum(c => c.LinesAdded),
                    Removed = g.Sum(c => c.LinesRemoved),
                })
                .ToList();

            if (mergedFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 文件变更汇总");
                sb.AppendLine();
                sb.AppendLine("| 文件 | 变更 |");
                sb.AppendLine("|------|------|");
                foreach (var f in mergedFiles)
                {
                    string delta = $"{(f.Added > 0 ? $"+{f.Added}" : "")}"
                        + $"{(f.Removed > 0 ? $" -{f.Removed}" : "")}";
                    sb.AppendLine($"| `{f.Path}` | {delta} |");
                }
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region IDisposable

        public override void Dispose()
        {
            _agentCts?.Cancel();
            _agentCts?.Dispose();
            base.Dispose();
        }

        #endregion
    }
}
