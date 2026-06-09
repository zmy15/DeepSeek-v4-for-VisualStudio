using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 审批模式：控制工具操作（终端命令、文件删除等）是否需要用户确认。
    /// </summary>
    public enum ApprovalMode
    {
        /// <summary>全部拦截：所有需要审批的操作都询问用户</summary>
        BlockAll,

        /// <summary>全部放行：所有需要审批的操作自动通过，不询问用户</summary>
        AllowAll,

        /// <summary>智能拦截：仅检测到危险操作时询问用户，安全操作自动放行</summary>
        SmartBlock,
    }

    /// <summary>
    /// 任务规模分类：用于决定走 Plan Agent（大型）还是 Edit Agent 直处理（中小型）。
    /// </summary>
    public enum TaskSize
    {
        /// <summary>小任务：单文件、简单修改（如改配置、修一行 bug）</summary>
        Small,

        /// <summary>中任务：跨 2-5 个文件、非结构性改动</summary>
        Medium,

        /// <summary>大任务：新功能、跨模块重构、架构变更</summary>
        Large,
    }

    /// <summary>
    /// 计划来源：区分计划是由 Plan Agent 产出还是 Edit Agent 自拆分。
    /// </summary>
    public enum PlanSource
    {
        /// <summary>无计划（单步执行）</summary>
        None = 0,

        /// <summary>Plan Agent 产出（生成 plan.md）</summary>
        PlanAgent = 1,

        /// <summary>Edit Agent 自拆分（不生成 plan.md，但显示面板）</summary>
        EditAgent = 2,
    }

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
            AgentType.Build => AgentIntent.CodeChange,
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

        /// <summary>本步骤修改的文件数量（执行后填充）</summary>
        public int FilesModified { get; set; }

        /// <summary>本步骤变更的代码行数（+/- 合计，执行后填充）</summary>
        public int LinesChanged { get; set; }
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

        /// <summary>计划来源：None=单步, PlanAgent=Plan Agent 产出, EditAgent=Edit Agent 自拆分。用于 UI 判断是否显示下方面板。</summary>
        public PlanSource Source { get; set; }

        /// <summary>[向后兼容] 标记此计划是否由 Plan Agent 产出。读取时桥接到 Source，旧 JSON 反序列化兼容。</summary>
        public bool IsFromPlanAgent
        {
            get => Source == PlanSource.PlanAgent;
            set { if (value && Source == PlanSource.None) Source = PlanSource.PlanAgent; }
        }

        // ═══════════════════════════════════════════════════════════════
        // 缓存策略 — 以后会被 RAG 替代
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Plan Agent 发现阶段已扫描的源文件路径列表。
        /// Edit Agent 可直接复用此列表，跳过重复的 DiscoverSolutionFilesAsync。
        /// 以后会被 RAG 向量检索替代。
        /// </summary>
        [JsonIgnore]
        public List<string> DiscoveredFiles { get; set; } = new();
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

        /// <summary>请求标题（简短摘要）</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>操作目的：为什么要执行此操作（向用户说明原因）</summary>
        public string Purpose { get; set; } = string.Empty;

        /// <summary>待执行的命令或操作描述（具体要做什么）</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>操作类型</summary>
        public string ActionType { get; set; } = "command"; // "command" | "file_write" | "web_access" | "file_delete" | "terminal_command"

        /// <summary>待删除/修改的文件路径列表（ActionType = "file_delete" / "file_write" 时使用）</summary>
        public List<string> FilePaths { get; set; } = new();

        /// <summary>额外详情内容（如文件写入时展示变更内容预览）</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>等待用户响应的 TaskCompletionSource</summary>
        [JsonIgnore]
        public TaskCompletionSource<bool>? ResponseTcs { get; set; }
    }

    /// <summary>
    /// Agent 向用户提问的请求（VisualStudio_askQuestions 工具使用）。
    /// 包含结构化的问题列表，支持单选/多选选项和自由文本输入。
    /// </summary>
    public class AgentQuestionRequest
    {
        /// <summary>请求 ID，用于匹配用户响应</summary>
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>问题列表</summary>
        public List<AgentQuestion> Questions { get; set; } = new();

        /// <summary>等待用户响应的 TaskCompletionSource（返回 JSON 格式的答案）</summary>
        [JsonIgnore]
        public TaskCompletionSource<string>? ResponseTcs { get; set; }
    }

    /// <summary>
    /// 单个问题定义。
    /// </summary>
    public class AgentQuestion
    {
        /// <summary>问题标题（简短标识）</summary>
        public string Header { get; set; } = string.Empty;

        /// <summary>问题文本</summary>
        public string Question { get; set; } = string.Empty;

        /// <summary>可选选项列表（为空则允许自由文本输入）</summary>
        public List<QuestionOption>? Options { get; set; }

        /// <summary>是否允许多选</summary>
        public bool MultiSelect { get; set; }

        /// <summary>是否允许自由文本输入（除选项外）</summary>
        public bool AllowFreeformInput { get; set; } = true;
    }

    /// <summary>
    /// 问题选项。
    /// 支持两种 JSON 格式：
    /// - 对象: {"label": "选项A", "description": "说明"}
    /// - 字符串: "选项A"（AI 可能简化为纯字符串）
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(QuestionOptionConverter))]
    public class QuestionOption
    {
        /// <summary>选项标签</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>选项描述（可选）</summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// QuestionOption 的自定义 JSON 转换器，兼容 AI 输出的两种格式。
    /// </summary>
    public class QuestionOptionConverter : System.Text.Json.Serialization.JsonConverter<QuestionOption>
    {
        public override QuestionOption? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
            {
                // AI 输出纯字符串: "选项A" → QuestionOption { Label = "选项A" }
                return new QuestionOption { Label = reader.GetString() ?? string.Empty };
            }

            if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
            {
                // 标准对象格式: {"label": "选项A", "description": "说明"}
                var option = new QuestionOption();
                while (reader.Read())
                {
                    if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject)
                        return option;

                    if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
                    {
                        string propName = reader.GetString() ?? string.Empty;
                        reader.Read();
                        if (string.Equals(propName, "label", StringComparison.OrdinalIgnoreCase))
                            option.Label = reader.GetString() ?? string.Empty;
                        else if (string.Equals(propName, "description", StringComparison.OrdinalIgnoreCase))
                            option.Description = reader.GetString();
                        else
                            reader.Skip();
                    }
                }
                return option;
            }

            throw new System.Text.Json.JsonException($"QuestionOption 期望 String 或 Object，实际为 {reader.TokenType}");
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, QuestionOption value, System.Text.Json.JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("label", value.Label);
            if (!string.IsNullOrEmpty(value.Description))
                writer.WriteString("description", value.Description);
            writer.WriteEndObject();
        }
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
