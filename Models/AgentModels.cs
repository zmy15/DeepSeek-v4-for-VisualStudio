using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// Agent 意图类型：判断用户请求是需要修改代码还是普通问答。
    /// 保留向后兼容，同时支持多 Agent 路由。
    /// </summary>
    public enum AgentIntent
    {
        /// <summary>普通问答，不需要修改项目代码</summary>
        QandA,

        /// <summary>需要修改项目代码 / 修复 bug</summary>
        CodeChange,
    }

    /// <summary>
    /// 将 AgentType 映射为 AgentIntent（向后兼容）。
    /// </summary>
    public static class AgentIntentMapper
    {
        public static AgentIntent ToIntent(this AgentType agentType) => agentType switch
        {
            AgentType.Ask => AgentIntent.QandA,
            AgentType.Explore => AgentIntent.QandA,
            AgentType.Plan => AgentIntent.CodeChange,
            AgentType.Edit => AgentIntent.CodeChange,
            _ => AgentIntent.QandA,
        };
    }

    /// <summary>
    /// Agent 单个执行步骤。
    /// </summary>
    public class AgentStep
    {
        /// <summary>步骤序号（从 1 开始）</summary>
        public int Index { get; set; }

        /// <summary>步骤标题（简短描述）</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>步骤详细描述（给 AI 的 prompt）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>执行状态</summary>
        public AgentStepStatus Status { get; set; } = AgentStepStatus.Pending;

        /// <summary>步骤执行结果摘要</summary>
        public string? ResultSummary { get; set; }

        /// <summary>是否需要用户许可</summary>
        public bool RequiresApproval { get; set; }

        /// <summary>待执行的命令（需要用户许可时）</summary>
        public string? PendingCommand { get; set; }

        /// <summary>AI 执行此步骤的完整响应文本（分析结论或代码变更）</summary>
        public string? AiResponse { get; set; }
    }

    /// <summary>
    /// Agent 步骤状态。
    /// </summary>
    public enum AgentStepStatus
    {
        Pending,
        InProgress,
        WaitingApproval,
        Completed,
        Skipped,
        Failed,
    }

    /// <summary>
    /// Agent 任务计划：包含意图类型和分解后的步骤列表。
    /// </summary>
    public class AgentTaskPlan
    {
        /// <summary>计划唯一 ID（用于 WebView DOM 元素 ID 前缀，避免多计划冲突）</summary>
        public string PlanId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>用户请求的意图类型</summary>
        public AgentIntent Intent { get; set; } = AgentIntent.QandA;

        /// <summary>任务总标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>分解后的步骤列表</summary>
        public List<AgentStep> Steps { get; set; } = new();

        /// <summary>变更的文件列表及其 +/- 行数</summary>
        public List<FileChangeSummary> ChangedFiles { get; set; } = new();

        /// <summary>当前执行到第几步（1-based）</summary>
        public int CurrentStepIndex { get; set; }

        /// <summary>是否所有步骤已完成</summary>
        public bool IsCompleted { get; set; }

        /// <summary>任务是否被用户取消</summary>
        public bool IsCancelled { get; set; }

        /// <summary>Plan Agent 生成的 plan.md 文件绝对路径（Edit Agent 执行完毕后删除）</summary>
        [JsonIgnore]
        public string? PlanFilePath { get; set; }

        /// <summary>标记此计划是否由 Plan Agent 产出（而非 Edit Agent 内部单步计划）。用于 UI 判断是否显示下方面板。</summary>
        public bool IsFromPlanAgent { get; set; }
    }

    /// <summary>
    /// 文件变更摘要：记录修改了哪个文件、增删行数。
    /// </summary>
    public class FileChangeSummary
    {
        public string FilePath { get; set; } = string.Empty;
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public string? BriefDescription { get; set; }

        /// <summary>修改后的新内容（内部使用，不序列化到 UI）</summary>
        [JsonIgnore]
        public string? NewContent { get; set; }

        /// <summary>修改前的原始内容（用于撤销/回退操作，内部使用）</summary>
        [JsonIgnore]
        public string? OriginalContent { get; set; }
    }

    /// <summary>
    /// Agent 权限请求：发送给用户确认。
    /// </summary>
    public class AgentPermissionRequest
    {
        /// <summary>请求 ID，用于匹配用户响应</summary>
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>请求标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>待执行的命令或操作描述</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>操作类型</summary>
        public string ActionType { get; set; } = "command"; // "command" | "file_write" | "web_access" | "file_delete"

        /// <summary>待删除的文件路径列表（ActionType = "file_delete" 时使用）</summary>
        public List<string> FilePaths { get; set; } = new();

        /// <summary>额外详情内容（如文件写入时展示变更内容预览）</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>等待用户响应的 TaskCompletionSource</summary>
        [JsonIgnore]
        public TaskCompletionSource<bool>? ResponseTcs { get; set; }
    }

    /// <summary>
    /// Agent 日志条目：记录中间步骤的简要日志。
    /// </summary>
    public class AgentLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "INFO"; // INFO, WARN, ERROR
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Agent 文件变更实时通知事件参数。
    /// 用于在编辑阶段逐文件实时推送变更信息到 WebView。
    /// </summary>
    public class AgentFileChangeEventArgs
    {
        /// <summary>关联的计划 ID</summary>
        public string PlanId { get; set; } = string.Empty;

        /// <summary>变更类型: modify, create, delete</summary>
        public string ChangeType { get; set; } = "modify";

        /// <summary>文件绝对路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>变更详情（如 +15 -3 行）</summary>
        public string Detail { get; set; } = string.Empty;
    }
}
