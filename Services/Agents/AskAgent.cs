using DeepSeek_v4_for_VisualStudio.Models;
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
    /// Ask Agent — 纯问答代理。
    /// 
    /// 职责：
    /// - 回答技术问题
    /// - 解释代码逻辑
    /// - 讨论方案和架构
    /// - 绝不修改任何文件
    /// 
    /// 这是默认的 fallback Agent，当用户意图不是代码修改时使用。
    /// </summary>
    public class AskAgent : BaseAgent
    {
        public AskAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask) { }

        /// <summary>
        /// ExploreAgent 引用，由 AgentDispatcher 注入。
        /// </summary>
        public new ExploreAgent? ExploreAgent { get; set; }

        #region Agent Definition

        /// <summary>
        /// Ask Agent 工具集 — 仅保留记忆和子代理委派能力。
        /// 所有代码库读取/搜索操作必须通过 runSubagent 委派给 ExploreAgent。
        /// </summary>
        public static readonly string[] AskTools = new[]
        {
            "runSubagent",       // 委派探索任务给 ExploreAgent
            "fetch_webpage",      // 联网搜索（无需代码库访问）
            "memory",             // 记忆管理
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                Description = LocalizationService.Instance["agent.ask.description"],
                ArgumentHint = LocalizationService.Instance["agent.ask.argumentHint"],
                UserInvocable = true,
                DisableModelInvocation = false,
                AllowedTools = new List<string>(AskTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>
                {
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.ask.handoffExploreLabel"],
                        TargetAgent = AgentType.Explore,
                        Prompt = LocalizationService.Instance["agent.ask.handoffExplorePrompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.ask.handoffEditLabel"],
                        TargetAgent = AgentType.Edit,
                        Prompt = LocalizationService.Instance["agent.ask.handoffEditPrompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.ask.handoffPlanLabel"],
                        TargetAgent = AgentType.Plan,
                        Prompt = LocalizationService.Instance["agent.ask.handoffPlanPrompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.ask.handoffBuildLabel"],
                        TargetAgent = AgentType.Build,
                        Prompt = LocalizationService.Instance["agent.ask.handoffBuildPrompt"],
                        AutoSend = true,
                        ShowContinueOn = true,
                    },
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return CommonSystemPromptPrefix + LocalizationService.Instance["agent.ask.systemPromptFragment"]
                + "\n\n## 代码库探索（runSubagent 工具）\n"
                + "你 **没有** 直接读取或搜索代码库文件的工具。当需要了解项目代码时，必须使用 `runSubagent` 工具委派给 Explore 子代理：\n"
                + "```json\n"
                + "{ \"agentName\": \"Explore\", \"prompt\": \"描述要探索的内容和详细程度 (quick/medium/thorough)\", \"description\": \"3-5词简短摘要\" }\n"
                + "```\n"
                + "- Explore 子代理会返回基于实际文件内容的分析结果\n"
                + "- 你基于 Explore 的返回结果回答问题，附上引用来源\n"
                + "- 需要探索代码库时**必须**先调用 runSubagent，不要凭训练数据猜测\n"
                + "- **Git 查询**（查看状态、日志、差异等）也通过 Explore 子代理进行，Explore 具有 git 只读工具\n\n"
                + "## 记忆系统 (memory 工具)\n"
                + "你拥有一个持久化记忆系统，通过 `memory` 工具管理三层记忆：\n"
                + "- **用户记忆** (`/memories/`): 跨所有工作区持久化，用于存储用户偏好、编码习惯、常用命令等\n"
                + "- **会话记忆** (`/memories/session/`): 当前对话内有效，存储临时上下文和进行中笔记\n"
                + "- **仓库记忆** (`/memories/repo/`): 当前解决方案内有效，存储项目约定、构建命令、架构决策等\n"
                + "建议：\n"
                + "- 用户说出明确的偏好或习惯时 → 主动记录到用户记忆\n"
                + "- 发现重要的项目特定信息时 → 记录到仓库记忆\n"
                + "- 长时间任务中积累的中间上下文 → 记录到会话记忆\n"
                + "- 开始新对话时前先 `memory view` 查看用户记忆和仓库记忆，了解已有知识";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Ask Agent 执行入口。
        /// 支持两种模式：
        /// 1. 普通问答：直接将用户问题发送给 AI 并返回回答。
        /// 2. 摘要生成：接收 Edit Agent 的 Handoff，生成代码变更总结。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 检测是否为 Edit Agent 的摘要 Handoff ──
            if (IsSummaryHandoff(context))
            {
                return await ExecuteSummaryAsync(userMessage, context);
            }

            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.askStarted"], userMessage.Truncate(100)));

            var result = new AgentResult
            {
                AgentType = AgentType.Ask,
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 使用 BuildContextAwareMessages 构建消息（优先 ContextManager 实时历史）──
                string contextualPrompt = BuildContextualPrompt(userMessage, context);
                var messages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    contextualPrompt,
                    maxRecentTurns: 10);

                // ── 使用工具调用循环（支持 runSubagent 委派探索任务 + request_handoff 移交）──
                string workspaceRoot = GetWorkspaceRoot(context);
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 4096,
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("INFO", toolSummary);
                    });

                // ── 检查 AI 是否通过 request_handoff 请求了移交 ──
                if (PendingHandoffRequest != null)
                {
                    result.Handoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                    result.Content = aiResponse;
                    AddLog("INFO", $"🔄 移交 → {PendingHandoffRequest.TargetAgent}");
                }
                else
                {
                    result.Content = aiResponse;
                }

                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.askDone"], aiResponse.Length));
                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance["agent.log.askCancelled"];
                AddLog("WARN", LocalizationService.Instance["agent.log.askCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(LocalizationService.Instance["agent.log.askFailed"], ex.Message));
            }

            return result;
        }

        /// <summary>
        /// 检测是否为 Edit Agent 移交的摘要生成请求。
        /// 当 context 中带有已完成的计划时，认为是摘要 Handoff。
        /// 即使 ChangedFiles 为空（如仅执行了 git add/commit 等版本控制操作），
        /// 也应生成摘要告知用户执行结果，而非进入普通问答模式。
        /// </summary>
        private static bool IsSummaryHandoff(AgentContext context)
        {
            return context.ActivePlan != null
                && context.ActivePlan.IsCompleted;
        }

        /// <summary>
        /// 执行代码变更摘要生成（接收 Edit Agent 的 Handoff）。
        /// 生成 AI 文字总结 + 文件变更统计表格 + 缓存命中率。
        /// </summary>
        private async Task<AgentResult> ExecuteSummaryAsync(string userMessage, AgentContext context)
        {
            var L = LocalizationService.Instance;
            AddLog("INFO", L["agent.log.askSummaryStarted"]);

            var result = new AgentResult
            {
                AgentType = AgentType.Ask,
                Success = true,
            };

            try
            {
                var plan = context.ActivePlan!;
                result.Plan = plan;
                result.FileChanges = plan.ChangedFiles;

                // ── 生成 AI 文字总结（允许读取文件以提供更准确的摘要）──
                string aiSummary = await GenerateChangeSummaryAsync(plan, context);

                // ── 构建最终 Markdown 总结 ──
                result.Content = BuildSummaryMarkdown(plan, aiSummary);

                AddLog("INFO", string.Format(L["agent.log.askSummaryDone"], result.Content.Length));
                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = L["agent.log.askCancelled"];
                AddLog("WARN", L["agent.log.askCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(L["agent.log.askFailed"], ex.Message));
            }

            return result;
        }

        /// <summary>
        /// 构建包含文件和项目上下文的 prompt。
        /// </summary>
        private static string BuildContextualPrompt(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"[当前解决方案: {context.SolutionPath}]");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine("[用户提供的文件内容]");
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            sb.AppendLine("[用户问题]");
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        #endregion

        #region Summary Generation

        /// <summary>
        /// 构建代码变更总结 Markdown（包含文件变更统计、步骤详情、缓存命中率等）。
        /// </summary>
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
                        Description = g.Select(c => c.BriefDescription).FirstOrDefault(d => !string.IsNullOrEmpty(d) && !d!.Contains("(Patch)") && !d!.Contains("(InsertEdit)") && !d!.Contains("(CreateFile)")),
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
        private async Task<string> GenerateChangeSummaryAsync(AgentTaskPlan plan, AgentContext context)
        {
            if (plan.ChangedFiles.Count == 0) return string.Empty;
            var ct = context.CancellationToken;

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
                        ? step.ResultSummary!
                        : "(无)";
                    summaryPrompt.AppendLine($"- {status} {step.Title}: {summary}");
                }
                summaryPrompt.AppendLine();

                // 语言跟随：根据当前语言选择摘要输出语言
                string langInstruction = AiPrompts.ChangeSummaryUserInstruction;
                summaryPrompt.AppendLine(langInstruction);

                // ── 构建消息（带工具循环）──
                string shortSystemPrompt = AiPrompts.ChangeSummarySystemPrompt;

                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = shortSystemPrompt },
                    new ChatApiMessage { Role = "user", Content = summaryPrompt.ToString() }
                };

                string workspaceRoot = GetWorkspaceRoot(context);

                string result = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 4096,
                    maxToolRounds: 3,
                    toolWhitelist: new List<string>(AskTools));

                // ── 安全剥离：防止 AI 意外输出工具调用标记或思考过程 ──
                result = StripToolCallMarkers(result);
                return result?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AskAgent] 生成变更摘要失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 剥离 AI 输出中意外包含的工具调用标记和思考过程文本。
        /// 当 toolChoice="none" 时 AI 仍可能输出工具调用意图文本。
        /// 处理 DSML invoke/parameter、管道分隔 tool_calls、以及中文思考前缀。
        /// </summary>
        private static string StripToolCallMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 移除 <invoke name="...">...</invoke> DSML 格式工具调用块
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"<\s*invoke\s+name=""[^""]+""[^>]*>.*?</\s*invoke\s*>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除 <parameter name="...">...</parameter> 残留片段
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"<\s*parameter\s+name=""[^""]+""[^>]*>.*?</\s*parameter\s*>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除残留的 invoke/parameter 开/闭标签
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"</?\s*(?:invoke|parameter)\s*[^>]*>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除 <|tool_calls|>...</|tool_calls|> XML 片段
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"<\|[^>]*tool_calls?[^>]*\|>.*?</\|[^>]*tool_calls?[^>]*\|>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除单个工具调用标签
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"</?\|[^>]*tool_calls?[^>]*\|>",
                string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 移除思考前缀（"让我先查看..."等，AI 思考但没有真正调用工具）
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^(让我(先)?查看|让我检查|我需要先|我先用|让我读取|我需要读取).*?[。\n]",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            return text.Trim();
        }

        #endregion
    }
}
