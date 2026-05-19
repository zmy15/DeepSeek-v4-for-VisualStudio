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
                AddLog("INFO", "[Plan] 跳过对齐阶段（需求明确，无需澄清）");

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
                    AddLog("WARN", L["agent.plan.noValidSteps"]);
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
                    $"{L["agent.plan.discoveryPrompt"]}\n\n" +
                    $"{L["plan.userTask"]}: {userMessage}\n\n" +
                    (string.IsNullOrEmpty(context.SolutionPath) ? ""
                        : $"Solution path: {context.SolutionPath}\n\n") +
                    $"{L["agent.plan.discoveryPromptTail"]}";

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
                AddLog("WARN", string.Format(LocalizationService.Instance["agent.plan.discoverError"], ex.Message));
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
                    Content = LocalizationService.Instance["agent.plan.discoveryFallback"] + "\n\n" + discoveryContext
                });
            }
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                string truncated = context.FileContext.Length > 2000
                    ? context.FileContext.Substring(0, 2000) + "\n" + LocalizationService.Instance["agent.plan.truncatedSuffix"]
                    : context.FileContext;
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = LocalizationService.Instance["agent.plan.fileContextHeader"] + "\n\n" + truncated
                });
            }

            // ── 用户消息保持简洁（只有任务描述 + 指令），不含动态内容 ──
            string planPrompt = BuildPlanCreationPrompt(userMessage, context);

            AddLog("INFO", "[Plan] 正在调用 AI 生成计划 JSON（可能需要 30-60 秒）...");
            string json = await CallAiLongAsync(
                Definition.SystemPrompt, planPrompt, extraSystemMessages, ct,
                maxTokens: 4096, toolChoice: "none");
            AddLog("INFO", "[Plan] AI 响应已收到，正在解析计划...");

            // ── 诊断：记录原始响应用于调试 JSON 解析失败 ──
            string rawResponse = json;
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
                // ── 诊断日志：记录原始响应片段以便排查 ──
                string truncated = rawResponse.Length > 300
                    ? rawResponse.Substring(0, 300) + "..."
                    : rawResponse;
                AddLog("WARN", string.Format(LocalizationService.Instance["agent.plan.jsonParseFailed"], ex.Message));
                AddLog("INFO", $"[Plan] JSON 解析失败的原始响应 (前300字符): {truncated}");
            }

            // 回退：单步计划
            return new AgentTaskPlan
            {
                Intent = AgentIntent.CodeChange,
                Title = LocalizationService.Instance["agent.plan.executeChangesLabel"],
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
            var L = LocalizationService.Instance;

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
                    ? discoveryContext.Substring(0, 4000) + "\n" + L["agent.plan.truncatedSuffix"]
                    : discoveryContext;
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = L["plan.md.codebaseFindings"] + "\n\n" + truncated
                });
            }
            extraSystemMessages.Add(new ChatApiMessage
            {
                Role = "system",
                Content = L["plan.md.jsonPlan"] + "\n```json\n" + planJson + "\n```"
            });

            // ── 用户消息保持简洁 ──
            var prompt = new StringBuilder();
            prompt.AppendLine(L["plan.md.userTask"]);
            prompt.AppendLine(userMessage);
            prompt.AppendLine();
            prompt.AppendLine(L["plan.md.instructions"]);
            prompt.AppendLine(L["plan.md.generatePrompt"]);
            prompt.AppendLine(L["plan.md.mustContainSections"]);
            prompt.AppendLine();
            prompt.AppendLine(L["plan.md.section1Title"]);
            prompt.AppendLine(L["plan.md.section1Desc"]);
            prompt.AppendLine();
            prompt.AppendLine(L["plan.md.section2Title"]);
            prompt.AppendLine(L["plan.md.section2Desc1"]);
            prompt.AppendLine(L["plan.md.section2Desc2"]);
            prompt.AppendLine();
            prompt.AppendLine(L["plan.md.section3Title"]);
            prompt.AppendLine(L["plan.md.section3Intro"]);
            prompt.AppendLine(L["plan.md.section3Goal"]);
            prompt.AppendLine(L["plan.md.section3Files"]);
            prompt.AppendLine(L["plan.md.section3Design"]);
            prompt.AppendLine(L["plan.md.section3Methods"]);
            prompt.AppendLine();
            prompt.AppendLine(L["agent.panel.fileChangeSummary"]);
            prompt.AppendLine(L["plan.md.section4Desc"]);
            prompt.AppendLine();
            prompt.AppendLine(L["agent.panel.dependencies"]);
            prompt.AppendLine(L["plan.md.section5Desc1"]);
            prompt.AppendLine(L["plan.md.section5Desc2"]);
            prompt.AppendLine();
            prompt.AppendLine(L["agent.panel.verification"]);
            prompt.AppendLine(L["plan.md.section6Desc"]);
            prompt.AppendLine();
            prompt.AppendLine(L["plan.md.notes"]);
            prompt.AppendLine(L["plan.md.note1"]);
            prompt.AppendLine(L["plan.md.note2"]);
            prompt.AppendLine(L["plan.md.note3"]);
            prompt.AppendLine(L["plan.md.note4"]);

            AddLog("INFO", "[Plan] 正在调用 AI 生成详细计划文档 plan.md（可能需要 60-120 秒）...");
            string markdown = await CallAiLongAsync(
                Definition.SystemPrompt, prompt.ToString(), extraSystemMessages, ct,
                maxTokens: 4096, toolChoice: "none");
            AddLog("INFO", "[Plan] plan.md 内容已生成，正在保存...");

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
            var L = LocalizationService.Instance;
            var sb = new StringBuilder();
            sb.AppendLine(L["plan.md.savedTitle"]);
            sb.AppendLine();
            sb.AppendLine(string.Format(L["plan.md.savedGeneratedAt"], DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine(string.Format(L["plan.md.savedSolution"], context.SolutionPath ?? "（无）"));
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
