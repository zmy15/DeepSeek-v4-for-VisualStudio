using DeepSeek_v4_for_VisualStudio.Models;
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

        /// <summary>
        /// ExploreAgent 引用，由 AgentDispatcher 注入。
        /// 用于在执行代码修改前智能发现相关文件。
        /// </summary>
        public ExploreAgent? ExploreAgent { get; set; }

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
            "replace_string_in_file",
            "multi_replace_string_in_file",
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
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Edit,
                Name = "Edit",
                Description = "执行代码修改。按计划逐步修改文件，支持构建验证和权限确认。",
                ArgumentHint = "描述要执行的代码修改任务",
                UserInvocable = true,
                AllowedTools = new List<string>(EditTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>(),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return
                "你是一个 Edit Agent——专精于按计划执行代码修改。\n\n" +
                "## 核心原则\n" +
                "- 你有权修改项目文件，但要谨慎、精确\n" +
                "- 每次修改后验证代码正确性（检查编译错误）\n" +
                "- 遵循项目中已有的编码规范和架构模式\n" +
                "- 优先使用项目已引入的框架和库\n\n" +
                "## 代码输出格式\n" +
                "修改文件时使用以下格式：\n" +
                "```file:完整/绝对/路径\n" +
                "// 修改后的完整文件内容\n" +
                "```\n\n" +
                "## 步骤执行\n" +
                "- 严格按照计划步骤顺序执行\n" +
                "- 每步完成报告进度\n" +
                "- 遇到错误不要静默跳过，报告并请求指导";
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
            }
            else
            {
                // ── 没有计划，作为单步代码修改执行 ──
                AddLog("INFO", "无计划，执行单步代码修改...");
                var plan = CreateSingleStepPlan(userMessage);
                await ExecutePlanAsync(plan, context);
                result.Plan = plan;
                result.FileChanges = plan.ChangedFiles;
                string aiSummary = await GenerateChangeSummaryAsync(plan, context.CancellationToken);
                result.Content = BuildSummaryMarkdown(plan, aiSummary);
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

                    AddLog("INFO", $"执行步骤 {step.Index}/{plan.Steps.Count}: {step.Title}");

                    try
                    {
                        await ExecuteStepAsync(step, plan, context);
                        step.Status = AgentStepStatus.Completed;
                        AddLog("INFO", $"✅ 步骤 {step.Index} 完成: {step.ResultSummary ?? "OK"}");
                    }
                    catch (OperationCanceledException)
                    {
                        step.Status = AgentStepStatus.Skipped;
                        AddLog("WARN", $"⏭ 步骤 {step.Index} 已取消");
                        plan.IsCancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        step.Status = AgentStepStatus.Failed;
                        step.ResultSummary = ex.Message;
                        AddLog("ERROR", $"❌ 步骤 {step.Index} 失败: {ex.Message}");
                    }

                    NotifyPlanUpdated();
                }

                plan.IsCompleted = plan.Steps.All(s =>
                    s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped);
            }
            finally
            {
                NotifyPlanUpdated();
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
                string buildResult = await ExecuteBuildStepAsync(step, context.SolutionPath, ct);
                step.AiResponse = buildResult;
                step.ResultSummary = buildResult.Truncate(100);
            }
            else if (isCodeStep)
            {
                await ExecuteCodeStepAsync(step, plan, context, stepPrompt, ct);
            }
            else
            {
                string result = await CallAiLongAsync(Definition.SystemPrompt, stepPrompt, ct, maxTokens: 4096);
                step.AiResponse = result;
                step.ResultSummary = result.Truncate(100);
            }
        }

        /// <summary>
        /// 执行代码编写步骤（支持重试 + 缺失文件智能发现）。
        /// 当 AI 表示缺少某些文件时，自动委托 ExploreAgent 搜索并补充上下文后重试。
        /// </summary>
        private async Task ExecuteCodeStepAsync(
            AgentStep step, AgentTaskPlan plan, AgentContext context,
            string stepPrompt, CancellationToken ct)
        {
            const int maxFormatRetries = 2;
            const int maxFileFetchRetries = 2;
            string result = string.Empty;
            List<FileChangeSummary> changes = new();

            // ── 收集项目文件上下文：委托 ExploreAgent 智能发现相关文件 ──
            string projectContext = await GatherProjectFilesContextAsync(
                context.SolutionPath, step.Description);
            string enrichedPrompt = stepPrompt;
            if (!string.IsNullOrEmpty(projectContext))
            {
                enrichedPrompt += "\n\n## 项目现有文件参考\n" + projectContext;
                AddLog("INFO", $"已读取项目文件上下文 ({projectContext.Split('\n').Length} 行)");
            }

            // ── 已尝试获取过的文件（防止重复获取）──
            var fetchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int retry = 0; retry <= maxFormatRetries; retry++)
            {
                if (ct.IsCancellationRequested) return;

                string prompt = retry == 0 ? enrichedPrompt
                    : enrichedPrompt + "\n\n⚠️ 上次输出格式有误。请严格按照以下格式输出每个要修改的文件：\n\n"
                        + "```file:完整路径\n完整文件内容\n```\n\n"
                        + "每个文件用独立的 ```file: 代码块。不要添加额外解释。";

                result = await CallAiLongAsync(Definition.SystemPrompt, prompt, ct, maxTokens: 8192);
                changes = ParseCodeChangesFromResult(result);

                if (changes.Count > 0) break;

                // ── AI 未产出有效代码块 → 检查是否表示缺少文件 ──
                if (retry >= maxFormatRetries) break; // 已达最大重试，不再尝试获取文件

                if (DetectMissingFilesInResponse(result))
                {
                    var requestedFiles = ExtractRequestedFileNames(result);
                    AddLog("INFO", $"AI 表示缺少文件，提取到 {requestedFiles.Count} 个文件引用: [{string.Join(", ", requestedFiles)}]");

                    // 过滤已获取过的文件
                    var newFiles = requestedFiles
                        .Where(f => !fetchedFiles.Contains(f))
                        .ToList();

                    if (newFiles.Count > 0 && ExploreAgent != null && !string.IsNullOrEmpty(context.SolutionPath))
                    {
                        // ── 委托 ExploreAgent 搜索缺失文件 ──
                        AddLog("INFO", $"[EditAgent] 委托 ExploreAgent 搜索缺失文件 ({newFiles.Count} 个)...");

                        var foundFiles = new List<string>();
                        foreach (var fileName in newFiles)
                        {
                            var discovered = await ExploreAgent.DiscoverRelevantFilesAsync(
                                context.SolutionPath, fileName, maxFiles: 5);
                            foundFiles.AddRange(discovered);
                            foreach (var f in discovered)
                                fetchedFiles.Add(f);
                        }

                        // 去重
                        foundFiles = foundFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                        if (foundFiles.Count > 0)
                        {
                            // ── 读取找到的文件内容，补充到 prompt ──
                            var fileContextSb = new StringBuilder();
                            fileContextSb.AppendLine("\n\n## 🔍 补充文件（根据 AI 请求发现）\n");
                            int totalChars = 0;
                            const int maxSupplementChars = 40000;

                            foreach (var file in foundFiles)
                            {
                                if (totalChars >= maxSupplementChars) break;
                                try
                                {
                                    string relativePath = GetRelativePath(context.SolutionPath!, file);
                                    string content = await Task.Run(() => File.ReadAllText(file), ct);
                                    int remaining = maxSupplementChars - totalChars;
                                    if (content.Length > remaining)
                                        content = content.Substring(0, remaining) + "\n// ... (已截断)";

                                    fileContextSb.AppendLine($"### {relativePath}");
                                    fileContextSb.AppendLine("```");
                                    fileContextSb.AppendLine(content);
                                    fileContextSb.AppendLine("```\n");
                                    totalChars += content.Length + relativePath.Length + 20;
                                }
                                catch { /* 跳过不可读文件 */ }
                            }

                            enrichedPrompt += fileContextSb.ToString();
                            AddLog("INFO", $"[EditAgent] 已补充 {foundFiles.Count} 个文件到上下文 ({totalChars} 字符)，重新请求 AI...");

                            // ── 重置重试计数，用新上下文重新开始 ──
                            retry = -1; // 循环末尾 +1 后变为 0
                            continue;
                        }
                        else
                        {
                            AddLog("WARN", $"[EditAgent] ExploreAgent 未找到请求的文件: [{string.Join(", ", newFiles)}]");
                        }
                    }
                    else
                    {
                        // ExploreAgent 不可用或没有新文件
                        foreach (var f in requestedFiles)
                            fetchedFiles.Add(f);
                    }
                }

                if (retry < maxFormatRetries)
                    AddLog("WARN", $"AI 输出格式不正确（未检测到 ```file: 代码块），第 {retry + 1} 次重试...");
                else
                    AddLog("WARN", "AI 多次重试后仍未输出有效代码块，将原样记录结果");
            }

            step.AiResponse = result;
            changes = ParseCodeChangesFromResult(result);

            // ── 保存原始文件内容（用于最终 diff 比较）──
            var originalContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ── 全局抑制 diff 预览，流程结束时统一显示一次 ──
            TerminalWindowHelper.SuppressDiffPreview = true;

            // ── 应用代码变更 ──
            foreach (var change in changes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string resolvedPath = ResolveFilePath(change.FilePath, context.SolutionPath);
                    change.FilePath = resolvedPath;

                    // 保存原始内容（用于最终 diff 和回退）
                    if (!originalContents.ContainsKey(resolvedPath))
                    {
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

                    // ── 新文件：先建空文件 → 加入项目 → 再写内容 ──
                    // 这样 WriteCodeToFileAsync 的 VS SDK 路径才能正常工作
                    bool isNewFile = !File.Exists(resolvedPath);
                    if (isNewFile)
                    {
                        // 1. 创建空文件
                        string? dir = Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        await Task.Run(() => File.WriteAllText(resolvedPath, string.Empty, System.Text.Encoding.UTF8), ct);
                        AddLog("INFO", $"📄 预创建文件并加入项目: {Path.GetFileName(resolvedPath)}");

                        // 2. 加入 VS 解决方案/项目
                        await AddFileToProjectAsync(resolvedPath, ct);
                    }

                    // 3. 写入完整内容（VS SDK 路径现在可用，因为文件已在项目中）
                    string? error = await TerminalWindowHelper.WriteCodeToFileAsync(
                        resolvedPath, change.NewContent ?? string.Empty);

                    if (error == null)
                    {
                        AddLog("INFO", $"✅ 已写入: {resolvedPath} (+{change.LinesAdded} -{change.LinesRemoved})");
                        plan.ChangedFiles.Add(change);
                    }
                    else
                    {
                        AddLog("ERROR", $"写入文件失败: {resolvedPath} - {error}");
                    }
                }
                catch (Exception ex)
                {
                    AddLog("ERROR", $"写入文件失败: {change.FilePath} - {ex.Message}");
                }
            }

            step.ResultSummary = changes.Count > 0
                ? $"修改 {changes.Count} 个文件 (+{changes.Sum(c => c.LinesAdded)} -{changes.Sum(c => c.LinesRemoved)})"
                : "未检测到文件变更";

            // ── 编译验证 + 多轮修复 ──
            if (changes.Count > 0 && !ct.IsCancellationRequested)
            {
                await BuildAndFixLoopAsync(step, plan, context, changes, ct);
            }

            // ── 恢复 diff 预览，统一显示一次最终 diff ──
            TerminalWindowHelper.SuppressDiffPreview = false;

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
        /// </summary>
        private async Task<string> ExecuteBuildStepAsync(AgentStep step, string? solutionPath, CancellationToken ct)
        {
            AddLog("INFO", $"开始构建步骤: {step.Title}");

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var dte = (EnvDTE.DTE?)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    Logger.Warn("[EditAgent] 无法获取 DTE 对象，跳过构建");
                    return "⚠️ 无法获取 Visual Studio DTE 对象，跳过构建。请在 VS 中手动构建。";
                }

                var sb = dte.Solution?.SolutionBuild;
                if (sb == null || dte.Solution == null || !dte.Solution.IsOpen)
                {
                    Logger.Warn("[EditAgent] 解决方案未打开，跳过构建");
                    return "⚠️ 解决方案未打开，跳过构建。";
                }

                Logger.Info("[EditAgent] 正在构建解决方案…");
                var firstProject = dte.Solution.Projects.Cast<EnvDTE.Project>().FirstOrDefault();
                sb.BuildProject(sb.ActiveConfiguration?.Name ?? "Debug",
                    firstProject?.UniqueName ?? "", WaitForBuildToFinish: true);

                int errors = sb.LastBuildInfo;

                // ── 收集编译错误详情 ──
                string errorDetails = CollectBuildErrors(dte);

                if (errors == 0)
                {
                    Logger.Info("[EditAgent] ✅ 构建成功，0 个错误");
                    return "✅ 构建成功，0 个错误";
                }

                var result = new StringBuilder();
                result.AppendLine($"⚠️ 构建完成，{errors} 个错误");
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
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 构建异常: {ex.Message}");
                return $"⚠️ 构建失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 从 VS Error List 和 Output 窗口收集编译错误详情。
        /// 按文件分组，每个错误包含文件名、行号、错误描述。
        /// </summary>
        private static string CollectBuildErrors(EnvDTE.DTE dte)
        {
            var sb = new StringBuilder();

            // ToolWindows 需要 DTE2 接口（EnvDTE80）
            var dte2 = dte as EnvDTE80.DTE2;
            if (dte2 == null) return sb.ToString();

            try
            {
                // ── 方案一：Error List 窗口 ──
                var errorList = dte2.ToolWindows.ErrorList;
                if (errorList != null)
                {
                    var errorItems = errorList.ErrorItems;
                    if (errorItems != null && errorItems.Count > 0)
                    {
                        // 按文件分组
                        var errorsByFile = new Dictionary<string, List<string>>(
                            StringComparer.OrdinalIgnoreCase);

                        for (int i = 1; i <= errorItems.Count; i++)
                        {
                            try
                            {
                                var item = errorItems.Item(i);
                                // 只收集错误（跳过警告）
                                if (item.ErrorLevel != EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh)
                                    continue;

                                string fullPath = item.FileName ?? "(未知文件)";
                                // 作为 heading 使用完整路径，以便后续能定位并读取文件内容
                                // 如果 AI 修改了 a.cpp 但错误指向 b.h，AI 需要能看到 b.h 的内容来修复
                                string headingKey = !string.IsNullOrEmpty(fullPath) && fullPath != "(未知文件)"
                                    ? fullPath
                                    : "(未知文件)";

                                string desc = $"- **行 {item.Line}**: {item.Description}";

                                if (!errorsByFile.ContainsKey(headingKey))
                                    errorsByFile[headingKey] = new List<string>();
                                errorsByFile[headingKey].Add(desc);
                            }
                            catch
                            {
                                // 跳过无法读取的错误项
                            }
                        }

                        if (errorsByFile.Count > 0)
                        {
                            foreach (var kvp in errorsByFile)
                            {
                                sb.AppendLine($"### {kvp.Key}");
                                foreach (var desc in kvp.Value)
                                    sb.AppendLine(desc);
                                sb.AppendLine();
                            }

                            Logger.Info($"[BuildErrors] 从 Error List 收集到 {errorsByFile.Sum(k => k.Value.Count)} 个错误，"
                                + $"涉及 {errorsByFile.Count} 个文件");
                            return sb.ToString();
                        }
                    }
                }

                // ── 方案二：Output 窗口（回退） ──
                try
                {
                    var outputWindow = dte2.ToolWindows.OutputWindow;
                    var buildPane = outputWindow?.OutputWindowPanes
                        ?.Cast<EnvDTE.OutputWindowPane>()
                        ?.FirstOrDefault(p => p.Name.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
                                           || p.Name.IndexOf("生成", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (buildPane != null)
                    {
                        var textDoc = buildPane.TextDocument;
                        if (textDoc != null)
                        {
                            var sel = textDoc.Selection;
                            sel.SelectAll();
                            string buildOutput = sel.Text ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(buildOutput))
                            {
                                // 提取错误行（MSBuild 格式：file(line): error CODE: message）
                                var errorLines = buildOutput
                                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)
                                                && !line.Contains("0 Error", StringComparison.OrdinalIgnoreCase)
                                                && !line.Contains("0 错误", StringComparison.OrdinalIgnoreCase))
                                    .Take(30) // 最多取30行
                                    .Select(line => line.Trim())
                                    .ToList();

                                if (errorLines.Count > 0)
                                {
                                    sb.AppendLine("### 构建输出 (Output Window)");
                                    foreach (var line in errorLines)
                                        sb.AppendLine($"- {line}");
                                    sb.AppendLine();

                                    Logger.Info($"[BuildErrors] 从 Output 窗口收集到 {errorLines.Count} 行错误信息");
                                    return sb.ToString();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[BuildErrors] Output 窗口读取失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuildErrors] Error List 收集失败: {ex.Message}");
            }

            return sb.ToString();
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

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"解决方案路径: {context.SolutionPath}");
                sb.AppendLine();
            }

            // ── 用户附加的文件上下文 ──
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine("## 用户上传的文件内容");
                sb.AppendLine(context.FileContext.Length > 8000
                    ? context.FileContext.Substring(0, 8000) + "\n... (文件内容已截断)"
                    : context.FileContext);
                sb.AppendLine();
            }

            if (isCodeStep)
            {
                sb.AppendLine("请执行此步骤。修改文件时使用以下格式：");
                sb.AppendLine();
                sb.AppendLine("```file:完整/绝对/路径");
                sb.AppendLine("// 修改后的完整文件内容（不是 diff）");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("重要规则：");
                sb.AppendLine("1. 不要输出额外解释，只输出代码变更");
                sb.AppendLine("2. 每个文件用独立的 ```file: 代码块");
                sb.AppendLine("3. 代码块中是修改后的完整文件内容");
                sb.AppendLine("4. 新建文件也使用相同格式");
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

        private static AgentTaskPlan CreateSingleStepPlan(string userMessage)
        {
            return new AgentTaskPlan
            {
                Intent = AgentIntent.CodeChange,
                Title = "执行代码变更",
                Steps = new List<AgentStep>
                {
                    new AgentStep
                    {
                        Index = 1,
                        Title = "分析并修改代码",
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
            AddLog("WARN", "任务已取消");
        }

        private void NotifyPlanUpdated()
        {
            try { PlanUpdated?.Invoke(CurrentPlan!); } catch { }
        }

        private static string BuildSummaryMarkdown(AgentTaskPlan plan, string? aiSummary = null)
        {
            if (plan.IsCancelled)
                return "⚠️ **任务已取消**";

            var sb = new StringBuilder();
            sb.AppendLine("## ✅ 代码变更完成");
            sb.AppendLine();
            sb.AppendLine($"**任务**: {plan.Title}");
            sb.AppendLine($"**修改文件数**: {plan.ChangedFiles.Count}");
            sb.AppendLine();

            // ── AI 生成的文字总结 ──
            if (!string.IsNullOrWhiteSpace(aiSummary))
            {
                sb.AppendLine("### 📝 变更摘要");
                sb.AppendLine();
                sb.AppendLine(aiSummary);
                sb.AppendLine();
            }

            if (plan.ChangedFiles.Count > 0)
            {
                sb.AppendLine("### 📊 文件变更统计");
                sb.AppendLine();
                sb.AppendLine("| 文件 | 变更 |");
                sb.AppendLine("|------|------|");
                foreach (var change in plan.ChangedFiles)
                {
                    string delta = $"{(change.LinesAdded > 0 ? $"+{change.LinesAdded}" : "")}"
                        + $"{(change.LinesRemoved > 0 ? $" -{change.LinesRemoved}" : "")}";
                    sb.AppendLine($"| `{change.FilePath}` | {delta} |");
                }
                sb.AppendLine();
                sb.AppendLine($"总变更: +{plan.ChangedFiles.Sum(c => c.LinesAdded)}"
                    + $" -{plan.ChangedFiles.Sum(c => c.LinesRemoved)} 行");
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
                var summaryPrompt = new StringBuilder();
                summaryPrompt.AppendLine("你是一个代码审查助手。请用5-10句话总结以下代码变更。");
                summaryPrompt.AppendLine("内容包括：改了什么、为什么改、改法思路、影响范围、新增/修改/删除的文件。");
                summaryPrompt.AppendLine("不要评价代码质量，只做客观描述。");
                summaryPrompt.AppendLine();
                summaryPrompt.AppendLine($"## 任务: {plan.Title}");
                summaryPrompt.AppendLine($"共 {plan.Steps.Count} 步，成功 {plan.Steps.Count(s => s.Status == AgentStepStatus.Completed)} 步");
                summaryPrompt.AppendLine();

                // ── 合并相同文件 ──
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Path = g.Key, Added = g.Sum(c => c.LinesAdded), Removed = g.Sum(c => c.LinesRemoved), Names = g.Select(c => Path.GetFileName(c.FilePath)).First() })
                    .ToList();
                int totalAdded = mergedFiles.Sum(f => f.Added);
                int totalRemoved = mergedFiles.Sum(f => f.Removed);

                summaryPrompt.AppendLine($"## 变更统计: +{totalAdded} -{totalRemoved} 行，{mergedFiles.Count} 个文件");
                summaryPrompt.AppendLine();
                summaryPrompt.AppendLine("## 修改的文件");
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
                        ? step.ResultSummary.Truncate(200)
                        : "(无)";
                    summaryPrompt.AppendLine($"- {status} {step.Title}: {summary}");
                }
                summaryPrompt.AppendLine();
                summaryPrompt.AppendLine("请用中文输出变更摘要（5-10句话，包含改了什么、为什么改、改法思路）：");

                string result = await CallAiShortAsync(
                    "你只输出中文代码变更摘要，不输出任何其他内容。",
                    summaryPrompt.ToString(), ct, maxTokens: 400);

                return result?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 生成变更摘要失败: {ex.Message}");
                return string.Empty;
            }
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
                        additionalCtx = $"当前任务: {CurrentPlan.Title}";
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
                        string content = await Task.Run(() => File.ReadAllText(file));
                        int remainingChars = maxTotalChars - totalChars;
                        if (content.Length > remainingChars)
                            content = content.Substring(0, remainingChars)
                                + "\n// ... (文件过长，已截断)";

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
        /// 构建验证 + 多轮修复循环。
        /// 代码写入后自动编译，如果有错误，尝试让 AI 修复并重新编译，
        /// 最多尝试 3 轮，直到构建成功或判定为无法修复。
        /// </summary>
        private async Task BuildAndFixLoopAsync(
            AgentStep step,
            AgentTaskPlan plan,
            AgentContext context,
            List<FileChangeSummary> appliedChanges,
            CancellationToken ct)
        {
            const int maxBuildFixRounds = 3;

            AddLog("INFO", "🔨 开始编译验证...");

            for (int round = 1; round <= maxBuildFixRounds; round++)
            {
                if (ct.IsCancellationRequested) return;

                // ── 执行构建 ──
                string buildResult = await ExecuteBuildStepAsync(
                    new AgentStep { Title = "编译验证" }, context.SolutionPath, ct);

                // 只记录一行摘要到 UI 日志（错误详情已在 ExecuteBuildStepAsync 内部记录）
                string oneLineSummary = buildResult.Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? buildResult;
                AddLog("INFO", $"[编译 第{round}轮] {oneLineSummary}");

                // 构建成功 → 结束
                if (buildResult.Contains("✅") || buildResult.Contains("0 个错误"))
                {
                    AddLog("INFO", "✅ 编译通过！");
                    return;
                }

                // 已达到最大修复轮数
                if (round >= maxBuildFixRounds)
                {
                    AddLog("WARN", $"⚠️ 已尝试 {maxBuildFixRounds} 轮修复，仍存在编译错误。请手动检查。");
                    return;
                }

                // ── AI 修复编译错误 ──
                AddLog("INFO", $"🔧 第 {round} 轮修复：让 AI 分析并修复编译错误...");

                var fixPrompt = new StringBuilder();
                fixPrompt.AppendLine("## 编译错误修复");
                fixPrompt.AppendLine();
                fixPrompt.AppendLine("以下代码修改后编译失败，请分析错误并修复。");
                fixPrompt.AppendLine();
                fixPrompt.AppendLine($"构建结果: {buildResult}");
                fixPrompt.AppendLine();
                fixPrompt.AppendLine("## 已修改的文件及其当前内容");
                foreach (var change in appliedChanges)
                {
                    fixPrompt.AppendLine($"### `{change.FilePath}`");
                    fixPrompt.AppendLine();
                    AppendFileContent(fixPrompt, change.FilePath);
                    fixPrompt.AppendLine();
                }

                // ── 错误可能指向非修改文件（如 AI 改了 a.cpp 但编译错误在 b.h） ──
                // 解析 buildResult 中的文件路径，读取这些"牵连文件"的内容
                AppendErrorReferencedFiles(fixPrompt, buildResult, appliedChanges, context.SolutionPath);

                fixPrompt.AppendLine("## 要求");
                fixPrompt.AppendLine("1. 仔细阅读上面的编译错误详情，理解每个错误的根本原因");
                fixPrompt.AppendLine("2. 使用 ```file: 格式输出修复后的完整文件内容");
                fixPrompt.AppendLine("3. 只修改有编译错误的文件，不要修改其他文件");
                fixPrompt.AppendLine("4. 确保修复后代码语法正确，类型匹配，引用完整，无未定义标识符");
                fixPrompt.AppendLine("5. 如果涉及头文件缺失或命名空间问题，请添加正确的 #include 或 using 声明");

                string fixResult = await CallAiLongAsync(
                    Definition.SystemPrompt, fixPrompt.ToString(), ct, maxTokens: 8192);

                var fixChanges = ParseCodeChangesFromResult(fixResult);

                if (fixChanges.Count == 0)
                {
                    AddLog("WARN", "AI 未产出有效的修复代码，跳过本轮修复");
                    continue;
                }

                // ── 应用修复 ──
                foreach (var fixChange in fixChanges)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        string resolvedPath = ResolveFilePath(fixChange.FilePath, context.SolutionPath);
                        string? error = await TerminalWindowHelper.WriteCodeToFileAsync(
                            resolvedPath, fixChange.NewContent ?? string.Empty);

                        if (error == null)
                        {
                            AddLog("INFO", $"🔧 修复写入: {resolvedPath}");
                            // 更新 appliedChanges 中的路径记录
                            if (!appliedChanges.Any(c =>
                                string.Equals(c.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase)))
                            {
                                appliedChanges.Add(fixChange);
                            }
                            plan.ChangedFiles.Add(fixChange);
                        }
                        else
                        {
                            AddLog("ERROR", $"修复写入失败: {resolvedPath} - {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog("ERROR", $"修复写入异常: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 将文件内容以带语法高亮的代码块形式附加到 prompt。
        /// </summary>
        private static void AppendFileContent(StringBuilder sb, string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string fileContent = File.ReadAllText(filePath);
                    string lang = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
                    sb.AppendLine($"```{lang}");
                    sb.AppendLine(fileContent);
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine("_(文件不存在)_");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"_无法读取文件: {ex.Message}_");
            }
        }

        /// <summary>
        /// 从构建结果中提取错误涉及的文件路径，读取这些"牵连文件"的内容并附加到 prompt。
        /// 
        /// 场景：AI 修改了 a.cpp，但编译错误指向 b.h（被 a.cpp 包含的头文件）。
        /// 如果不提供 b.h 的内容，AI 无法有效修复错误。
        /// </summary>
        private static void AppendErrorReferencedFiles(
            StringBuilder fixPrompt,
            string buildResult,
            List<FileChangeSummary> appliedChanges,
            string? solutionPath)
        {
            if (string.IsNullOrEmpty(buildResult)) return;

            // ── 从 markdown 标题 `### F:\path\to\file` 和 `### `filepath`` 中提取文件路径 ──
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 匹配 `### C:\...` 或 `### /path/...` 格式（绝对路径）
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(buildResult,
                    @"###\s+(?:`?)([A-Za-z]:[^\r\n`]+)"))
            {
                string path = m.Groups[1].Value.Trim();
                if (path.Length > 3 && path.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    referencedPaths.Add(path);
            }

            // 从 Output Window 回退格式中匹配路径：`- file(line): error ...`
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(buildResult,
                    @"-\s+([A-Za-z]:[^(]+\.[a-zA-Z]+)\s*\("))
            {
                string path = m.Groups[1].Value.Trim();
                if (File.Exists(path))
                    referencedPaths.Add(path);
            }

            if (referencedPaths.Count == 0) return;

            // ── 过滤：只保留不在 appliedChanges 中的文件 ──
            var alreadyCovered = new HashSet<string>(
                appliedChanges.Select(c => c.FilePath),
                StringComparer.OrdinalIgnoreCase);

            var missingPaths = referencedPaths
                .Where(p => !alreadyCovered.Contains(p))
                .Take(5) // 最多额外读取 5 个牵连文件
                .ToList();

            if (missingPaths.Count == 0) return;

            fixPrompt.AppendLine("## 编译错误涉及的其他文件（非本轮修改，但错误指向它们）");
            fixPrompt.AppendLine();

            foreach (var path in missingPaths)
            {
                fixPrompt.AppendLine($"### `{path}`");
                fixPrompt.AppendLine();
                AppendFileContent(fixPrompt, path);
                fixPrompt.AppendLine();
            }

            // ── 也加入 appliedChanges 追踪，防止后续重复读取 ──
            foreach (var path in missingPaths)
            {
                appliedChanges.Add(new FileChangeSummary
                {
                    FilePath = path,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                });
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
