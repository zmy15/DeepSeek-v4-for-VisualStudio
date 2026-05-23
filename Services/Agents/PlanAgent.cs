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
        /// ExploreAgent 引用，由 AgentDispatcher 注入。
        /// 用于在发现阶段并行探索代码库。
        /// 设置时自动转发 ExploreAgent 的日志和文件变更事件。
        /// </summary>
        public ExploreAgent? ExploreAgent
        {
            get => _exploreAgent;
            set
            {
                if (_exploreAgent != null)
                {
                    _exploreAgent.LogEntryAdded -= OnExploreLog;
                    _exploreAgent.FileChangeNotified -= OnExploreFileChange;
                }
                _exploreAgent = value;
                if (_exploreAgent != null)
                {
                    _exploreAgent.LogEntryAdded += OnExploreLog;
                    _exploreAgent.FileChangeNotified += OnExploreFileChange;
                }
            }
        }

        private void OnExploreLog(AgentLogEntry entry)
        {
            AddLog(entry.Level, $"[Explore] {entry.Message}");
        }

        private void OnExploreFileChange(AgentFileChangeEventArgs args)
        {
            NotifyFileChange(args.PlanId, args.ChangeType, args.FilePath, args.Detail);
        }

        public PlanAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Plan)
        {
            _exploreAgent = new ExploreAgent(apiService);
            _exploreAgent.LogEntryAdded += OnExploreLog;
            _exploreAgent.FileChangeNotified += OnExploreFileChange;
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
                    "VisualStudio_askQuestions", // 向用户提问澄清
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
                bool alignmentNeeded = await CheckAlignmentNeededAsync(userMessage, discoveryContext, context);
                if (alignmentNeeded)
                {
                    AddLog("INFO", L["agent.log.planAlignNeeded"]);
                    await RunAlignmentAsync(userMessage, discoveryContext, context);
                }
                else
                {
                    AddLog("INFO", L["agent.log.planAlignSkipped"]);
                }

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

                        // ── 回退步骤提取：如果 JSON 解析回退为单步计划，从 plan.md 中提取步骤 ──
                        if (plan.Steps.Count <= 1 && !string.IsNullOrEmpty(planMarkdown))
                        {
                            var extractedSteps = ExtractStepsFromPlanMarkdown(planMarkdown);
                            if (extractedSteps.Count > 1)
                            {
                                plan.Steps = extractedSteps;
                                plan.Title = plan.Title ?? extractedSteps.FirstOrDefault()?.Title ?? plan.Title;
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
        /// <summary>
        /// 运行发现阶段：两阶段探索。
        /// 阶段 1（串行）：快速扫描项目顶层结构（目录树 + 配置文件），结果共享给后续子代理。
        /// 阶段 2（并行）：基于阶段 1 的结构信息，深入探索各 AI 识别的代码区域。
        /// 
        /// 设计原因：多个 Explore 子代理并行时，各自的 System Prompt 都强制从根目录
        /// list_dir 开始，导致 3x 重复扫描。两阶段设计将公共基础扫描提取为串行阶段 1，
        /// 阶段 2 注入结构上下文后跳过重复扫描，大幅减少工具调用次数。
        /// </summary>
        private async Task<string> RunDiscoveryAsync(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            try
            {
                var L = LocalizationService.Instance;

                // ── 阶段 1（串行）：快速扫描项目顶层结构 ──
                AddLog("INFO", L["agent.log.explorePhase1"]);
                string structureContext = "";
                try
                {
                    string phase1Prompt =
                        $"{L["agent.plan.discoveryPhase1Prompt"]}\n\n" +
                        (string.IsNullOrEmpty(context.SolutionPath) ? ""
                            : $"Workspace root: {context.SolutionPath}\n\n") +
                        $"{L["agent.plan.discoveryPhase1Tail"]}";

                    var phase1Result = await RunSingleExploreAsync("structure", phase1Prompt, context);
                    if (phase1Result.Success && !string.IsNullOrEmpty(phase1Result.Findings))
                    {
                        structureContext = phase1Result.Findings;
                        sb.AppendLine(string.Format(L["plan.discovery.areaHeader"], L["agent.log.explorePhase1Label"]));
                        sb.AppendLine(structureContext);
                        sb.AppendLine();
                        AddLog("INFO", string.Format(L["agent.log.explorePhase1Done"], structureContext.Length));
                    }
                    else
                    {
                        AddLog("WARN", L["agent.log.explorePhase1Failed"]);
                    }
                }
                catch (Exception ex)
                {
                    AddLog("WARN", string.Format(L["agent.log.explorePhase1Error"], ex.Message));
                }

                // ── AI 判断需要探索哪些代码区域 ──
                string routingPrompt =
                    $"{L["agent.plan.discoveryPrompt"]}\n\n" +
                    $"{L["plan.userTask"]}: {userMessage}\n\n" +
                    (string.IsNullOrEmpty(context.SolutionPath) ? ""
                        : $"Solution path: {context.SolutionPath}\n\n") +
                    (string.IsNullOrEmpty(structureContext) ? ""
                        : $"{L["agent.plan.discoveryStructureHint"]}\n{structureContext.Truncate(1500)}\n\n") +
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

                // ── 阶段 2（并行）：深入探索各区域，注入结构上下文避免重复扫描 ──
                AddLog("INFO", L["agent.log.explorePhase2"]);
                var exploreTasks = new List<Task<SubagentResult>>();
                for (int i = 0; i < areas.Count; i++)
                {
                    string area = areas[i];
                    // ── 注入阶段 1 的结构上下文，明确告知跳过根目录重复扫描 ──
                    string contextualPrompt = BuildPhase2Prompt(area, structureContext, context);
                    var task = RunSingleExploreAsync(i.ToString(), contextualPrompt, context);
                    exploreTasks.Add(task);
                }

                var exploreResults = await Task.WhenAll(exploreTasks);

                // ── 汇总探索结果 ──
                foreach (var exploreResult in exploreResults)
                {
                    if (exploreResult.Success && !string.IsNullOrEmpty(exploreResult.Findings))
                    {
                        sb.AppendLine(string.Format(L["plan.discovery.areaHeader"], exploreResult.TaskId));
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
        /// 构建阶段 2 深入探索 prompt。
        /// 注入阶段 1 的结构上下文，明确指示跳过根目录重复扫描，
        /// 直接聚焦目标区域进行深入探索。
        /// </summary>
        private static string BuildPhase2Prompt(string area, string structureContext, AgentContext context)
        {
            var L = LocalizationService.Instance;
            var sb = new StringBuilder();

            sb.AppendLine(area);

            if (!string.IsNullOrEmpty(structureContext))
            {
                // 截断结构上下文（保留前 3000 字符，足够理解项目结构）
                string truncatedStructure = structureContext.Length > 3000
                    ? structureContext.Substring(0, 3000) + "\n... (truncated)"
                    : structureContext;

                sb.AppendLine();
                sb.AppendLine(L["agent.plan.discoveryPhase2InjectedHeader"]);
                sb.AppendLine(truncatedStructure);
                sb.AppendLine();
                sb.AppendLine(L["agent.plan.discoveryPhase2SkipHint"]);
            }

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine();
                sb.AppendLine($"Workspace root: {context.SolutionPath}");
            }

            sb.AppendLine();
            sb.AppendLine(L["agent.plan.discoveryPhase2Tail"]);

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
                // ── 确保内部 ExploreAgent 拥有工具服务（兜底）──
                // AgentDispatcher 通过 ExploreAgent 属性注入后，此检查为幂等空操作。
                // 但如果因任何原因属性未被注入（如测试环境），此处从 PlanAgent 自身传播。
                if (_exploreAgent != null)
                {
                    if (_exploreAgent.BuiltInTools == null && this.BuiltInTools != null)
                        _exploreAgent.BuiltInTools = this.BuiltInTools;
                    if (_exploreAgent.McpManager == null && this.McpManager != null)
                        _exploreAgent.McpManager = this.McpManager;
                }

                var exploreResult = await _exploreAgent!.ExecuteAsync(prompt, context);
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

        #region Alignment Phase

        /// <summary>
        /// 使用 AI 快速判断是否需要与用户对齐需求。
        /// 返回 true 表示 AI 有需要澄清的问题。
        /// </summary>
        private async Task<bool> CheckAlignmentNeededAsync(
            string userMessage, string discoveryContext, AgentContext context)
        {
            var L = LocalizationService.Instance;
            AddLog("INFO", L["agent.log.planAlignCheck"]);

            try
            {
                string checkPrompt =
                    $"用户任务: {userMessage}\n\n" +
                    $"代码库研究发现:\n{discoveryContext.Truncate(2000)}\n\n" +
                    "基于以上信息，在制定实现计划之前，你是否需要向用户提问澄清需求？\n" +
                    "只回复 YES 或 NO。";

                string response = await CallAiShortAsync(
                    Definition.SystemPrompt, checkPrompt, context.CancellationToken, maxTokens: 16);

                bool needed = response.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
                AddLog("INFO", string.Format(L["agent.log.planAlignCheckResult"], needed ? "需要对齐" : "无需对齐"));
                return needed;
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"对齐检查失败（默认跳过）: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 运行对齐阶段：使用工具调用循环让 AI 通过 VisualStudio_askQuestions 向用户提问。
        /// </summary>
        private async Task RunAlignmentAsync(
            string userMessage, string discoveryContext, AgentContext context)
        {
            var L = LocalizationService.Instance;
            var ct = context.CancellationToken;

            try
            {
                // ── 构建对齐对话的消息列表 ──
                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = Definition.SystemPrompt },
                };

                // 注入发现上下文
                if (!string.IsNullOrEmpty(discoveryContext))
                {
                    messages.Add(new ChatApiMessage
                    {
                        Role = "system",
                        Content = L["agent.plan.discoveryFallback"] + "\n\n" +
                                  discoveryContext.Truncate(3000)
                    });
                }

                // 对齐指令：先表明规划思路，再主动询问
                messages.Add(new ChatApiMessage
                {
                    Role = "user",
                    Content = $"用户任务: {userMessage}\n\n" +
                              "请按以下流程与用户对齐需求：\n\n" +
                              "1. **先简要说明你的理解**：用 2-3 句话概括你对任务的理解、你打算采用的技术方案方向，让用户知道你的规划思路。\n" +
                              "2. **再主动提问澄清**：使用 VisualStudio_askQuestions 工具向用户提问，澄清任何模糊的需求或技术决策。\n" +
                              "   - 每次只问 1-2 个最关键的问题\n" +
                              "   - 问题应结合你已经了解的信息，让用户感到你在认真思考\n" +
                              "   - 获得用户回复后，可以继续追问或结束对齐\n" +
                              "3. 当你认为需求已经足够清晰时，回复 DONE 结束对齐阶段。"
                });

                // ── 使用工具调用循环（仅允许 VisualStudio_askQuestions）──
                string alignmentResult = await CallAiWithToolLoopAsync(
                    messages,
                    context.SolutionPath,
                    ct,
                    maxTokens: 2048,
                    toolWhitelist: new List<string> { "VisualStudio_askQuestions" });

                AddLog("INFO", $"[Plan] 对齐阶段完成 ({alignmentResult.Truncate(200)})");
            }
            catch (OperationCanceledException)
            {
                AddLog("WARN", "[Plan] 对齐阶段被用户取消");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Plan] 对齐阶段出错（非致命，继续规划）: {ex.Message}");
            }
        }

        #endregion

        #region Plan Creation

        /// <summary>
        /// 剥离 DeepSeek V4 泄露到 content 中的 DSML/工具调用 XML 标签。
        /// 当 toolChoice=none 时，DeepSeek V4 仍可能在 content 中输出工具调用意图的 XML 片段。
        /// 此方法移除所有已知的 DSML 标签及其内容，保留纯文本/JSON。
        /// </summary>
        private static string StripDsmlContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // ── 移除完整的 DSML/工具调用 XML 块（含嵌套内容）──
            // 匹配 DSML, function_calls, tool_calls, invoke, parameter 等标签
            string[] blockTags = {
                "DSML", "function_calls?", "tool_calls?", "invoke", "parameter",
                "VisualStudio_askQuestions", "runSubagent", "tool_result",
                "file_search", "grep_search", "list_dir", "read_file",
                "semantic_search", "fetch_webpage", "run_in_terminal",
                "create_file", "replace_string_in_file", "edit_notebook_file",
                "create_directory"
            };

            string result = text;
            foreach (var tag in blockTags)
            {
                // 移除自闭合标签
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\s*" + tag + @"(\s+[^>]*)?\s*/\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // 移除配对标签及其内容
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\s*" + tag + @"(\s+[^>]*)?\s*>.*?</\s*" + tag + @"\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            }

            // ── 移除残留的独立开标签/闭标签 ──
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?\s*(?:DSML|function_calls?|tool_calls?|invoke|parameter|VisualStudio_askQuestions|runSubagent|tool_result)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // ── 移除 <｜end▁of▁thinking｜>  and  thinking 伪标签 ──
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?\s*(?:response|thinking|analysis|reasoning|plan|reflection)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return result.Trim();
        }

        /// <summary>
        /// 使用 AI 创建实现计划（JSON 格式）。
        /// </summary>
        private async Task<AgentTaskPlan?> CreatePlanAsync(
            string userMessage, string discoveryContext, AgentContext context)
        {
            var L = LocalizationService.Instance;
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

            AddLog("INFO", L["agent.log.planGeneratingJson"]);
            string json = await CallAiLongAsync(
                Definition.SystemPrompt, planPrompt, extraSystemMessages, ct,
                maxTokens: 8192, toolChoice: "none");
            AddLog("INFO", L["agent.log.planJsonReceived"]);

            // ── 诊断：记录原始响应用于调试 JSON 解析失败 ──
            string rawResponse = json;
            // 先剥离 DSML/XML 标签（DeepSeek V4 可能在 toolChoice=none 时仍泄露工具调用意图到 content）
            json = StripDsmlContent(json);
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
                var L2 = LocalizationService.Instance;
                AddLog("WARN", string.Format(L2["agent.plan.jsonParseFailed"], ex.Message));
                AddLog("INFO", string.Format(L2["agent.log.planJsonRawResponse"], truncated));
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
            sb.AppendLine(SB["plan.creation.jsonFormat"]);
            sb.AppendLine("{");
            sb.AppendLine("  \"title\": \"任务标题\",");
            sb.AppendLine("  \"steps\": [");
            sb.AppendLine("    { \"index\": 1, \"title\": \"步骤标题\", \"description\": \"详细描述\", \"requiresApproval\": false }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine(SB["plan.creation.outputJson"]);

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
            sb.AppendLine(L["plan.format.seePanel"]);
            sb.AppendLine();
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
                // RAG-MARK: no-truncate — 不再截断代码库发现上下文
                // RAG-SOURCE: codebase-discovery 代码库探索发现结果（PlanAgent 计划生成）
                extraSystemMessages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = L["plan.md.codebaseFindings"] + "\n\n" + discoveryContext
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

            AddLog("INFO", L["agent.log.planGeneratingMd"]);
            string markdown = await CallAiLongAsync(
                Definition.SystemPrompt, prompt.ToString(), extraSystemMessages, ct,
                maxTokens: 16384, toolChoice: "none");
            AddLog("INFO", L["agent.log.planMdGenerated"]);

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
