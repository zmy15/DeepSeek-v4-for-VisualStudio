using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Coding Agent 编排服务。
    /// 
    /// 职责：
    /// 1. 意图分析 — 判断用户请求是需要改代码还是普通问答
    /// 2. 任务分解 — 将代码变更请求拆分为有序执行步骤
    /// 3. 步骤执行 — 按序执行每个步骤，追踪进度
    /// 4. 权限管控 — 执行命令/写文件前请求用户确认
    /// 5. 结果汇总 — 收集变更摘要，仅输出最终结果
    /// </summary>
    public class CodingAgentService : IDisposable
    {
        private readonly DeepSeekApiService _apiService;
        private readonly List<AgentLogEntry> _logs = new();
        private CancellationTokenSource? _agentCts;

        /// <summary>当前正在执行的任务计划</summary>
        public AgentTaskPlan? CurrentPlan { get; set; }

        /// <summary>当前待确认的权限请求</summary>
        public AgentPermissionRequest? PendingPermission { get; private set; }

        /// <summary>Agent 步骤状态变更事件（UI 订阅）</summary>
        public event Action<AgentTaskPlan>? PlanUpdated;

        /// <summary>权限请求事件（UI 订阅以显示确认按钮）</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

        /// <summary>Agent 日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        public CodingAgentService(DeepSeekApiService apiService)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        }

        #region Public API

        /// <summary>
        /// 分析用户意图：判断是 CodeChange 还是 QandA。
        /// 优先使用 AI 分类，失败时回退到启发式规则。
        /// </summary>
        public async Task<AgentIntent> AnalyzeIntentAsync(string userMessage, CancellationToken ct = default)
        {
            AddLog("INFO", $"开始意图分析 ({userMessage.Length} 字符)...");

            // ── 使用 AI 精确分类 ──
            try
            {
                string prompt =
                    "你是一个意图分类器，工作在 Visual Studio 编程助手中。\n" +
                    "你的任务是判断用户消息的意图是「修改代码」还是「普通问答」。\n\n" +
                    "## 判断标准\n" +
                    "- **CodeChange**: 用户希望修改、创建、删除、重构项目中的代码文件。\n" +
                    "  典型表达：添加功能、修复bug、写一个类/方法、改代码、生成测试、\n" +
                    "  重构、优化性能、添加注释/文档、修改配置等。\n" +
                    "- **QandA**: 用户只是询问技术问题、寻求解释、讨论方案，不涉及实际文件修改。\n" +
                    "  典型表达：什么是X、为什么Y、如何理解Z、对比A和B、请教问题等。\n\n" +
                    "## 输出要求\n" +
                    "只输出一个词：CodeChange 或 QandA。不要输出任何其他内容。\n\n" +
                    "## 用户消息\n" +
                    userMessage + "\n\n" +
                    "意图:";

                var classification = await CallAiShortAsync(prompt, ct);
                bool isCodeChange = classification.Contains("CodeChange", StringComparison.OrdinalIgnoreCase)
                    && !classification.Contains("QandA", StringComparison.OrdinalIgnoreCase);

                AddLog("INFO", $"意图分析: AI 判定 → {(isCodeChange ? "CodeChange" : "QandA")}");
                return isCodeChange ? AgentIntent.CodeChange : AgentIntent.QandA;
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"意图分析: AI 调用失败 ({ex.Message})，回退到启发式");

                // ── 启发式回退（仅当 AI 不可用时） ──
                var codeKeywords = new[]
                {
                    "修改", "修复", "fix", "改", "添加功能", "实现", "implement",
                    "重构", "refactor", "优化", "optimize", "bug", "错误", "报错",
                    "写一个", "创建一个", "增加", "添加", "删除", "更新代码", "改代码",
                    "帮我写", "帮忙写", "coding", "代码", "函数", "function",
                    "class", "类", "接口", "interface", "方法", "method",
                    "出错了", "不工作", "doesn't work", "报异常", "exception",
                    "崩溃", "crash", "改一下", "修改一下", "完善", "改进",
                    "测试", "测试用例", "单元测试", "生成", "编写", "用例",
                };

                bool hasCodeKeyword = codeKeywords.Any(k =>
                    userMessage.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!hasCodeKeyword)
                {
                    AddLog("INFO", "意图分析: 启发式 → 无代码关键词，问答模式");
                    return AgentIntent.QandA;
                }

                AddLog("INFO", "意图分析: 启发式 → CodeChange");
                return AgentIntent.CodeChange;
            }
        }

        /// <summary>
        /// 将代码变更任务分解为执行步骤。
        /// </summary>
        public async Task<AgentTaskPlan> DecomposeTaskAsync(
            string userMessage, string? fileContext = null, CancellationToken ct = default)
        {
            AddLog("INFO", "开始任务分解...");

            string prompt = "你是一个 Coding Agent 任务规划器。将以下用户请求分解为有序的执行步骤。\n" +
                "每个步骤应该是一个具体的、可执行的操作。步骤数量控制在 2-6 个。\n\n" +
                "输出格式（严格 JSON）:\n" +
                "{\n" +
                "  \"title\": \"任务标题（简短）\",\n" +
                "  \"steps\": [\n" +
                "    {\n" +
                "      \"index\": 1,\n" +
                "      \"title\": \"步骤标题（简短）\",\n" +
                "      \"description\": \"步骤详细描述\",\n" +
                "      \"requiresApproval\": false\n" +
                "    }\n" +
                "  ]\n" +
                "}\n\n" +
                "用户请求:\n" +
                userMessage + "\n" +
                (string.IsNullOrEmpty(fileContext) ? "" : "\n相关文件上下文:\n" + fileContext + "\n") +
                "\n请输出 JSON:";

            try
            {
                string json = await CallAiShortAsync(prompt, ct, maxTokens: 1024);

                // 提取 JSON（可能被 markdown 包裹）
                json = ExtractJsonFromMarkdown(json);

                var plan = JsonSerializer.Deserialize<AgentTaskPlan>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                if (plan != null && plan.Steps.Count > 0)
                {
                    plan.Intent = AgentIntent.CodeChange;
                    AddLog("INFO", $"任务分解完成: {plan.Steps.Count} 个步骤 → {plan.Title}");
                    return plan;
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"任务分解 JSON 解析失败: {ex.Message}，使用默认单步计划");
            }

            // 回退：单步计划
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
        /// 执行任务计划中的所有步骤。
        /// </summary>
        public async Task ExecutePlanAsync(
            AgentTaskPlan plan,
            string? solutionPath = null,
            Func<string, Task<string?>>? readFileAsync = null,
            CancellationToken ct = default)
        {
            CurrentPlan = plan;
            _agentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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
                        await ExecuteStepAsync(step, plan, solutionPath, readFileAsync, _agentCts.Token);
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
                        // 继续执行后续步骤（某些步骤失败不阻塞整体）
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
        /// 请求用户许可执行某个命令。
        /// 返回 true 表示用户同意。
        /// </summary>
        public async Task<bool> RequestPermissionAsync(string title, string command, string actionType = "command")
        {
            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = actionType,
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"等待用户许可: {title}");

            // 等待用户响应（无超时，直到用户点击允许或拒绝）
            bool approved = await request.ResponseTcs.Task;

            PendingPermission = null;
            AddLog("INFO", $"权限请求结果: {(approved ? "✅ 允许" : "❌ 拒绝")} → {title}");
            return approved;
        }

        /// <summary>
        /// 用户对权限请求的响应。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            if (PendingPermission?.RequestId == requestId)
            {
                PendingPermission.ResponseTcs?.TrySetResult(approved);
            }
        }

        /// <summary>
        /// 取消当前任务。
        /// </summary>
        public void Cancel()
        {
            _agentCts?.Cancel();
            AddLog("WARN", "任务已取消");
        }

        /// <summary>
        /// 获取日志列表。
        /// </summary>
        public IReadOnlyList<AgentLogEntry> GetLogs() => _logs.AsReadOnly();

        #endregion

        #region Private Methods

        private async Task ExecuteStepAsync(
            AgentStep step,
            AgentTaskPlan plan,
            string? solutionPath,
            Func<string, Task<string?>>? readFileAsync,
            CancellationToken ct)
        {
            // 如果需要权限确认
            if (step.RequiresApproval && !string.IsNullOrEmpty(step.PendingCommand))
            {
                step.Status = AgentStepStatus.WaitingApproval;
                NotifyPlanUpdated();

                bool approved = await RequestPermissionAsync(
                    step.Title,
                    step.PendingCommand,
                    "command");

                if (!approved)
                {
                    step.Status = AgentStepStatus.Skipped;
                    step.ResultSummary = "用户拒绝执行";
                    return;
                }

                step.Status = AgentStepStatus.InProgress;
                NotifyPlanUpdated();
            }

            // ── 判断步骤类型：代码编写步骤 vs 分析步骤 ──
            bool isCodeStep = IsCodeWritingStep(step.Title);

            // ── 构建步骤执行的 AI prompt ──
            string stepPrompt = BuildStepPrompt(step, plan, solutionPath, readFileAsync, isCodeStep);

            // ── 调用 AI 执行此步骤 ──
            string result;
            List<FileChangeSummary> changes;

            if (isCodeStep)
            {
                // 代码编写步骤：期望 AI 输出 ```file: 代码块，最多重试 2 次
                const int maxRetries = 2;
                result = string.Empty;
                changes = new List<FileChangeSummary>();

                for (int retry = 0; retry <= maxRetries; retry++)
                {
                    if (ct.IsCancellationRequested) return;

                    string prompt = retry == 0
                        ? stepPrompt
                        : stepPrompt + "\n\n⚠️ 上次输出格式有误。请严格按照以下格式输出每个要修改的文件：\n\n" +
                          "```file:完整路径\n完整文件内容\n```\n\n" +
                          "每个文件用独立的 ```file: 代码块。不要添加额外解释。";

                    result = await CallAiLongAsync(prompt, ct, maxTokens: 4096);

                    // ── 解析并验证 AI 返回的代码变更 ──
                    changes = ParseCodeChangesFromResult(result);

                    if (changes.Count > 0)
                        break; // 有有效的代码变更 → 跳出重试循环

                    if (retry < maxRetries)
                    {
                        AddLog("WARN", $"AI 输出格式不正确（未检测到 ```file: 代码块），第 {retry + 1} 次重试...");
                    }
                    else
                    {
                        AddLog("WARN", "AI 多次重试后仍未输出有效代码块，将原样记录结果");
                    }
                }

                changes = ParseCodeChangesFromResult(result);

                // ── 保存 AI 原始响应 ──
                step.AiResponse = result;

                // ── 应用代码变更 ──
                foreach (var change in changes)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        string? error = await TerminalWindowHelper.WriteCodeToFileAsync(
                            change.FilePath, change.NewContent ?? string.Empty);

                        if (error == null)
                        {
                            AddLog("INFO", $"✅ 已写入: {change.FilePath} (+{change.LinesAdded} -{change.LinesRemoved})");
                            plan.ChangedFiles.Add(change);
                        }
                        else
                        {
                            AddLog("ERROR", $"写入文件失败: {change.FilePath} - {error}");
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
            }
            else
            {
                // 分析步骤：只需要 AI 的文本分析结果，不要求代码块格式
                result = await CallAiLongAsync(stepPrompt, ct, maxTokens: 4096);
                step.AiResponse = result;

                // 简要摘要：取 AI 响应的前 100 字符
                string summary = result?.Trim() ?? string.Empty;
                if (summary.Length > 100)
                    summary = summary.Substring(0, 100) + "…";
                step.ResultSummary = string.IsNullOrEmpty(summary) ? "完成" : summary;
                changes = new List<FileChangeSummary>();
            }
        }

        /// <summary>
        /// 判断步骤是否为代码编写类步骤（需要输出 ```file: 代码块）。
        /// </summary>
        private static bool IsCodeWritingStep(string stepTitle)
        {
            if (string.IsNullOrWhiteSpace(stepTitle))
                return false;

            var codeKeywords = new[]
            {
                "编写", "写", "修改", "创建", "添加", "生成", "实现",
                "重构", "修复", "改代码", "改", "开发", "build",
                "write", "code", "implement", "create", "add", "fix",
                "refactor", "modify", "change", "update",
            };

            bool isCode = codeKeywords.Any(k =>
                stepTitle.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

            var analysisKeywords = new[]
            {
                "确定", "分析", "查找", "了解", "理解", "定位",
                "研究", "检查", "审查", "评估", "阅读", "查看",
                "review", "analyze", "find", "check", "examine",
                "investigate", "understand", "identify",
            };

            bool isAnalysis = analysisKeywords.Any(k =>
                stepTitle.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

            // 如果同时匹配，优先按代码步骤处理；否则按匹配结果
            if (isCode) return true;
            if (isAnalysis) return false;

            // 默认：无法判断时按代码步骤处理（安全侧：会重试但不会漏掉代码）
            return true;
        }

        private string BuildStepPrompt(
            AgentStep step,
            AgentTaskPlan plan,
            string? solutionPath,
            Func<string, Task<string?>>? readFileAsync,
            bool isCodeStep)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"你是一个 Coding Agent，正在执行任务：「{plan.Title}」。");
            sb.AppendLine($"当前步骤 ({step.Index}/{plan.Steps.Count}): {step.Title}");
            sb.AppendLine($"步骤详情: {step.Description}");
            sb.AppendLine();

            if (isCodeStep)
            {
                sb.AppendLine("请执行此步骤。如果需要修改文件，请使用以下格式输出代码变更：");
                sb.AppendLine();
                sb.AppendLine("```file:path/to/file.cs");
                sb.AppendLine("// 完整的文件内容（不是 diff）");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("重要规则：");
                sb.AppendLine("1. 不要输出额外解释，只输出代码变更");
                sb.AppendLine("2. 每个要修改的文件用一个 ```file:完整路径 代码块，路径必须是完整的绝对路径");
                sb.AppendLine("3. 代码块中必须是修改后的完整文件内容，不要只输出 diff");
                sb.AppendLine("4. 创建新文件也使用相同格式，路径指向要创建的位置");
            }
            else
            {
                sb.AppendLine("这是一个分析/规划步骤，不需要修改代码。");
                sb.AppendLine("请直接输出你的分析结论、发现或建议。");
                sb.AppendLine();
                sb.AppendLine("要求：");
                sb.AppendLine("1. 使用清晰的结构化文本输出（可包含 Markdown 列表、表格等）");
                sb.AppendLine("2. 具体、可执行，避免泛泛而谈");
                sb.AppendLine("3. 如果发现关键信息（如目标文件路径、依赖关系、潜在风险），请明确标注");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从 AI 返回结果中解析文件变更。
        /// 支持格式：```file:完整路径\n完整内容\n```
        /// </summary>
        private static List<FileChangeSummary> ParseCodeChangesFromResult(string aiResult)
        {
            var changes = new List<FileChangeSummary>();

            if (string.IsNullOrWhiteSpace(aiResult))
                return changes;

            // 解析 ```file:path 代码块（兼容 \r\n 和 \n）
            var regex = new System.Text.RegularExpressions.Regex(
                @"```file:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var matches = regex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string filePath = match.Groups["path"].Value.Trim();
                string newContent = match.Groups["content"].Value;

                // 跳过空路径
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                // 规范化路径（处理 AI 可能输出的相对路径）
                if (!Path.IsPathRooted(filePath))
                {
                    // 如果不是绝对路径，尝试在解决方案目录下查找
                    Logger.Warn($"[ParseCodeChanges] AI 输出了非绝对路径: {filePath}，将保留原路径");
                }

                // 计算行数变化
                int newLines = CountLines(newContent);
                int linesAdded = newLines;
                int linesRemoved = 0;

                if (File.Exists(filePath))
                {
                    try
                    {
                        string oldContent = File.ReadAllText(filePath);
                        int oldLines = CountLines(oldContent);
                        linesAdded = Math.Max(0, newLines - oldLines);
                        linesRemoved = Math.Max(0, oldLines - newLines);
                    }
                    catch { /* 读取失败不影响 */ }
                }

                changes.Add(new FileChangeSummary
                {
                    FilePath = filePath,
                    NewContent = newContent,
                    LinesAdded = linesAdded,
                    LinesRemoved = linesRemoved,
                    BriefDescription = Path.GetFileName(filePath) + (File.Exists(filePath) ? " (修改)" : " (新建)"),
                });
            }

            return changes;
        }

        /// <summary>
        /// 计算文本的行数。
        /// </summary>
        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        /// <summary>
        /// 调用 AI 进行简短回答（用于意图分类、任务分解等）。
        /// </summary>
        private async Task<string> CallAiShortAsync(string prompt, CancellationToken ct, int maxTokens = 512)
        {
            var messages = new List<ChatApiMessage>
            {
                new ChatApiMessage { Role = "user", Content = prompt }
            };

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 调用 AI 进行长回答（用于步骤执行）。
        /// </summary>
        private async Task<string> CallAiLongAsync(string prompt, CancellationToken ct, int maxTokens = 4096)
        {
            var messages = new List<ChatApiMessage>
            {
                new ChatApiMessage { Role = "user", Content = prompt }
            };

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }

            return sb.ToString().Trim();
        }

        private void NotifyPlanUpdated()
        {
            try { PlanUpdated?.Invoke(CurrentPlan!); }
            catch { }
        }

        private void AddLog(string level, string message)
        {
            var entry = new AgentLogEntry { Level = level, Message = message };
            _logs.Add(entry);
            try { LogEntryAdded?.Invoke(entry); }
            catch { }

            // 同步写入 Logger
            if (level == "ERROR")
                Logger.Error($"[Agent] {message}");
            else if (level == "WARN")
                Logger.Warn($"[Agent] {message}");
            else
                Logger.Info($"[Agent] {message}");
        }

        /// <summary>
        /// 从 markdown 代码块中提取 JSON。
        /// </summary>
        private static string ExtractJsonFromMarkdown(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text, @"```(?:json)?\s*\n?(.*?)\n?```",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            return match.Success ? match.Groups[1].Value.Trim() : text.Trim();
        }

        public void Dispose()
        {
            _agentCts?.Cancel();
            _agentCts?.Dispose();
            _agentCts = null;
        }

        #endregion
    }
}
