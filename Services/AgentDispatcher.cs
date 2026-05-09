using DeepSeek_v4_for_VisualStudio.Models;
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
    public class AgentDispatcher : IDisposable
    {
        private readonly DeepSeekApiService _apiService;

        // ── Agent 实例（懒加载） ──
        private AskAgent? _askAgent;
        private ExploreAgent? _exploreAgent;
        private PlanAgent? _planAgent;
        private EditAgent? _editAgent;

        // ── 属性 ──
        public AskAgent AskAgent => _askAgent ??= new AskAgent(_apiService);
        public ExploreAgent ExploreAgent => _exploreAgent ??= new ExploreAgent(_apiService);
        public PlanAgent PlanAgent => _planAgent ??= new PlanAgent(_apiService);
        public EditAgent EditAgent => _editAgent ??= new EditAgent(_apiService);

        /// <summary>当前活跃的 Agent 类型</summary>
        public AgentType ActiveAgentType { get; private set; } = AgentType.Ask;

        /// <summary>当前正在执行的任务计划</summary>
        public AgentTaskPlan? ActivePlan { get; set; }

        /// <summary>当前待确认的权限请求</summary>
        public AgentPermissionRequest? PendingPermission => GetActiveAgent()?.PendingPermission;

        // ── 事件（转发到 UI） ──
        public event Action<AgentTaskPlan>? PlanUpdated;
        public event Action<AgentPermissionRequest>? PermissionRequested;
        public event Action<AgentLogEntry>? LogEntryAdded;

        public AgentDispatcher(DeepSeekApiService apiService)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
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
        private BaseAgent? GetActiveAgent()
        {
            return ActiveAgentType switch
            {
                AgentType.Ask => _askAgent,
                AgentType.Explore => _exploreAgent,
                AgentType.Plan => _planAgent,
                AgentType.Edit => _editAgent,
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
                _ => AskAgent,
            };

            // 绑定事件（如果尚未绑定）
            agent.PermissionRequested -= OnAgentPermissionRequested;
            agent.PermissionRequested += OnAgentPermissionRequested;
            agent.LogEntryAdded -= OnAgentLogEntryAdded;
            agent.LogEntryAdded += OnAgentLogEntryAdded;

            // EditAgent 特殊处理：PlanUpdated 事件
            if (agent is EditAgent editAgent)
            {
                editAgent.PlanUpdated -= OnEditAgentPlanUpdated;
                editAgent.PlanUpdated += OnEditAgentPlanUpdated;
            }

            return agent;
        }

        #region Event Forwarding

        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            PermissionRequested?.Invoke(request);
        }

        private void OnAgentLogEntryAdded(AgentLogEntry entry)
        {
            LogEntryAdded?.Invoke(entry);
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
        public async Task<AgentRoutingResult> RouteAsync(string userMessage, CancellationToken ct = default)
        {
            // ── 用户显式指定 Agent ──
            if (userMessage.StartsWith("@"))
            {
                return ParseExplicitAgentRoute(userMessage);
            }

            // ── AI 分类 ──
            try
            {
                string classificationPrompt =
                    "你是一个意图分类器，工作在 Visual Studio 多 Agent 编程助手中。\n" +
                    "你的任务是判断用户消息应该路由到哪个 Agent。\n\n" +
                    "## 可用 Agent\n" +
                    "- **Ask**: 纯技术问答、代码解释、方案讨论。用户只是在问问题或聊天。\n" +
                    "  典型表达：什么是X、为什么Y、如何理解Z、对比A和B、解释这段代码。\n" +
                    "- **Plan**: 需要详细的实现计划。任务复杂、涉及多个文件、需要先研究再行动。\n" +
                    "  典型表达：设计一个X系统、规划Y功能的重构、制定Z的技术方案。\n" +
                    "- **Edit**: 直接修改代码。用户明确给出了修改目标且范围清晰。\n" +
                    "  典型表达：修复这个Bug、添加一个方法、改一下这段代码、实现X功能。\n\n" +
                    "## 判断标准\n" +
                    "- 如果任务复杂（涉及3+文件或需要架构设计），路由到 Plan\n" +
                    "- 如果是明确的、范围小的代码修改，路由到 Edit\n" +
                    "- 如果是纯问答或聊天，路由到 Ask\n\n" +
                    "## 输出要求\n" +
                    "只输出一个 JSON:\n" +
                    "{\"targetAgent\":\"Ask|Plan|Edit\",\"confidence\":\"high|medium|low\",\"needsPlanning\":true|false,\"reason\":\"简短理由\"}\n\n" +
                    "## 用户消息\n" + userMessage + "\n\n路由 JSON:";

                var askAgent = EnsureAgent(AgentType.Ask);
                string response = await askAgent.CallAiShortAsync(
                    "你只返回 JSON，不返回任何其他内容。",
                    classificationPrompt, ct, maxTokens: 256);

                string json = ExtractJsonFromText(response);
                // 规范化 targetAgent 值：AI 可能返回小写/混合大小写（如 "ask"/"Ask"/"ASK"），
                // 统一转换为 PascalCase 以确保 JsonStringEnumConverter 能正常反序列化
                json = NormalizeAgentTypeInJson(json);
                var routing = JsonSerializer.Deserialize<AgentRoutingResult>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (routing != null && Enum.IsDefined(typeof(AgentType), routing.TargetAgent))
                {
                    Logger.Info($"[AgentDispatcher] AI 路由: → {routing.TargetAgent} "
                        + $"(置信度: {routing.Confidence}, 需要规划: {routing.NeedsPlanning})");
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
            var editKeywords = new[] { "修改", "修复", "fix", "改", "添加功能", "实现",
                "implement", "重构", "refactor", "优化", "optimize", "bug", "错误", "报错",
                "写一个", "创建一个", "增加", "删除", "更新代码", "改代码", "帮我写",
                "coding", "代码", "函数", "function", "class", "类", "接口", "interface",
                "方法", "method", "出错了", "不工作", "报异常", "exception", "崩溃", "crash",
                "改一下", "修改一下", "完善", "改进", "测试", "单元测试", "生成", "编写" };

            bool hasPlanKeyword = planKeywords.Any(k =>
                userMessage.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasEditKeyword = editKeywords.Any(k =>
                userMessage.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

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
            var routing = routingOverride ?? await RouteAsync(userMessage, context.CancellationToken);
            ActiveAgentType = routing.TargetAgent;

            Logger.Info($"[AgentDispatcher] 路由到 {routing.TargetAgent} Agent "
                + $"(原因: {routing.Reason}, 需要规划: {routing.NeedsPlanning})");

            // ── 步骤 2: 如果需要规划，先走 Plan Agent ──
            AgentTaskPlan? plan = null;
            if (routing.NeedsPlanning || routing.TargetAgent == AgentType.Plan)
            {
                var planAgent = (PlanAgent)EnsureAgent(AgentType.Plan);
                ActiveAgentType = AgentType.Plan;

                Logger.Info("[AgentDispatcher] 启动 Plan Agent...");
                var planResult = await planAgent.ExecuteAsync(userMessage, context);

                if (planResult.Success && planResult.Plan != null)
                {
                    plan = planResult.Plan;
                    ActivePlan = plan;
                    context.ActivePlan = plan;

                    // ── 通知 UI 显示计划 ──
                    PlanUpdated?.Invoke(plan);

                    Logger.Info($"[AgentDispatcher] Plan Agent 产出: {plan.Steps.Count} 个步骤");
                }

                // 如果用户只想要计划（不执行），直接返回
                if (routing.TargetAgent == AgentType.Plan)
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
                    ActiveAgentType = AgentType.Ask;
                    result = await askAgent.ExecuteAsync(userMessage, context);
                    break;

                case AgentType.Edit:
                    var editAgent = (EditAgent)EnsureAgent(AgentType.Edit);
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
                    ActiveAgentType = AgentType.Explore;
                    result = await exploreAgent.ExecuteAsync(userMessage, context);
                    break;

                default:
                    var defaultAgent = (AskAgent)EnsureAgent(AgentType.Ask);
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

            // 构建 Handoff prompt（包含原计划信息）
            string handoffMessage = handoff.Prompt;
            if (ActivePlan != null)
            {
                handoffMessage += $"\n\n计划: {ActivePlan.Title}\n步骤数: {ActivePlan.Steps.Count}";
            }

            context.ActivePlan = ActivePlan;
            return await agent.ExecuteAsync(handoffMessage, context);
        }

        #endregion

        #region Intent Analysis

        /// <summary>
        /// 意图分析：判断用户请求是 CodeChange 还是 QandA。
        /// </summary>
        public async Task<AgentIntent> AnalyzeIntentAsync(string userMessage, CancellationToken ct = default)
        {
            var routing = await RouteAsync(userMessage, ct);

            return routing.TargetAgent switch
            {
                AgentType.Ask => AgentIntent.QandA,
                AgentType.Explore => AgentIntent.QandA,
                AgentType.Plan => AgentIntent.CodeChange,
                AgentType.Edit => AgentIntent.CodeChange,
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

        #endregion

        #region Permission

        /// <summary>
        /// 响应权限请求。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            GetActiveAgent()?.RespondToPermission(requestId, approved);
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
        /// 从文本中提取 JSON（可能被 markdown 包裹）。
        /// </summary>
        private static string ExtractJsonFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";
            int jsonStart = text.IndexOf('{');
            int jsonEnd = text.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                return text.Substring(jsonStart, jsonEnd - jsonStart + 1);
            return text.Trim();
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
