using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
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
    /// </summary>
    public class EditAgent : BaseAgent
    {
        private CancellationTokenSource? _agentCts;
        private ExploreAgent? _exploreAgent;

        // ── 编辑工具（懒加载，由 EnsureEditTools 初始化）──
        private ApplyPatchTool? _applyPatchTool;
        private InsertEditTool? _insertEditTool;
        private ReplaceStringTool? _replaceStringTool;
        private MultiReplaceStringTool? _multiReplaceStringTool;

        /// <summary>
        /// ExploreAgent 引用，由 AgentDispatcher 注入。
        /// 用于在执行代码修改前智能发现相关文件。
        /// 设置时自动转发 ExploreAgent 的日志和文件变更事件。
        /// </summary>
        public new ExploreAgent? ExploreAgent
        {
            get => _exploreAgent;
            set => RegisterExploreAgent(value, ref _exploreAgent);
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
            "get_errors",
            // 终端与构建
            "run_in_terminal",
            "get_terminal_output",
            "create_and_run_task",
            "build_solution",
            // Git 版本控制
            "git",
            // 子代理委派与移交
            "runSubagent",
            "request_handoff",
            // 任务与记忆
            "manage_todo_list",
            "memory",
        };

        /// <summary>
        /// Edit Agent 代码步骤专用探索工具集。
        /// AI 在编写代码前使用 runSubagent 委派 ExploreAgent 探索项目结构，
        /// 配合 read_file（利用缓存命中）和 get_errors 完成上下文收集。
        /// </summary>
        private static readonly string[] ExplorationTools = new[]
        {
            "read_file",
            "get_errors",
            "runSubagent",
            "build_solution",      // 允许探索阶段编译验证当前代码状态
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
                        ShowContinueOn = true,
                    },
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return GetCommonSystemPromptPrefix() + LocalizationService.Instance["system.agent.editPromptFragment"];
        }

        #endregion

        #region Execute

        /// <summary>
        /// Edit Agent 执行入口。
        /// 接收计划并逐步执行代码修改。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清空上次执行的日志，防止 HasBuildWarningsInLogs() 被旧日志误导 ──
            _logs.Clear();

            var result = new AgentResult
            {
                AgentType = AgentType.Edit,
                Success = true,
            };

            // ── 如果有 ActivePlan，执行计划 ──
            if (context.ActivePlan != null && context.ActivePlan.Steps.Count > 0)
            {
                await ExecutePlanAsync(context.ActivePlan, context);
                result.Plan = context.ActivePlan;
                result.FileChanges = context.ActivePlan.ChangedFiles;

                // ── AI 通过 request_handoff 工具主动请求移交（优先于程序化移交）──
                if (PendingHandoffRequest != null)
                {
                    result.Handoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                }
                else
                {
                    // ── 移交 Ask Agent 生成最终总结（包含文件变更统计、缓存命中率等）──
                    result.Handoff = BuildSummaryHandoff(context.ActivePlan);

                    // ── 如果最终编译存在警告/失败，叠加 Handoff 到 Build Agent ──
                    if (HasBuildWarningsInLogs())
                    {
                        result.Handoff = Definition.Handoffs.FirstOrDefault(h => h.TargetAgent == AgentType.Build);
                    }
                }
            }
            else
            {
                // ── 没有计划，作为单步代码修改执行 ──
                AddLog("INFO", LocalizationService.Instance["agent.log.editNoPlan"]);
                var plan = CreateSingleStepPlan(userMessage);
                context.ActivePlan = plan; // 确保 Handoff 时 AskAgent 可检测到已完成计划
                await ExecutePlanAsync(plan, context);
                result.Plan = plan;
                result.FileChanges = plan.ChangedFiles;

                // ── AI 通过 request_handoff 工具主动请求移交（优先于程序化移交）──
                if (PendingHandoffRequest != null)
                {
                    result.Handoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                }
                else
                {
                    // ── 移交 Ask Agent 生成最终总结（包含文件变更统计、缓存命中率等）──
                    result.Handoff = BuildSummaryHandoff(plan);

                    // ── 如果最终编译存在警告/失败，叠加 Handoff 到 Build Agent ──
                    if (HasBuildWarningsInLogs())
                    {
                        result.Handoff = Definition.Handoffs.FirstOrDefault(h => h.TargetAgent == AgentType.Build);
                    }
                }
            }

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
                    }
                }

                plan.IsCompleted = plan.Steps.All(s =>
                    s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped);

                // ── 诊断日志：记录步骤完成情况 ──
                int completedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int skippedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);
                int failedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int pendingCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Pending);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editPlanProgress"],
                    plan.Steps.Count, completedCount, skippedCount, failedCount, pendingCount));

                // ── Planning 模式：所有步骤完成后统一编译验证一次 ──
                // 必须 plan.IsCompleted 才触发最终构建（防止 JSON 回退单步计划误触发）
                if (context.IsPlanningMode && plan.IsCompleted
                    && plan.ChangedFiles.Count > 0
                    && !plan.IsCancelled && !_agentCts!.IsCancellationRequested)
                {
                    AddLog("INFO", LocalizationService.Instance["agent.log.editFinalBuild"]);
                    NotifyPlanUpdated();
                    try
                    {
                        string finalBuildResult;
                        if (BuiltInTools != null)
                        {
                            finalBuildResult = await BuiltInTools.ExecuteBuiltInToolAsync(
                                "build_solution", "{}", context.SolutionPath)
                                ?? LocalizationService.Instance["agent.log.editBuildToolNoResult"];
                        }
                        else
                        {
                            finalBuildResult = await ExecuteBuildStepAsync(
                                new AgentStep { Title = LocalizationService.Instance["agent.log.editFinalBuildStepTitle"] }, context.SolutionPath,
                                _agentCts.Token);
                        }
                        string oneLine = finalBuildResult.Split(new[] { '\r', '\n' },
                            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? finalBuildResult;
                        if (finalBuildResult.Contains("✅") || finalBuildResult.Contains("0 个错误") || finalBuildResult.Contains("0 errors"))
                            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editFinalBuildOk"], oneLine));
                        else
                            AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.editFinalBuildWarn"], oneLine));
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.finalBuildException"], ex.Message));
                    }
                }
            }
            finally
            {
                NotifyPlanUpdated();

                // ── 清理 Plan Agent 生成的 plan.md ──
                await CleanupPlanMarkdownAsync(plan, context);
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
            bool isCodeStep = IsCodeWritingStep(step.Title);
            bool isBuildStep = IsBuildOrRunStep(step.Title);

            // ── 构建 AI prompt ──
            string stepPrompt = BuildStepPrompt(step, plan, context, isCodeStep);

            if (isBuildStep)
            {
                // ── 使用 build_solution 工具（统一通过 BuiltInToolService 构建）──
                string buildResult;
                if (BuiltInTools != null)
                {
                    buildResult = await BuiltInTools.ExecuteBuiltInToolAsync(
                        "build_solution", "{}", context.SolutionPath)
                        ?? LocalizationService.Instance["agent.log.editBuildToolNoResult"];
                }
                else
                {
                    buildResult = await ExecuteBuildStepAsync(step, context.SolutionPath, ct);
                }
                step.AiResponse = buildResult;
                step.ResultSummary = buildResult;

                // ── 记录构建结果到日志，使 HasBuildWarningsInLogs() 能检测到步骤级构建失败 ──
                if (buildResult.Contains("❌") || buildResult.Contains("error CS") || buildResult.Contains("error C")
                    || buildResult.Contains("Build FAILED") || buildResult.Contains("0 succeeded"))
                {
                    string oneLine = buildResult.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? buildResult;
                    AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.editFinalBuildWarn"], oneLine));
                }
                else
                {
                    string oneLine = buildResult.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? buildResult;
                    AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editFinalBuildOk"], oneLine));
                }
            }
            else if (isCodeStep)
            {
                await ExecuteCodeStepAsync(step, plan, context, stepPrompt, ct);
            }
            else
            {
                string result = await CallAiLongAsync(Definition.SystemPrompt, stepPrompt, ct, maxTokens: 4096);
                step.AiResponse = result;
                step.ResultSummary = result;
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

            // ── AI 调用循环（支持格式重试）──
            // messages 在循环外声明，重试时复用前一次的完整对话上下文（含工具调用结果），
            // 避免重复读取文件、重复搜索目录等浪费。
            var retryOutputs = new List<string>();
            List<ChatApiMessage>? messages = null;

            for (int retry = 0; retry <= maxFormatRetries; retry++)
            {
                if (ct.IsCancellationRequested) return;

                if (retry == 0)
                {
                    // 首次尝试：创建全新的消息列表
                    messages = BuildContextAwareMessages(Definition.SystemPrompt, stepPrompt);
                }
                else
                {
                    // 重试：在上次消息基础上追加格式修正指令
                    // messages 中已包含前次尝试的全部工具调用及结果（文件内容等），无需重复读取
                    messages!.Add(new ChatApiMessage
                    {
                        Role = "assistant",
                        Content = result // 上次的（格式错误）输出，作为对话上下文
                    });
                    messages.Add(new ChatApiMessage
                    {
                        Role = "user",
                        Content = AiPrompts.EditFormatRecoveryPrompt
                    });
                }

                // ── 使用工具调用循环：AI 可以先探索再修改 ──
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.callingAiToolLoop"], retry));
                result = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 8192,
                    toolWhitelist: new List<string>(ExplorationTools),
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("INFO", toolSummary);
                    });

                retryOutputs.Add(result);

                // ── 检测 AI 是否明确表示没有要更改的内容 ──
                if (IsNoChangesResponse(result))
                {
                    AddLog("INFO", LocalizationService.Instance["agent.log.editEmptyResponse"]);
                    result = string.Empty; // 统一置空，后续流程据此跳过编辑
                    break;
                }

                // ── 检测编辑格式并解析 ──
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

            // ── AI 明确表示没有要更改的内容 → 跳过编辑执行 ──
            if (string.IsNullOrWhiteSpace(result))
            {
                step.ResultSummary = LocalizationService.Instance["agent.log.editNoChangesConfirmed"];
                TerminalWindowHelper.SuppressDiffPreview = false;
                AddLog("INFO", LocalizationService.Instance["agent.log.editNoChange"]);
                return;
            }

            // ── 检测编辑操作类型 ──
            var operationType = DetectOperationType(result);

            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editTypeDetected"], operationType));

            // ── 初始化编辑工具（懒加载，使用当前 workspaceRoot）──
            EnsureEditTools(workspaceRoot);

            // ── 保存原始文件内容（用于最终 diff 比较）──
            var originalContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var appliedResults = new List<EditApplyResult>();

            // ── 全局抑制 diff 预览，流程结束时统一显示一次 ──
            TerminalWindowHelper.SuppressDiffPreview = true;

            switch (operationType)
            {
                case EditOperationType.ApplyPatch:
                    // ── 方法1：apply_patch ──
                    await ExecutePatchEditsAsync(result, plan, context, workspaceRoot,
                        originalContents, appliedResults, ct);
                    break;

                case EditOperationType.InsertEditIntoFile:
                    // ── 方法2：insert_edit_into_file ──
                    await ExecuteInsertEditsAsync(result, plan, context, workspaceRoot,
                        originalContents, appliedResults, ct);
                    break;

                case EditOperationType.CreateFile:
                default:
                    // ── 方法3：create_file（原有逻辑）──
                    await ExecuteCreateFileEditsAsync(result, plan, context, workspaceRoot,
                        originalContents, appliedResults, ct);
                    break;
            }

            // ── 处理文件删除（delete: 格式，原有逻辑）──
            await ProcessFileDeletionsAsync(result, plan, context, ct);

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
                            realAdded = CountLines(r.FinalContent);
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
            step.ResultSummary = changes.Count > 0
                ? string.Format(L["agent.log.editFilesModified"], changes.Count, operationType)
                : string.Format(L["agent.log.editNoFilesChanged"], operationType);

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

            // ── 编译验证阶段（AI 可使用完整 EditTools，包括 build_solution）──
            if (changes.Count > 0 && !ct.IsCancellationRequested && !context.IsPlanningMode)
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.verifyPhaseStarted"]);

                // ── 验证阶段专用 system prompt（从 i18n 加载，支持中英切换）──
                // ⚠️ 缓存优化：作为 extraSystemMessages 注入而非替换 messages[0]，
                //    保持 Definition.SystemPrompt 在 messages[0] 不变，
                //    使 DeepSeek Prompt Cache 能命中编辑阶段已缓存的前缀。
                string verifySystemPrompt = LocalizationService.Instance.Format(
                    "system.agent.verifyPromptFragment",
                    workspaceRoot,
                    string.Join("\n", changes.Select(c => $"- `{c.FilePath}`")));

                // ── 验证阶段专用工具白名单：build + 只读 + 编辑工具（不含探索工具）──
                var verifyToolWhitelist = new List<string>
                {
                    "build_solution",
                    "read_file",
                    "get_errors",
                    "replace_string_in_file",
                    "multi_replace_string_in_file",
                    "create_file",
                    "run_in_terminal",
                    "get_terminal_output",
                };

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

                string verifyUserMessage =
                    "## 代码修改已完成\n\n" +
                    $"已修改 {changes.Count} 个文件：{string.Join(", ", changes.Select(c => Path.GetFileName(c.FilePath)))}\n" +
                    verifyExploreContext + "\n" +
                    (sanityWarnings != null
                        ? $"⚠️ **编辑后健全性检查发现可能的问题**: {sanityWarnings}\n" +
                          $"请在验证时重点检查这些文件的括号/圆括号是否匹配，必要时用 read_file 查看文件末尾附近。\n\n"
                        : "\n") +
                    "请立即执行以下操作（分两轮进行）：\n" +
                    "**第 1 轮**：只调用 build_solution 工具触发编译（build_solution 是异步的，会立即返回\"构建已启动\"）\n" +
                    "**第 2 轮**：收到 build_solution 回复后，调用 get_errors（不带参数）获取编译错误\n" +
                    "  - 根据错误信息，用 read_file 读取报错文件的相关行，用 replace_string_in_file 直接修复\n" +
                    "  - 修复后重新调用 build_solution 验证\n\n" +
                    "⚠️ 重要规则：\n" +
                    "- **不要在同一轮中同时调用 build_solution 和 get_errors**\n" +
                    "- **始终使用 build_solution 工具进行编译**，不要尝试在终端中运行 cl.exe、msbuild、dotnet build 等命令\n" +
                    "- build_solution 已内置 VS 编译环境，终端中这些工具可能不在 PATH 中而失败\n" +
                    "- get_errors 默认不带参数即可收集所有编译错误（从 VS 错误列表和输出窗口获取）\n" +
                    "- 最多尝试修复 3 次，但如果修复后出现的错误与之前不同（新错误），则不计入次数限制，重新计数\n\n" +
                    "如果项目不支持构建（如纯脚本项目），请直接说明并跳过验证。";

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
                        AddLog("INFO", toolSummary);
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

            // ── 恢复 diff 预览，统一显示一次最终 diff ──
            TerminalWindowHelper.SuppressDiffPreview = false;

            foreach (var kvp in originalContents)
            {
                // RAG-SOURCE: file-read 读取最终文件内容（diff 预览对比）
                string finalContent = File.Exists(kvp.Key)
                    ? await Task.Run(() => File.ReadAllText(kvp.Key), ct)
                    : string.Empty;
                if (kvp.Value != finalContent)
                {
                    await TerminalWindowHelper.ShowFinalDiffAsync(kvp.Value, finalContent, kvp.Key);
                }
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
            CancellationToken ct)
        {
            if (_applyPatchTool == null)
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.patchServiceMissing"]);
                return;
            }

            var patches = ApplyPatchTool.ParsePatches(aiResult);
            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.parsedPatches"], patches.Count));

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
            CancellationToken ct)
        {
            if (_insertEditTool == null)
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.editNoInsertEditTool"]);
                return;
            }

            var insertEdits = InsertEditTool.ParseInsertEdits(aiResult);
            AddLog("INFO", LocalizationService.Instance.Format("agent.log.editInsertEditsParsed", insertEdits.Count));

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
                        $"InsertEdit 修改项目文件: {Path.GetFileName(resolvedPath)}",
                        "",
                        $"对 `{Path.GetFileName(resolvedPath)}` 进行必要的配置变更");
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
                            resolvedPath, $"{applyResult.AppliedEdits.Count} 个编辑点", applyResult.FinalContent!);
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
            CancellationToken ct)
        {
            var changes = ParseCodeChangesFromResult(aiResult);

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
                    if (isNewFile)
                    {
                        string? dir = Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        await Task.Run(() => File.WriteAllText(resolvedPath, string.Empty, System.Text.Encoding.UTF8), ct);
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editPreCreateFile", Path.GetFileName(resolvedPath)));
                        await AddFileToProjectAsync(resolvedPath, ct);
                    }

                    // ── 项目文件拦截：新建/修改 .vcxproj/.sln 等前请求用户确认 ──
                    string createOpDesc = isNewFile
                        ? $"新建项目文件: {Path.GetFileName(resolvedPath)}"
                        : $"修改文件: {Path.GetFileName(resolvedPath)} (+{change.LinesAdded} -{change.LinesRemoved})";
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

                    string? error = await TerminalWindowHelper.WriteCodeToFileAsync(
                        resolvedPath, change.NewContent ?? string.Empty);

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
                await AgentDispatcher.DeleteFilesViaEnvDTEAsync(resolvedDeletions);
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

        #endregion

        #region Build Step

        /// <summary>
        /// 判断步骤是否为构建/运行/验证类。
        /// </summary>
        private static bool IsBuildOrRunStep(string stepTitle)
        {
            if (string.IsNullOrWhiteSpace(stepTitle)) return false;
            var buildKeywords = new[] { "运行", "验证", "构建", "编译", "测试运行", "执行测试",
                "跑测试", "build", "run", "test", "集成到测试套件", "运行并验证", "构建并运行" };
            return buildKeywords.Any(k => stepTitle.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 执行构建/运行步骤。
        /// 委托给 BuildService 统一处理（支持 .sln 和 CMake/Open Folder）。
        /// </summary>
        private async Task<string> ExecuteBuildStepAsync(AgentStep step, string? solutionPath, CancellationToken ct)
        {
            AddLog("INFO", LocalizationService.Instance.Format("agent.log.editStepStart", step.Title));

            try
            {
                var buildService = new BuildService();
                string result = await buildService.BuildAsync(solutionPath, ct);
                Logger.Info($"[EditAgent] 构建完成: {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 构建异常: {ex.Message}");
                return string.Format(LocalizationService.Instance["agent.log.editBuildFailed"], ex.Message);
            }
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
            sb.AppendLine(string.Format(AiPrompts.EditStepPromptPrefix, plan.Title));
            sb.AppendLine($"当前步骤 ({step.Index}/{plan.Steps.Count}): {step.Title}");
            sb.AppendLine($"步骤详情: {step.Description}");
            sb.AppendLine();

            // ── 注入之前步骤的累积上下文（所有模式通用），避免重复搜索解决方案 ──
            if (!string.IsNullOrEmpty(context.AccumulatedContext))
            {
                sb.AppendLine("## 前面步骤的执行结果（请基于这些结果继续，不要重复搜索已发现的文件）");
                // RAG-MARK: no-truncate — 已在 ExecutePlanAsync 中做了 8000 字符截断
                // RAG-SOURCE: accumulated-context 之前步骤的累积执行结果
                sb.AppendLine(context.AccumulatedContext);
                sb.AppendLine();
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
                    var safeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in relevantFiles)
                    {
                        if (!modifiedPaths.Contains(NormalizePath(kvp.Key)))
                            safeFiles[kvp.Key] = kvp.Value;
                    }

                    if (safeFiles.Count > 0)
                    {
                        sb.AppendLine("## 前面步骤已读取的文件内容（可直接使用，无需重复调用 read_file）");
                        sb.AppendLine("> ⚠️ 以下文件内容来自前面步骤的读取缓存，这些文件在之前步骤中**未被修改**，内容仍然有效。已被修改过的文件已自动排除。");
                        sb.AppendLine();

                        const int maxFilesToInclude = 15;
                        const int maxCharsPerFile = 4000; // 每个文件最多注入 4KB
                        int included = 0;
                        long totalChars = 0;
                        const long maxTotalChars = 30000; // 总计最多 30KB

                        foreach (var kvp in safeFiles)
                        {
                            if (included >= maxFilesToInclude || totalChars >= maxTotalChars)
                                break;

                            string filePath = kvp.Key;
                            string content = kvp.Value;
                            bool truncated = content.Length > maxCharsPerFile;
                            if (truncated)
                                content = content.Substring(0, maxCharsPerFile) + "\n... (内容已截断，如需完整内容请使用 read_file)";

                            sb.AppendLine($"### 📄 `{filePath}`");
                            sb.AppendLine("```");
                            sb.AppendLine(content);
                            sb.AppendLine("```");
                            sb.AppendLine();

                            included++;
                            totalChars += content.Length;
                        }

                        if (included < safeFiles.Count)
                        {
                            sb.AppendLine($"> 💡 还有 {safeFiles.Count - included} 个已缓存文件未显示（超出大小限制）。如需要，请使用 read_file 读取。");
                            sb.AppendLine();
                        }

                        sb.AppendLine("**重要**: 上述文件内容已在前面步骤中通过 read_file 获取且未被修改。请直接使用这些内容进行分析和编辑，不要重复调用 read_file。");
                        sb.AppendLine();
                    }
                }
            }

            // ── 提示 AI 利用已有计划上下文，避免不必要的全项目搜索 ──
            sb.AppendLine("## 重要提示");
            sb.AppendLine("- 用户消息中已包含完整的 plan.md 计划文档，其中记录了项目结构、相关文件路径和修改方案");
            sb.AppendLine("- 请优先使用 plan.md 中已列出的文件路径，直接用 read_file 读取目标文件内容");
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

            if (isCodeStep)
            {
                sb.AppendLine("## 编辑方法（按优先级选择）");
                sb.AppendLine();
                sb.AppendLine("### 首选：apply_patch（局部修改，最快）");
                sb.AppendLine("对每个需要修改的文件，使用以下格式输出补丁：");
                sb.AppendLine();
                sb.AppendLine("*** Begin Patch");
                sb.AppendLine("*** Update File: 完整/绝对/路径");
                sb.AppendLine("@@ 类名或函数名（用于定位）");
                sb.AppendLine("     上下文行（原样保留）");
                sb.AppendLine("-    要删除的行");
                sb.AppendLine("+    要新增的行");
                sb.AppendLine("     上下文行（原样保留）");
                sb.AppendLine("*** End Patch");
                sb.AppendLine();
                sb.AppendLine("- 多个修改点用多个 @@ 标记");
                sb.AppendLine("- 新建文件用 *** Add File:");
                sb.AppendLine("- 删除文件用 *** Delete File:");
                sb.AppendLine("- 重命名用 *** Move to: <新路径>");
                sb.AppendLine();
                sb.AppendLine("### 备选：insert_edit_into_file（多处修改/重构）");
                sb.AppendLine("```insert_edit_into_file:完整/绝对/路径");
                sb.AppendLine("// ...existing code...");
                sb.AppendLine("（修改后的代码段，保留足够上下文以精确定位）");
                sb.AppendLine("// ...existing code...");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("### 新建文件：create_file");
                sb.AppendLine("```file:完整/绝对/路径");
                sb.AppendLine("完整文件内容");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("### 删除文件：");
                sb.AppendLine("delete:完整/绝对/路径");
                sb.AppendLine();
                sb.AppendLine("重要规则：");
                sb.AppendLine("1. 优先使用 apply_patch 格式（最精确、最快）");
                sb.AppendLine("2. 每种格式都必须包含文件的完整绝对路径");
                sb.AppendLine("3. 不要输出额外解释，只输出编辑操作");
                sb.AppendLine("4. 多个文件用多个独立的编辑块");
                sb.AppendLine();
                sb.AppendLine("## ⚠️ 项目配置文件规则（必须遵守）");
                sb.AppendLine("- ✅ **可以编辑 .vcxproj / .csproj**：NuGet 包引用、外部依赖路径、编译选项、项目间引用");
                sb.AppendLine("- ❌ **禁止手动添加/移除源文件引用**（<ClInclude> / <ClCompile> / <Compile> 等 ItemGroup 项）");
                sb.AppendLine("- 添加新源文件的方法：用 create_file (```file: 格式) 创建文件 → 系统自动通过 VS SDK 加入项目");
                sb.AppendLine("- **CMakeLists.txt** 不在此限制范围，可直接编辑（系统会请求确认）");
                sb.AppendLine("- 对于 CMake 项目：**必须先 create_file 创建源文件，再编辑 CMakeLists.txt** 添加引用");
                sb.AppendLine("- 如果 read_file 返回「文件不存在」，先创建该文件，不要尝试修改项目配置来绕过");
            }
            else
            {
                sb.AppendLine("这是一个分析/验证步骤，不需要修改代码。");
                sb.AppendLine("请直接输出你的分析结论、发现或建议。");
            }

            return sb.ToString();
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
                $"即将修改项目配置文件 `{fileName}`\n\n路径: {filePath}\n\n{desc}\n\n⚠️ 修改项目文件可能影响构建配置和项目结构。",
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
            return new AgentTaskPlan
            {
                Intent = AgentIntent.CodeChange,
                Title = LocalizationService.Instance["agent.step.executeCodeChange"],
                Steps = new List<AgentStep>
                {
                    new AgentStep
                    {
                        Index = 1,
                        Title = LocalizationService.Instance["agent.step.analyzeAndModify"],
                        Description = userMessage,
                        RequiresApproval = false,
                    }
                },
            };
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
            sb.AppendLine($"**{L["edit.summary.fileCount"]}**: {plan.ChangedFiles.Count}");
            sb.AppendLine();

            // 合并相同文件的变更记录
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

            // 步骤执行情况
            if (plan.Steps.Count > 0)
            {
                sb.AppendLine("## 步骤执行情况");
                foreach (var step in plan.Steps)
                {
                    string statusIcon = step.Status == AgentStepStatus.Completed ? "✅"
                        : step.Status == AgentStepStatus.Failed ? "❌"
                        : step.Status == AgentStepStatus.Skipped ? "⏭️"
                        : "🔄";
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary!
                        : "(无)";
                    sb.AppendLine($"- {statusIcon} **步骤 {step.Index}**: {step.Title} — {summary}");
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
                    var cached = ExploreAgent.GetCachedDiscoveredFiles(solutionPath);
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
                                additionalCtx += "\n已完成步骤: " + string.Join("; ",
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

                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "bin", "obj", ".git", ".vs", "node_modules", "packages",
                    "Debug", "Release",
                };

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
                    Logger.Info($"[EditAgent] ✅ 已将文件加入项目: {Path.GetFileName(filePath)} → {bestProject.Name}");
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

                        // 判断操作是否成功（✅ 开头表示成功）
                        if (!toolResult.StartsWith("✅")) continue;

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

                        string fileName = System.IO.Path.GetFileName(filePath);
                        string description = toolName switch
                        {
                            "replace_string_in_file" => $"修改 {fileName}",
                            "multi_replace_string_in_file" => $"批量修改 {fileName}",
                            "create_file" => $"新建 {fileName}",
                            _ => $"操作 {fileName}",
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
        /// 因日志中包含 "build"/"Build"/"❌" 等通用词而产生误判。
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

                // ── 明确的构建/编译失败标记（含 ❌ 前缀）──
                if (msg.Contains("❌ 构建失败") || msg.Contains("❌ 编译失败")
                    || msg.Contains("❌ build") || msg.Contains("❌ Build")
                    || msg.Contains("❌ CMake") || msg.Contains("❌ MSBuild"))
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
                if (msg.Contains("⚠️ 最终编译") || msg.Contains("Final build has issues")
                    || (msg.IndexOf("final build", StringComparison.OrdinalIgnoreCase) >= 0
                        && (msg.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("issues", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)))
                    return true;
            }
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
