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
        /// Ask Agent 工具集 — 仅保留记忆和子代理委派能力。
        /// 所有代码库读取/搜索操作必须通过 runSubagent 委派给 ExploreAgent。
        /// </summary>
        public static readonly string[] AskTools = new[]
        {
            "runSubagent",        // 委派探索任务给 ExploreAgent
            "fetch_webpage",      // 联网搜索（无需代码库访问）
            "request_handoff",    // 移交任务给其他 Agent（Edit/Plan/Explore/Build）
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
                + "\n\n## 代码库探索（runSubagent 工具）\n"
                + "你 **没有** 直接读取或搜索代码库文件的工具。当需要了解项目代码时，必须使用 `runSubagent` 工具委派给 Explore 子代理：\n"
                + "```json\n"
                + "{ \"agentName\": \"Explore\", \"prompt\": \"描述要探索的内容和详细程度 (quick/medium/thorough)\", \"description\": \"3-5词简短摘要\" }\n"
                + "```\n"
                + "- Explore 子代理会返回基于实际文件内容的分析结果\n"
                + "- 你基于 Explore 的返回结果回答问题，附上引用来源\n"
                + "- 需要探索代码库时**必须**先调用 runSubagent，不要凭训练数据猜测\n"
                + "- **Git 查询**（查看状态、日志、差异等）也通过 Explore 子代理进行，Explore 具有 git 只读工具\n"
                + "- **MCP 外部工具**（如数据库查询、API 检索等）同样通过 Explore 子代理访问——你无法直接调用 MCP 只读工具，使用 runSubagent 委派即可\n\n"
                + "## 记忆系统 (memory 工具)\n"
                + "你拥有一个持久化记忆系统，通过 `memory` 工具管理三层记忆：\n"
                + "- **用户记忆** (`/memories/`): 跨所有工作区持久化，用于存储用户偏好、编码习惯、常用命令等\n"
                + "- **会话记忆** (`/memories/session/`): 当前对话内有效，存储临时上下文和进行中笔记\n"
                + "- **仓库记忆** (`/memories/repo/`): 当前解决方案内有效，存储项目约定、构建命令、架构决策等\n"
                + "建议：\n"
                + "- 用户说出明确的偏好或习惯时 → 主动记录到用户记忆\n"
                + "- 发现重要的项目特定信息时 → 记录到仓库记忆\n"
                + "- 长时间任务中积累的中间上下文 → 记录到会话记忆\n"
                + "- 开始新对话时前先 `memory view` 查看用户记忆和仓库记忆，了解已有知识\n\n"
                + "## 任务移交（request_handoff 工具）\n"
                + "当用户的请求超出你的职责范围（纯问答）时，按以下优先级使用 `request_handoff` 工具移交：\n\n"
                + "### 优先级 1（最高）：复杂问题 → Plan Agent\n"
                + "- **需要设计架构/规划方案/分析技术选型/多步骤复杂任务** → 移交给 `Plan` Agent\n"
                + "- **涉及多个文件/模块的改动、需要先研究再决定的开放性任务** → 移交给 `Plan` Agent\n"
                + "- **用户明确要求'制定计划'/'规划方案'/'设计架构'** → 移交给 `Plan` Agent\n\n"
                + "### 优先级 2：简单修改 → Edit Agent\n"
                + "- **简单直接的代码修改/修复小 bug/添加小功能/单文件重构** → 移交给 `Edit` Agent\n"
                + "- **Git 写操作（commit/push/分支切换/stash 等）** → 移交给 `Edit` Agent\n"
                + "- **执行终端命令（dotnet build/npm install 等）** → 移交给 `Edit` Agent\n"
                + "- **需要 MCP 写工具（如部署、数据库写入等）** → 移交给 `Edit` 或 `Build` Agent\n\n"
                + "### 优先级 3：报错修复 → Build Agent\n"
                + "- **遇到编译错误/构建失败/链接报错需要修复（含代码修改）** → 移交给 `Build` Agent（Build 可以修改代码来修复编译问题）\n"
                + "- **用户明确要求'修复报错'/'fix errors'/'解决编译问题'** → 直接移交给 `Build` Agent，不要用 Explore 探索\n\n"
                + "### 底线：探索 → Explore 子代理（不要移交！）\n"
                + "- **探索代码库 / Git 只读查询（status/diff/log）/ MCP 只读工具** → 不要移交！使用 `runSubagent` 委派给 Explore 子代理即可\n"
                + "- Explore 子代理**仅服务于你的问答操作**——它是你的眼睛和耳朵，不是独立的执行者\n"
                + "- ⚠️ **关键规则**：如果任务本身是复杂/多步骤的（如\"实现X功能\"/\"完成Y模块\"/\"修复Z系统\"），"
                + "**直接移交 Plan Agent**，不要自己先探索代码库。Plan Agent 有自己的 Explore 子代理，会自行研究。"
                + "你探索完再移交是浪费时间和 token。\n\n"
                + "### 其他规则\n"
                + "- 收到复杂任务时**第一时间判断并移交**，不要先探索再判断\n"
                + "- 简单问答/概念解释/技术讨论 → 你直接回答，不要移交";
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
                string aiSummary = string.Empty;
                if (!string.IsNullOrWhiteSpace(memorySummary) && plan.ChangedFiles.Count > 0)
                {
                    aiSummary = await PolishSummaryWithAiAsync(directSummary, context);
                }

                // ── 构建最终 Markdown 总结 ──
                result.Content = string.IsNullOrWhiteSpace(aiSummary)
                    ? directSummary
                    : BuildSummaryMarkdown(plan, aiSummary);

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
        /// </summary>
        private async Task<string> PolishSummaryWithAiAsync(string directSummary, AgentContext context)
        {
            try
            {
                var ct = context.CancellationToken;
                var L = LocalizationService.Instance;

                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage
                    {
                        Role = "system",
                        Content = "你是一个代码变更总结助手。请将下方结构化摘要润色为流畅的自然语言段落（3-5句话），保持客观描述，不评价代码质量。只输出润色后的文本，不添加任何额外说明。"
                    },
                    new ChatApiMessage
                    {
                        Role = "user",
                        Content = $"请润色以下代码变更摘要为自然语言：\n\n{directSummary}"
                    }
                };

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
                sb.AppendLine(L["agent.panel.fileChangeTableHeader"]);
                sb.AppendLine("|------|------|------|");
                foreach (var change in mergedFiles)
                {
                    string delta = $"{(change.LinesAdded > 0 ? $"+{change.LinesAdded}" : "")}"
                        + $"{(change.LinesRemoved > 0 ? $" -{change.LinesRemoved}" : "")}";
                    string desc = change.Description ?? (change.LinesAdded > 0 && change.LinesRemoved == 0 ? L["agent.panel.fileChangeAdded"] : change.LinesRemoved > 0 && change.LinesAdded == 0 ? L["agent.panel.fileChangeDeleted"] : L["agent.panel.fileChangeModified"]);
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
