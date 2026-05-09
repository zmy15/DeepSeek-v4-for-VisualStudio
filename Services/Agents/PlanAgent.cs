using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Plan Agent — 研究和规划代理。
    /// 
    /// 职责：
    /// - 深入分析用户需求
    /// - 研究代码库（通过调用 Explore 子代理）
    /// - 与用户对齐需求（通过提问澄清）
    /// - 产出详细的、可执行的实现计划
    /// - 将计划 Handoff 给 Edit Agent 执行
    /// - 绝不修改任何文件
    /// 
    /// 参考: VS Code Copilot Chat Plan Agent
    /// </summary>
    public class PlanAgent : BaseAgent
    {
        private readonly ExploreAgent _exploreAgent;

        public PlanAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Plan)
        {
            _exploreAgent = new ExploreAgent(apiService);
        }

        #region Agent Definition

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Plan,
                Name = "Plan",
                Description = "研究和规划多步骤实现方案。深入分析需求，研究代码库，与用户对齐后产出详细计划。",
                ArgumentHint = "描述要规划的目标或问题",
                UserInvocable = true,
                DisableModelInvocation = false,
                AllowedTools = new List<string>(ExploreAgent.DefaultReadTools)
                {
                    "vscode_askQuestions", // 向用户提问澄清
                    "runSubagent",          // 调用 Explore 子代理
                },
                SubAgents = new List<AgentType> { AgentType.Explore },
                Handoffs = new List<AgentHandoff>
                {
                    new AgentHandoff
                    {
                        Label = "开始实现",
                        TargetAgent = AgentType.Edit,
                        Prompt = "根据以下计划执行代码修改。严格按步骤执行，每步完成后报告进度。\n\n计划：",
                        AutoSend = false,
                        ShowContinueOn = true,
                    }
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return
                "你是一个 Planning Agent——与用户合作创建详细的、可执行的实现计划。\n\n" +
                "你的职责是研究代码库 → 与用户对齐 → 将发现和决策整理成全面计划。\n" +
                "这种迭代方法在实际实现之前就捕获边缘情况和非显而易见的需求。\n\n" +
                "你的唯一职责是规划。绝不开始实现。\n\n" +
                "## 规则\n" +
                "- 如果你考虑使用文件编辑工具——停止。计划是给别人执行的。\n" +
                "- 使用 vscode_askQuestions 工具随时澄清需求——不要做大假设\n" +
                "- 在实现之前呈现一个经过充分研究的、没有遗漏的计划\n\n" +
                "## 工作流\n" +
                "基于用户输入循环以下阶段。这是迭代的，不是线性的。\n\n" +
                "### 1. 发现 (Discovery)\n" +
                "启动 Explore 子代理收集上下文、可作为实现模板的类似已有功能、以及潜在阻碍或歧义。\n" +
                "当任务跨越多个独立区域（如前后端、不同功能、不同仓库）时，并行启动 2-3 个 Explore 子代理。\n\n" +
                "### 2. 对齐 (Alignment)\n" +
                "如果研究揭示了重大歧义或需要验证假设：\n" +
                "- 使用 vscode_askQuestions 与用户澄清意图\n" +
                "- 呈现发现的技术约束或替代方案\n" +
                "- 如果回答显著改变了范围，回到发现阶段\n\n" +
                "### 3. 设计 (Design)\n" +
                "一旦上下文清晰，起草全面的实现计划。\n" +
                "计划应反映：\n" +
                "- 结构简洁到可扫描，详细到可执行\n" +
                "- 逐步实现，明确依赖关系——标记哪些步骤可并行，哪些依赖前置步骤\n" +
                "- 对于多步骤计划，分组为独立可验证的阶段\n" +
                "- 自动化和手动的验证步骤\n" +
                "- 可复用或参考的关键架构——引用具体函数、类型或模式，而非仅文件名\n" +
                "- 需要修改的关键文件（含完整路径）\n" +
                "- 明确的范围边界——包含什么和刻意排除什么\n\n" +
                "## 输出格式\n" +
                "计划应输出为 JSON:\n" +
                "```json\n" +
                "{\n" +
                "  \"title\": \"任务标题\",\n" +
                "  \"steps\": [\n" +
                "    { \"index\": 1, \"title\": \"步骤标题\", \"description\": \"详细描述\", \"requiresApproval\": false }\n" +
                "  ]\n" +
                "}\n" +
                "```";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Plan Agent 执行入口。
        /// 执行发现 → 对齐 → 设计循环，产出 AgentTaskPlan。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            AddLog("INFO", $"Plan Agent 开始规划: \"{userMessage.Truncate(100)}\"");

            var result = new AgentResult
            {
                AgentType = AgentType.Plan,
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 阶段 1: 发现 — 通过 Explore 子代理了解代码库 ──
                AddLog("INFO", "阶段 1/3: 发现 — 研究代码库...");
                string discoveryContext = await RunDiscoveryAsync(userMessage, context);

                // ── 阶段 2: 对齐 — 与用户澄清需求 ──
                AddLog("INFO", "阶段 2/3: 对齐 — 分析需求...");

                // ── 阶段 3: 设计 — 产出实现计划 ──
                AddLog("INFO", "阶段 3/3: 设计 — 创建实现计划...");
                var plan = await CreatePlanAsync(userMessage, discoveryContext, context);
                result.Plan = plan;

                if (plan != null && plan.Steps.Count > 0)
                {
                    AddLog("INFO", $"计划创建完成: {plan.Steps.Count} 个步骤 → \"{plan.Title}\"");
                    result.Content = FormatPlanAsMarkdown(plan);

                    // ── 设置 Handoff：计划完成后自动建议切换到 Edit Agent 执行 ──
                    result.Handoff = new AgentHandoff
                    {
                        Label = "开始实现",
                        TargetAgent = AgentType.Edit,
                        Prompt = $"根据以下计划执行代码修改。\n\n计划: {plan.Title}\n步骤数: {plan.Steps.Count}\n\n严格按步骤执行，每步完成后报告进度。",
                        AutoSend = false,
                        ShowContinueOn = true,
                    };
                }
                else
                {
                    result.Content = "未能生成有效的实现计划。请提供更多信息或尝试不同的描述。";
                    AddLog("WARN", "计划生成失败：无有效步骤");
                }

                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "规划已取消";
                AddLog("WARN", "规划已取消");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", $"规划失败: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region Discovery Phase

        /// <summary>
        /// 运行发现阶段：使用 AI 判断需要探索哪些区域，然后并行启动 Explore 子代理。
        /// </summary>
        private async Task<string> RunDiscoveryAsync(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            try
            {
                // ── 先让 AI 判断需要探索哪些代码区域 ──
                string routingPrompt =
                    "根据以下用户任务，列出需要探索的代码区域（最多3个）。" +
                    "每个区域一行，只输出区域描述，不要其他内容。\n\n" +
                    $"用户任务: {userMessage}\n\n" +
                    (string.IsNullOrEmpty(context.SolutionPath) ? ""
                        : $"解决方案路径: {context.SolutionPath}\n\n") +
                    "需要探索的区域:";

                string routingResponse = await CallAiShortAsync(
                    "你是一个代码库探索路由器。", routingPrompt, context.CancellationToken);

                var areas = routingResponse
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.Trim().TrimStart('-', ' ', '*', '1', '2', '3', '.', ':', '）', ')'))
                    .Where(a => a.Length > 3)
                    .Take(3)
                    .ToList();

                if (areas.Count == 0)
                {
                    areas.Add(userMessage); // 回退：直接搜索用户消息
                }

                AddLog("INFO", $"探索路由: {areas.Count} 个区域 → [{string.Join(", ", areas)}]");

                // ── 并行启动 Explore 子代理 ──
                var exploreTasks = new List<Task<SubagentResult>>();
                for (int i = 0; i < areas.Count; i++)
                {
                    string area = areas[i];
                    var task = RunSingleExploreAsync(i.ToString(), area, context);
                    exploreTasks.Add(task);
                }

                var exploreResults = await Task.WhenAll(exploreTasks);

                // ── 汇总探索结果 ──
                foreach (var exploreResult in exploreResults)
                {
                    if (exploreResult.Success && !string.IsNullOrEmpty(exploreResult.Findings))
                    {
                        sb.AppendLine($"## 探索区域: {exploreResult.TaskId}");
                        sb.AppendLine(exploreResult.Findings);
                        sb.AppendLine();
                    }
                }

                AddLog("INFO", $"发现阶段完成: {exploreResults.Length} 个子代理，"
                    + $"{exploreResults.Count(r => r.Success)} 成功");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"发现阶段出错: {ex.Message}，继续规划");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 运行单个 Explore 子代理。
        /// </summary>
        private async Task<SubagentResult> RunSingleExploreAsync(string taskId, string prompt, AgentContext context)
        {
            var result = new SubagentResult { TaskId = taskId };
            try
            {
                var exploreResult = await _exploreAgent.ExecuteAsync(prompt, context);
                result.Success = exploreResult.Success;
                result.Findings = exploreResult.Content;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        #endregion

        #region Plan Creation

        /// <summary>
        /// 使用 AI 创建实现计划（JSON 格式）。
        /// </summary>
        private async Task<AgentTaskPlan?> CreatePlanAsync(
            string userMessage, string discoveryContext, AgentContext context)
        {
            var ct = context.CancellationToken;

            string planPrompt = BuildPlanCreationPrompt(userMessage, discoveryContext, context);
            string json = await CallAiLongAsync(Definition.SystemPrompt, planPrompt, ct, maxTokens: 2048);

            json = ExtractJsonFromMarkdown(json);

            try
            {
                var plan = JsonSerializer.Deserialize<AgentTaskPlan>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                if (plan != null && plan.Steps.Count > 0)
                {
                    plan.Intent = AgentIntent.CodeChange;
                    return plan;
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"计划 JSON 解析失败: {ex.Message}");
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

        private static string BuildPlanCreationPrompt(
            string userMessage, string discoveryContext, AgentContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 用户任务");
            sb.AppendLine(userMessage);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(discoveryContext))
            {
                sb.AppendLine("## 代码库研究发现");
                sb.AppendLine(discoveryContext);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine("## 用户提供的文件上下文");
                sb.AppendLine(context.FileContext.Length > 2000
                    ? context.FileContext.Substring(0, 2000) + "\n... (已截断)"
                    : context.FileContext);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"## 解决方案路径");
                sb.AppendLine(context.SolutionPath);
                sb.AppendLine();
            }

            sb.AppendLine("## 指令");
            sb.AppendLine("根据以上信息，创建一个详细的实现计划。");
            sb.AppendLine("计划应是逐步的、可执行的，包含验证步骤。");
            sb.AppendLine();
            sb.AppendLine("输出 JSON 格式:");
            sb.AppendLine("{");
            sb.AppendLine("  \"title\": \"任务标题\",");
            sb.AppendLine("  \"steps\": [");
            sb.AppendLine("    { \"index\": 1, \"title\": \"步骤标题\", \"description\": \"详细描述\", \"requiresApproval\": false }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("请输出计划 JSON:");

            return sb.ToString();
        }

        /// <summary>
        /// 将计划格式化为 Markdown 展示。
        /// </summary>
        private static string FormatPlanAsMarkdown(AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 📋 实现计划: {plan.Title}");
            sb.AppendLine();
            sb.AppendLine($"共 {plan.Steps.Count} 个步骤：");
            sb.AppendLine();

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                string icon = step.RequiresApproval ? "🔐" : "📌";
                sb.AppendLine($"{icon} **步骤 {step.Index}**: {step.Title}");
                sb.AppendLine($"   {step.Description}");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("✅ 计划就绪，是否开始执行？");

            return sb.ToString();
        }

        #endregion

        #region IDisposable

        public override void Dispose()
        {
            _exploreAgent?.Dispose();
            base.Dispose();
        }

        #endregion
    }
}
