using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
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
            // ── 转发 ExploreAgent 的日志到 PlanAgent（进而到 AgentDispatcher → UI）──
            _exploreAgent.LogEntryAdded += (entry) => AddLog(entry.Level, $"[Explore] {entry.Message}");
            _exploreAgent.FileChangeNotified += (args) => NotifyFileChange(args.PlanId, args.ChangeType, args.FilePath, args.Detail);
        }

        #region Agent Definition

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Plan,
                Name = "Plan",
                Description = LocalizationService.Instance["agent.plan.description"],
                ArgumentHint = LocalizationService.Instance["agent.plan.argumentHint"],
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
                        Label = LocalizationService.Instance["plan.handoff.label"],
                        TargetAgent = AgentType.Edit,
                        Prompt = LocalizationService.Instance["plan.handoff.prompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    }
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return CommonSystemPromptPrefix + LocalizationService.Instance["agent.plan.systemPromptFragment"];
        }

        #endregion

        #region Execute

        /// <summary>
        /// Plan Agent 执行入口。
        /// 执行发现 → 对齐 → 设计循环，产出 AgentTaskPlan。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            var L = LocalizationService.Instance;
            AddLog("INFO", string.Format(L["agent.log.planStarted"], userMessage.Truncate(100)));

            var result = new AgentResult
            {
                AgentType = AgentType.Plan,
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 阶段 1: 发现 — 通过 Explore 子代理了解代码库 ──
                AddLog("INFO", L["agent.log.planPhaseDiscover"]);
                string discoveryContext = await RunDiscoveryAsync(userMessage, context);

                // ── 阶段 2: 对齐 — 与用户澄清需求 ──
                AddLog("INFO", L["agent.log.planPhaseAlign"]);

                // ── 阶段 3: 设计 — 产出实现计划 ──
                AddLog("INFO", L["agent.log.planPhaseDesign"]);
                var plan = await CreatePlanAsync(userMessage, discoveryContext, context);
                result.Plan = plan;

                if (plan != null && plan.Steps.Count > 0)
                {
                    AddLog("INFO", string.Format(L["agent.log.planDone"], plan.Steps.Count, plan.Title));
                    result.Content = FormatPlanAsMarkdown(plan);

                    // ── 生成详细 plan.md 文件 ──
                    try
                    {
                        string planMarkdown = await GenerateDetailedPlanMarkdownAsync(
                            userMessage, discoveryContext, plan, context);
                        string planFilePath = await SavePlanMarkdownAsync(planMarkdown, context);
                        plan.PlanFilePath = planFilePath;
                        context.PlanFilePath = planFilePath;
                        AddLog("INFO", string.Format(L["agent.log.planMdSaved"], planFilePath));

                        // 在结果内容中附加 plan.md 路径信息
                        result.Content += string.Format(L["agent.log.planMdAppended"], planFilePath);
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", $"plan.md 生成失败（非致命）: {ex.Message}");
                    }

                    // ── 设置 Handoff：计划完成后自动建议切换到 Edit Agent 执行 ──
                    result.Handoff = new AgentHandoff
                    {
                        Label = L["plan.handoff.label"],
                        TargetAgent = AgentType.Edit,
                        Prompt = string.Format(L["plan.handoff.promptWithPlan"], plan.Title, plan.Steps.Count),
                        AutoSend = false,
                        ShowContinueOn = true,
                    };
                }
                else
                {
                    result.Content = L["plan.noValidPlan"];
                    AddLog("WARN", "计划生成失败：无有效步骤");
                }

                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = L["agent.log.planCancelled"];
                AddLog("WARN", L["agent.log.planCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(L["agent.log.planFailed"], ex.Message));
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
                var L = LocalizationService.Instance;
                // ── 先让 AI 判断需要探索哪些代码区域 ──
                string routingPrompt =
                    "根据以下用户任务，列出需要探索的代码区域（最多3个）。" +
                    "每个区域一行，只输出区域描述，不要其他内容。\n\n" +
                    $"{L["plan.userTask"]}: {userMessage}\n\n" +
                    (string.IsNullOrEmpty(context.SolutionPath) ? ""
                        : $"解决方案路径: {context.SolutionPath}\n\n") +
                    "需要探索的区域:";

                string routingResponse = await CallAiShortAsync(
                    Definition.SystemPrompt, routingPrompt, context.CancellationToken);

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

                AddLog("INFO", string.Format(L["agent.log.exploreRouting"], areas.Count, string.Join(", ", areas)));

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

                var L2 = LocalizationService.Instance;
                AddLog("INFO", string.Format(L2["agent.log.exploreDone"], exploreResults.Length, exploreResults.Count(r => r.Success)));
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

            // ── 构建额外的 system 消息（发现上下文），放在历史之后、用户消息之前 ──
            // 这样 messages[0]（Agent System Prompt）保持稳定，可被 DeepSeek Prefix Cache 命中
            var extraSystemMessages = new List<ChatApiMessage>();
            if (!string.IsNullOrEmpty(discoveryContext))
            {
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = "## 代码库研究发现（以下内容由 Explore 子代理搜集）\n\n" + discoveryContext
                });
            }
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                string truncated = context.FileContext.Length > 2000
                    ? context.FileContext.Substring(0, 2000) + "\n... (已截断)"
                    : context.FileContext;
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = "## 用户提供的文件上下文\n\n" + truncated
                });
            }

            // ── 用户消息保持简洁（只有任务描述 + 指令），不含动态内容 ──
            string planPrompt = BuildPlanCreationPrompt(userMessage, context);

            string json = await CallAiLongAsync(
                Definition.SystemPrompt, planPrompt, extraSystemMessages, ct, maxTokens: 2048);

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

        /// <summary>
        /// 构建计划创建的 user prompt（仅包含任务描述和指令，不含动态发现内容）。
        /// 发现上下文通过 extraSystemMessages 注入，保持 user message 简洁稳定以利于缓存。
        /// </summary>
        private static string BuildPlanCreationPrompt(
            string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## {LocalizationService.Instance["plan.userTask"]}");
            sb.AppendLine(userMessage);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"## 解决方案路径");
                sb.AppendLine(context.SolutionPath);
                sb.AppendLine();
            }

            sb.AppendLine("## 指令");
            sb.AppendLine("根据以上信息和代码库研究发现（已在 system 消息中提供），创建一个详细的实现计划。");
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

        /// <summary>
        /// 使用 AI 将 JSON 计划展开为详细的 Markdown 计划文档（plan.md）。
        /// 包含：要实现的功能、实现方案、详细步骤、涉及文件、类/接口/方法设计、依赖关系、验证步骤。
        /// </summary>
        private async Task<string> GenerateDetailedPlanMarkdownAsync(
            string userMessage, string discoveryContext, AgentTaskPlan plan, AgentContext context)
        {
            var ct = context.CancellationToken;

            // 先将现有计划步骤序列化为 JSON 供 AI 参考
            string planJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
            });

            // ── 发现上下文作为额外 system 消息注入（保持 messages[0] 稳定）──
            var extraSystemMessages = new List<ChatApiMessage>();
            if (!string.IsNullOrEmpty(discoveryContext))
            {
                string truncated = discoveryContext.Length > 4000
                    ? discoveryContext.Substring(0, 4000) + "\n... (已截断)"
                    : discoveryContext;
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = "## 代码库研究发现\n\n" + truncated
                });
            }
            extraSystemMessages.Add(new ChatApiMessage
            {
                Role = "system",
                Content = "## 已生成的 JSON 计划\n```json\n" + planJson + "\n```"
            });

            // ── 用户消息保持简洁 ──
            var prompt = new StringBuilder();
            prompt.AppendLine("## 用户原始需求");
            prompt.AppendLine(userMessage);
            prompt.AppendLine();
            prompt.AppendLine("## 指令");
            prompt.AppendLine("请基于以上信息（代码库研究发现和 JSON 计划已在 system 消息中提供），生成一份详细的实现计划 Markdown 文档。");
            prompt.AppendLine("文档必须包含以下章节（用中文撰写）：");
            prompt.AppendLine();
            prompt.AppendLine("### 1. 🎯 要实现的功能");
            prompt.AppendLine("- 用自然语言描述本次要实现的完整功能列表");
            prompt.AppendLine();
            prompt.AppendLine("### 2. 🏗️ 实现方案");
            prompt.AppendLine("- 总体技术思路和架构决策");
            prompt.AppendLine("- 关键设计模式和原则");
            prompt.AppendLine();
            prompt.AppendLine("### 3. 📝 详细步骤");
            prompt.AppendLine("对每个步骤展开描述：");
            prompt.AppendLine("- **目标**: 该步骤要达成什么");
            prompt.AppendLine("- **涉及文件**: 列出「✨ 新建」「✏️ 修改」「🗑️ 删除」的文件及其绝对路径（可基于项目结构推断）");
            prompt.AppendLine("- **类/接口设计**: 该步骤涉及的关键类、接口的完整定义（含命名空间、访问修饰符、继承关系）");
            prompt.AppendLine("- **方法设计**: 关键方法的签名、参数说明、返回值、核心逻辑描述");
            prompt.AppendLine();
            prompt.AppendLine("### 4. 📊 文件变更汇总");
            prompt.AppendLine("- 以表格形式列出所有文件变更（操作类型 | 文件路径 | 说明）");
            prompt.AppendLine();
            prompt.AppendLine("### 5. 🔗 依赖关系");
            prompt.AppendLine("- 步骤之间的依赖关系（哪些步骤可并行，哪些需串行）");
            prompt.AppendLine("- 外部依赖（NuGet 包、API、配置文件等）");
            prompt.AppendLine();
            prompt.AppendLine("### 6. ✅ 验证步骤");
            prompt.AppendLine("- 每个步骤完成后的验证方法（编译、运行测试、手动检查等）");
            prompt.AppendLine();
            prompt.AppendLine("## 注意事项");
            prompt.AppendLine("- 直接输出 Markdown，不要包裹在代码块中");
            prompt.AppendLine("- 类/接口/方法设计尽可能具体，包含完整的签名和关键实现逻辑");
            prompt.AppendLine("- 文件路径使用项目内的绝对路径");
            prompt.AppendLine("- 保持专业、清晰、可执行");

            string markdown = await CallAiLongAsync(
                Definition.SystemPrompt, prompt.ToString(), extraSystemMessages, ct, maxTokens: 4096);

            // 如果 AI 返回了代码块包裹的内容，去掉包裹
            markdown = markdown.Trim();
            if (markdown.StartsWith("```markdown") || markdown.StartsWith("```md"))
            {
                int start = markdown.IndexOf('\n') + 1;
                int end = markdown.LastIndexOf("```");
                if (end > start)
                    markdown = markdown.Substring(start, end - start).Trim();
            }
            else if (markdown.StartsWith("```") && markdown.EndsWith("```"))
            {
                markdown = markdown.Substring(3, markdown.Length - 6).Trim();
            }

            return markdown;
        }

        /// <summary>
        /// 将详细计划 Markdown 保存到磁盘。
        /// 优先保存到解决方案根目录，若不可用则保存到 %TEMP%。
        /// </summary>
        /// <returns>保存的文件绝对路径</returns>
        private static async Task<string> SavePlanMarkdownAsync(string markdown, AgentContext context)
        {
            // 确定保存目录
            string? dir = null;
            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                // 如果 SolutionPath 是 .sln 文件，取其目录
                if (File.Exists(context.SolutionPath))
                    dir = Path.GetDirectoryName(context.SolutionPath);
                else if (Directory.Exists(context.SolutionPath))
                    dir = context.SolutionPath;
            }

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Path.GetTempPath();

            // 文件名：固定为 plan.md（每次覆盖，避免积累）
            string filePath = Path.Combine(dir, "plan.md");

            // 写入文件头
            var sb = new StringBuilder();
            sb.AppendLine($"# 📋 实现计划");
            sb.AppendLine();
            sb.AppendLine($"> **生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"> **解决方案**: {context.SolutionPath ?? "（无）"}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(markdown);

            string fullContent = sb.ToString();
            await Task.Run(() => File.WriteAllText(filePath, fullContent, Encoding.UTF8));

            return filePath;
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
