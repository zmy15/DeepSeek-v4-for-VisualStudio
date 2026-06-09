using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // 多 Agent 类型定义
    // 参考: VS Code Copilot Chat Multi-Agent Architecture
    // ========================================================================

    /// <summary>
    /// Agent 类型枚举。
    /// 每个 Agent 有明确的职责边界，通过 handoff 机制流转。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentType
    {
        /// <summary>纯问答，不修改项目代码</summary>
        Ask,

        /// <summary>只读代码库探索，专注于搜索和分析</summary>
        Explore,

        /// <summary>研究和规划，禁止修改代码</summary>
        Plan,

        /// <summary>代码修改执行</summary>
        Edit,

        /// <summary>构建修复，专注于编译错误诊断与修复</summary>
        Build,
    }

    /// <summary>
    /// Agent 元数据定义。
    /// </summary>
    public class AgentDefinition
    {
        /// <summary>Agent 类型</summary>
        public AgentType Type { get; set; }

        /// <summary>Agent 名称（1-64 字符）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Agent 描述（用于 AI 路由判断）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>参数提示</summary>
        public string ArgumentHint { get; set; } = string.Empty;

        /// <summary>是否允许用户直接调用</summary>
        public bool UserInvocable { get; set; } = true;

        /// <summary>是否禁止 AI 自动路由到此 Agent</summary>
        public bool DisableModelInvocation { get; set; } = false;

        /// <summary>允许使用的工具列表</summary>
        public List<string> AllowedTools { get; set; } = new();

        /// <summary>可调用的子 Agent 列表</summary>
        public List<AgentType> SubAgents { get; set; } = new();

        /// <summary>Handoff 目标列表</summary>
        public List<AgentHandoff> Handoffs { get; set; } = new();

        /// <summary>Agent 系统提示词</summary>
        public string SystemPrompt { get; set; } = string.Empty;
    }

    /// <summary>
    /// Agent 之间的 Handoff 定义。
    /// 当一个 Agent 完成其工作后，可以将控制权移交给另一个 Agent。
    /// 例如：Plan Agent → Edit Agent。
    /// </summary>
    public class AgentHandoff
    {
        /// <summary>Handoff 显示标签</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>目标 Agent 类型</summary>
        public AgentType TargetAgent { get; set; }

        /// <summary>Handoff 时注入的 prompt</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>是否自动发送（不等待用户确认）</summary>
        public bool AutoSend { get; set; } = false;

        /// <summary>是否显示"继续"按钮</summary>
        public bool ShowContinueOn { get; set; } = true;

        /// <summary>推荐模型（可选）</summary>
        public string? Model { get; set; }
    }

    /// <summary>
    /// Agent 路由结果。
    /// 由 AgentDispatcher 根据用户意图和当前上下文决定路由到哪个 Agent。
    /// </summary>
    public class AgentRoutingResult
    {
        /// <summary>路由目标 Agent 类型</summary>
        public AgentType TargetAgent { get; set; } = AgentType.Ask;

        /// <summary>匹配置信度</summary>
        public string Confidence { get; set; } = "medium"; // high / medium / low

        /// <summary>简短匹配理由</summary>
        public string? Reason { get; set; }

        /// <summary>是否需要先经过 Plan Agent 规划</summary>
        public bool NeedsPlanning { get; set; }

        /// <summary>任务规模分类（Small / Medium / Large），用于决定路由策略</summary>
        public TaskSize TaskSize { get; set; } = TaskSize.Small;

        /// <summary>路由是否是用户显式指定的</summary>
        public bool IsExplicit { get; set; }
    }

    /// <summary>
    /// Agent 执行上下文：包含当前会话状态和工作目录信息。
    /// </summary>
    public class AgentContext
    {
        /// <summary>解决方案路径</summary>
        public string? SolutionPath { get; set; }

        /// <summary>文件上下文（用户上传的附件内容）</summary>
        public string? FileContext { get; set; }

        /// <summary>当前计划（Plan Agent 产出，Edit Agent 消费）</summary>
        public AgentTaskPlan? ActivePlan { get; set; }

        /// <summary>会话历史消息</summary>
        public List<ChatApiMessage> ConversationHistory { get; set; } = new();

        /// <summary>会话上下文管理器引用（用于 Agent 内部构建带历史的 API 消息，优化缓存命中率）</summary>
        [JsonIgnore]
        public Services.ConversationContextManager? ContextManager { get; set; }

        /// <summary>文件读取回调</summary>
        [JsonIgnore]
        public Func<string, Task<string?>>? ReadFileAsync { get; set; }

        /// <summary>CancellationToken</summary>
        [JsonIgnore]
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        /// <summary>是否处于 Planning 模式（多步骤计划执行中）。为 true 时跳过每步编译，最后统一构建。</summary>
        public bool IsPlanningMode { get; set; }

        /// <summary>首次路由时预分类的任务规模。EditAgent 应使用此值而非对 handoff 消息重复分类。</summary>
        [JsonIgnore]
        public TaskSize PreClassifiedTaskSize { get; set; } = TaskSize.Small;

        /// <summary>是否由用户 @agent 显式路由。为 true 时 Agent 不应主动移交控制权（除非必要的链式移交如 Plan→Edit）。</summary>
        public bool IsExplicitRoute { get; set; }

        /// <summary>Planning 模式下累积的上下文（前面步骤的结果和文件变更信息，供后续步骤继承）。</summary>
        public string? AccumulatedContext { get; set; }

        /// <summary>Plan Agent 生成的 plan.md 文件绝对路径（供 Edit Agent 执行完毕后清理）。</summary>
        [JsonIgnore]
        public string? PlanFilePath { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 缓存策略 — 以后会被 RAG 替代
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 全局文件读取缓存（文件路径 → 文件内容）。
        /// 跨 Agent 共享：PlanAgent 读取的文件 EditAgent 可直接复用，避免重复 read_file。
        /// 注意：已被修改的文件应从缓存中移除。
        /// 以后会被 RAG 向量检索替代。
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> FileReadCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 已发现的解决方案源文件列表（PlanAgent 产出，EditAgent 复用）。
        /// 避免 EditAgent 重新调用 DiscoverSolutionFilesAsync。
        /// 以后会被 RAG 向量检索替代。
        /// </summary>
        [JsonIgnore]
        public List<string>? DiscoveredFiles { get; set; }

        /// <summary>
        /// 代码记忆 — 跨步骤持久化的关键代码片段。
        /// 每个步骤完成后，从文件读取缓存中提取未被修改的关键文件内容存入此处。
        /// 后续步骤可直接使用这些代码片段，无需重复 read_file。
        /// 格式: Markdown 代码块，按文件分组，总量控制在 ~12KB 以内。
        /// </summary>
        [JsonIgnore]
        public string? CodeMemory { get; set; }

        /// <summary>
        /// 🔑 Handoff 时源 Agent 的最终工具循环消息列表（v1.1.10 缓存优化）。
        /// 设置后，目标 Agent 的 BuildContextAwareMessages 将复用此列表作为前缀，
        /// 而非从 ContextManager 重建，确保 Handoff 前后消息结构一致，
        /// DeepSeek Prefix Cache 可直接命中。
        /// Handoff 完成后由目标 Agent 首次 BuildContextAwareMessages 消费并清空。
        /// </summary>
        [JsonIgnore]
        public List<ChatApiMessage>? ForwardedMessages { get; set; }

        /// <summary>
        /// 实时推理流回调。Agent 内部每收到一个 thinking chunk 时调用，
        /// 供 UI 层实时流式更新思考面板。
        /// </summary>
        [JsonIgnore]
        public Action<string>? OnThinkingChunk { get; set; }
    }

    /// <summary>
    /// Agent 执行结果。
    /// </summary>
    public class AgentResult
    {
        /// <summary>执行的 Agent 类型</summary>
        public AgentType AgentType { get; set; }

        /// <summary>是否成功</summary>
        public bool Success { get; set; } = true;

        /// <summary>错误信息（失败时）</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>生成的消息内容（Markdown 格式）</summary>
        public string? Content { get; set; }

        /// <summary>推理/思考内容（DeepSeek V4 reasoning_content），用于渲染思考面板</summary>
        public string? ReasoningContent { get; set; }

        /// <summary>文件变更列表（Edit Agent 产出）</summary>
        public List<FileChangeSummary> FileChanges { get; set; } = new();

        /// <summary>任务计划（Plan Agent 产出）</summary>
        public AgentTaskPlan? Plan { get; set; }

        /// <summary>是否需要 Handoff 到另一个 Agent</summary>
        public AgentHandoff? Handoff { get; set; }

        /// <summary>日志条目</summary>
        public List<AgentLogEntry> Logs { get; set; } = new();
    }

    /// <summary>
    /// 子 Agent 任务定义（用于并行探索等场景）。
    /// </summary>
    public class SubagentTask
    {
        /// <summary>任务 ID</summary>
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>使用的 Agent 类型</summary>
        public AgentType AgentType { get; set; } = AgentType.Explore;

        /// <summary>子 Agent 的 prompt</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>搜索区域描述（用于 Explore Agent）</summary>
        public string? SearchArea { get; set; }
    }

    /// <summary>
    /// 子 Agent 执行结果。
    /// </summary>
    public class SubagentResult
    {
        /// <summary>对应的任务 ID</summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>是否成功</summary>
        public bool Success { get; set; } = true;

        /// <summary>错误信息</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>子 Agent 的发现结果</summary>
        public string? Findings { get; set; }

        /// <summary>搜索到的相关文件路径</summary>
        public List<string> RelevantFiles { get; set; } = new();

        /// <summary>搜索到的关键符号（函数名、类名等）</summary>
        public List<string> KeySymbols { get; set; } = new();
    }

    // ========================================================================
    // Handoff 请求 — 专用 JSON 格式的 Agent 间移交协议
    // ========================================================================

    /// <summary>
    /// Agent 间移交请求的专用 JSON 格式。
    /// 当 Agent 需要将任务移交给另一个 Agent 时，通过此结构声明移交意图。
    /// 
    /// 使用场景：
    /// - AskAgent/EditAgent/BuildAgent 需要代码库探索 → 移交给 ExploreAgent
    /// - PlanAgent 完成规划 → 移交给 EditAgent
    /// - EditAgent 完成修改遇到编译错误 → 移交给 BuildAgent
    /// 
    /// 移交流程：
    /// 1. 源 Agent 输出 HandoffRequest JSON
    /// 2. AgentDispatcher 解析并执行 ExecuteHandoffAsync
    /// 3. 目标 Agent 接收完整上下文（含计划、文件缓存等）
    /// 4. 目标 Agent 执行完毕后，可选择链回源 Agent 或 AskAgent（生成总结）
    /// </summary>
    public class HandoffRequest
    {
        /// <summary>移交请求 ID（用于追踪）</summary>
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>源 Agent 类型</summary>
        public AgentType SourceAgent { get; set; }

        /// <summary>目标 Agent 类型</summary>
        public AgentType TargetAgent { get; set; }

        /// <summary>移交原因（简短说明为什么要移交）</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>委派给目标 Agent 的任务描述</summary>
        public string TaskDescription { get; set; } = string.Empty;

        /// <summary>是否需要目标 Agent 完成后链回源 Agent</summary>
        public bool ChainBack { get; set; }

        /// <summary>是否自动执行（不等待用户确认）</summary>
        public bool AutoSend { get; set; }

        /// <summary>传递给目标 Agent 的附加上下文（如已探索的文件列表）</summary>
        public string? AdditionalContext { get; set; }

        /// <summary>是否被拦截（显式路由时拒绝非必要移交）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Rejected { get; set; }

        /// <summary>拦截原因（作为 tool 结果返回给 AI）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? RejectReason { get; set; }
    }

    /// <summary>
    /// Handoff 响应：目标 Agent 完成后的结果。
    /// </summary>
    public class HandoffResponse
    {
        /// <summary>对应的请求 ID</summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>目标 Agent 的执行结果</summary>
        public string? Result { get; set; }

        /// <summary>是否需要进一步移交</summary>
        public AgentHandoff? NextHandoff { get; set; }
    }
}
