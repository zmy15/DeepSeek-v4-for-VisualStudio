using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Agent 调度器 — 多 Agent 系统的中央路由。
    /// 
    /// 职责：
    /// 1. 根据用户意图路由到合适的 Agent（Ask / Plan / Edit）
    /// 2. 管理 Agent 之间的 Handoff
    /// 3. 协调 Plan → Explore → Edit 的工作流
    /// 4. 统一管理权限请求和事件
    /// 
    /// 参考: VS Code Copilot Chat Agent Orchestration
    /// </summary>
    public class AgentDispatcher : IAgentDispatcher
    {
        private readonly DeepSeekApiService _apiService;
        private readonly BuiltInToolService? _builtInToolService;
        private McpManagerService? _mcpManager;

        // ── Agent 实例（懒加载） ──
        private AskAgent? _askAgent;
        private ExploreAgent? _exploreAgent;
        private PlanAgent? _planAgent;
        private EditAgent? _editAgent;
        private BuildAgent? _buildAgent;

        // ── 属性（注入工具服务） ──
        public AskAgent AskAgent
        {
            get
            {
                if (_askAgent == null)
                {
                    _askAgent = new AskAgent(_apiService);
                    InjectToolServices(_askAgent);
                }
                return _askAgent;
            }
        }
        public ExploreAgent ExploreAgent
        {
            get
            {
                if (_exploreAgent == null)
                {
                    _exploreAgent = new ExploreAgent(_apiService);
                    InjectToolServices(_exploreAgent);
                }
                return _exploreAgent;
            }
        }
        public PlanAgent PlanAgent
        {
            get
            {
                if (_planAgent == null)
                {
                    _planAgent = new PlanAgent(_apiService);
                    InjectToolServices(_planAgent);
                }
                return _planAgent;
            }
        }
        public EditAgent EditAgent
        {
            get
            {
                if (_editAgent == null)
                {
                    _editAgent = new EditAgent(_apiService);
                    InjectToolServices(_editAgent);
                }
                return _editAgent;
            }
        }
        public BuildAgent BuildAgent
        {
            get
            {
                if (_buildAgent == null)
                {
                    _buildAgent = new BuildAgent(_apiService);
                    InjectToolServices(_buildAgent);
                }
                return _buildAgent;
            }
        }

        /// <summary>
        /// 向 Agent 注入工具服务（BuiltInToolService 和 McpManagerService）。
        /// </summary>
        private void InjectToolServices(BaseAgent agent)
        {
            if (agent.BuiltInTools == null && _builtInToolService != null)
                agent.BuiltInTools = _builtInToolService;
            if (agent.McpManager == null && _mcpManager != null)
                agent.McpManager = _mcpManager;
        }

        /// <summary>
        /// 更新 MCP 管理器引用（MCP 异步初始化完成后调用）。
        /// 会将新的 MCP 管理器注入到所有已创建的 Agent 实例中。
        /// </summary>
        public void UpdateMcpManager(McpManagerService mcpManager)
        {
            _mcpManager = mcpManager;

            // 注入到已创建的 Agent 实例
            if (_askAgent != null) _askAgent.McpManager = mcpManager;
            if (_exploreAgent != null) _exploreAgent.McpManager = mcpManager;
            if (_planAgent != null) _planAgent.McpManager = mcpManager;
            if (_editAgent != null) _editAgent.McpManager = mcpManager;
            if (_buildAgent != null) _buildAgent.McpManager = mcpManager;

            Logger.Info($"[AgentDispatcher] MCP 管理器已注入 (工具数: {mcpManager.AllTools.Count})");
        }

        /// <summary>
        /// IAgentDispatcher 接口实现 — 接受 IMcpManagerService 并委托给 UpdateMcpManager。
        /// </summary>
        public void SetMcpManager(IMcpManagerService? mcpManager)
        {
            if (mcpManager is McpManagerService concrete)
                UpdateMcpManager(concrete);
        }

        /// <summary>当前活跃的 Agent 类型</summary>
        public AgentType ActiveAgentType { get; private set; } = AgentType.Ask;

        /// <summary>
        /// 会话上下文管理器引用（由 DeepSeekChatControl 注入），
        /// 用于在 Agent 执行时注入对话历史以优化缓存命中率。
        /// </summary>
        public ConversationContextManager? ContextManager { get; set; }

        /// <summary>
        /// 获取当前活跃 Agent 允许使用的工具名称列表。
        /// 用于过滤 MCP 工具定义，确保 Agent 只能调用其声明 whitelist 中的工具。
        /// </summary>
        public List<string>? ActiveAgentAllowedTools => GetActiveAgent()?.Definition.AllowedTools;

        /// <summary>当前正在执行的任务计划</summary>
        public AgentTaskPlan? ActivePlan { get; set; }

        /// <summary>当前待确认的权限请求</summary>
        public AgentPermissionRequest? PendingPermission => GetActiveAgent()?.PendingPermission;

        // ── 事件（转发到 UI） ──
        public event Action<AgentTaskPlan>? PlanUpdated;
        public event Action<AgentPermissionRequest>? PermissionRequested;
        public event Action<AgentQuestionRequest>? QuestionsRequested;
        public event Action<AgentLogEntry>? LogEntryAdded;
        public event Action<AgentFileChangeEventArgs>? FileChangeNotified;

        public AgentDispatcher(DeepSeekApiService apiService,
            BuiltInToolService? builtInToolService = null,
            McpManagerService? mcpManager = null)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            _builtInToolService = builtInToolService;
            _mcpManager = mcpManager;
            WireAgentEvents();
        }

        /// <summary>
        /// 初始化/重新绑定 Agent 事件。
        /// </summary>
        private void WireAgentEvents()
        {
            // 在首次访问时绑定，所以这里只是占位
            // 实际事件在 GetOrCreate 时绑定
        }

        /// <summary>
        /// 获取当前活跃的 BaseAgent 实例。
        /// </summary>
        public BaseAgent? GetActiveAgent()
        {
            return ActiveAgentType switch
            {
                AgentType.Ask => _askAgent,
                AgentType.Explore => _exploreAgent,
                AgentType.Plan => _planAgent,
                AgentType.Edit => _editAgent,
                AgentType.Build => _buildAgent,
                _ => _askAgent,
            };
        }

        /// <summary>
        /// 确保 Agent 实例已创建并绑定事件。
        /// </summary>
        private BaseAgent EnsureAgent(AgentType type)
        {
            BaseAgent agent = type switch
            {
                AgentType.Ask => AskAgent,
                AgentType.Explore => ExploreAgent,
                AgentType.Plan => PlanAgent,
                AgentType.Edit => EditAgent,
                AgentType.Build => BuildAgent,
                _ => AskAgent,
            };

            // ── 注入 ExploreAgent 到 EditAgent，使其能智能发现相关文件 ──
            // ── EditAgent 特殊处理：PlanUpdated 事件 ──
            if (agent is EditAgent editAgent)
            {
                if (editAgent.ExploreAgent == null)
                    editAgent.ExploreAgent = ExploreAgent;

                editAgent.PlanUpdated -= OnEditAgentPlanUpdated;
                editAgent.PlanUpdated += OnEditAgentPlanUpdated;
            }

            // ── 注入 ExploreAgent 到 AskAgent，使其能委托探索任务 ──
            if (agent is AskAgent askAgent)
            {
                if (askAgent.ExploreAgent == null)
                    askAgent.ExploreAgent = ExploreAgent;
            }

            // ── 注入 ExploreAgent 到 PlanAgent，使其发现阶段能正确执行工具调用 ──
            // PlanAgent 内部创建的 ExploreAgent 缺少工具服务注入，必须替换为
            // AgentDispatcher 中已正确注入 BuiltInTools/McpManager 的实例。
            // 注意：PlanAgent 构造函数会创建私有 ExploreAgent，因此必须无条件替换（不能用 == null 检查）。
            // 替换后的共享 ExploreAgent 携带文件列表缓存，可跨 Agent 复用（以后会被 RAG 替代）。
            if (agent is PlanAgent planAgent)
            {
                planAgent.ExploreAgent = ExploreAgent;
            }

            // ── 注入 ExploreAgent 到 BuildAgent，使其能通过 runSubagent 委派探索任务 ──
            if (agent is BuildAgent buildAgent)
            {
                if (buildAgent.ExploreAgent == null)
                    buildAgent.ExploreAgent = ExploreAgent;
            }

            // 绑定事件（如果尚未绑定）
            agent.PermissionRequested -= OnAgentPermissionRequested;
            agent.PermissionRequested += OnAgentPermissionRequested;
            agent.QuestionsRequested -= OnAgentQuestionsRequested;
            agent.QuestionsRequested += OnAgentQuestionsRequested;
            agent.LogEntryAdded -= OnAgentLogEntryAdded;
            agent.LogEntryAdded += OnAgentLogEntryAdded;
            agent.FileChangeNotified -= OnAgentFileChangeNotified;
            agent.FileChangeNotified += OnAgentFileChangeNotified;

            return agent;
        }

        #region Event Forwarding

        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            PermissionRequested?.Invoke(request);
        }

        private void OnAgentQuestionsRequested(AgentQuestionRequest request)
        {
            QuestionsRequested?.Invoke(request);
        }

        private void OnAgentLogEntryAdded(AgentLogEntry entry)
        {
            LogEntryAdded?.Invoke(entry);
        }

        private void OnAgentFileChangeNotified(AgentFileChangeEventArgs args)
        {
            FileChangeNotified?.Invoke(args);
        }

        private void OnEditAgentPlanUpdated(AgentTaskPlan plan)
        {
            PlanUpdated?.Invoke(plan);
        }

        #endregion

        #region Intent Analysis & Routing

        /// <summary>
        /// 分析用户意图并返回路由结果。
        /// 优先使用 AI 分类，失败时回退到启发式规则。
        /// </summary>
        /// <param name="userMessage">用户消息文本</param>
        /// <param name="conversationContext">可选的对话上下文摘要，帮助 AI 理解用户在指代什么（如"进行修复"后的上文）</param>
        /// <param name="ct">取消令牌</param>
        public async Task<AgentRoutingResult> RouteAsync(string userMessage, string? conversationContext = null, CancellationToken ct = default)
        {
            // ── 用户显式指定 Agent ──
            if (userMessage.StartsWith("@"))
            {
                return ParseExplicitAgentRoute(userMessage);
            }

            // ── AI 分类 ──
            try
            {
                // 构建分类提示：如果有对话上下文，附加到用户消息中帮助 AI 理解指代
                string messageForClassification = userMessage;
                if (!string.IsNullOrWhiteSpace(conversationContext))
                {
                    messageForClassification = $"上文对话摘要:\n{conversationContext}\n\n当前用户消息: {userMessage}";
                }

                string classificationPrompt = AiPrompts.AgentRoutingUserPrompt
                    .Replace("{0}", messageForClassification);

                var askAgent = EnsureAgent(AgentType.Ask);
                string response;
                try
                {
                    response = await askAgent.CallAiShortAsync(
                        AiPrompts.AgentRoutingSystemPrompt,
                        classificationPrompt, ct, maxTokens: 512);
                }
                catch (Exception ex)
                {
                    Logger.Info($"[AgentDispatcher] AI 路由调用失败 ({ex.GetType().Name}: {ex.Message})，回退到启发式");
                    return HeuristicRoute(userMessage);
                }

                string json = ExtractJsonFromText(response);
                // 规范化 targetAgent 值：AI 可能返回小写/混合大小写（如 "ask"/"Ask"/"ASK"），
                // 统一转换为 PascalCase 以确保 JsonStringEnumConverter 能正常反序列化
                json = NormalizeAgentTypeInJson(json);

                AgentRoutingResult? routing = null;
                try
                {
                    routing = JsonSerializer.Deserialize<AgentRoutingResult>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException jex)
                {
                    Logger.Info($"[AgentDispatcher] AI 路由 JSON 解析失败: {jex.Message}");
                    routing = FallbackParseRouting(response, json);
                }
                catch (FormatException fex)
                {
                    Logger.Info($"[AgentDispatcher] AI 路由格式异常: {fex.Message}");
                    routing = FallbackParseRouting(response, json);
                }

                if (routing != null && Enum.IsDefined(typeof(AgentType), routing.TargetAgent))
                {
                    Logger.Info($"[AgentDispatcher] AI 路由: → {routing.TargetAgent} "
                        + $"(置信度: {routing.Confidence}, 需要规划: {routing.NeedsPlanning})");

                    // ── 低置信度补正：AI 对短消息/缺乏上下文的消息可能误判为 Ask ──
                    // 用启发式规则做二次校验，避免"进行修复"/"帮我改一下"被路由到问答 Agent
                    if (string.Equals(routing.Confidence, "low", StringComparison.OrdinalIgnoreCase))
                    {
                        var heuristic = HeuristicRoute(userMessage);
                        // 只有启发式结果与 AI 不同，且启发式结果更具体（非 Ask）时才覆盖
                        if (heuristic.TargetAgent != AgentType.Ask
                            && heuristic.TargetAgent != routing.TargetAgent)
                        {
                            Logger.Info($"[AgentDispatcher] 🔄 低置信度补正: AI→{routing.TargetAgent}, "
                                + $"启发式→{heuristic.TargetAgent}, 采用启发式结果");
                            return heuristic;
                        }
                    }

                    return routing;
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[AgentDispatcher] AI 路由失败 ({ex.Message})，回退到启发式");
            }

            // ── 启发式回退 ──
            return HeuristicRoute(userMessage);
        }

        /// <summary>
        /// 解析用户显式指定的 Agent（如 "@edit 修复这个bug"）。
        /// </summary>
        private static AgentRoutingResult ParseExplicitAgentRoute(string userMessage)
        {
            var parts = userMessage.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string agentName = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

            AgentType target = agentName switch
            {
                "ask" or "问答" => AgentType.Ask,
                "plan" or "规划" => AgentType.Plan,
                "edit" or "修改" => AgentType.Edit,
                "explore" or "探索" => AgentType.Explore,
                "build" or "构建" or "编译" => AgentType.Build,
                _ => AgentType.Ask,
            };

            return new AgentRoutingResult
            {
                TargetAgent = target,
                Confidence = "high",
                Reason = $"用户显式指定 @{agentName}",
                NeedsPlanning = target == AgentType.Plan,
                IsExplicit = true,
            };
        }

        /// <summary>
        /// 启发式路由：基于关键词判断。
        /// </summary>
        private static AgentRoutingResult HeuristicRoute(string userMessage)
        {
            // 规划类关键词
            var planKeywords = new[] { "设计", "架构", "重构方案", "技术方案", "规划",
                "系统设计", "如何实现", "怎么做", "整体", "框架", "design", "architecture",
                "plan", "方案", "策略", "选型" };

            // 代码修改类关键词
            var editKeywords = new[] { "修改", "改", "添加功能", "实现",
                "implement", "重构", "refactor", "优化", "optimize",
                "写一个", "创建一个", "增加", "删除", "更新代码", "改代码", "帮我写",
                "coding", "代码", "函数", "function", "class", "类", "接口", "interface",
                "方法", "method",
                "改一下", "修改一下", "完善", "改进", "测试", "单元测试", "生成", "编写" };

            // 构建修复类关键词（优先级高于普通修改）
            var buildKeywords = new[] { "编译不过", "编译失败", "编译错误", "构建失败",
                "build error", "build failed", "生成失败", "生成错误", "链接错误",
                "link error", "无法编译", "编译不通过", "生成解决方案",
                "修复", "fix", "bug", "报错", "错误", "出错了", "不工作",
                "报异常", "exception", "崩溃", "crash", "解决", "排查" };

            bool hasPlanKeyword = planKeywords.Any(k =>
                userMessage.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasEditKeyword = editKeywords.Any(k =>
                userMessage.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasBuildKeyword = buildKeywords.Any(k =>
                userMessage.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

            // ── 构建修复关键词 → 直接路由到 Build Agent ──
            if (hasBuildKeyword)
            {
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Build,
                    Confidence = "high",
                    Reason = "检测到构建/编译错误关键词，路由到 Build Agent 进行诊断修复",
                    NeedsPlanning = false,
                };
            }

            if (hasPlanKeyword && hasEditKeyword)
            {
                // 两者都有 → 需要先规划再执行 → Edit + needsPlanning
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Edit,
                    Confidence = "medium",
                    Reason = "任务同时涉及规划和修改关键词，先规划再执行",
                    NeedsPlanning = true,
                };
            }

            if (hasPlanKeyword)
            {
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Plan,
                    Confidence = "medium",
                    Reason = "检测到规划相关关键词",
                    NeedsPlanning = true,
                };
            }

            if (hasEditKeyword)
            {
                // 复杂修改 → 建议先规划
                bool isComplex = userMessage.Length > 50
                    && (userMessage.Contains("重构") || userMessage.Contains("架构")
                        || userMessage.Contains("系统") || userMessage.Contains("模块"));
                return new AgentRoutingResult
                {
                    TargetAgent = AgentType.Edit,
                    Confidence = "medium",
                    Reason = "检测到代码修改关键词",
                    NeedsPlanning = isComplex,
                };
            }

            // 默认 → Ask
            return new AgentRoutingResult
            {
                TargetAgent = AgentType.Ask,
                Confidence = "high",
                Reason = "无特定关键词，默认问答模式",
                NeedsPlanning = false,
            };
        }

        #endregion

        #region Core Execution

        /// <summary>
        /// 总执行入口。
        /// 根据路由结果选择 Agent 并执行，处理 Handoff 链。
        /// </summary>
        public async Task<AgentResult> ExecuteAsync(
            string userMessage,
            AgentContext context,
            AgentRoutingResult? routingOverride = null)
        {
            // ── 步骤 1: 路由 ──
            var routing = routingOverride ?? await RouteAsync(userMessage, ct: context.CancellationToken);
            ActiveAgentType = routing.TargetAgent;

            // ── 注入 ContextManager 到 AgentContext（供 Agent 内部构建上下文感知消息）──
            if (context.ContextManager == null)
                context.ContextManager = ContextManager;

            Logger.Info($"[AgentDispatcher] 路由到 {routing.TargetAgent} Agent "
                + $"(原因: {routing.Reason}, 需要规划: {routing.NeedsPlanning})");

            // ── 步骤 2: 如果需要规划，先走 Plan Agent ──
            AgentTaskPlan? plan = null;
            if (routing.NeedsPlanning || routing.TargetAgent == AgentType.Plan)
            {
                var planAgent = (PlanAgent)EnsureAgent(AgentType.Plan);
                planAgent.Context = context;
                ActiveAgentType = AgentType.Plan;

                Logger.Info("[AgentDispatcher] 启动 Plan Agent...");
                var planResult = await planAgent.ExecuteAsync(userMessage, context);

                if (planResult.Success && planResult.Plan != null)
                {
                    plan = planResult.Plan;
                    plan.IsFromPlanAgent = true; // 标记来自 Plan Agent，UI 据此显示下方面板
                    ActivePlan = plan;
                    context.ActivePlan = plan;

                    // ── 通知 UI 显示计划 ──
                    PlanUpdated?.Invoke(plan);

                    Logger.Info($"[AgentDispatcher] Plan Agent 产出: {plan.Steps.Count} 个步骤");
                }

                // 如果用户只想要计划（不执行），或需要先规划再让用户决定是否执行，直接返回
                // Plan Agent 的职责是规划，不自动流转到 Edit；用户需通过 UI Handoff 按钮显式触发 Edit
                if (routing.TargetAgent == AgentType.Plan || routing.NeedsPlanning)
                {
                    return planResult;
                }
            }

            // ── 步骤 3: 执行 ──
            AgentResult result;
            switch (routing.TargetAgent)
            {
                case AgentType.Ask:
                    var askAgent = (AskAgent)EnsureAgent(AgentType.Ask);
                    askAgent.Context = context;
                    ActiveAgentType = AgentType.Ask;
                    result = await askAgent.ExecuteAsync(userMessage, context);
                    break;

                case AgentType.Edit:
                    var editAgent = (EditAgent)EnsureAgent(AgentType.Edit);
                    editAgent.Context = context;
                    ActiveAgentType = AgentType.Edit;

                    // 如果有计划，注入到上下文
                    if (plan != null)
                    {
                        context.ActivePlan = plan;
                    }

                    result = await editAgent.ExecuteAsync(userMessage, context);
                    break;

                case AgentType.Explore:
                    var exploreAgent = (ExploreAgent)EnsureAgent(AgentType.Explore);
                    exploreAgent.Context = context;
                    ActiveAgentType = AgentType.Explore;
                    result = await exploreAgent.ExecuteAsync(userMessage, context);
                    break;

                case AgentType.Build:
                    var buildAgent = (BuildAgent)EnsureAgent(AgentType.Build);
                    buildAgent.Context = context;
                    ActiveAgentType = AgentType.Build;
                    result = await buildAgent.ExecuteAsync(userMessage, context);
                    break;

                default:
                    var defaultAgent = (AskAgent)EnsureAgent(AgentType.Ask);
                    defaultAgent.Context = context;
                    ActiveAgentType = AgentType.Ask;
                    result = await defaultAgent.ExecuteAsync(userMessage, context);
                    break;
            }

            return result;
        }

        /// <summary>
        /// 执行 Handoff：从一个 Agent 切换到另一个。
        /// </summary>
        public async Task<AgentResult> ExecuteHandoffAsync(
            AgentHandoff handoff, AgentContext context)
        {
            Logger.Info($"[AgentDispatcher] Handoff: → {handoff.TargetAgent} ({handoff.Label})");

            ActiveAgentType = handoff.TargetAgent;
            var agent = EnsureAgent(handoff.TargetAgent);
            agent.Context = context;

            // ── 构建 Handoff prompt（包含完整计划上下文）──
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(handoff.Prompt);
            sb.AppendLine();

            // ── 🔄 Handoff 上下文提示：避免重复探索 ──
            // 前一 Agent 已经探索并读取过项目文件，文件内容在对话历史中可见。
            // 接收 Agent 应优先从对话历史获取上下文，而非重新探索相同文件。
            sb.AppendLine(AiPrompts.HandoffContextPrompt);
            sb.AppendLine();

            if (ActivePlan != null)
            {
                sb.AppendLine();
                sb.AppendLine($"## 任务计划: {ActivePlan.Title}");
                sb.AppendLine($"共 {ActivePlan.Steps.Count} 个步骤：");
                sb.AppendLine();

                foreach (var s in ActivePlan.Steps)
                {
                    sb.AppendLine($"### 步骤 {s.Index}: {s.Title}");
                    sb.AppendLine(s.Description);
                    sb.AppendLine();
                }
            }

            // ── 注入 plan.md 内容（如果存在）──
            string? planFilePath = context.PlanFilePath ?? ActivePlan?.PlanFilePath;
            if (!string.IsNullOrEmpty(planFilePath) && System.IO.File.Exists(planFilePath))
            {
                try
                {
                    string planMd = await Task.Run(() => System.IO.File.ReadAllText(planFilePath));
                    // RAG-SOURCE: file-read 读取 plan.md 计划文档（Agent Handoff 上下文）
                    if (planMd.Length > 0)
                    {
                        sb.AppendLine("## 📄 详细计划文档 (plan.md)");
                        // RAG-MARK: no-truncate — 不再截断计划文档，完整传递给目标 Agent
                        sb.AppendLine(planMd);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AgentDispatcher] 读取 plan.md 失败: {ex.Message}");
                }
            }

            string handoffMessage = sb.ToString();
            context.ActivePlan = ActivePlan;
            context.IsPlanningMode = true; // Handoff 后进入 Planning 模式执行

            return await agent.ExecuteAsync(handoffMessage, context);
        }

        #endregion

        #region Intent Analysis

        /// <summary>
        /// 意图分析：判断用户请求是 CodeChange 还是 QandA。
        /// </summary>
        public async Task<AgentIntent> AnalyzeIntentAsync(string userMessage, CancellationToken ct = default)
        {
            var routing = await RouteAsync(userMessage, ct: ct);

            return routing.TargetAgent switch
            {
                AgentType.Ask => AgentIntent.QandA,
                AgentType.Explore => AgentIntent.QandA,
                AgentType.Plan => AgentIntent.CodeChange,
                AgentType.Edit => AgentIntent.CodeChange,
                AgentType.Build => AgentIntent.CodeChange,
                _ => AgentIntent.QandA,
            };
        }

        /// <summary>
        /// 任务分解：分析用户需求并产出 AgentTaskPlan。
        /// </summary>
        public async Task<AgentTaskPlan> DecomposeTaskAsync(
            string userMessage, string? fileContext = null, CancellationToken ct = default)
        {
            var context = new AgentContext
            {
                FileContext = fileContext,
                CancellationToken = ct,
            };

            var planAgent = (PlanAgent)EnsureAgent(AgentType.Plan);
            var result = await planAgent.ExecuteAsync(userMessage, context);

            return result.Plan ?? new AgentTaskPlan
            {
                Intent = AgentIntent.CodeChange,
                Title = LocalizationService.Instance["agent.step.executeCodeChange"],
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

        #endregion

        #region Permission

        /// <summary>
        /// 响应权限请求。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            GetActiveAgent()?.RespondToPermission(requestId, approved);
        }

        /// <summary>
        /// 响应提问回答（VisualStudio_askQuestions）。
        /// </summary>
        public void RespondToQuestions(string requestId, string answersJson)
        {
            GetActiveAgent()?.RespondToQuestions(requestId, answersJson);
        }

        /// <summary>
        /// 响应文件删除确认：先完成权限响应，若确认则通过 EnvDTE 执行实际删除。
        /// </summary>
        /// <param name="requestId">权限请求 ID</param>
        /// <param name="approved">用户是否确认删除</param>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        public async Task RespondToFileDeletePermissionAsync(string requestId, bool approved, List<string> filePaths)
        {
            // 先完成权限响应（解除 Agent 的等待）
            GetActiveAgent()?.RespondToPermission(requestId, approved);

            if (approved && filePaths != null && filePaths.Count > 0)
            {
                await DeleteFilesViaEnvDTEAsync(filePaths);
            }
        }

        /// <summary>
        /// 通过 EnvDTE 项目系统删除文件。
        /// 先尝试通过 ProjectItem.Delete() 从项目中移除，再删除磁盘文件。
        /// 必须在 UI 主线程上调用。
        /// </summary>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        public static async Task DeleteFilesViaEnvDTEAsync(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0) return;

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .SwitchToMainThreadAsync();

            var dte = (EnvDTE.DTE?)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                .GetService(typeof(EnvDTE.DTE));
            if (dte == null || dte.Solution == null || !dte.Solution.IsOpen)
            {
                // 回退：仅删除磁盘文件
                foreach (string fp in filePaths)
                {
                    try { if (System.IO.File.Exists(fp)) System.IO.File.Delete(fp); }
                    catch (Exception ex) { Logger.Warn($"[AgentDispatcher] 磁盘删除失败: {fp} - {ex.Message}"); }
                }
                Logger.Warn("[AgentDispatcher] DTE 不可用，仅执行磁盘文件删除");
                return;
            }

            foreach (string filePath in filePaths)
            {
                try
                {
                    // 尝试查找 ProjectItem 并从项目中移除
                    EnvDTE.ProjectItem? item = FindProjectItemByPath(dte, filePath);
                    if (item != null)
                    {
                        item.Delete();
                        Logger.Info($"[AgentDispatcher] ✅ 已通过 EnvDTE 从项目中删除: {filePath}");
                    }
                    else
                    {
                        // 未找到 ProjectItem，直接删除磁盘文件
                        if (System.IO.File.Exists(filePath))
                        {
                            System.IO.File.Delete(filePath);
                            Logger.Info($"[AgentDispatcher] ✅ 已从磁盘删除: {filePath}");
                        }
                        else
                        {
                            Logger.Warn($"[AgentDispatcher] 文件不存在，跳过: {filePath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AgentDispatcher] 删除文件失败: {filePath} - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 在解决方案中递归查找匹配给定路径的 ProjectItem。
        /// </summary>
        private static EnvDTE.ProjectItem? FindProjectItemByPath(EnvDTE.DTE dte, string filePath)
        {
            try
            {
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    var result = FindProjectItemRecursive(project.ProjectItems, filePath);
                    if (result != null) return result;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 递归搜索 ProjectItems 匹配文件路径。
        /// </summary>
        private static EnvDTE.ProjectItem? FindProjectItemRecursive(
            EnvDTE.ProjectItems? items, string filePath)
        {
            if (items == null) return null;

            foreach (EnvDTE.ProjectItem item in items)
            {
                try
                {
                    // 获取 ProjectItem 的完整路径（通过 Properties）
                    string? itemPath = null;
                    try
                    {
                        itemPath = item.Properties?.Item("FullPath")?.Value?.ToString();
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(itemPath) &&
                        string.Equals(itemPath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }

                    // 递归搜索子项
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        var found = FindProjectItemRecursive(item.ProjectItems, filePath);
                        if (found != null) return found;
                    }
                }
                catch
                {
                    // 跳过无法访问的 ProjectItem
                }
            }
            return null;
        }

        #endregion

        #region Cancel

        /// <summary>
        /// 取消当前任务。
        /// </summary>
        public void Cancel()
        {
            (_editAgent as EditAgent)?.Cancel();
            Logger.Info("[AgentDispatcher] 任务已取消");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _askAgent?.Dispose();
            _exploreAgent?.Dispose();
            _planAgent?.Dispose();
            _editAgent?.Dispose();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// JSON 反序列化失败时的回退解析：从 AI 原始响应文本中提取 Agent 类型。
        /// 使用正则匹配 targetAgent 值 + 关键词检测作为双重保险。
        /// </summary>
        private static AgentRoutingResult? FallbackParseRouting(string rawResponse, string extractedJson)
        {
            // ── 策略 1：从提取的 JSON 中用简单正则提取 targetAgent ──
            var agentMatch = System.Text.RegularExpressions.Regex.Match(
                extractedJson,
                @"""targetAgent""\s*:\s*""(\w+)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (agentMatch.Success)
            {
                string agentName = agentMatch.Groups[1].Value;
                AgentType? parsed = ParseAgentType(agentName);
                if (parsed.HasValue)
                {
                    return new AgentRoutingResult
                    {
                        TargetAgent = parsed.Value,
                        Confidence = "low",
                        Reason = "文本回退解析（targetAgent 正则匹配）"
                    };
                }
            }

            // ── 策略 2：从原始 AI 响应中关键词检测 ──
            string lower = rawResponse.ToLowerInvariant();
            if (lower.Contains("\"edit\"") || lower.Contains("edit agent") || lower.Contains("代码修改"))
                return new AgentRoutingResult { TargetAgent = AgentType.Edit, Confidence = "low", Reason = "文本回退解析（关键词: edit）" };
            if (lower.Contains("\"plan\"") || lower.Contains("plan agent") || lower.Contains("规划"))
                return new AgentRoutingResult { TargetAgent = AgentType.Plan, Confidence = "low", Reason = "文本回退解析（关键词: plan）" };
            if (lower.Contains("\"explore\"") || lower.Contains("explore agent") || lower.Contains("探索"))
                return new AgentRoutingResult { TargetAgent = AgentType.Explore, Confidence = "low", Reason = "文本回退解析（关键词: explore）" };
            if (lower.Contains("\"build\"") || lower.Contains("build agent") || lower.Contains("构建") || lower.Contains("编译"))
                return new AgentRoutingResult { TargetAgent = AgentType.Build, Confidence = "low", Reason = "文本回退解析（关键词: build）" };

            // ── 策略 3：返回 null 让调用方回退到启发式 ──
            Logger.Info("[AgentDispatcher] 回退解析失败，返回 null 以触发启发式路由");
            return null;
        }

        /// <summary>
        /// 将字符串解析为 AgentType（不区分大小写）。
        /// </summary>
        private static AgentType? ParseAgentType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return name.ToLowerInvariant() switch
            {
                "ask" => AgentType.Ask,
                "plan" => AgentType.Plan,
                "edit" => AgentType.Edit,
                "explore" => AgentType.Explore,
                "build" => AgentType.Build,
                _ => null
            };
        }

        /// <summary>
        /// 从文本中提取 JSON 对象（可能被 markdown 代码块、额外文本包裹）。
        /// 使用平衡括号计数以正确处理字符串内的 } 字符，并支持截断 JSON 的修复。
        /// </summary>
        private static string ExtractJsonFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            // ── 第一步：找到最外层 JSON 对象（平衡括号计数）──
            int jsonStart = -1;
            int braceDepth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;

                if (c == '{')
                {
                    if (braceDepth == 0) jsonStart = i;
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0 && jsonStart >= 0)
                    {
                        // 找到匹配的最外层 }
                        string extracted = text.Substring(jsonStart, i - jsonStart + 1);
                        return RepairTruncatedJson(extracted);
                    }
                }
            }

            // ── 第二步：回退到 IndexOf/LastIndexOf（处理简单情况）──
            int fallbackStart = text.IndexOf('{');
            int fallbackEnd = text.LastIndexOf('}');
            if (fallbackStart >= 0 && fallbackEnd > fallbackStart)
            {
                string extracted = text.Substring(fallbackStart, fallbackEnd - fallbackStart + 1);
                return RepairTruncatedJson(extracted);
            }

            return text.Trim();
        }

        /// <summary>
        /// 修复截断的 JSON：如果 JSON 被 token 限制截断（如字符串值未闭合），
        /// 尝试补全缺失的引号和括号，使其成为合法 JSON。
        /// </summary>
        private static string RepairTruncatedJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";

            // 快速验证：如果已经是合法 JSON，直接返回
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return json; // 合法 JSON，无需修复
            }
            catch
            {
                // JSON 不合法，尝试修复
            }

            // ── 修复策略：补全未闭合的字符串和括号 ──
            var sb = new System.Text.StringBuilder(json);
            bool inString = false;
            bool escaped = false;
            int braceDepth = 0;

            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
            }

            // 如果在字符串中间截断，闭合引号
            if (inString)
            {
                sb.Append('"');
            }

            // 闭合未匹配的括号
            while (braceDepth > 0)
            {
                sb.Append('}');
                braceDepth--;
            }

            string repaired = sb.ToString();

            // 验证修复结果
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(repaired);
                Logger.Info($"[AgentDispatcher] JSON 修复成功 (原始长度: {json.Length}, 修复后: {repaired.Length})");
                return repaired;
            }
            catch
            {
                // 修复失败，返回空 JSON 让调用方回退到启发式
                Logger.Info($"[AgentDispatcher] JSON 修复失败，回退到空对象");
                return "{}";
            }
        }

        /// <summary>
        /// 规范化 JSON 中的 targetAgent 字段值。
        /// AI 可能返回小写（如 "ask"）、大写（"ASK"）或混合大小写，
        /// 统一转换为 PascalCase（"Ask"）以确保 JsonStringEnumConverter 正常反序列化。
        /// </summary>
        private static string NormalizeAgentTypeInJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;

            return System.Text.RegularExpressions.Regex.Replace(
                json,
                @"""targetAgent""\s*:\s*""([^""]+)""",
                match =>
                {
                    string rawValue = match.Groups[1].Value;
                    string normalized = rawValue.ToLowerInvariant() switch
                    {
                        "ask" => "Ask",
                        "plan" => "Plan",
                        "edit" => "Edit",
                        "explore" => "Explore",
                        _ => rawValue.Length > 0
                            ? char.ToUpperInvariant(rawValue[0]) + rawValue.Substring(1).ToLowerInvariant()
                            : rawValue,
                    };
                    return $"\"targetAgent\": \"{normalized}\"";
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        #endregion
    }
}
