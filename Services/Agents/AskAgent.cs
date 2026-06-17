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

        // ExploreAgent 引用由基类 BaseAgent.ExploreAgent 提供，
        // 由 AgentFactory 统一注入。不再使用 new 隐藏基类属性，
        // 避免 BaseAgent.ExecuteToolAsync 中 ExploreHandler 注入失败。

        #region Agent Definition

        /// <summary>
        /// Ask Agent 工具集 — 保留基础代码库读取能力和子代理委派能力。
        /// 简单查找（类/方法/文件/内容）直接用内置工具；
        /// 深度多步骤探索才委派给 ExploreAgent。
        /// </summary>
        public static readonly string[] AskTools = new[]
        {
            // ── 简单搜索与读取（无需委派 Explore）──
            "symbol_search",      // 符号搜索（类/方法/接口/属性定义）
            "file_search",        // Glob 文件名搜索
            "grep_search",        // 文本/正则内容搜索
            "read_file",          // 读取文件
            "list_dir",           // 浏览目录结构
            "get_errors",         // 获取编译错误
            // ── 委派与联网 ──
            "runSubagent",        // 深度探索任务委派给 ExploreAgent
            "fetch_webpage",      // 联网搜索（无需代码库访问）
            "request_handoff",    // 移交任务给其他 Agent
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
                        Label = LocalizationService.Instance["agent.ask.handoffEditLabel"],
                        TargetAgent = AgentType.Edit,
                        Prompt = LocalizationService.Instance["agent.ask.handoffEditPrompt"],
                        AutoSend = true,
                        ShowContinueOn = false,
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
            return LocalizationService.Instance["agent.ask.systemPromptFragment"]
                + AiPrompts.AskAgentPromptFragment;
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
            // ── 清理上次执行的移交状态，防止跨调用污染 ──
            PendingHandoffRequest = null;

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
                var thinkingBuilder = new StringBuilder();
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 4096,
                    onThinking: (thinking) =>
                    {
                        thinkingBuilder.Append(thinking);
                        context.OnThinkingChunk?.Invoke(thinking);
                    },
                    onContent: (content) =>
                    {
                        context.OnContentChunk?.Invoke(content);
                    },
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("INFO", toolSummary);
                    });

                // ── 保存推理内容，供 UI 渲染思考面板 ──
                if (thinkingBuilder.Length > 0)
                    result.ReasoningContent = thinkingBuilder.ToString();

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
        /// 
        /// 优先级：
        /// 1. 从会话记忆中读取 EditAgent 写入的步骤摘要文件（step-NN-summary.md、plan-final-summary.md）
        /// 2. 回退到 plan.ChangedFiles 和 step.ResultSummary 直接构建摘要
        /// 3. 最后可选：用一次无工具 AI 调用对聚合结果进行自然语言润色
        /// 
        /// 不再调用工具（read_file 等），直接基于已有数据合成摘要。
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

                // ── 第1层：尝试从会话记忆读取步骤摘要 ──
                string memorySummary = await ReadStepSummariesFromMemoryAsync(context);

                // ── 第2层：直接从 plan 数据构建结构化摘要（无需工具调用）──
                string directSummary = BuildDirectSummaryMarkdown(plan, memorySummary);

                // ── 第3层（可选）：用一次无工具 AI 调用润色自然语言部分 ──
                // 🔑 v1.1.11：消费 ForwardedMessages（摘要生成是终端步骤，不需要完整对话历史），
                // 避免 PolishSummaryWithAiAsync 通过 BuildContextAwareMessages 注入大量历史消息。
                string aiSummary = string.Empty;
                bool hasMeaningfulChanges = plan.ChangedFiles.Count > 0
                    || plan.Steps.Any(s => s.Status == AgentStepStatus.Completed && !string.IsNullOrWhiteSpace(s.ResultSummary));
                if (hasMeaningfulChanges)
                {
                    context.ForwardedMessages = null;
                    // 将步骤语义描述追加到 directSummary，确保润色 AI 有足够上下文
                    string enrichedSummary = AppendStepDescriptions(directSummary, plan);
                    aiSummary = await PolishSummaryWithAiAsync(enrichedSummary, context);
                }

                // ── 构建最终 Markdown 总结（总是使用 BuildSummaryMarkdown，它有内置回退）──
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
        /// 从会话记忆中读取 EditAgent 写入的步骤摘要文件。
        /// 返回聚合后的 Markdown 文本，如果无记忆则返回空字符串。
        /// </summary>
        private async Task<string> ReadStepSummariesFromMemoryAsync(AgentContext context)
        {
            if (MemoryService == null) return string.Empty;

            try
            {
                string sessionId = BuiltInTools?.CurrentSessionId;
                var sb = new StringBuilder();

                // 先尝试读取最终摘要
                try
                {
                    var finalResult = await MemoryService.ViewAsync(
                        MemoryScope.Session, "plan-final-summary.md",
                        sessionId, context.SolutionPath);
                    if (!string.IsNullOrWhiteSpace(finalResult.Content))
                    {
                        sb.AppendLine(finalResult.Content);
                        return sb.ToString().TrimEnd();
                    }
                }
                catch { /* 文件不存在，继续读取分步摘要 */ }

                // 逐个读取步骤摘要
                for (int i = 1; i <= 50; i++) // 安全上限 50 步
                {
                    string fileName = $"step-{i:D2}-summary.md";
                    try
                    {
                        var stepResult = await MemoryService.ViewAsync(
                            MemoryScope.Session, fileName,
                            sessionId, context.SolutionPath);
                        if (!string.IsNullOrWhiteSpace(stepResult.Content))
                        {
                            sb.AppendLine(stepResult.Content);
                            sb.AppendLine();
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        break; // 没有更多步骤文件
                    }
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Memory] 读取步骤摘要失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 直接从 plan 数据和记忆摘要构建结构化 Markdown 总结（无需 AI 工具调用）。
        /// </summary>
        private static string BuildDirectSummaryMarkdown(AgentTaskPlan plan, string memorySummary)
        {
            var L = LocalizationService.Instance;

            if (plan.IsCancelled)
                return L["edit.summary.cancelled"];

            var sb = new StringBuilder();
            sb.AppendLine(L["edit.summary.complete"]);
            sb.AppendLine();
            sb.AppendLine($"**{L["edit.summary.taskLabel"]}**: {plan.Title}");
            if (plan.ChangedFiles.Count > 0)
                sb.AppendLine($"**{L["edit.summary.fileCount"]}**: {plan.ChangedFiles.Count}");

            // ── 步骤执行情况 ──
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int skipped = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);
            sb.AppendLine($"**步骤**: {completed}/{plan.Steps.Count} 完成"
                + (failed > 0 ? $"，{failed} 失败" : "")
                + (skipped > 0 ? $"，{skipped} 跳过" : ""));
            sb.AppendLine();

            // ── 如果有记忆中的步骤摘要，优先展示 ──
            if (!string.IsNullOrWhiteSpace(memorySummary))
            {
                sb.AppendLine($"### {L["edit.summary.changeSummary"]}");
                sb.AppendLine();
                sb.AppendLine(memorySummary);
                sb.AppendLine();
            }
            else
            {
                // ── 回退：从 plan 数据直接生成步骤摘要 ──
                sb.AppendLine($"### {L["edit.summary.changeSummary"]}");
                sb.AppendLine();
                foreach (var step in plan.Steps)
                {
                    string icon = step.Status switch
                    {
                        AgentStepStatus.Completed => "✅",
                        AgentStepStatus.Failed => "❌",
                        AgentStepStatus.Skipped => "⏭",
                        _ => "⬜",
                    };
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary
                        : "(无)";
                    sb.AppendLine($"- {icon} **{step.Title}**: {summary}");

                    // 当 ResultSummary 仅为机械统计时，补充 Description 展示实际修改内容
                    if (!string.IsNullOrWhiteSpace(step.Description)
                        && (string.IsNullOrWhiteSpace(step.ResultSummary)
                            || step.ResultSummary.StartsWith("修改了 ")
                            || step.ResultSummary.StartsWith("Modified ")))
                    {
                        string desc = step.Description.Length > 120
                            ? step.Description.Substring(0, 117) + "..."
                            : step.Description;
                        sb.AppendLine($"  > {desc}");
                    }
                }
                sb.AppendLine();
            }

            // ── 文件变更统计 ──
            if (plan.ChangedFiles.Count > 0)
            {
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        DisplayPath = g.First().FilePath,
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                        Description = g.Select(c => c.BriefDescription)
                            .FirstOrDefault(d => !string.IsNullOrEmpty(d)
                                && !d!.Contains("(Patch)")
                                && !d!.Contains("(InsertEdit)")
                                && !d!.Contains("(CreateFile)")),
                    })
                    .ToList();

                sb.AppendLine(LocalizationService.Instance["agent.panel.fileChangeStats"]);
                sb.AppendLine();
                sb.AppendLine(L["agent.panel.fileChangeTableHeader"]);
                sb.AppendLine("|------|------|------|");
                foreach (var change in mergedFiles)
                {
                    string delta = $"{(change.LinesAdded > 0 ? $"+{change.LinesAdded}" : "")}"
                        + $"{(change.LinesRemoved > 0 ? $" -{change.LinesRemoved}" : "")}";
                    string desc = change.Description ?? (change.LinesAdded > 0 && change.LinesRemoved == 0 ? L["agent.panel.fileChangeAdded"]
                        : change.LinesRemoved > 0 && change.LinesAdded == 0 ? L["agent.panel.fileChangeDeleted"] : L["agent.panel.fileChangeModified"]);
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
        /// 用一次无工具 AI 调用对直接摘要进行自然语言润色。
        /// 仅在记忆摘要非空且存在文件变更时调用。
        /// 
        /// 🔑 v1.1.11：通过 BuildContextAwareMessages + handoff 路径构建消息，
        /// 而非手动拼接 raw messages。润色专用指令作为 AskAgent 的子任务 prompt
        /// 注入在 Agent 系统提示之后、用户消息之前。
        /// 
        /// 消息结构：
        ///   [0] SharedImmutablePrefix      ← 始终命中缓存
        ///   [1] AskAgent.Definition.SystemPrompt  ← 与正常 Ask 调用共享
        ///   [2] SummaryPolishSystemPrompt  ← 润色子任务指令
        ///   [3] user: 待润色摘要内容       ← 每次不同
        /// 
        /// 前置条件：调用方已清空 context.ForwardedMessages，避免注入完整对话历史。
        /// </summary>
        private async Task<string> PolishSummaryWithAiAsync(string directSummary, AgentContext context)
        {
            try
            {
                var ct = context.CancellationToken;

                // ── 润色子任务指令作为额外 system 消息注入 ──
                var polishInstructions = new List<ChatApiMessage>
                {
                    new ChatApiMessage
                    {
                        Role = "system",
                        Content = AiPrompts.SummaryPolishSystemPrompt
                    }
                };

                // ── 通过 BuildContextAwareMessages 走标准 handoff 路径 ──
                // maxRecentTurns:0 → 不注入对话历史，保持润色调用轻量
                var messages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    string.Format(AiPrompts.SummaryPolishUserPrompt, directSummary),
                    polishInstructions,
                    maxRecentTurns: 0);

                // 使用无工具调用的简单 API 调用
                string result = await CallAiWithToolLoopAsync(
                    messages,
                    GetWorkspaceRoot(context),
                    ct,
                    maxTokens: 1024,
                    toolWhitelist: new List<string>()); // 空白名单 = 不允许任何工具

                result = StripToolCallMarkers(result);
                return result?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[AskAgent] AI 润色失败，使用直接摘要: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 构建包含文件和项目上下文的 prompt。
        /// </summary>
        /// <summary>
        /// 将 plan 中每个步骤的 Description 追加到摘要文本中，
        /// 确保 AI 润色时能看到具体的任务语义（而非仅文件统计）。
        /// </summary>
        private static string AppendStepDescriptions(string summary, AgentTaskPlan plan)
        {
            var meaningfulSteps = plan.Steps
                .Where(s => !string.IsNullOrWhiteSpace(s.Description))
                .ToList();
            if (meaningfulSteps.Count == 0) return summary;

            var sb = new StringBuilder(summary);
            sb.AppendLine();
            sb.AppendLine("## 步骤任务描述（润色参考）");
            foreach (var step in meaningfulSteps)
            {
                sb.AppendLine($"- 步骤 {step.Index}: {step.Description}");
            }
            return sb.ToString();
        }

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
            sb.AppendLine();

            // ── AI 生成的文字总结（功能性摘要，放在最前面）──
            if (!string.IsNullOrWhiteSpace(aiSummary))
            {
                sb.AppendLine($"### {L["edit.summary.changeSummary"]}");
                sb.AppendLine();
                sb.AppendLine(aiSummary);
                sb.AppendLine();
            }
            else if (plan.Steps.Count > 0)
            {
                // ── AI 润色未产出时，从步骤数据构建基础摘要 ──
                var completedSteps = plan.Steps
                    .Where(s => s.Status == AgentStepStatus.Completed && !string.IsNullOrWhiteSpace(s.ResultSummary))
                    .ToList();
                if (completedSteps.Count > 0)
                {
                    sb.AppendLine($"### {L["edit.summary.changeSummary"]}");
                    sb.AppendLine();
                    foreach (var step in completedSteps)
                    {
                        sb.AppendLine($"- ✅ **{step.Title}**: {step.ResultSummary}");
                        // 当 ResultSummary 仅为机械统计时，补充 Description
                        if (!string.IsNullOrWhiteSpace(step.Description)
                            && (step.ResultSummary!.StartsWith("修改了 ") || step.ResultSummary.StartsWith("Modified ")))
                        {
                            string desc = step.Description.Length > 120
                                ? step.Description.Substring(0, 117) + "..."
                                : step.Description;
                            sb.AppendLine($"  > {desc}");
                        }
                    }
                    sb.AppendLine();
                }
            }

            // ── 步骤执行情况 ──
            if (plan.Steps.Count > 0)
            {
                sb.AppendLine(L["edit.summary.stepDetails"]);
                sb.AppendLine();
                sb.AppendLine($"**{L["edit.summary.stepCount"]}**: {plan.Steps.Count(s => s.Status == AgentStepStatus.Completed)} / {plan.Steps.Count}");
                sb.AppendLine();
                foreach (var step in plan.Steps)
                {
                    string icon = step.Status switch
                    {
                        AgentStepStatus.Completed => "✅",
                        AgentStepStatus.Failed => "❌",
                        AgentStepStatus.Skipped => "⏭️",
                        _ => "⬜",
                    };
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary
                        : "(无)";
                    sb.AppendLine($"- {icon} **{step.Title}**: {summary}");
                }
                sb.AppendLine();
            }

            // ── 文件变更统计（折叠为附录）──
            if (plan.ChangedFiles.Count > 0)
            {
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
                sb.AppendLine(L["agent.panel.fileChangeTableHeader"]);
                sb.AppendLine("|------|------|------|");
                foreach (var change in mergedFiles)
                {
                    string delta = $"{(change.LinesAdded > 0 ? $"+{change.LinesAdded}" : "")}"
                        + $"{(change.LinesRemoved > 0 ? $" -{change.LinesRemoved}" : "")}";
                    string desc = change.Description ?? (change.LinesAdded > 0 && change.LinesRemoved == 0 ? L["agent.panel.fileChangeAdded"] : change.LinesRemoved > 0 && change.LinesAdded == 0 ? L["agent.panel.fileChangeDeleted"] : L["agent.panel.fileChangeModified"]);
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
