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
                result.Content = BuildSummaryMarkdown(context.ActivePlan);
            }
            else
            {
                // ── 没有计划，作为单步代码修改执行 ──
                AddLog("INFO", "无计划，执行单步代码修改...");
                var plan = CreateSingleStepPlan(userMessage);
                await ExecutePlanAsync(plan, context);
                result.Plan = plan;
                result.FileChanges = plan.ChangedFiles;
                result.Content = BuildSummaryMarkdown(plan);
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
        /// 执行代码编写步骤（支持重试）。
        /// </summary>
        private async Task ExecuteCodeStepAsync(
            AgentStep step, AgentTaskPlan plan, AgentContext context,
            string stepPrompt, CancellationToken ct)
        {
            const int maxRetries = 2;
            string result = string.Empty;
            List<FileChangeSummary> changes = new();

            // ── 收集项目文件上下文（帮助 AI 理解现有代码架构和风格）──
            string projectContext = await GatherProjectFilesContextAsync(context.SolutionPath);
            string enrichedPrompt = stepPrompt;
            if (!string.IsNullOrEmpty(projectContext))
            {
                enrichedPrompt += "\n\n## 项目现有文件参考\n" + projectContext;
                AddLog("INFO", $"已读取项目文件上下文 ({projectContext.Split('\n').Length} 行)");
            }

            for (int retry = 0; retry <= maxRetries; retry++)
            {
                if (ct.IsCancellationRequested) return;

                string prompt = retry == 0 ? enrichedPrompt
                    : enrichedPrompt + "\n\n⚠️ 上次输出格式有误。请严格按照以下格式输出每个要修改的文件：\n\n"
                        + "```file:完整路径\n完整文件内容\n```\n\n"
                        + "每个文件用独立的 ```file: 代码块。不要添加额外解释。";

                result = await CallAiLongAsync(Definition.SystemPrompt, prompt, ct, maxTokens: 8192);
                changes = ParseCodeChangesFromResult(result);

                if (changes.Count > 0) break;

                if (retry < maxRetries)
                    AddLog("WARN", $"AI 输出格式不正确（未检测到 ```file: 代码块），第 {retry + 1} 次重试...");
                else
                    AddLog("WARN", "AI 多次重试后仍未输出有效代码块，将原样记录结果");
            }

            step.AiResponse = result;
            changes = ParseCodeChangesFromResult(result);

            // ── 应用代码变更 ──
            foreach (var change in changes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string resolvedPath = ResolveFilePath(change.FilePath, context.SolutionPath);
                    change.FilePath = resolvedPath;

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

                        // 已有文件：确认已在项目中（新文件已在上方加入）
                        if (!isNewFile)
                            await AddFileToProjectAsync(resolvedPath, ct);
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
                string result = errors == 0
                    ? "✅ 构建成功，0 个错误"
                    : $"⚠️ 构建完成，{errors} 个错误";

                Logger.Info($"[EditAgent] {result}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 构建异常: {ex.Message}");
                return $"⚠️ 构建失败: {ex.Message}";
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

        private static string BuildSummaryMarkdown(AgentTaskPlan plan)
        {
            if (plan.IsCancelled)
                return "⚠️ **任务已取消**";

            var sb = new StringBuilder();
            sb.AppendLine("## ✅ 代码变更完成");
            sb.AppendLine();
            sb.AppendLine($"**任务**: {plan.Title}");
            sb.AppendLine($"**修改文件数**: {plan.ChangedFiles.Count}");
            sb.AppendLine();

            if (plan.ChangedFiles.Count > 0)
            {
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

        #endregion

        #region Project Integration Helpers

        /// <summary>
        /// 收集项目文件上下文 — 读取解决方案下的所有源代码文件内容，
        /// 为 AI 提供完整的项目结构和代码风格参考。
        /// 限制总大小防止超出 token 限制。
        /// </summary>
        private static async Task<string> GatherProjectFilesContextAsync(string? solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || !Directory.Exists(solutionPath))
                return string.Empty;

            const int maxTotalChars = 60000; // 总字符数上限，防止超出 token 限制
            var sb = new StringBuilder();
            int totalChars = 0;

            try
            {
                // 常见源代码文件扩展名
                var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".cs", ".vb", ".cpp", ".h", ".hpp", ".c", ".cc", ".cxx",
                    ".xaml", ".xml", ".config", ".csproj", ".vbproj", ".vcxproj",
                    ".json", ".ts", ".js", ".py", ".java", ".fs", ".fsx",
                    ".sln", ".targets", ".props", ".md", ".txt",
                };

                // 排除目录
                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "bin", "obj", ".git", ".vs", "node_modules", "packages",
                    "Debug", "Release", ".vscode", ".idea",
                };

                var files = await Task.Run(() =>
                    Directory.GetFiles(solutionPath, "*.*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            string dir = Path.GetDirectoryName(f) ?? "";
                            string ext = Path.GetExtension(f);
                            // 跳过排除目录
                            foreach (var excludeDir in excludeDirs)
                                if (dir.IndexOf(excludeDir, StringComparison.OrdinalIgnoreCase) >= 0)
                                    return false;
                            return codeExtensions.Contains(ext);
                        })
                        .Take(80) // 最多读取80个文件
                        .ToList());

                foreach (var file in files)
                {
                    if (totalChars >= maxTotalChars) break;

                    try
                    {
                        string relativePath = GetRelativePath(solutionPath, file);
                        string content = await Task.Run(() => File.ReadAllText(file));
                        int remainingChars = maxTotalChars - totalChars;
                        if (content.Length > remainingChars)
                            content = content.Substring(0, remainingChars) + "\n// ... (文件过长，已截断)";

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

                Logger.Info($"[EditAgent] 项目文件上下文: {files.Count} 个文件, {totalChars} 字符");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 收集项目文件上下文失败: {ex.Message}");
            }

            return sb.ToString();
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

                AddLog("INFO", $"[编译 第{round}轮] {buildResult}");

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
                fixPrompt.AppendLine("## 已修改的文件");
                foreach (var change in appliedChanges)
                {
                    fixPrompt.AppendLine($"- `{change.FilePath}`");
                }
                fixPrompt.AppendLine();
                fixPrompt.AppendLine("## 要求");
                fixPrompt.AppendLine("1. 分析编译错误的原因");
                fixPrompt.AppendLine("2. 使用 ```file: 格式输出修复后的完整文件内容");
                fixPrompt.AppendLine("3. 只修改有问题的文件，不要修改其他文件");
                fixPrompt.AppendLine("4. 确保修复后代码语法正确，类型匹配，引用完整");

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
