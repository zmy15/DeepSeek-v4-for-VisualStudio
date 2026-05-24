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

        /// <summary>Planning 模式下累积的上下文（前面步骤的结果和文件变更信息，供后续步骤继承）。</summary>
        public string? AccumulatedContext { get; set; }

        /// <summary>Plan Agent 生成的 plan.md 文件绝对路径（供 Edit Agent 执行完毕后清理）。</summary>
        [JsonIgnore]
        public string? PlanFilePath { get; set; }
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
}
