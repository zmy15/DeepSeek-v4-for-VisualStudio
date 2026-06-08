using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// EditAgent 任务拆分与限制策略（v1.1.10）。
    /// </summary>
    public partial class EditAgent
    {
        #region Task Size Classification

        /// <summary>
        /// 三级任务规模分类：Small / Medium / Large。
        /// Large 应由 Plan Agent 处理；Medium 由 Edit Agent 自行拆分；Small 单步执行。
        /// </summary>
        public static TaskSize ClassifyTaskSize(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return TaskSize.Small;

            var msg = userMessage.Trim();

            // ════════════════════════════════════════════════════════
            // 第0层：简单操作预检 — 构建/验证/git 等明确的小任务直接返回 Small
            // ════════════════════════════════════════════════════════
            if (IsTrivialTask(msg))
            {
                Logger.Info($"[TaskSize] 简单操作预检命中 → Small: \"{msg.Truncate(60)}\"");
                return TaskSize.Small;
            }

            // ════════════════════════════════════════════════════════
            // 第1层：Large 任务关键词（新功能、架构级变更）
            // ════════════════════════════════════════════════════════
            var largeKeywords = new[]
            {
                // 中文 — 大型任务信号
                "新功能", "新增功能", "新模块", "新系统", "从头实现",
                "架构", "重新设计", "重构整个", "整体重构",
                "设计一个", "搭建一个", "构建一个", "开发一个",
                "完整实现", "全部实现", "整套",
                "从零开始", "重新实现", "重建",
                // 英文
                "new feature", "new module", "new system", "implement a",
                "build a", "create a", "from scratch", "architecture",
                "redesign", "restructure", "overhaul",
            };

            int largeHits = largeKeywords.Count(k =>
                msg.Contains(k, StringComparison.OrdinalIgnoreCase));

            // 消息 >500 字符 + 至少 1 个 Large 关键词 → Large
            bool isVeryLongMessage = msg.Length > 500;

            // 明确提到 6+ 个文件名 → Large
            int fileRefCount = System.Text.RegularExpressions.Regex.Matches(
                msg, @"\b\w+\.(cs|ts|js|py|java|cpp|h|hpp|xml|json|yaml|yml|md|csproj|sln)\b").Count;

            // ── Large 判定需要更强的信号组合 ──
            // 条件 A: ≥3 个 Large 关键词（从 2 提高到 3）
            // 条件 B: 长消息 + ≥2 个 Large 关键词（从 1 提高到 2）
            // 条件 C: ≥8 个文件引用（从 6 提高到 8）
            if (largeHits >= 3 || (isVeryLongMessage && largeHits >= 2) || fileRefCount >= 8)
            {
                Logger.Info($"[TaskSize] Large: hits={largeHits}, long={isVeryLongMessage}, files={fileRefCount}, msg=\"{msg.Truncate(60)}\"");
                return TaskSize.Large;
            }

            // ════════════════════════════════════════════════════════
            // 第2层：Medium 任务关键词（多文件/多步骤，但非架构级）
            // ════════════════════════════════════════════════════════
            var mediumKeywords = new[]
            {
                "多个文件", "多处", "同时", "一并", "并且", "还有", "另外",
                "以及", "包括", "等等", "几个", "一些",
                "不只", "不止", "除了", "另外还有",
                "修复", "改进", "优化", "调整", "更新",
                "multiple files", "several", "also", "and also",
                "in addition", "as well as", "including",
                "fix", "improve", "optimize", "update",
            };

            int mediumHits = mediumKeywords.Count(k =>
                msg.Contains(k, StringComparison.OrdinalIgnoreCase));

            bool isLongMessage = msg.Length > 200;

            // 3-7 个文件引用 → Medium
            if (fileRefCount >= 3 && fileRefCount < 8) return TaskSize.Medium;

            // 2+ 关键词 → Medium
            if (mediumHits >= 2) return TaskSize.Medium;

            // 长消息 + 至少 1 个关键词 → Medium
            if (isLongMessage && mediumHits >= 1) return TaskSize.Medium;

            // ════════════════════════════════════════════════════════
            // 默认：Small
            // ════════════════════════════════════════════════════════
            return TaskSize.Small;
        }

        /// <summary>
        /// 简单操作预检：纯构建、验证、git 推送、编译检查等明确的小任务，直接返回 Small。
        /// 避免这类单步骤、无代码修改的任务被误判为 Medium/Large 而走 Plan Agent。
        /// </summary>
        private static bool IsTrivialTask(string userMessage)
        {
            // ── 纯构建/验证/编译 ──
            var buildOnlyPatterns = new[]
            {
                "验证编译", "编译验证", "编译并提交", "提交推送",
                "构建并推送", "构建推送", "build and push", "commit and push",
                "编译通过", "编译检查", "构建验证",
            };
            if (buildOnlyPatterns.Any(p => userMessage.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            // ── 纯 git 操作（提交/推送/合并）──
            var gitOnlyPatterns = new[]
            {
                "提交推送", "commit and push", "提交并推送",
                "git push", "git commit",
            };
            // 要排除含代码修改意图的 git 操作
            bool hasCodeIntent = userMessage.Contains("修改", StringComparison.OrdinalIgnoreCase)
                || userMessage.Contains("添加", StringComparison.OrdinalIgnoreCase)
                || userMessage.Contains("修复", StringComparison.OrdinalIgnoreCase)
                || userMessage.Contains("实现", StringComparison.OrdinalIgnoreCase)
                || userMessage.Contains("创建", StringComparison.OrdinalIgnoreCase)
                || userMessage.Contains("change", StringComparison.OrdinalIgnoreCase)
                || userMessage.Contains("implement", StringComparison.OrdinalIgnoreCase);

            if (!hasCodeIntent && gitOnlyPatterns.Any(p =>
                userMessage.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            // ── 短消息且无文件名引用 → 大概率是简单任务 ──
            bool isVeryShort = userMessage.Length <= 20;
            int fileRefCount = System.Text.RegularExpressions.Regex.Matches(
                userMessage, @"\b\w+\.(cs|ts|js|py|java|cpp|h|hpp|xml|json|yaml|yml|md|csproj|sln)\b").Count;

            if (isVeryShort && fileRefCount == 0 && !hasCodeIntent)
                return true;

            return false;
        }

        #endregion

        #region Large Task Handoff

        /// <summary>
        /// Large 任务不应由 Edit Agent 直接处理。构建一个 Handoff 到 Plan Agent 的结果，
        /// 由上层 handoff 链机制自动将控制权移交给 Plan Agent。
        /// </summary>
        private AgentResult BuildLargeTaskHandoffResult(string userMessage)
        {
            var L = LocalizationService.Instance;
            AddLog("INFO", "Large task detected — handing off to Plan Agent for deep planning.");

            return new AgentResult
            {
                AgentType = AgentType.Edit,
                Success = true,
                Content = $"⚠️ 此任务规模较大（\"{userMessage.Truncate(80)}\"），需要先制定详细计划。正在转交 Plan Agent...",
                Plan = null,
                Handoff = new AgentHandoff
                {
                    Label = "规划此大型任务",
                    TargetAgent = AgentType.Plan,
                    Prompt = string.Format(
                        "用户提出了一个大型任务，请深入分析需求、研究代码库，并制定详细的实现计划。\n\n用户请求: {0}",
                        userMessage),
                    AutoSend = true,
                    ShowContinueOn = false,
                },
            };
        }

        #endregion

        #region Task Auto-Splitting

        /// <summary>
        /// 启发式判断：用户请求是否需要拆分为多个步骤（Medium 任务）。
        /// 大任务应由 Plan Agent 处理，此处仅判断 Medium。
        /// </summary>
        private static bool ShouldAutoSplitTask(string userMessage)
        {
            return ClassifyTaskSize(userMessage) == TaskSize.Medium;
        }

        /// <summary>
        /// 让 AI 将中型任务拆分为多个步骤，构建 EditAgent 自拆分计划。
        /// 不生成 plan.md 文件。
        /// </summary>
        private async Task<AgentTaskPlan> CreateAutoSplitPlanAsync(
            string userMessage, AgentContext context)
        {
            var ct = context.CancellationToken;

            // ── 使用轻量级 prompt 让 AI 生成步骤列表 ──
            string splitPrompt = BuildAutoSplitPrompt(userMessage);

            try
            {
                string aiResult = await CallAiLongAsync(
                    Definition.SystemPrompt,
                    splitPrompt,
                    ct,
                    maxTokens: 2048,
                    responseFormat: "json_object");

                // ── 解析 AI 返回的步骤 ──
                var steps = ParseStepsFromAiResponse(aiResult, userMessage);

                if (steps.Count > 1)
                {
                    AddLog("INFO", $"AI 将任务拆分为 {steps.Count} 个步骤");
                    return new AgentTaskPlan
                    {
                        Intent = AgentIntent.CodeChange,
                        Title = ExtractTaskTitle(userMessage),
                        Steps = steps,
                        Source = PlanSource.EditAgent,
                    };
                }

                // ── AI 拆分失败或只返回 1 步 → 回退到单步 ──
                AddLog("INFO", "AI 拆分返回 ≤1 步，回退到单步执行");
                return CreateSingleStepPlan(userMessage);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 自动拆分失败: {ex.Message}，回退到单步执行");
                return CreateSingleStepPlan(userMessage);
            }
        }

        /// <summary>
        /// 构建让 AI 拆分任务的 prompt。
        /// </summary>
        private static string BuildAutoSplitPrompt(string userMessage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一个代码修改步骤规划器。请分析以下用户请求，将其分解为可独立执行的步骤。");
            sb.AppendLine();
            sb.AppendLine("## 规划规则");
            sb.AppendLine("- 每个步骤应该是可以独立完成的代码修改操作");
            sb.AppendLine($"- 每个步骤最多修改 {MaxFilesPerEdit} 个文件，不超过 {MaxLinesPerEdit} 行代码");
            sb.AppendLine("- 步骤数量不超过 5 个（如果是简单任务，1-2 步即可）");
            sb.AppendLine("- 如果任务非常简单（单文件、少量修改），返回单步骤即可");
            sb.AppendLine("- 步骤之间应尽量减少依赖，便于独立执行和验证");
            sb.AppendLine();
            sb.AppendLine("## 输出格式");
            sb.AppendLine("只返回 JSON 数组，每个元素包含 index（步骤序号从1开始）、title（简短标题）、description（详细描述）。");
            sb.AppendLine("不要包含任何其他文本或 markdown 包裹。");
            sb.AppendLine();
            sb.AppendLine("## 示例输出");
            sb.AppendLine("[{\"index\":1,\"title\":\"修改 UserService 接口\",\"description\":\"在 IUserService 中添加 GetByIdAsync 方法签名\"},");
            sb.AppendLine("{\"index\":2,\"title\":\"实现 UserService\",\"description\":\"在 UserService.cs 中实现 GetByIdAsync 方法\"},");
            sb.AppendLine("{\"index\":3,\"title\":\"更新调用方\",\"description\":\"在 UserController.cs 中调用新的 GetByIdAsync 方法\"}]");
            sb.AppendLine();
            sb.AppendLine("## 用户请求");
            sb.AppendLine(userMessage);
            sb.AppendLine();
            sb.AppendLine("请输出步骤规划 JSON：");

            return sb.ToString();
        }

        /// <summary>
        /// 从 AI 响应中解析步骤列表。
        /// </summary>
        private static List<AgentStep> ParseStepsFromAiResponse(string aiResult, string userMessage)
        {
            var steps = new List<AgentStep>();

            if (string.IsNullOrWhiteSpace(aiResult)) return steps;

            // ── 清理可能的 markdown 包裹 ──
            string json = aiResult.Trim();
            if (json.StartsWith("```"))
            {
                int start = json.IndexOf('\n');
                int end = json.LastIndexOf("```");
                if (start >= 0 && end > start)
                    json = json.Substring(start + 1, end - start - 1).Trim();
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return steps;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    int index = item.TryGetProperty("index", out var idx) ? idx.GetInt32() : steps.Count + 1;
                    string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        steps.Add(new AgentStep
                        {
                            Index = index,
                            Title = title.Trim(),
                            Description = string.IsNullOrWhiteSpace(desc) ? title.Trim() : desc.Trim(),
                        });
                    }
                }
            }
            catch
            {
                // JSON 解析失败 → 返回空列表，调用方回退到单步
            }

            return steps;
        }

        /// <summary>
        /// 从用户消息中提取任务标题。
        /// </summary>
        private static string ExtractTaskTitle(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return "代码修改任务";

            // 取第一行或前 80 字符
            string firstLine = userMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? userMessage;

            return firstLine.Length > 80 ? firstLine.Substring(0, 80) + "..." : firstLine;
        }

        #endregion
    }
}
