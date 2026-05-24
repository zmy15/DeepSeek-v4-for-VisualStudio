using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
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
        private EditPatchService? _editPatchService;

        /// <summary>
        /// EditPatchService 引用，由 AgentDispatcher 注入。
        /// 用于解析和应用 apply_patch / insert_edit_into_file / create_file 三种编辑格式。
        /// </summary>
        public EditPatchService? EditPatchService
        {
            get => _editPatchService;
            set => _editPatchService = value;
        }

        /// <summary>
        /// ExploreAgent 引用，由 AgentDispatcher 注入。
        /// 用于在执行代码修改前智能发现相关文件。
        /// 设置时自动转发 ExploreAgent 的日志和文件变更事件。
        /// </summary>
        public ExploreAgent? ExploreAgent
        {
            get => _exploreAgent;
            set
            {
                if (_exploreAgent != null)
                {
                    _exploreAgent.LogEntryAdded -= OnExploreLog;
                    _exploreAgent.FileChangeNotified -= OnExploreFileChange;
                }
                _exploreAgent = value;
                if (_exploreAgent != null)
                {
                    _exploreAgent.LogEntryAdded += OnExploreLog;
                    _exploreAgent.FileChangeNotified += OnExploreFileChange;
                }
            }
        }

        private void OnExploreLog(AgentLogEntry entry)
        {
            // 转发 ExploreAgent 日志到 EditAgent 订阅者，避免重复写日志文件
            var forwarded = new AgentLogEntry { Level = entry.Level, Message = $"[Explore] {entry.Message}" };
            _logs.Add(forwarded);
            RaiseLogEntryAdded(forwarded);
        }

        private void OnExploreFileChange(AgentFileChangeEventArgs args)
        {
            // 转发 ExploreAgent 的文件变更通知
            NotifyFileChange(args.PlanId, args.ChangeType, args.FilePath, args.Detail);
        }

        /// <summary>当前正在执行的任务计划</summary>
        public AgentTaskPlan? CurrentPlan { get; set; }

        /// <summary>计划/步骤状态变更事件（UI 订阅）</summary>
        public event Action<AgentTaskPlan>? PlanUpdated;

        public EditAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Edit) { }

        #region Agent Definition

        /// <summary>
        /// Edit Agent 工具集 — 包含文件读写和终端执行能力。
        /// </summary>
        public static readonly string[] EditTools = new[]
        {
            "create_file",
            "delete_file",
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "apply_patch",
            "create_directory",
            "read_file",
            "file_search",
            "grep_search",
            "list_dir",
            "get_errors",
            "get_changed_files",
            "run_in_terminal",
            "get_terminal_output",
            "create_and_run_task",
            "manage_todo_list",
            "build_solution",
        };

        /// <summary>
        /// Edit Agent 代码步骤专用探索工具集（只读）。
        /// AI 在编写代码前使用这些工具探索项目结构，避免盲写。
        /// </summary>
        private static readonly string[] ExplorationTools = new[]
        {
            "read_file",
            "file_search",
            "grep_search",
            "list_dir",
            "get_errors",
            "get_changed_files",
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
                        Label = LocalizationService.Instance["agent.edit.handoffBuildLabel"],
                        TargetAgent = AgentType.Build,
                        Prompt = LocalizationService.Instance["agent.edit.handoffBuildPrompt"],
                        AutoSend = false,
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
                string aiSummary = await GenerateChangeSummaryAsync(context.ActivePlan, context.CancellationToken);
                result.Content = BuildSummaryMarkdown(context.ActivePlan, aiSummary);

                // ── 如果最终编译存在警告/失败，建议 Handoff 到 Build Agent ──
                if (HasBuildWarningsInLogs())
                {
                    result.Handoff = Definition.Handoffs.FirstOrDefault(h => h.TargetAgent == AgentType.Build);
                }
            }
            else
            {
                // ── 没有计划，作为单步代码修改执行 ──
                AddLog("INFO", LocalizationService.Instance["agent.log.editNoPlan"]);
                var plan = CreateSingleStepPlan(userMessage);
                await ExecutePlanAsync(plan, context);
                result.Plan = plan;
                result.FileChanges = plan.ChangedFiles;
                string aiSummary = await GenerateChangeSummaryAsync(plan, context.CancellationToken);
                result.Content = BuildSummaryMarkdown(plan, aiSummary);

                // ── 如果最终编译存在警告/失败，建议 Handoff 到 Build Agent ──
                if (HasBuildWarningsInLogs())
                {
                    result.Handoff = Definition.Handoffs.FirstOrDefault(h => h.TargetAgent == AgentType.Build);
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
                        AddLog("WARN", string.Format(L["agent.log.editStepSkipped"], step.Index));
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

                    // ── Planning 模式下继承上下文：将刚完成的步骤结果累积 ──
                    if (context.IsPlanningMode && step.Status == AgentStepStatus.Completed)
                    {
                        string stepResult = string.IsNullOrEmpty(step.ResultSummary)
                            ? $"步骤 {step.Index} ({step.Title}) 已完成"
                            : $"步骤 {step.Index} ({step.Title}): {step.ResultSummary}";
                        context.AccumulatedContext = (context.AccumulatedContext ?? "") + "\n" + stepResult;
                        if (!string.IsNullOrEmpty(step.AiResponse) && step.AiResponse.Length < 3000)
                            context.AccumulatedContext += "\n" + step.AiResponse;
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
                                ?? "⚠️ 构建工具未返回结果";
                        }
                        else
                        {
                            finalBuildResult = await ExecuteBuildStepAsync(
                                new AgentStep { Title = "最终编译验证" }, context.SolutionPath,
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

                bool approved = await RequestPermissionAsync(step.Title, step.PendingCommand, "command");
                if (!approved)
                {
                    step.Status = AgentStepStatus.Skipped;
                    step.ResultSummary = "用户拒绝执行";
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
                        ?? "⚠️ 构建工具未返回结果";
                }
                else
                {
                    buildResult = await ExecuteBuildStepAsync(step, context.SolutionPath, ct);
                }
                step.AiResponse = buildResult;
                step.ResultSummary = buildResult;
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
            var retryOutputs = new List<string>();
            for (int retry = 0; retry <= maxFormatRetries; retry++)
            {
                if (ct.IsCancellationRequested) return;

                string currentPrompt = stepPrompt;
                if (retry > 0)
                {
                    currentPrompt += "\n\n⚠️ 上次输出格式有误。请使用以下格式之一输出代码变更：\n\n" +
                        "1. apply_patch（首选）: *** Begin Patch / *** End Patch\n" +
                        "2. insert_edit_into_file: ```insert_edit_into_file:路径\\n...existing code...\n" +
                        "3. create_file: ```file:路径\\n完整内容\n" +
                        "不要添加额外解释，只输出编辑操作。";
                }
                var messages = BuildContextAwareMessages(Definition.SystemPrompt, currentPrompt);

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
                        sb.AppendLine($"> ⚠️ 第 {i + 1} 次尝试（前次格式错误，已自动重试）");
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

            // ── 检测编辑操作类型 ──
            var operationType = _editPatchService?.DetectOperationType(result)
                ?? EditOperationType.CreateFile;

            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editTypeDetected"], operationType));

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
                        // 新文件：所有行都是新增
                        realAdded = r.AppliedEdits.Count;
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
                .Select(g => g.First())
                .ToList();

            step.ResultSummary = changes.Count > 0
                ? $"修改 {changes.Count} 个文件（格式: {operationType}）"
                : $"未检测到文件变更（格式: {operationType}，已尝试匹配并应用编辑）";

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

                string verifyUserMessage =
                    "## 代码修改已完成\n\n" +
                    $"已修改 {changes.Count} 个文件：{string.Join(", ", changes.Select(c => Path.GetFileName(c.FilePath)))}\n\n" +
                    (sanityWarnings != null
                        ? $"⚠️ **编辑后健全性检查发现可能的问题**: {sanityWarnings}\n" +
                          $"请在验证时重点检查这些文件的括号/圆括号是否匹配，必要时用 read_file 查看文件末尾附近。\n\n"
                        : "") +
                    "请立即执行以下操作：\n" +
                    "1. 第一步就调用 build_solution 工具编译验证\n" +
                    "2. 如果编译失败且 build_solution 没有返回具体错误详情，调用 get_errors 获取错误列表\n" +
                    "3. 根据错误信息，用 read_file 读取报错文件的相关行，用 replace_string_in_file 直接修复\n" +
                    "4. 修复后重新调用 build_solution 验证，直到编译通过\n\n" +
                    "⚠️ 重要规则：\n" +
                    "- **始终使用 build_solution 工具进行编译**，不要尝试在终端中运行 cl.exe、msbuild、dotnet build 等命令\n" +
                    "- build_solution 已内置 VS 编译环境，终端中这些工具可能不在 PATH 中而失败\n" +
                    "- 如果 build_solution 返回的错误信息不完整，用 get_errors 补充获取\n" +
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
                    step.AiResponse = (step.AiResponse ?? "") + "\n\n## 验证\n\n" + verifyResult;
                    AddLog("INFO", LocalizationService.Instance["agent.log.verifyPhaseComplete"]);

                    // ── 追踪验证阶段的文件变更到 plan.ChangedFiles ──
                    TrackVerifyPhaseChanges(verifyMessages, plan);

                    // ── 智能检测编译是否真的失败 ──
                    // 避免因 AI 回复中的否定表述（如"没有错误"）误报警告
                    if (HasBuildFailure(verifyResult))
                    {
                        AddLog("WARN", "⚠️ 最终编译存在问题，请查看上方详情");
                    }
                }
            }
            else if (changes.Count > 0 && context.IsPlanningMode)
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.planningSkipVerify"]);
            }

            // ── 编辑后诊断检查 ──
            if (_editPatchService != null && appliedResults.Count > 0)
            {
                foreach (var editResult in appliedResults.Where(r => r.Success))
                {
                    var newDiags = await _editPatchService.CheckNewDiagnosticsAsync(editResult.FilePath);
                    if (newDiags.Count > 0)
                    {
                        editResult.NewDiagnostics = newDiags;
                        AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.newDiagnostics"],
                            Path.GetFileName(editResult.FilePath), newDiags.Count,
                            string.Join("; ", newDiags.Take(5))));
                    }
                }
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
        /// 执行 apply_patch 格式的编辑。
        /// </summary>
        private async Task ExecutePatchEditsAsync(
            string aiResult, AgentTaskPlan plan, AgentContext context,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct)
        {
            if (_editPatchService == null)
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.patchServiceMissing"]);
                return;
            }

            var patches = _editPatchService.ParsePatches(aiResult);
AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.parsedPatches"], patches.Count));

            // ── 跟踪每个文件的内存内容，实现跨 Patch 原子性 ──
            // Key: 文件路径, Value: (是否已写入磁盘, 当前内存中的内容)
            var fileState = new Dictionary<string, (bool written, string content)>(StringComparer.OrdinalIgnoreCase);

            foreach (var patch in patches)
            {
                if (ct.IsCancellationRequested) break;

                string resolvedPath = EditPatchService.ResolvePath(patch.FilePath, workspaceRoot);

                // ── 保存原始内容（仅首次）──
                if (!originalContents.ContainsKey(resolvedPath))
                {
                    // RAG-SOURCE: file-read 读取文件原始内容（Patch 应用前保存）
                    string original = File.Exists(resolvedPath)
                        ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                        : string.Empty;
                    originalContents[resolvedPath] = original;

                    // 初始化内存状态：以原始文件内容为起点
                    fileState[resolvedPath] = (written: false, content: original);
                }

                // ── 使用内存中的最新内容进行匹配（避免读盘拿到旧内容）──
                string currentContent = fileState.TryGetValue(resolvedPath, out var state)
                    ? state.content
                    : originalContents[resolvedPath];

                var applyResult = await _editPatchService.ApplyPatchWithContentAsync(
                    patch, workspaceRoot, ct, existingContent: currentContent);

                if (!applyResult.Success && applyResult.FailedHunks != null && applyResult.FailedHunks.Count > 0)
                {
                    // ── Healing 机制：匹配失败 → 降级模型修正 ──
                    string failedHunkDetails = string.Join("; ", applyResult.FailedHunks.Select(h =>
                        $"Hunk ({h.ContextMarkers.Count} 定位标记: {string.Join(", ", h.ContextMarkers.Take(3))})"));
                    AddLog("WARN", $"[EditAgent] Patch 匹配失败 ({applyResult.ErrorMessage})，失败详情: {failedHunkDetails}，启动 healing...");

                    var healingRequest = new HealingRequest
                    {
                        FilePath = resolvedPath,
                        CurrentFileContent = currentContent,
                        OriginalOperationType = EditOperationType.ApplyPatch,
                        FailedPatch = patch,
                        FailureReason = applyResult.ErrorMessage ?? "未知原因",
                    };

                    var healingResponse = await _editPatchService.HealFailedEditAsync(healingRequest, ct);

                    // ── Healing 兜底：降级模型失败 → 用完整模型重试一次 ──
                    if (healingResponse?.Success != true || healingResponse.CorrectedPatch == null)
                    {
                        AddLog("WARN", $"[EditAgent] 降级模型 healing 失败 ({healingResponse?.ErrorMessage ?? "无响应"})，尝试完整模型...");
                        healingResponse = await _editPatchService.HealFailedEditWithFullModelAsync(healingRequest, ct);
                    }

                    if (healingResponse?.Success == true && healingResponse.CorrectedPatch != null)
                    {
                        AddLog("INFO", "[EditAgent] Healing 成功，使用修正后的 Patch 重试...");
                        applyResult = await _editPatchService.ApplyPatchWithContentAsync(
                            healingResponse.CorrectedPatch, workspaceRoot, ct, existingContent: currentContent);

                        // ── 兜底：Healing 修正后仍失败 → 尝试作为 create_file 写入完整内容 ──
                        if (!applyResult.Success && !string.IsNullOrEmpty(applyResult.FinalContent))
                        {
                            AddLog("WARN", $"[EditAgent] Healing 修正后仍失败 ({applyResult.ErrorMessage})，启用 create_file 兜底写入...");
                            try
                            {
                                bool fallbackAllowed = await EnsureProjectFileWriteConfirmedAsync(
                                    resolvedPath, "Healing 兜底写入（Patch→create_file）", applyResult.FinalContent ?? string.Empty);
                                if (fallbackAllowed)
                                {
                                    await TerminalWindowHelper.WriteCodeToFileAsync(
                                        applyResult.FilePath, applyResult.FinalContent!);
                                    applyResult.Success = true;
                                    fileState[resolvedPath] = (written: true, content: applyResult.FinalContent!);
                                    AddLog("INFO", $"[EditAgent] ✅ create_file 兜底成功: {resolvedPath}");
                                }
                                else
                                {
                                    AddLog("WARN", $"[EditAgent] ⏭ 已跳过项目文件兜底写入（用户拒绝）: {Path.GetFileName(resolvedPath)}");
                                }
                            }
                            catch (Exception fallbackEx)
                            {
                                AddLog("ERROR", $"[EditAgent] create_file 兜底也失败: {fallbackEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        AddLog("ERROR", $"[EditAgent] Healing 完全失败: {healingResponse?.ErrorMessage ?? "未知"}");
                    }
                }
                else if (!applyResult.Success)
                {
                    // ── 无 FailedHunks 但仍失败（如文件不存在、权限问题等）──
                    AddLog("ERROR", $"[EditAgent] Patch 应用失败（非匹配问题）: {resolvedPath} - {applyResult.ErrorMessage}");
                }

                appliedResults.Add(applyResult);

                if (applyResult.Success)
                {
                    // ── 更新内存中的文件内容（延迟写盘，保证原子性）──
                    if (!string.IsNullOrEmpty(applyResult.FinalContent))
                    {
                        fileState[resolvedPath] = (written: false, content: applyResult.FinalContent!);
                    }

                    // ── 新文件：需要先写入再加入项目 ──
                    bool isNewFile = patch.Action == PatchFileAction.Add;
                    if (isNewFile)
                    {
                        // 新文件必须立即写入磁盘才能加入项目
                        bool writeAllowed = await EnsureProjectFileWriteConfirmedAsync(
                            resolvedPath, $"Patch 新建文件", applyResult.FinalContent ?? string.Empty);
                        if (writeAllowed)
                        {
                            await TerminalWindowHelper.WriteCodeToFileAsync(
                                applyResult.FilePath, applyResult.FinalContent!);
                            fileState[resolvedPath] = (written: true, content: applyResult.FinalContent!);

                            if (File.Exists(resolvedPath))
                                await AddFileToProjectAsync(resolvedPath, ct);
                        }
                        else
                        {
                            AddLog("WARN", $"⏭ 已跳过项目文件写入（用户拒绝）: {Path.GetFileName(resolvedPath)}");
                            applyResult.Success = false;
                            applyResult.ErrorMessage = "用户拒绝修改项目文件";
                        }
                    }

                    if (applyResult.Success)
                    {
                        AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.patchMatched"], resolvedPath, applyResult.AppliedEdits.Count));
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
                                BriefDescription = $"{Path.GetFileName(resolvedPath)} (Patch)",
                            });
                        }
                    }
                }
                else
                {
                    AddLog("ERROR", $"❌ Patch 应用失败: {resolvedPath} - {applyResult.ErrorMessage}");
                }
            } // foreach patch

            // ── 原子写盘：所有 Patch 匹配成功后，统一批量写入文件 ──
            // 项目配置文件（.csproj/.slnx 等）优先写入，避免 VS 弹出"检测到冲突文件修改"对话框
            var sortedFilePaths = SortPathsWithProjectFilesFirst(fileState.Keys);
            foreach (var filePath in sortedFilePaths)
            {
                if (!fileState.TryGetValue(filePath, out var state)) continue;
                if (state.written) continue; // 已写入（新文件等）
                if (ct.IsCancellationRequested) break;

                string finalContent = state.content;

                bool writeAllowed = await EnsureProjectFileWriteConfirmedAsync(
                    filePath, $"Patch 批量写入（原子模式）", finalContent);
                if (writeAllowed)
                {
                    await TerminalWindowHelper.WriteCodeToFileAsync(filePath, finalContent);
                    AddLog("INFO", $"💾 批量写盘: {Path.GetFileName(filePath)}");
                }
                else
                {
                    AddLog("WARN", $"⏭ 已跳过批量写盘（用户拒绝）: {Path.GetFileName(filePath)}");
                }
            }
        }

        /// <summary>
        /// 执行 insert_edit_into_file 格式的编辑。
        /// </summary>
        private async Task ExecuteInsertEditsAsync(
            string aiResult, AgentTaskPlan plan, AgentContext context,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct)
        {
            if (_editPatchService == null)
            {
                AddLog("WARN", "EditPatchService 未注入，无法处理 insert_edit_into_file 格式");
                return;
            }

            var insertEdits = _editPatchService.ParseInsertEdits(aiResult);
            AddLog("INFO", $"[EditAgent] 解析到 {insertEdits.Count} 个 InsertEdit 操作");

            // ── 项目配置文件（.csproj/.slnx 等）优先处理，避免 VS 弹出"检测到冲突文件修改"对话框 ──
            var sortedEdits = insertEdits
                .OrderBy(e => IsProjectFile(EditPatchService.ResolvePath(e.FilePath, workspaceRoot)) ? 0 : 1)
                .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var edit in sortedEdits)
            {
                if (ct.IsCancellationRequested) break;

                string resolvedPath = EditPatchService.ResolvePath(edit.FilePath, workspaceRoot);

                // ── 保存原始内容 ──
                if (!originalContents.ContainsKey(resolvedPath))
                {
                    // RAG-SOURCE: file-read 读取文件原始内容（InsertEdit 应用前保存）
                    string original = File.Exists(resolvedPath)
                        ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                        : string.Empty;
                    originalContents[resolvedPath] = original;
                }

                // ── 应用 InsertEdit ──
                var applyResult = await _editPatchService.ApplyInsertEditAsync(edit, workspaceRoot, ct);

                if (!applyResult.Success && applyResult.FailedRegions != null && applyResult.FailedRegions.Count > 0)
                {
                    // ── Healing 机制 ──
                    string failedRegionDetails = string.Join("; ", applyResult.FailedRegions.Select(r =>
                        r.Length > 80 ? r.Substring(0, 80) + "…" : r));
                    AddLog("WARN", $"[EditAgent] InsertEdit 匹配失败 ({applyResult.ErrorMessage})，失败区域: {failedRegionDetails}，启动 healing...");

                    var healingRequest = new HealingRequest
                    {
                        FilePath = resolvedPath,
                        CurrentFileContent = originalContents.TryGetValue(resolvedPath, out string? orig2) ? orig2 : "",
                        OriginalOperationType = EditOperationType.InsertEditIntoFile,
                        FailedInsertEditContent = edit.FullContent,
                        FailureReason = applyResult.ErrorMessage ?? "未知原因",
                    };

                    var healingResponse = await _editPatchService.HealFailedEditAsync(healingRequest, ct);

                    if (healingResponse?.Success == true && !string.IsNullOrEmpty(healingResponse.CorrectedInsertEditContent))
                    {
                        AddLog("INFO", "[EditAgent] Healing 成功，使用修正后的内容重试...");
                        var correctedEdit = new InsertEditOperation
                        {
                            FilePath = edit.FilePath,
                            FullContent = healingResponse.CorrectedInsertEditContent!,
                        };
                        applyResult = await _editPatchService.ApplyInsertEditAsync(correctedEdit, workspaceRoot, ct);

                        // ── 兜底：Healing 修正后仍失败 → 尝试作为 create_file 写入完整内容 ──
                        // 优先使用 applyResult.FinalContent（匹配成功时有值），
                        // 其次使用 healing 修正后的内容（全替换场景下 FinalContent 可能为 null）
                        string? fallbackContent = applyResult.FinalContent;
                        if (string.IsNullOrEmpty(fallbackContent))
                            fallbackContent = healingResponse.CorrectedInsertEditContent;
                        if (string.IsNullOrEmpty(fallbackContent))
                            fallbackContent = edit.FullContent;

                        if (!applyResult.Success && !string.IsNullOrEmpty(fallbackContent))
                        {
                            AddLog("WARN", $"[EditAgent] Healing 修正后仍失败 ({applyResult.ErrorMessage})，启用 create_file 兜底写入...");
                            try
                            {
                                // ── 项目文件拦截 ──
                                bool fallbackAllowed = await EnsureProjectFileWriteConfirmedAsync(
                                    resolvedPath, "Healing 兜底写入（InsertEdit→create_file）", fallbackContent ?? string.Empty);
                                if (fallbackAllowed)
                                {
                                    await TerminalWindowHelper.WriteCodeToFileAsync(
                                        applyResult.FilePath, fallbackContent!);
                                    applyResult.Success = true;
                                    AddLog("INFO", $"[EditAgent] ✅ create_file 兜底成功: {resolvedPath}");
                                }
                                else
                                {
                                    AddLog("WARN", $"[EditAgent] ⏭ 已跳过项目文件兜底写入（用户拒绝）: {Path.GetFileName(resolvedPath)}");
                                }
                            }
                            catch (Exception fallbackEx)
                            {
                                AddLog("ERROR", $"[EditAgent] create_file 兜底也失败: {fallbackEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        AddLog("ERROR", $"[EditAgent] Healing 失败: {healingResponse?.ErrorMessage ?? "未知"}");
                    }
                }
                else if (!applyResult.Success)
                {
                    // ── 无 FailedRegions 但仍失败（如文件不存在、权限问题等）──
                    AddLog("ERROR", $"[EditAgent] InsertEdit 应用失败（非匹配问题）: {resolvedPath} - {applyResult.ErrorMessage}");
                }

                appliedResults.Add(applyResult);

                if (applyResult.Success)
                {
                    // ── 通过 VS SDK 写入文件 ──
                    if (!string.IsNullOrEmpty(applyResult.FinalContent))
                    {
                        // ── 项目文件拦截：修改 .vcxproj/.sln 等前请求用户确认 ──
                        bool writeAllowed = await EnsureProjectFileWriteConfirmedAsync(
                            resolvedPath, string.Format(LocalizationService.Instance["agent.log.insertEditPoints"], applyResult.AppliedEdits.Count), applyResult.FinalContent ?? string.Empty);
                        if (writeAllowed)
                        {
                            await TerminalWindowHelper.WriteCodeToFileAsync(
                                applyResult.FilePath, applyResult.FinalContent!);
                        }
                        else
                        {
                            AddLog("WARN", $"⏭ 已跳过项目文件写入（用户拒绝）: {Path.GetFileName(resolvedPath)}");
                            applyResult.Success = false;
                            applyResult.ErrorMessage = "用户拒绝修改项目文件";
                        }
                    }

                    if (applyResult.Success)
                    {
                        AddLog("INFO", string.Format("✅ InsertEdit 已应用: {0} ({1} 个编辑)", resolvedPath, applyResult.AppliedEdits.Count));
                        NotifyFileChange(plan.PlanId, "modify", resolvedPath,
                            string.Format(LocalizationService.Instance["agent.log.patchEditPoints"], applyResult.AppliedEdits.Count));

                        // ── 更新 plan.ChangedFiles 确保文件计数正确（计算真实行数变化）──
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
                    AddLog("ERROR", $"❌ InsertEdit 应用失败: {resolvedPath} - {applyResult.ErrorMessage}");
                }
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

            // ── 项目配置文件（.csproj/.slnx 等）优先处理，避免 VS 弹出"检测到冲突文件修改"对话框 ──
            var sortedChanges = changes
                .OrderBy(c => IsProjectFile(ResolveFilePath(c.FilePath, context.SolutionPath)) ? 0 : 1)
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
                        AddLog("INFO", $"📄 预创建文件并加入项目: {Path.GetFileName(resolvedPath)}");
                        await AddFileToProjectAsync(resolvedPath, ct);
                    }

                    // ── 项目文件拦截：新建/修改 .vcxproj/.sln 等前请求用户确认 ──
                    string createOpDesc = isNewFile
                        ? $"新建项目文件: {Path.GetFileName(resolvedPath)}"
                        : $"修改文件: {Path.GetFileName(resolvedPath)} (+{change.LinesAdded} -{change.LinesRemoved})";
                    bool createWriteAllowed = await EnsureProjectFileWriteConfirmedAsync(resolvedPath, createOpDesc, change.NewContent ?? string.Empty);
                    if (!createWriteAllowed)
                    {
                        AddLog("WARN", $"⏭ 已跳过项目文件写入（用户拒绝）: {Path.GetFileName(resolvedPath)}");
                        appliedResults.Add(new EditApplyResult
                        {
                            FilePath = resolvedPath,
                            Success = false,
                            OperationType = EditOperationType.CreateFile,
                            ErrorMessage = "用户拒绝修改项目文件",
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
                        AddLog("ERROR", $"写入文件失败: {resolvedPath} - {error}");
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
                    AddLog("ERROR", $"写入文件失败: {change.FilePath} - {ex.Message}");
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

            AddLog("INFO", $"检测到 {resolvedDeletions.Count} 个待删除文件: [{string.Join(", ", resolvedDeletions.Select(Path.GetFileName))}]");

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

            string deleteReason = plan.Title ?? "代码重构";
            bool confirmed = await RequestFileDeleteConfirmationAsync(resolvedDeletions, deleteReason);

            if (confirmed)
            {
                await AgentDispatcher.DeleteFilesViaEnvDTEAsync(resolvedDeletions);
                AddLog("INFO", $"✅ 已删除 {resolvedDeletions.Count} 个文件");

                foreach (string deletedPath in resolvedDeletions)
                {
                    deletionOriginals.TryGetValue(deletedPath, out string? capturedOriginal);
                    plan.ChangedFiles.Add(new FileChangeSummary
                    {
                        FilePath = deletedPath,
                        LinesAdded = 0,
                        LinesRemoved = -1,
                        BriefDescription = $"{Path.GetFileName(deletedPath)} (已删除)",
                        OriginalContent = capturedOriginal,
                    });
                    NotifyFileChange(plan.PlanId, "delete", deletedPath, "已删除");
                }
            }
            else
            {
                AddLog("WARN", "❌ 用户取消了文件删除");
            }
        }

        /// <summary>
        /// 检测 AI 输出是否包含任何有效的编辑格式。
        /// </summary>
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
        /// 使用 IVsSolutionBuildManager (VS SDK Interop) 替代 EnvDTE。
        /// </summary>
        private async Task<string> ExecuteBuildStepAsync(AgentStep step, string? solutionPath, CancellationToken ct)
        {
            AddLog("INFO", $"开始构建步骤: {step.Title}");

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // ── 获取 IVsSolutionBuildManager（触发解决方案构建）──
            var buildManager = (IVsSolutionBuildManager?)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                .GetService(typeof(SVsSolutionBuildManager));

            if (buildManager == null)
            {
                Logger.Warn("[EditAgent] 无法获取 IVsSolutionBuildManager，跳过构建");
                return "⚠️ 无法获取构建管理器，请在 VS 中手动构建。";
            }

            // ── 检查构建管理器是否正忙 ──
            int isBusy;
            buildManager.QueryBuildManagerBusy(out isBusy);
            if (isBusy != 0)
            {
                Logger.Warn("[EditAgent] 构建管理器正忙，跳过构建");
                return "⚠️ 构建管理器正忙，请等待当前构建完成后重试。";
            }

            var buildEventsSink = new BuildEventsSink();
            uint buildCookie = 0;
            bool advised = false;

            try
            {
                // ── 订阅构建事件以获知构建完成 ──
                buildManager.AdviseUpdateSolutionEvents(buildEventsSink, out buildCookie);
                advised = true;

                Logger.Info("[EditAgent] 正在构建解决方案…");

                // ── 启动解决方案构建 ──
                int hr = buildManager.StartSimpleUpdateSolutionConfiguration(
                    (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD,
                    0,      // dwDefQueryResults
                    0);     // fSuppressUI

                if (hr < 0)
                {
                    Logger.Warn($"[EditAgent] StartSimpleUpdateSolutionConfiguration 失败: 0x{hr:X8}");
                    return $"⚠️ 启动构建失败 (0x{hr:X8})";
                }

                // ── 等待构建完成（最长 5 分钟超时）──
                bool completed = await buildEventsSink.WaitForCompletionAsync(ct, TimeSpan.FromMinutes(5));

                if (!completed)
                {
                    Logger.Warn("[EditAgent] ⚠️ 构建超时（5 分钟），请检查解决方案状态");
                    return "⚠️ 构建超时（5 分钟），请手动检查构建状态。可能是解决方案过大或存在循环依赖。";
                }

                // ── 收集构建结果 ──
                int buildSucceeded, buildFailed, buildCancelled;
                buildEventsSink.GetBuildResult(out buildSucceeded, out buildFailed, out buildCancelled);

                // ── 收集错误列表 ──
                string errorDetails = CollectBuildErrors();

                if (buildFailed == 0 && buildCancelled == 0)
                {
                    Logger.Info($"[EditAgent] ✅ 构建成功 ({buildSucceeded} 个项目)");
                    return $"✅ 构建成功，{buildSucceeded} 个项目通过";
                }

                if (buildCancelled != 0)
                {
                    Logger.Info("[EditAgent] ⚠️ 构建已取消");
                    return "⚠️ 构建已取消";
                }

                var result = new StringBuilder();
                result.AppendLine($"⚠️ 构建完成，{buildFailed} 个项目失败");
                if (!string.IsNullOrEmpty(errorDetails))
                {
                    result.AppendLine();
                    result.AppendLine("## 编译错误详情");
                    result.Append(errorDetails);
                }

                string fullResult = result.ToString();
                Logger.Info($"[EditAgent] {fullResult.Truncate(500)}");
                return fullResult;
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[EditAgent] 构建已取消");
                return "⚠️ 构建已取消";
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 构建异常: {ex.Message}");
                return $"⚠️ 构建失败: {ex.Message}";
            }
            finally
            {
                // ── 确保取消订阅事件 ──
                if (advised && buildCookie != 0)
                {
                    try { buildManager.UnadviseUpdateSolutionEvents(buildCookie); } catch { }
                }
                buildEventsSink.Dispose();
            }
        }

        /// <summary>
        /// 从 VS Task List 收集编译错误详情。
        /// 委托给 BuildService.CollectBuildErrors() 统一实现。
        /// </summary>
        private static string CollectBuildErrors()
        {
            return BuildService.CollectBuildErrors();
        }

        /// <summary>
        /// 构建事件接收器，实现 IVsUpdateSolutionEvents 以监听构建开始/完成/取消。
        /// 通过 TaskCompletionSource 将事件驱动的回调转换为可等待的 Task。
        /// 内置超时保护，防止因构建事件丢失而永久挂起。
        /// </summary>
        private sealed class BuildEventsSink : IVsUpdateSolutionEvents, IDisposable
        {
            private readonly TaskCompletionSource<bool> _tcs = new();
            private CancellationTokenRegistration _ctRegistration;
            private int _succeeded;
            private int _failed;
            private int _cancelled;
            private bool _disposed;

            /// <summary>
            /// 等待构建完成或超时。
            /// </summary>
            /// <returns>true 表示构建事件正常触发；false 表示超时</returns>
            public async Task<bool> WaitForCompletionAsync(CancellationToken ct, TimeSpan timeout)
            {
                _ctRegistration = ct.Register(() => _tcs.TrySetCanceled());
                try
                {
                    var completedTask = await Task.WhenAny(_tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                    if (completedTask == _tcs.Task)
                    {
                        await completedTask.ConfigureAwait(false); // 传播异常（如有）
                        return true;
                    }
                    return false; // 超时
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            public void GetBuildResult(out int succeeded, out int failed, out int cancelled)
            {
                succeeded = _succeeded;
                failed = _failed;
                cancelled = _cancelled;
            }

            public int UpdateSolution_Begin(ref int pfCancelUpdate)
            {
                pfCancelUpdate = 0; // 不取消构建
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdate(ref int pfCancelUpdate)
            {
                pfCancelUpdate = 0; // 不取消更新
                return VSConstants.S_OK;
            }

            public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
            {
                if (fCancelCommand != 0)
                    _cancelled = 1;
                else if (fSucceeded != 0)
                    _succeeded = 1;
                else
                    _failed = 1;

                _tcs.TrySetResult(true);
                return VSConstants.S_OK;
            }

            public int UpdateSolution_StartUpdateProjectCfg(
                ref int pfCancel, IVsHierarchy pHierProj, IVsCfg pCfgProj,
                IVsCfg pCfgSln, uint dwProjectCfgOfInterest, uint dwCopyFlags, int fCancel)
            {
                return VSConstants.S_OK;
            }

            public int UpdateSolution_Cancel()
            {
                _cancelled = 1;
                _tcs.TrySetResult(true);
                return VSConstants.S_OK;
            }

            public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy)
            {
                return VSConstants.S_OK;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _ctRegistration.Dispose();
                _tcs.TrySetResult(false);
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
            sb.AppendLine($"你是一个 Edit Agent，正在执行任务：「{plan.Title}」。");
            sb.AppendLine($"当前步骤 ({step.Index}/{plan.Steps.Count}): {step.Title}");
            sb.AppendLine($"步骤详情: {step.Description}");
            sb.AppendLine();

            // ── Planning 模式：注入之前步骤的累积上下文，避免重复搜索解决方案 ──
            if (context.IsPlanningMode && !string.IsNullOrEmpty(context.AccumulatedContext))
            {
                sb.AppendLine("## 前面步骤的执行结果（请基于这些结果继续，不要重复搜索已发现的文件）");
                // RAG-MARK: no-truncate — 不再截断累积上下文，完整传递给 EditAgent
                // RAG-SOURCE: accumulated-context 之前步骤的累积执行结果
                sb.AppendLine(context.AccumulatedContext);
                sb.AppendLine();
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
                sb.AppendLine("- 如果 read_file 返回「文件不存在」，先创建该文件，不要尝试修改项目配置来绕过");
            }
            else
            {
                sb.AppendLine("这是一个分析/验证步骤，不需要修改代码。");
                sb.AppendLine("请直接输出你的分析结论、发现或建议。");
            }

            return sb.ToString();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名集合。
        /// 修改这些文件可能影响项目结构，需要用户明确许可。
        /// </summary>
        private static readonly HashSet<string> ProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".vcxproj", ".csproj", ".fsproj", ".vbproj",
            ".sln", ".slnx",
            ".vcxproj.filters", ".vcxproj.user",
            ".csproj.user", ".vbproj.user",
            "CMakeLists.txt", "CMakeSettings.json",
            "packages.config", "Directory.Build.props", "Directory.Build.targets",
            ".props", ".targets",
            "Makefile", "makefile", "GNUmakefile",
            ".slnf",
        };

        /// <summary>
        /// 检查文件是否为项目文件（需要用户确认才能修改）。
        /// </summary>
        private static bool IsProjectFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath);
            return ProjectFileExtensions.Contains(fileName) || ProjectFileExtensions.Contains(ext);
        }

        /// <summary>
        /// 将文件路径列表排序，确保项目配置文件（.csproj/.slnx等）优先写入。
        /// 避免 VS 在外部修改源文件后才检测到项目文件变更而弹出"检测到冲突文件修改"对话框。
        /// </summary>
        private static List<string> SortPathsWithProjectFilesFirst(IEnumerable<string> paths)
        {
            return paths
                .OrderBy(p => IsProjectFile(p) ? 0 : 1)
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
        /// <returns>true=允许写入, false=用户拒绝</returns>
        private async Task<bool> EnsureProjectFileWriteConfirmedAsync(string filePath, string operationDescription = "", string fileContent = "")
        {
            if (!IsProjectFile(filePath))
                return true; // 非项目文件，直接放行

            string fileName = Path.GetFileName(filePath);
            string desc = !string.IsNullOrEmpty(operationDescription)
                ? operationDescription
                : $"修改项目文件: {fileName}";

            AddLog("WARN", $"⚠️ 检测到项目文件修改: {fileName}，请求用户确认...");

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
                detail);

            if (!approved)
            {
                AddLog("WARN", $"❌ 用户拒绝了项目文件修改: {fileName}");
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

        private static string BuildSummaryMarkdown(AgentTaskPlan plan, string? aiSummary = null)
        {
            var L = LocalizationService.Instance;

            if (plan.IsCancelled)
                return L["edit.summary.cancelled"];

            var sb = new StringBuilder();
            sb.AppendLine(L["edit.summary.complete"]);
            sb.AppendLine();
            sb.AppendLine($"**{L["edit.summary.taskLabel"]}**: {plan.Title}");
            sb.AppendLine($"**{L["edit.summary.fileCount"]}**: {plan.ChangedFiles.Count}");
            sb.AppendLine();

            // ── AI 生成的文字总结 ──
            if (!string.IsNullOrWhiteSpace(aiSummary))
            {
                sb.AppendLine($"### {L["edit.summary.changeSummary"]}");
                sb.AppendLine();
                sb.AppendLine(aiSummary);
                sb.AppendLine();
            }

            // ── 步骤执行详情 ──
            if (plan.Steps.Count > 0)
            {
                sb.AppendLine($"### {L["edit.summary.stepDetails"]}");
                sb.AppendLine();
                foreach (var step in plan.Steps)
                {
                    string statusIcon = step.Status == AgentStepStatus.Completed ? "✅"
                        : step.Status == AgentStepStatus.Failed ? "❌"
                        : step.Status == AgentStepStatus.Skipped ? "⏭️"
                        : "🔄";
                    sb.AppendLine($"- {statusIcon} **步骤 {step.Index}**: {step.Title}");
                    if (!string.IsNullOrEmpty(step.ResultSummary))
                        sb.AppendLine($"  - 结果: {step.ResultSummary}");

                    // ── 包含验证结果：如果步骤的 AiResponse 中有验证章节，提取关键信息 ──
                    if (!string.IsNullOrEmpty(step.AiResponse))
                    {
                        string aiResp = step.AiResponse!;
                        int verifyIdx = aiResp.IndexOf("## 验证");
                        if (verifyIdx >= 0)
                        {
                            string verifySection = aiResp.Substring(verifyIdx);
                            // 截取前 500 字符作为摘要
                            if (verifySection.Length > 500)
                                verifySection = verifySection.Substring(0, 500) + "\n\n...(已截断，完整日志见执行过程)";
                            // ── 使用 Markdown 块引用格式（非 HTML，兼容 DisableHtml 渲染管线）──
                            sb.AppendLine();
                            sb.AppendLine($"> 🔍 **验证详情**");
                            sb.AppendLine(">");
                            foreach (var line in verifySection.Split('\n'))
                            {
                                sb.AppendLine($"> {line}");
                            }
                            sb.AppendLine();
                        }
                    }
                }
                sb.AppendLine();
            }

            if (plan.ChangedFiles.Count > 0)
            {
                // ── 按文件路径合并相同文件的多条变更记录 ──
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        DisplayPath = g.First().FilePath,
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                        Description = g.Select(c => c.BriefDescription).FirstOrDefault(d => !string.IsNullOrEmpty(d) && !d.Contains("(Patch)") && !d.Contains("(InsertEdit)") && !d.Contains("(CreateFile)")),
                    })
                    .ToList();

                sb.AppendLine(LocalizationService.Instance["agent.panel.fileChangeStats"]);
                sb.AppendLine();
                sb.AppendLine("| 文件 | 变更 | 说明 |");
                sb.AppendLine("|------|------|------|");
                foreach (var change in mergedFiles)
                {
                    string delta = $"{(change.LinesAdded > 0 ? $"+{change.LinesAdded}" : "")}"
                        + $"{(change.LinesRemoved > 0 ? $" -{change.LinesRemoved}" : "")}";
                    string desc = change.Description ?? (change.LinesAdded > 0 && change.LinesRemoved == 0 ? "新增" : change.LinesRemoved > 0 && change.LinesAdded == 0 ? "删除" : "修改");
                    // 截断过长的描述
                    if (desc.Length > 40) desc = desc.Substring(0, 37) + "...";
                    sb.AppendLine($"| `{change.FileName}` | {delta} | {desc} |");
                }
                sb.AppendLine();
                sb.AppendLine(LocalizationService.Instance.Format("edit.summary.totalChanges",
                    mergedFiles.Sum(c => c.LinesAdded),
                    mergedFiles.Sum(c => c.LinesRemoved)));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成 AI 文字总结，概括本次代码变更的内容和目的。
        /// 包含变更统计、受影响文件、每步操作概述，提供给 AI 生成更详细的摘要。
        /// </summary>
        private async Task<string> GenerateChangeSummaryAsync(AgentTaskPlan plan, CancellationToken ct)
        {
            if (plan.ChangedFiles.Count == 0) return string.Empty;

            try
            {
                var L = LocalizationService.Instance;
                var summaryPrompt = new StringBuilder();
                summaryPrompt.AppendLine(L["edit.summary.genPrompt"]);
                summaryPrompt.AppendLine();
                summaryPrompt.AppendLine(L.Format("edit.summary.taskHeader", plan.Title));
                summaryPrompt.AppendLine(L.Format("edit.summary.stepCount", plan.Steps.Count, plan.Steps.Count(s => s.Status == AgentStepStatus.Completed)));
                summaryPrompt.AppendLine();

                // ── 合并相同文件 ──
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Path = g.Key, Added = g.Sum(c => c.LinesAdded), Removed = g.Sum(c => c.LinesRemoved), Names = g.Select(c => Path.GetFileName(c.FilePath)).First() })
                    .ToList();
                int totalAdded = mergedFiles.Sum(f => f.Added);
                int totalRemoved = mergedFiles.Sum(f => f.Removed);

                summaryPrompt.AppendLine(L.Format("edit.summary.changeStats", totalAdded, totalRemoved, mergedFiles.Count));
                summaryPrompt.AppendLine();
                summaryPrompt.AppendLine(L["edit.summary.modifiedFiles"]);
                foreach (var file in mergedFiles)
                {
                    summaryPrompt.AppendLine($"- **{file.Names}** (+{file.Added} -{file.Removed})");
                }
                summaryPrompt.AppendLine();

                summaryPrompt.AppendLine("## 步骤执行情况");
                foreach (var step in plan.Steps)
                {
                    string status = step.Status switch
                    {
                        AgentStepStatus.Completed => "✅",
                        AgentStepStatus.Failed => "❌",
                        AgentStepStatus.Skipped => "⏭",
                        _ => "⬜",
                    };
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary
                        : "(无)";
                    summaryPrompt.AppendLine($"- {status} {step.Title}: {summary}");
                }
                summaryPrompt.AppendLine();

                // 语言跟随：根据当前语言选择摘要输出语言
                bool isEnglish = !string.Equals(LocalizationService.Instance.CurrentLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase);
                string langInstruction = isEnglish
                    ? "Please output a detailed change summary in English (be thorough, covering what was changed, why, the approach taken, and any notable details):"
                    : "请用中文输出详细的变更摘要（尽量详细，包含改了什么、为什么改、改法思路、技术细节等）：";
                summaryPrompt.AppendLine(langInstruction);

                string shortSystemPrompt = isEnglish
                    ? "You only output a code change summary in English, nothing else."
                    : "你只输出中文代码变更摘要，不输出任何其他内容。";

                string result = await CallAiShortAsync(
                    shortSystemPrompt,
                    summaryPrompt.ToString(), ct, maxTokens: 4096);

                // ── 安全剥离：防止 AI 意外输出工具调用标记或思考过程 ──
                result = StripToolCallMarkers(result);
                return result?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 生成变更摘要失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 剥离 AI 输出中意外包含的工具调用标记和思考过程文本。
        /// 当 toolChoice="none" 时 AI 仍可能输出工具调用意图文本。
        /// </summary>
        private static string StripToolCallMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 移除 <|tool_calls|>...</|tool_calls|>  XML 片段
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"<\|[^>]*tool_calls?[^>]*\|>.*?</\|[^>]*tool_calls?[^>]*\|>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除单个工具调用标签
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"</?\|[^>]*tool_calls?[^>]*\|>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除思考前缀（"让我先查看..."等，AI 思考但没有真正调用工具）
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^(让我先查看|让我检查|我需要先|我先用|让我读取|我需要读取).*?[。\n]",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            return text.Trim();
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

                // ── 优先使用 ExploreAgent 智能发现相关文件 ──
                if (ExploreAgent != null && !string.IsNullOrWhiteSpace(userQuery))
                {
                    // ── 构建附加上下文：当前计划标题 + 已完成的步骤信息 ──
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

                    AddLog("INFO", $"[EditAgent] 委托 ExploreAgent 智能发现相关文件: \"{userQuery.Truncate(80)}\"");
                    relevantFiles = await ExploreAgent.DiscoverRelevantFilesAsync(
                        solutionPath, userQuery, maxFiles: 30,
                        additionalContext: additionalCtx);
                    AddLog("INFO", $"[EditAgent] ExploreAgent 返回 {relevantFiles.Count} 个相关文件");
                }
                else
                {
                    // ── 回退：使用 ExploreAgent 全量发现 ──
                    if (ExploreAgent != null)
                    {
                        relevantFiles = await ExploreAgent.DiscoverSolutionFilesAsync(
                            solutionPath, maxFiles: 50);
                    }
                    else
                    {
                        // ── 最终回退：简单的目录扫描 ──
                        relevantFiles = await FallbackFileScanAsync(solutionPath);
                    }
                }

                // ── 读取发现的文件内容 ──
                foreach (var file in relevantFiles)
                {
                    if (totalChars >= maxTotalChars) break;

                    try
                    {
                        string relativePath = GetRelativePath(solutionPath, file);
                        // RAG-SOURCE: file-read 项目文件内容（EditAgent 项目上下文收集）
                        string content = await Task.Run(() => File.ReadAllText(file));
                        // RAG-MARK: no-truncate — 不再截断项目文件内容，完整提供给 AI

                        sb.AppendLine($"### {relativePath}");
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

                AddLog("INFO", $"[EditAgent] 项目文件上下文: {relevantFiles.Count} 个文件, {totalChars} 字符");
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
                var dte = (EnvDTE.DTE?)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte?.Solution == null || !dte.Solution.IsOpen)
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

        /// <summary>
        /// 规范化文件路径（统一分隔符、去除尾部空格），用于 GroupBy 合并。
        /// </summary>
        private static string NormalizePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            return filePath.Replace('/', '\\').Trim().TrimEnd('\\');
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
                            linesAdded = 1; // 至少标记为有变更
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
                                FilePath = filePath,
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
        /// （如"没有错误"、"编译通过无错误"），只在实际有工具级失败标志时返回 true。
        /// 
        /// 检测策略：
        /// 1. 工具返回的 ❌ 失败前缀（最可靠的失败信号）
        /// 2. MSBuild 风格错误代码（error CS####、error C####、error LNK####）
        /// 3. 明确的失败摘要（"0 succeeded, 1 failed"、"Build FAILED"）
        /// 4. 中文"编译失败"/"构建失败"（排除"未编译失败"/"没有编译失败"等否定前置）
        /// </summary>
        private static bool HasBuildFailure(string verifyResult)
        {
            if (string.IsNullOrWhiteSpace(verifyResult))
                return false;

            // ── 策略1：工具级失败标志 ❌ ──
            // 所有内置工具在失败时都以 ❌ 开头，这是最可靠的信号
            // 但需要确认 ❌ 出现在 build/compile 上下文中
            if (verifyResult.Contains("❌ 构建失败") || verifyResult.Contains("❌ 编译失败")
                || verifyResult.Contains("❌ build") || verifyResult.Contains("❌ Build"))
            {
                return true;
            }

            // ── 策略2：MSBuild / 编译器错误代码 ──
            // error CS1234, error C2065, error LNK2001 等
            if (System.Text.RegularExpressions.Regex.IsMatch(verifyResult,
                @"\berror\s+(CS|C|LNK|MSB|BC|FS|TS|RUST)\d+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }

            // ── 策略3：MSBuild 摘要失败模式 ──
            // "0 succeeded, 1 failed" / "Build FAILED"（大写 FAILED 是 MSBuild 输出）
            if (verifyResult.Contains("Build FAILED")
                || System.Text.RegularExpressions.Regex.IsMatch(verifyResult,
                    @"\b0\s+succeeded.*\b[1-9]\d*\s+failed\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }

            // ── 策略4：编译/构建失败（排除否定表述）──
            // "编译失败" / "构建失败" 但不能是 "没有编译失败" / "未编译失败"
            if (System.Text.RegularExpressions.Regex.IsMatch(verifyResult,
                @"(?<!\u6ca1\u6709|\u672a|\u4e0d|\u65e0)(\u7f16\u8bd1\u5931\u8d25|\u6784\u5efa\u5931\u8d25)"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 检查执行日志中是否有编译警告或失败信号。
        /// 用于判断是否应建议 Handoff 到 Build Agent。
        /// </summary>
        private bool HasBuildWarningsInLogs()
        {
            foreach (var log in _logs)
            {
                if (log.Level == "WARN" || log.Level == "ERROR")
                {
                    string msg = log.Message ?? string.Empty;
                    if (msg.Contains("编译") || msg.Contains("构建") || msg.Contains("build")
                        || msg.Contains("Build") || msg.Contains("⚠️ 最终编译")
                        || msg.Contains("error CS") || msg.Contains("error C")
                        || msg.Contains("❌"))
                    {
                        return true;
                    }
                }
            }
            return false;
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
