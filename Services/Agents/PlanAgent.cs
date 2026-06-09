using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        private ExploreAgent? _exploreAgent;

        /// <summary>
        /// ExploreAgent 引用，由 AgentFactory 注入。
        /// 用于在发现阶段并行探索代码库。
        /// 设置时自动转发 ExploreAgent 的日志和文件变更事件。
        /// </summary>
        public new ExploreAgent? ExploreAgent
        {
            get => _exploreAgent;
            set
            {
                RegisterExploreAgent(value, ref _exploreAgent);
                base.ExploreAgent = value; // 🔑 同步到基类属性，确保 ExecuteToolAsync 可见
            }
        }

        public PlanAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Plan)
        {
            var explore = new ExploreAgent(apiService);
            RegisterExploreAgent(explore, ref _exploreAgent);
            base.ExploreAgent = _exploreAgent; // 🔑 同步到基类，确保 ExecuteToolAsync 可见
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
                // 🔑 Prefix Cache 优化：全会话统一工具集。所有阶段使用相同工具白名单。
                // 深度探索通过 runSubagent 委派给 ExploreAgent；快速查阅允许直接使用只读工具。
                AllowedTools = new List<string>
                {
                    "runSubagent",               // 调用 Explore 子代理进行代码库探索
                    "VisualStudio_askQuestions",  // 向用户提问澄清
                    "memory",                     // 记忆管理
                    "list_dir",                   // 列出目录（对齐阶段快速查阅）
                    "read_file",                  // 读取文件（对齐阶段快速查阅）
                    "grep_search",                // 文本搜索（对齐阶段快速查阅）
                    "file_search",                // 文件搜索（对齐阶段快速查阅）
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
            return LocalizationService.Instance["agent.plan.systemPromptFragment"]
                + "\n\n## 🔌 MCP 外部工具\n"
                + "你可能需要访问 MCP 外部工具（如数据库查询、API 文档检索等）。\n"
                + "Plan Agent 不直接持有这些工具——请通过 `runSubagent` 委派 Explore 子代理来访问 MCP 只读工具。";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Plan Agent 执行入口。
        /// 执行发现 → 对齐 → 设计循环，产出 AgentTaskPlan。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清理上次执行的移交状态，防止跨调用污染 ──
            PendingHandoffRequest = null;

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

                // ── 阶段 1: 发现 — 通过 runSubagent 工具在 PlanAgent 对话内探索代码库 ──
                AddLog("INFO", L["agent.log.planPhaseDiscover"]);
                var (discoveryContext, messages) = await RunDiscoveryAsync(userMessage, context);

                // ── 阶段 2: 对齐 — 延续阶段 1 的对话，与用户澄清需求
                //     DeepSeek Prefix Cache 可命中整个阶段 1 的对话前缀（~80-90% 命中率）──
                AddLog("INFO", L["agent.log.planPhaseAlign"]);
                var (alignmentSummary, alignmentMessages) = await RunAlignmentAsync(userMessage, messages, context);

                // ── 阶段 3: 设计 — 延续阶段 1+2 的对话，产出实现计划
                //     DeepSeek Prefix Cache 可命中阶段 1+2 的全部历史（~90-95% 命中率）──
                AddLog("INFO", L["agent.log.planPhaseDesign"]);
                var (plan, designMessages) = await CreatePlanAsync(userMessage, discoveryContext, alignmentMessages, context);
                result.Plan = plan;

                // ═══════════════════════════════════════════════════════════
                // 缓存策略：将探索阶段已发现的文件列表传递给 EditAgent
                // 避免 EditAgent 重新扫描整个解决方案（以后会被 RAG 替代）
                // ═══════════════════════════════════════════════════════════
                if (plan != null && ExploreAgent != null && !string.IsNullOrEmpty(context.SolutionPath))
                {
                    var cachedFiles = ExploreAgent.GetCachedDiscoveredFiles(context.SolutionPath!);
                    if (cachedFiles != null && cachedFiles.Count > 0)
                    {
                        plan.DiscoveredFiles = cachedFiles;
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.planCachedFiles", cachedFiles.Count));
                    }
                }

                if (plan != null && plan.Steps.Count > 0)
                {
                    plan.Source = PlanSource.PlanAgent;  // 标记为由 PlanAgent 产出，UI 层据此创建任务面板
                    AddLog("INFO", string.Format(L["agent.log.planDone"], plan.Steps.Count, plan.Title));
                    result.Content = FormatPlanAsMarkdown(plan);

                    // ── 将对齐阶段的规划概要前置到结果最前面 ──
                    if (!string.IsNullOrWhiteSpace(alignmentSummary))
                    {
                        result.Content = "## 📋 规划概要\n\n" + alignmentSummary + "\n\n---\n\n" + result.Content;
                    }

                    // ── 生成详细 plan.md 文件 ──
                    try
                    {
                        string planMarkdown = await GenerateDetailedPlanMarkdownAsync(
                            userMessage, discoveryContext, plan, context, designMessages);
                        string planFilePath = await SavePlanMarkdownAsync(planMarkdown, context);
                        plan.PlanFilePath = planFilePath;
                        context.PlanFilePath = planFilePath;
                        AddLog("INFO", string.Format(L["agent.log.planMdSaved"], planFilePath));

                        // ── 回退步骤提取：如果 JSON 解析回退为单步计划，从 plan.md 中提取步骤 ──
                        if (plan.Steps.Count <= 1 && !string.IsNullOrEmpty(planMarkdown))
                        {
                            var extractedSteps = ExtractStepsFromPlanMarkdown(planMarkdown);
                            if (extractedSteps.Count > 1)
                            {
                                plan.Steps = extractedSteps;
                                plan.Title = plan.Title ?? extractedSteps.FirstOrDefault()?.Title ?? plan.Title ?? "";
                                AddLog("INFO", string.Format(L["agent.log.planStepsExtractedFromMd"],
                                    extractedSteps.Count, planFilePath));
                                // 更新 Handoff prompt 中的步骤数
                                result.Content = FormatPlanAsMarkdown(plan);
                            }
                        }

                        // 在结果内容中附加 plan.md 路径信息
                        result.Content += string.Format(L["agent.log.planMdAppended"], planFilePath);
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", string.Format(L["agent.log.planMdGenFailed"], ex.Message));
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
        /// 运行发现阶段：通过 runSubagent 工具在 PlanAgent 对话内探索代码库。
        ///
        /// 🔑 缓存关键：所有探索结果作为 tool 消息保留在 PlanAgent 的对话历史中，
        /// 使后续对齐和设计阶段可复用整个发现对话前缀，DeepSeek Prefix Cache 持续命中。
        /// AI 自主决定调用几次 runSubagent —— 先扫结构，再深入探索关键区域。
        /// </summary>
        /// <returns>(发现上下文文本, 含发现历史的完整消息列表)</returns>
        private async Task<(string DiscoveryContext, List<ChatApiMessage> Messages)> RunDiscoveryAsync(
            string userMessage, AgentContext context)
        {
            var L = LocalizationService.Instance;

            // ── 缓存检查：如 ExploreAgent 已有结构缓存，注入摘要跳过重复扫描 ──
            var extraSystemMessages = new List<ChatApiMessage>();
            string? structureCache = null;
            if (!string.IsNullOrEmpty(context.SolutionPath) && _exploreAgent != null)
            {
                var cachedFiles = _exploreAgent.GetCachedDiscoveredFiles(context.SolutionPath!);
                if (cachedFiles != null && cachedFiles.Count > 0)
                {
                    structureCache = BuildStructureContextFromCache(cachedFiles);
                    extraSystemMessages.Add(new ChatApiMessage { Role = "system", Content = structureCache });
                    AddLog("INFO", string.Format(L["agent.log.explorePhase1Cached"], cachedFiles.Count));
                }
            }

            // ── 构建消息列表（含对话历史，保持前缀缓存稳定）──
            string discoveryPrompt = BuildUnifiedDiscoveryPrompt(userMessage, context, structureCache);
            var messages = BuildContextAwareMessages(Definition.SystemPrompt, discoveryPrompt, extraSystemMessages);

            AddLog("INFO", L["agent.log.explorePhase1"]);

            try
            {
                // 🔑 使用统一工具白名单（不再按阶段过滤 tools JSON，由客户端拦截保证安全）
                await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot: context.SolutionPath,
                    ct: context.CancellationToken,
                    maxTokens: 8192);
            }
            catch (Exception ex)
            {
                AddLog("WARN", string.Format(L["agent.plan.discoverError"], ex.Message));
            }

            string discoveryContext = ExtractDiscoveryContextFromMessages(messages);
            int toolResultCount = messages.Count(m => m.Role == "tool" && m.Name == "runSubagent");
            AddLog("INFO", string.Format(L["agent.log.planExploreDone"], toolResultCount, toolResultCount));

            return (discoveryContext, messages);
        }

        /// <summary>
        /// 构建统一探索 prompt：AI 自主判断是否需要探索代码库。
        /// 
        /// 如果用户消息、文件上下文、对话历史中已包含足够信息，AI 应直接回复 DONE 跳过探索。
        /// 如需探索，通过 runSubagent 工具委托 ExploreAgent，所有探索结果作为 tool 消息保留在对话历史中。
        /// </summary>
        private static string BuildUnifiedDiscoveryPrompt(
            string userMessage, AgentContext context, string? structureCache)
        {
            var L = LocalizationService.Instance;
            var sb = new StringBuilder();

            sb.AppendLine(L["agent.plan.unifiedDiscoveryHeader"]);
            sb.AppendLine();
            sb.AppendLine($"## {L["plan.userTask"]}");
            sb.AppendLine(userMessage);
            sb.AppendLine();

            // ── 用户提供的文件上下文（如果存在，AI 可能无需探索）──
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine(L["agent.plan.fileContextHeader"]);
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            // ── 项目结构缓存或探索指引 ──
            if (!string.IsNullOrEmpty(structureCache))
            {
                sb.AppendLine(L["agent.plan.discoveryStructureCached"]);
                sb.AppendLine(structureCache);
                sb.AppendLine();
                sb.AppendLine(L["agent.plan.discoverySkipRootScan"]);
            }
            else
            {
                sb.AppendLine(L["agent.plan.discoveryFirstExplore"]);
            }

            // ── 上下文充足判定 ──
            bool hasSufficientContext = !string.IsNullOrEmpty(context.FileContext)
                || !string.IsNullOrEmpty(structureCache);
            if (hasSufficientContext)
            {
                sb.AppendLine();
                sb.AppendLine(L["agent.plan.discoveryNoExploreNeeded"]);
            }

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine();
                sb.AppendLine($"Workspace root: {context.SolutionPath}");
            }

            sb.AppendLine();
            sb.AppendLine(L["agent.plan.discoveryUnifiedTail"]);

            // ── 并行子代理策略（仅探索时相关）──
            sb.AppendLine();
            sb.AppendLine("## 探索策略（仅在需要探索时参考）");
            sb.AppendLine("- 需要探索多个独立区域时，在一次回复中同时调用多个 runSubagent（最多 3 个并行）");
            sb.AppendLine("- **为每个子代理分配不同的探索区域**，避免重复探索相同的文件或目录");
            sb.AppendLine("- 例如：子代理 1 探索存储层，子代理 2 探索执行引擎，子代理 3 探索索引结构");
            sb.AppendLine("- 如果文件已被之前子代理读取（cached=\"true\"），直接使用缓存内容，无需重复读取");
            sb.AppendLine("- 探索完成后汇总所有子代理的发现，形成完整的分析报告");
            sb.AppendLine();
            sb.AppendLine("## ⚠️ 关键规则");
            sb.AppendLine("- 如果用户消息 + 已有上下文已足够制定实现计划 → **直接回复 DONE，不调用任何工具**");
            sb.AppendLine("- 如果缺少关键信息（项目结构、相关代码等）→ 使用 runSubagent 探索后再回复 DONE");
            sb.AppendLine("- DONE 必须是纯文本，不要包裹在 markdown、代码块或 JSON 中");

            return sb.ToString();
        }

        /// <summary>
        /// 从对话消息列表中的 tool 消息提取发现上下文文本。
        /// 遍历所有 role=tool 的消息，提取子代理返回的探索结果。
        /// </summary>
        private static string ExtractDiscoveryContextFromMessages(List<ChatApiMessage> messages)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
            {
                if (msg.Role == "tool" && msg.Name == "runSubagent" && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    if (sb.Length > 0)
                        sb.AppendLine("\n---\n");
                    sb.AppendLine(msg.Content);
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 从缓存的文件列表构建项目结构摘要（替代 Phase 1 的 ExploreAgent API 调用）。
        /// 提取目录层级和前几级文件，生成与 Phase 1 输出格式兼容的结构上下文。
        /// </summary>
        private static string BuildStructureContextFromCache(List<string> cachedFiles)
        {
            if (cachedFiles == null || cachedFiles.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            // 提取唯一目录
            var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootFiles = new List<string>();
            string? commonRoot = null;

            foreach (var file in cachedFiles)
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(file) ?? "";
                    if (!string.IsNullOrEmpty(dir))
                    {
                        dirs.Add(dir);

                        // 找公共根目录
                        if (commonRoot == null)
                            commonRoot = dir;
                        else
                        {
                            // 逐步缩短公共根
                            while (commonRoot.Length > 0 && !dir.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                                commonRoot = System.IO.Path.GetDirectoryName(commonRoot) ?? "";
                        }
                    }
                }
                catch { }
            }

            // 按层级分组展示目录树
            sb.AppendLine("## 项目结构 (来自缓存)");
            sb.AppendLine();

            // 列出顶级目录
            var topDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                string relative = dir;
                if (!string.IsNullOrEmpty(commonRoot) && dir.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                {
                    relative = dir.Substring(commonRoot!.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                }
                if (string.IsNullOrEmpty(relative)) continue;

                string[] parts = relative.Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                    topDirs.Add(parts[0]);
            }

            foreach (var d in topDirs)
            {
                // 统计该顶级目录下的文件数
                int count = 0;
                string prefix = !string.IsNullOrEmpty(commonRoot)
                    ? System.IO.Path.Combine(commonRoot, d)
                    : d;
                foreach (var file in cachedFiles)
                {
                    if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                sb.AppendLine($"- 📁 {d}/ ({count} 个文件)");
            }

            sb.AppendLine();
            sb.AppendLine($"共 {cachedFiles.Count} 个文件, {dirs.Count} 个目录");
            return sb.ToString();
        }

        #endregion

        #region Alignment Phase

        /// <summary>
        /// 运行对齐阶段：使用工具调用循环让 AI 通过 VisualStudio_askQuestions 向用户提问。
        /// 🔑 缓存关键：在阶段 1 的对话历史上追加对齐指令，
        /// DeepSeek Prefix Cache 可命中整个阶段 1 的对话前缀（~80-90% 命中率）。
        /// 返回对齐阶段的完整消息列表（含 tool call 历史），供设计阶段复用。
        /// </summary>
        /// <returns>(规划概要, 对齐对话消息列表)</returns>
        private async Task<(string Summary, List<ChatApiMessage> Messages)> RunAlignmentAsync(
            string userMessage, List<ChatApiMessage> existingMessages, AgentContext context)
        {
            var L = LocalizationService.Instance;
            var ct = context.CancellationToken;

            try
            {
                // 🔑 直接在阶段 1 的对话历史上追加对齐指令
                existingMessages.Add(new ChatApiMessage
                {
                    Role = "user",
                    Content = string.Format(AiPrompts.PlanAlignmentUserPrompt, userMessage)
                });

                // ── 使用 onContent 回调实时捕获 AI 生成的规划概要 ──
                var alignmentContent = new StringBuilder();
                // 🔑 使用统一工具白名单（不再按阶段过滤 tools JSON，由客户端拦截保证安全）
                string alignmentResult = await CallAiWithToolLoopAsync(
                    existingMessages,
                    context.SolutionPath,
                    ct,
                    maxTokens: 4096,
                    onContent: (chunk) =>
                    {
                        alignmentContent.Append(chunk);
                    });

                // 将 AI 在提问前生成的规划概要合并到结果中
                string planSummary = alignmentContent.ToString().Trim();

                AddLog("INFO", LocalizationService.Instance.Format("agent.log.planAlignmentDone", alignmentResult.Truncate(200)));
                return (planSummary, existingMessages);
            }
            catch (OperationCanceledException)
            {
                AddLog("WARN", LocalizationService.Instance["agent.log.planAlignmentCancelled"]);
                return (string.Empty, new List<ChatApiMessage>());
            }
            catch (Exception ex)
            {
                AddLog("WARN", LocalizationService.Instance.Format("agent.log.planAlignmentError", ex.Message));
                return (string.Empty, new List<ChatApiMessage>());
            }
        }

        #endregion

        #region Plan Creation

        /// <summary>
        /// 剥离 DeepSeek V4 泄露到 content 中的 DSML/工具调用 XML 标签。
        /// 当 toolChoice=none 时，DeepSeek V4 仍可能在 content 中输出工具调用意图的 XML 片段。
        /// 此方法移除所有已知的 DSML 标签及其内容，保留纯文本/JSON。
        /// </summary>
        private new static string StripDsmlContent(string text)
        {
            return BaseAgent.StripDsmlContent(text);
        }

        /// <summary>
        /// 使用 AI 创建实现计划（JSON 格式）。
        /// 🔑 缓存关键：如果传入了 alignmentMessages，则在对齐对话基础上继续（追加系统上下文 + 设计指令），
        /// DeepSeek Prefix Cache 可匹配整个对齐对话前缀，避免设计阶段冷启动（从 ~5% → ~60%+ 命中率）。
        /// 如果 alignmentMessages 为 null，则独立创建新对话（兼容旧调用路径）。
        /// </summary>
        /// <returns>(计划, 含 AI JSON 回复的完整消息列表供 Phase 3.5 复用)</returns>
        private async Task<(AgentTaskPlan? Plan, List<ChatApiMessage> Messages)> CreatePlanAsync(
            string userMessage, string discoveryContext,
            List<ChatApiMessage>? alignmentMessages, AgentContext context)
        {
            var L = LocalizationService.Instance;
            var ct = context.CancellationToken;

            // ── 构建额外的 system 消息（发现上下文），放在历史之后、用户消息之前 ──
            // 这样 messages[0]（Agent System Prompt）保持稳定，可被 DeepSeek Prefix Cache 命中
            //
            // 🔑 缓存关键：discoveryContext 的包装头必须与 GenerateDetailedPlanMarkdownAsync
            // 使用完全相同的 i18n key（plan.md.codebaseFindings），确保两个 API 调用之间的
            // 发现上下文系统消息前缀完全一致，从而命中 DeepSeek Prefix Cache。
            var extraSystemMessages = new List<ChatApiMessage>();
            if (!string.IsNullOrEmpty(discoveryContext))
            {
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = L["plan.md.codebaseFindings"] + "\n\n" + discoveryContext
                });
            }
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                // RAG-MARK: no-truncate — 不再截断文件上下文，完整传递给计划生成
                // RAG-SOURCE: file-read 用户上传的文件上下文（PlanAgent）
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = LocalizationService.Instance["agent.plan.fileContextHeader"] + "\n\n" + context.FileContext
                });
            }

            // ── 用户消息保持简洁（只有任务描述 + 指令），不含动态内容 ──
            string planPrompt = BuildPlanCreationPrompt(userMessage, context);

            // ── 如果有对齐对话历史，在此基础上继续（跨阶段缓存延续）──
            List<ChatApiMessage> messages;
            if (alignmentMessages != null && alignmentMessages.Count > 0)
            {
                // 🔑 在对齐对话基础上追加：系统上下文 + 设计指令
                // 不调用 BuildContextAwareMessages，直接复用对齐阶段的完整消息列表
                messages = new List<ChatApiMessage>(alignmentMessages);
                messages.AddRange(extraSystemMessages);
                messages.Add(new ChatApiMessage { Role = "user", Content = planPrompt });
                AddLog("INFO", LocalizationService.Instance["agent.log.planReuseAlignment"]);
            }
            else
            {
                // 回退：独立构建消息（无对齐历史时）
                messages = BuildContextAwareMessages(Definition.SystemPrompt, planPrompt, extraSystemMessages);
            }

            AddLog("INFO", L["agent.log.planGeneratingJson"]);
            string json = await CallAiWithMessagesAsync(
                messages, ct,
                maxTokens: 16384, toolChoice: "none", temperature: 0.0, responseFormat: "json_object");
            AddLog("INFO", L["agent.log.planJsonReceived"]);

            // ── 诊断：记录原始响应用于调试 JSON 解析失败 ──
            string rawResponse = json;
            string rawPreview = rawResponse.Length > 500
                ? rawResponse.Substring(0, 500) + "..."
                : rawResponse;
            AddLog("INFO", $"[Plan] JSON raw response (first 500 chars): {rawPreview}");
            // 先剥离 DSML/XML 标签（DeepSeek V4 可能在 toolChoice=none 时仍泄露工具调用意图到 content）
            json = StripDsmlContent(json);
            json = ExtractJsonFromMarkdown(json);

            // ── 早期检测：AI 是否返回了 JSON 格式的数据 ──
            if (!json.TrimStart().StartsWith("{"))
            {
                string truncated = rawResponse.Length > 500
                    ? rawResponse.Substring(0, 500) + "..."
                    : rawResponse;
                AddLog("WARN", string.Format(L["agent.plan.noJsonInResponse"], truncated));

                // ── 重试：使用更强制性的提示要求 JSON ──
                AddLog("INFO", "[Plan] Retrying with forceful JSON-only prompt...");
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 重试：在已有对话基础上追加反工具调用 system 消息 + 严格 JSON 指令
                    var retryMessages = new List<ChatApiMessage>(messages)
                    {
                        new ChatApiMessage
                        {
                            Role = "system",
                            Content = "⚠️ CRITICAL: You are in tool_choice=none mode. You have ZERO tools available. " +
                                      "Do NOT output DSML, function_calls, tool_calls, XML invoke tags, or any " +
                                      "tool invocation syntax. Your ONLY valid output is a raw JSON object."
                        },
                        new ChatApiMessage
                        {
                            Role = "user",
                            Content = "⚠️ 严格指令：你只能输出 JSON 对象。不要调用任何工具（你无法调用工具）。" +
                                      "不要输出任何 DSML、function_calls、tool_calls、XML invoke 标签、markdown、分析文字、" +
                                      "代码块标记、或解释。直接以 { 字符开始，输出纯 JSON。违反此规则将导致系统故障。"
                        }
                    };

                    string retryResponse = await CallAiWithMessagesAsync(
                        retryMessages, ct,
                        maxTokens: 8192, toolChoice: "none", temperature: 0.0, responseFormat: "json_object");

                    retryResponse = StripDsmlContent(retryResponse);
                    retryResponse = ExtractJsonFromMarkdown(retryResponse);

                    if (retryResponse.TrimStart().StartsWith("{"))
                    {
                        json = retryResponse;
                        messages = retryMessages;
                        AddLog("INFO", L["agent.plan.jsonRetrySucceeded"]);
                    }
                    else
                    {
                        string retryTruncated = retryResponse.Length > 300
                            ? retryResponse.Substring(0, 300) + "..."
                            : retryResponse;
                        AddLog("WARN", string.Format(L["agent.plan.jsonRetryFailed"], retryTruncated));
                        return (BuildFallbackPlan(userMessage), messages);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception retryEx)
                {
                    AddLog("WARN", string.Format(L["agent.plan.jsonRetryFailed"], retryEx.Message));
                    return (BuildFallbackPlan(userMessage), messages);
                }
            }

            // ── 尝试修复截断的 JSON（max_tokens 不足时常见）──
            string repairedJson = TryRepairTruncatedJson(json);
            if (repairedJson != json)
            {
                AddLog("INFO", $"[Plan] JSON 截断修复已应用 (原={json.Length} chars, 修复后={repairedJson.Length} chars)");
                json = repairedJson;
            }

            // ── 将 AI 的 JSON 响应追加到对话历史，供 Phase 3.5 plan.md 生成复用 ──
            messages.Add(new ChatApiMessage { Role = "assistant", Content = json });

            try
            {
                var plan = JsonSerializer.Deserialize<AgentTaskPlan>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                if (plan != null && plan.Steps.Count > 0)
                {
                    plan.Intent = AgentIntent.CodeChange;
                    return (plan, messages);
                }

                // JSON 解析成功但 steps 为空 → 尝试从原始文本中提取步骤
                if (plan != null && plan.Steps.Count == 0)
                {
                    AddLog("WARN", "[Plan] JSON parsed but steps array is empty. Attempting text-based step extraction...");
                    var extractedSteps = ExtractStepsFromRawAiText(rawResponse);
                    if (extractedSteps.Count > 0)
                    {
                        plan.Steps = extractedSteps;
                        plan.Title = plan.Title ?? extractedSteps[0].Title;
                        plan.Intent = AgentIntent.CodeChange;
                        AddLog("INFO", $"[Plan] Extracted {extractedSteps.Count} steps from raw AI text (JSON steps were empty).");
                        return (plan, messages);
                    }
                }
            }
            catch (Exception ex)
            {
                // ── 诊断日志：记录原始响应片段以便排查 ──
                string truncated = rawResponse.Length > 500
                    ? rawResponse.Substring(0, 500) + "..."
                    : rawResponse;
                var L2 = LocalizationService.Instance;
                AddLog("WARN", string.Format(L2["agent.plan.jsonParseFailed"], ex.Message));
                AddLog("INFO", string.Format(L2["agent.log.planJsonRawResponse"], truncated));

                // ── JSON 解析异常 → 尝试从原始文本中提取步骤作为最后手段 ──
                AddLog("INFO", "[Plan] JSON deserialization threw exception. Attempting text-based step extraction from raw response...");
                var fallbackSteps = ExtractStepsFromRawAiText(rawResponse);
                if (fallbackSteps.Count > 0)
                {
                    AddLog("INFO", $"[Plan] Extracted {fallbackSteps.Count} steps from raw AI text after JSON parse failure.");
                    return (new AgentTaskPlan
                    {
                        Intent = AgentIntent.CodeChange,
                        Title = fallbackSteps[0].Title,
                        Steps = fallbackSteps,
                    }, messages);
                }
            }

            // 回退：单步计划
            return (BuildFallbackPlan(userMessage), messages);
        }

        /// <summary>
        /// 构建 JSON 解析失败时的单步回退计划。
        /// </summary>
        private static AgentTaskPlan BuildFallbackPlan(string userMessage)
        {
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
            var SB = LocalizationService.Instance;
            var sb = new StringBuilder();
            sb.AppendLine($"## {SB["plan.userTask"]}");
            sb.AppendLine(userMessage);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine(SB["plan.creation.solutionPath"]);
                sb.AppendLine(context.SolutionPath);
                sb.AppendLine();
            }

            sb.AppendLine(SB["plan.creation.instructions"]);
            sb.AppendLine(SB["plan.creation.instruction1"]);
            sb.AppendLine(SB["plan.creation.instruction2"]);
            sb.AppendLine();
            sb.AppendLine(SB["plan.creation.instruction3"]);
            sb.AppendLine(SB["plan.creation.instruction4"]);
            sb.AppendLine();
            sb.AppendLine(SB["plan.creation.jsonFormat"]);
            sb.AppendLine("{");
            sb.AppendLine("  \"title\": \"任务标题\",");
            sb.AppendLine("  \"steps\": [");
            sb.AppendLine("    { \"index\": 1, \"title\": \"步骤标题\", \"description\": \"详细描述\", \"requiresApproval\": false }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine(SB["plan.creation.jsonOnlyHint"]);

            return sb.ToString();
        }

        /// <summary>
        /// 将计划格式化为 Markdown 展示。
        /// </summary>
        private static string FormatPlanAsMarkdown(AgentTaskPlan plan)
        {
            var L = LocalizationService.Instance;
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L["plan.format.title"], plan.Title));
            sb.AppendLine();
            sb.AppendLine(string.Format(L["plan.format.stepCount"], plan.Steps.Count));
            sb.AppendLine();

            // ── 详细步骤直接展示在对话中 ──
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                string prefix = step.RequiresApproval ? "🔐" : "📌";
                sb.AppendLine($"{prefix} **步骤 {step.Index}**: {step.Title}");

                if (!string.IsNullOrWhiteSpace(step.Description))
                {
                    // 描述过长时截断，避免对话过于冗长
                    string desc = step.Description.Length > 200
                        ? step.Description.Substring(0, 200) + "..."
                        : step.Description;
                    // 将描述转为引用格式，缩进展示
                    sb.AppendLine($"> {desc.Replace("\n", "\n> ")}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine(L["plan.format.readyToExecute"]);

            return sb.ToString();
        }

        /// <summary>
        /// 从 plan.md Markdown 中提取步骤列表（备用方案：JSON 解析失败时使用）。
        /// 支持的格式：
        ///   ### 步骤 N: 标题
        ///   ### Step N: 标题
        ///   ## 步骤 N: 标题
        ///   描述文本跟在标题后的段落中。
        /// </summary>
        private static List<AgentStep> ExtractStepsFromPlanMarkdown(string markdown)
        {
            var steps = new List<AgentStep>();
            if (string.IsNullOrWhiteSpace(markdown)) return steps;

            // 匹配模式: ### 步骤 N: 标题  或  ### Step N: 标题  或  ## 步骤 N: 标题
            var stepPattern = new System.Text.RegularExpressions.Regex(
                @"^(?:#{2,3})\s*(?:步骤|Step)\s*(\d+)[：:]\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = stepPattern.Matches(markdown);
            if (matches.Count == 0)
            {
                // 尝试备用模式: **步骤 N**: 标题  或   N. 标题（编号列表）
                var altPattern = new System.Text.RegularExpressions.Regex(
                    @"(?:\*\*)?(?:步骤|Step)\s*(\d+)(?:\*\*)?[：:]\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                matches = altPattern.Matches(markdown);
            }

            if (matches.Count == 0)
            {
                // 尝试编号列表模式: 1. **标题**  或  - **步骤 1**: 标题
                var listPattern = new System.Text.RegularExpressions.Regex(
                    @"^(?:\d+\.|\-)\s*\*?\*?(?:步骤|Step)?\s*(\d+)[：:.\s]*\*?\*?\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                matches = listPattern.Matches(markdown);
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (!int.TryParse(match.Groups[1].Value, out int stepIndex))
                    stepIndex = i + 1;

                string title = match.Groups[2].Value.Trim();
                // 清理标题中的 markdown 格式
                title = System.Text.RegularExpressions.Regex.Replace(title, @"\*+", "").Trim();

                // 提取描述：标题行后到下一个步骤标题之间的第一个非空段落
                int matchEnd = match.Index + match.Length;
                int nextMatchStart = (i + 1 < matches.Count) ? matches[i + 1].Index : markdown.Length;
                string section = markdown.Substring(matchEnd, nextMatchStart - matchEnd);

                // 取第一个非空行作为描述（跳过空行和 markdown 格式符号）
                var descLines = section.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("```") && !l.StartsWith("---"))
                    .Take(3)
                    .ToList();

                string description = descLines.Count > 0
                    ? string.Join(" ", descLines)
                    : title;

                // 判断是否需要审批（标题或描述中包含特定关键词）
                bool requiresApproval = title.Contains("🔐") || title.Contains("权限")
                    || description.Contains("需要确认") || description.Contains("需要审批")
                    || description.Contains("权限") || description.Contains("terminal")
                    || description.Contains("Terminal");

                steps.Add(new AgentStep
                {
                    Index = stepIndex,
                    Title = title,
                    Description = description.Truncate(500),
                    RequiresApproval = requiresApproval,
                });
            }

            // 重新编号确保连续
            for (int i = 0; i < steps.Count; i++)
                steps[i].Index = i + 1;

            return steps;
        }

        /// <summary>
        /// 从 AI 原始文本响应中提取步骤（JSON 解析失败时的兜底方案）。
        /// 支持的格式比 ExtractStepsFromPlanMarkdown 更宽松：
        ///   - "Step 1: ..." 或 "步骤 1: ..."（不要求 Markdown 标题）
        ///   - 编号列表 "1. ..." "2. ..."
        ///   - 破折号列表 "- ..."
        ///   - "### Step N" 或 "## 步骤 N"（Markdown 格式，委托给 ExtractStepsFromPlanMarkdown）
        /// </summary>
        /// <param name="rawText">AI 原始响应文本（含可能的 DSML/XML 污染）</param>
        /// <returns>提取的步骤列表</returns>
        private static List<AgentStep> ExtractStepsFromRawAiText(string rawText)
        {
            var steps = new List<AgentStep>();
            if (string.IsNullOrWhiteSpace(rawText)) return steps;

            // 先尝试用已有的 Markdown 步骤提取器
            steps = ExtractStepsFromPlanMarkdown(rawText);
            if (steps.Count > 0) return steps;

            // ── 宽松模式 1: "Step N: Title" 或 "步骤 N: Title"（纯文本）──
            var stepLinePattern = new System.Text.RegularExpressions.Regex(
                @"(?:步骤|Step)\s*(\d+)[：:]\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = stepLinePattern.Matches(rawText);
            if (matches.Count > 0)
            {
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    if (!int.TryParse(m.Groups[1].Value, out int idx))
                        idx = i + 1;
                    string title = m.Groups[2].Value.Trim();
                    title = System.Text.RegularExpressions.Regex.Replace(title, @"[\*\#]+", "").Trim();
                    if (title.Length < 3) continue;

                    steps.Add(new AgentStep
                    {
                        Index = idx,
                        Title = title,
                        Description = title,
                        RequiresApproval = false,
                    });
                }
            }

            // ── 宽松模式 2: 编号列表 "N. Title" ──
            if (steps.Count == 0)
            {
                var numberedPattern = new System.Text.RegularExpressions.Regex(
                    @"^(\d+)[\.\)、]\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                matches = numberedPattern.Matches(rawText);
                // 至少需要 2 个匹配才算有效步骤列表
                if (matches.Count >= 2)
                {
                    for (int i = 0; i < matches.Count; i++)
                    {
                        var m = matches[i];
                        if (!int.TryParse(m.Groups[1].Value, out int idx))
                            idx = i + 1;
                        string title = m.Groups[2].Value.Trim();
                        title = System.Text.RegularExpressions.Regex.Replace(title, @"[\*\#]+", "").Trim();
                        if (title.Length < 5) continue;

                        steps.Add(new AgentStep
                        {
                            Index = idx,
                            Title = title,
                            Description = title,
                            RequiresApproval = false,
                        });
                    }
                }
            }

            // ── 宽松模式 3: 破折号列表 "- Title"（至少 2 个）──
            if (steps.Count == 0)
            {
                var dashPattern = new System.Text.RegularExpressions.Regex(
                    @"^[\-\*]\s+(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                matches = dashPattern.Matches(rawText);
                if (matches.Count >= 2)
                {
                    for (int i = 0; i < matches.Count; i++)
                    {
                        string title = matches[i].Groups[1].Value.Trim();
                        title = System.Text.RegularExpressions.Regex.Replace(title, @"[\*\#]+", "").Trim();
                        if (title.Length < 5) continue;

                        steps.Add(new AgentStep
                        {
                            Index = i + 1,
                            Title = title,
                            Description = title,
                            RequiresApproval = false,
                        });
                    }
                }
            }

            // 重新编号确保连续
            for (int i = 0; i < steps.Count; i++)
                steps[i].Index = i + 1;

            return steps;
        }

        /// <summary>
        /// 使用 AI 将 JSON 计划展开为详细的 Markdown 计划文档（plan.md）。
        /// 包含：要实现的功能、实现方案、详细步骤、涉及文件、类/接口/方法设计、依赖关系、验证步骤。
        /// </summary>
        /// <param name="designMessages">Phase 3 的完整对话历史（含 AI JSON 回复），
        /// Phase 3.5 在此基础之上追加 plan.md 指令，最大化 DeepSeek Prefix Cache 命中率。</param>
        private async Task<string> GenerateDetailedPlanMarkdownAsync(
            string userMessage, string discoveryContext, AgentTaskPlan plan,
            AgentContext context, List<ChatApiMessage> designMessages)
        {
            var ct = context.CancellationToken;
            var L = LocalizationService.Instance;

            // 先将现有计划步骤序列化为 JSON 供 AI 参考
            string planJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
            });

            // ── 🔑 在 Phase 3 对话历史上追加 plan.md 指令，最大化 Prefix Cache 命中 ──
            // discoveryContext 已在 designMessages 的历史中，无需重复注入
            var mdMessages = new List<ChatApiMessage>(designMessages);
            mdMessages.Add(new ChatApiMessage
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

            // ── 将 plan.md 用户指令追加到对话历史末尾 ──
            mdMessages.Add(new ChatApiMessage { Role = "user", Content = prompt.ToString() });

            AddLog("INFO", L["agent.log.planGeneratingMd"]);
            string markdown = await CallAiWithMessagesAsync(
                mdMessages, ct,
                maxTokens: 16384, toolChoice: "none");
            AddLog("INFO", L["agent.log.planMdGenerated"]);

            // ── 后处理：剥离 DSML/工具调用泄露 ──
            // DeepSeek V4 即使在 toolChoice=none 时也可能将工具调用意图泄露到 content 字段，
            // 导致 plan.md 内容被 DSML/XML 标签污染。此处先剥离再检测。
            string rawMarkdown = markdown;
            markdown = StripDsmlContent(markdown);
            markdown = markdown.Trim();

            // ── 检测空内容：剥离 DSML 后内容过短 → AI 返回了工具调用而非计划文档 ──
            if (markdown.Length < 200)
            {
                string rawPreview = rawMarkdown.Length > 500
                    ? rawMarkdown.Substring(0, 500) + "..."
                    : rawMarkdown;
                AddLog("WARN", $"[Plan] plan.md content is too short after DSML stripping ({markdown.Length} chars). Raw preview: {rawPreview}");
                AddLog("INFO", "[Plan] Retrying plan.md generation with stricter anti-tool-call prompt...");

                ct.ThrowIfCancellationRequested();
                try
                {
                    // ── 重试：在 Phase 3 对话历史基础上追加反工具调用指令 ──
                    var retryMdMessages = new List<ChatApiMessage>(mdMessages);
                    retryMdMessages.Add(new ChatApiMessage
                    {
                        Role = "system",
                        Content = "⚠️ CRITICAL: You are in tool_choice=none mode. You have NO tools available. " +
                                  "Do NOT output any function calls, DSML tags, XML tags, tool invocations, " +
                                  "or code blocks that look like tool usage. Output ONLY the implementation plan " +
                                  "in clean Markdown format as instructed below."
                    });
                    retryMdMessages.Add(new ChatApiMessage
                    {
                        Role = "user",
                        Content = "⚠️ RETRY: Your previous response contained tool call syntax instead of a plan document. " +
                                  "Re-read the instructions and output ONLY a clean Markdown implementation plan. " +
                                  "No DSML, no XML, no tool calls, no function invocations — just the plan document."
                    });

                    string retryMd = await CallAiWithMessagesAsync(
                        retryMdMessages, ct,
                        maxTokens: 16384, toolChoice: "none");

                    retryMd = StripDsmlContent(retryMd);
                    retryMd = retryMd.Trim();

                    if (retryMd.Length >= 200)
                    {
                        markdown = retryMd;
                        AddLog("INFO", $"[Plan] plan.md retry succeeded ({markdown.Length} chars).");
                    }
                    else
                    {
                        AddLog("WARN", $"[Plan] plan.md retry also returned short content ({retryMd.Length} chars). Using best available.");
                        markdown = retryMd.Length > markdown.Length ? retryMd : markdown;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception retryEx)
                {
                    AddLog("WARN", $"[Plan] plan.md retry failed: {retryEx.Message}. Using original ({markdown.Length} chars).");
                }
            }

            // 如果 AI 返回了代码块包裹的内容，去掉包裹
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
        /// 存储到 %LocalAppData%\DeepSeekVS\plans\{solution_hash}\plan.md，
        /// 同一解决方案的计划可重复覆盖使用。
        /// </summary>
        /// <returns>保存的文件绝对路径</returns>
        private static async Task<string> SavePlanMarkdownAsync(string markdown, AgentContext context)
        {
            // 基础目录：%LocalAppData%\DeepSeekVS\plans
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekVS", "plans");

            // 根据解决方案路径计算子目录哈希（与 ChatPersistenceService 保持一致）
            string subDir;
            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                using (var sha256 = SHA256.Create())
                {
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(context.SolutionPath));
                    var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
                    subDir = Path.Combine(baseDir, $"proj_{hash}");
                }
            }
            else
            {
                subDir = Path.Combine(baseDir, "_unsaved");
            }

            Directory.CreateDirectory(subDir);

            // 文件名：固定为 plan.md（每次覆盖，同一方案可重复使用）
            string filePath = Path.Combine(subDir, "plan.md");

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
