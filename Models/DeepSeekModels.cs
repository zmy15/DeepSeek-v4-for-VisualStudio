using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ======== API 请求模型 ========
    public class DeepSeekChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "deepseek-v4-pro";

        [JsonPropertyName("messages")]
        public List<ChatApiMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = true;

        // 思考模式控制 (V4 新参数)
        [JsonPropertyName("thinking")]
        public ThinkingControl? Thinking { get; set; }

        // 推理强度 (V4 新参数, 仅思考模式开启时有效)
        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; set; }

        // 最大输出 token 数（用于校验 ping）
        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

        // ── 工具调用（MCP / Function Calling） ──
        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolDefinition>? Tools { get; set; }

        [JsonPropertyName("tool_choice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolChoice { get; set; } // "auto" | "none" | "required" | { "type": "function", "function": { "name": "..." } }

        // 注意: 思考模式下 temperature/top_p 等参数不生效，此处省略
    }

    public class ThinkingControl
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "enabled"; // "enabled" 或 "disabled"
    }

    // 对话消息（API 格式）
    public class ChatApiMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningContent { get; set; }

        // ── 工具调用（MCP / Function Calling） ──
        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolCall>? ToolCalls { get; set; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
    }

    // ======== 非流式响应模型 ========
    public class DeepSeekChatResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<DeepSeekChoice> Choices { get; set; } = new();

        [JsonPropertyName("usage")]
        public DeepSeekUsage? Usage { get; set; }
    }

    public class DeepSeekChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public DeepSeekMessage? Message { get; set; }

        [JsonPropertyName("delta")]
        public DeepSeekDelta? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    public class DeepSeekMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolCall>? ToolCalls { get; set; }
    }

    public class DeepSeekDelta
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolCallDelta>? ToolCalls { get; set; }
    }

    public class DeepSeekUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }

        // ── Prompt Cache 相关字段（DeepSeek Context Caching）──
        [JsonPropertyName("prompt_cache_hit_tokens")]
        public int PromptCacheHitTokens { get; set; }

        [JsonPropertyName("prompt_cache_miss_tokens")]
        public int PromptCacheMissTokens { get; set; }

        /// <summary>
        /// Cache 命中率（0.0 ~ 1.0）。当 prompt_tokens 为 0 时返回 0。
        /// </summary>
        [JsonIgnore]
        public double CacheHitRate =>
            PromptCacheHitTokens + PromptCacheMissTokens > 0
                ? (double)PromptCacheHitTokens / (PromptCacheHitTokens + PromptCacheMissTokens)
                : 0;

        /// <summary>
        /// Cache 命中率百分比字符串，如 "98.5%"。
        /// </summary>
        [JsonIgnore]
        public string CacheHitRatePercent => $"{CacheHitRate * 100:F1}%";
    }

    // ======== 流式响应行 ========
    public class DeepSeekStreamChunk
    {
        [JsonPropertyName("choices")]
        public List<DeepSeekChoice> Choices { get; set; } = new();

        /// <summary>
        /// 流式响应的最后一个 chunk 中可能包含 usage 信息（含 cache 命中统计）。
        /// </summary>
        [JsonPropertyName("usage")]
        public DeepSeekUsage? Usage { get; set; }
    }

    // ======== FIM（Fill-In-the-Middle）补全模型 ========

    /// <summary>
    /// FIM 补全请求。使用 prefix/suffix 模式进行代码补全，
    /// 替代 chat/completions 的 AUTOCOMPLETE_HERE 标记方案。
    /// 端点: POST https://api.deepseek.com/beta/completions
    /// </summary>
    public class DeepSeekFimRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "deepseek-v4-pro";

        /// <summary>光标前的代码（prefix）</summary>
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        /// <summary>光标后的代码（suffix）</summary>
        [JsonPropertyName("suffix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Suffix { get; set; }

        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TopP { get; set; }

        [JsonPropertyName("stop")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Stop { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;
    }

    /// <summary>
    /// FIM 补全响应。
    /// </summary>
    public class DeepSeekFimResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public string Object { get; set; } = "text_completion";

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<DeepSeekFimChoice> Choices { get; set; } = new();

        [JsonPropertyName("usage")]
        public DeepSeekUsage? Usage { get; set; }
    }

    /// <summary>
    /// FIM 补全选择的文本。
    /// </summary>
    public class DeepSeekFimChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    // ======== 视图模型用的 UI 消息模型（添加时间戳等） ========
    [DataContract]
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _role = "user";
        private string _content = string.Empty;
        private string _reasoningContent = string.Empty;
        private string _htmlContent = string.Empty;
        private string _htmlDataUri = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isStreaming;
        private bool _isRendered;

        [DataMember]
        public string Role
        {
            get => _role;
            set => SetProperty(ref _role, value);
        }

        [DataMember]
        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }

        /// <summary>
        /// 思考模式下的推理过程内容，以单行滚动形式展示。
        /// 非思考模式下此属性为空。
        /// </summary>
        [DataMember]
        public string ReasoningContent
        {
            get => _reasoningContent;
            set => SetProperty(ref _reasoningContent, value);
        }

        /// <summary>
        /// Markdown 渲染后的 HTML 内容（带深色主题 CSS）。
        /// 由 MarkdownRenderService 在流式完成后生成。
        /// 为空时表示尚未渲染或正在流式传输中。
        /// 注意：此属性较大（~60KB），不参与持久化，由 UI 重新渲染。
        /// </summary>
        [JsonIgnore]
        public string HtmlContent
        {
            get => _htmlContent;
            set => SetProperty(ref _htmlContent, value);
        }

        /// <summary>
        /// data:text/html;base64,... 格式的 Data URI，可直接绑定到 WebView2.Source。
        /// 由 MarkdownRenderService.ConvertToDataUri() 生成。
        /// 注意：此属性较大（~60KB），不参与持久化，由 UI 重新渲染。
        /// </summary>
        [JsonIgnore]
        public string HtmlDataUri
        {
            get => _htmlDataUri;
            set => SetProperty(ref _htmlDataUri, value);
        }

        [DataMember]
        public DateTime Timestamp
        {
            get => _timestamp;
            set => SetProperty(ref _timestamp, value);
        }

        [DataMember]
        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        /// <summary>
        /// 指示 Markdown 是否已完成 HTML 渲染。
        /// 用于 XAML 触发器切换 TextBox → WebView2 显示。
        /// </summary>
        [DataMember]
        public bool IsRendered
        {
            get => _isRendered;
            set => SetProperty(ref _isRendered, value);
        }

        /// <summary>
        /// 用户消息附带的文件名列表（不含路径，仅文件名）。
        /// 用于 UI 展示已上传的文件标签。
        /// 仅对 Role == "user" 的消息有意义。
        /// </summary>
        [DataMember]
        public List<string> AttachedFileNames { get; set; } = new();

        /// <summary>
        /// 用户消息附带的文件解析结果列表。
        /// 包含文件名、内容、错误信息等，用于可折叠 UI 展示。
        /// 此属性不参与 RPC 同步（较大），仅用于本地持久化。
        /// 仅对 Role == "user" 的消息有意义。
        /// </summary>
        [DataMember]
        public List<FileParseResult> AttachedFiles { get; set; } = new();

        // ======== 树状结构字段 ========

        /// <summary>
        /// 关联的对话树节点 ID (ConvNode.Id)。
        /// 用于从消息反向定位到树节点。
        /// </summary>
        [DataMember]
        public string NodeId { get; set; } = string.Empty;

        /// <summary>
        /// 在兄弟节点中的显示位置（1-based）。
        /// 仅当 SiblingCount > 1 时有意义。
        /// </summary>
        [DataMember]
        public int SiblingIndex { get; set; } = 1;

        /// <summary>
        /// 兄弟节点总数。>1 时表示此处有分叉，应渲染 <> 导航按钮。
        /// </summary>
        [DataMember]
        public int SiblingCount { get; set; } = 1;

        /// <summary>
        /// 分叉原因："edit"（编辑用户消息产生）或 "retry"（重试助手回复产生）。
        /// 决定 <> 导航按钮放在用户气泡下还是 AI 气泡下：
        /// - "edit" → 用户气泡下方（分叉点在用户消息）
        /// - "retry" → AI气泡下方（分叉点在助手消息）
        /// </summary>
        [DataMember]
        public string? ForkReason { get; set; }
        /// <summary>
        /// 处理此消息的 Agent 类型。
        /// 用于判断编辑/重试时是否使用分支（树状）还是原地修改：
        /// - EditAgent → 原地修改，不产生分支
        /// - 其他 Agent → 保持树状分叉
        /// </summary>
        [DataMember]
        public AgentType? AgentType { get; set; }
        /// <summary>
        /// 指示 Content 是否已经是渲染好的 HTML（而非 Markdown）。
        /// 为 true 时，AppendAssistantMessageHtml 跳过 Markdown 渲染，直接使用原始 HTML。
        /// 用于 Coding Agent 的计划/摘要等预渲染的 HTML 内容。
        /// </summary>
        [DataMember]
        public bool IsHtml { get; set; }

        /// <summary>
        /// Agent 任务计划的 JSON 序列化数据。
        /// 用于重启后重建任务面板，null 表示无关联计划。
        /// </summary>
        [DataMember]
        public string? PlanJson { get; set; }

        // INotifyPropertyChanged 实现
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    // ======== 会话模型（多轮对话支持） ========

    /// <summary>
    /// 表示一次对话会话，包含多条消息和元数据。
    /// 会话标题默认为"新对话"，在用户发送第一条消息时自动更新。
    /// </summary>
    [DataContract]
    public class ChatSession
    {
        [DataMember]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [DataMember]
        public string Title { get; set; } = "新对话";

        /// <summary>
        /// API 级别的完整对话历史（含 tool 消息、reasoning_content 等）。
        /// 用于完整恢复上下文。
        /// </summary>
        [DataMember]
        public List<ChatApiMessage> ApiHistory { get; set; } = new();

        [DataMember]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [DataMember]
        public DateTime LastActiveAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 树结构的 JSON 序列化数据。
        /// 包含所有 user/assistant 消息，是对话的唯一权威数据源。
        /// </summary>
        [DataMember]
        public string? TreeDataJson { get; set; }

        // ── 累计 Cache 统计（跨会话持久化，重启后恢复显示）──

        /// <summary>累计 Cache 命中 Token 数</summary>
        [DataMember]
        public long CumulativeCacheHitTokens { get; set; }

        /// <summary>累计 Cache 未命中 Token 数</summary>
        [DataMember]
        public long CumulativeCacheMissTokens { get; set; }

        /// <summary>累计 Prompt Token 数</summary>
        [DataMember]
        public long CumulativePromptTokens { get; set; }

        /// <summary>累计 Completion Token 数</summary>
        [DataMember]
        public long CumulativeCompletionTokens { get; set; }
    }

    /// <summary>
    /// 持久化用的会话容器，存储一个解决方案下的所有会话。
    /// </summary>
    [DataContract]
    public class SessionsContainer
    {
        [DataMember]
        public string SolutionPath { get; set; } = string.Empty;

        [DataMember]
        public DateTime LastSaved { get; set; }

        [DataMember]
        public List<ChatSession> Sessions { get; set; } = new();

        [DataMember]
        public string? ActiveSessionId { get; set; }
    }

    // ======== 搜索查询优化模型 ========

    /// <summary>
    /// AI 返回的搜索查询优化结果。由 AI 分析用户问题和上下文后生成。
    /// 必须严格校验 JSON 格式。
    /// </summary>
    public class SearchQueryOptimization
    {
        /// <summary>优化后的搜索关键词（必填，不超过72字符）</summary>
        [JsonPropertyName("search_query")]
        public string SearchQuery { get; set; } = string.Empty;

        /// <summary>搜索时效过滤（可选）: week/month/semiyear/year</summary>
        [JsonPropertyName("search_recency")]
        public string? SearchRecency { get; set; }

        /// <summary>是否需要联网搜索</summary>
        [JsonPropertyName("need_search")]
        public bool NeedSearch { get; set; } = true;
    }

    // ======== 文件上传模型 ========

    /// <summary>
    /// 文件解析结果。
    /// 存储上传文件的元数据和解析后的文本内容。
    /// </summary>
    [DataContract]
    public class FileParseResult
    {
        /// <summary>文件名（含扩展名）</summary>
        [DataMember]
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件扩展名（含点号）</summary>
        [DataMember]
        public string FileExtension { get; set; } = string.Empty;

        /// <summary>文件完整路径（仅上传时使用，不持久化）</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>解析出的文本内容</summary>
        [DataMember]
        public string? Content { get; set; }

        /// <summary>是否成功解析</summary>
        [DataMember]
        public bool Success { get; set; }

        /// <summary>解析错误信息（仅失败时）</summary>
        [DataMember]
        public string? Error { get; set; }

        /// <summary>内容是否因过长而被截断</summary>
        [DataMember]
        public bool Truncated { get; set; }

        /// <summary>截断提示信息</summary>
        [DataMember]
        public string? TruncationNote { get; set; }
    }

    // ======== 工具调用模型（MCP / Function Calling）========

    /// <summary>
    /// OpenAI 兼容的工具定义，发送给 API 的 tools 参数。
    /// </summary>
    public class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public ToolFunction Function { get; set; } = new();
    }

    /// <summary>
    /// 函数定义（工具的核心描述）
    /// </summary>
    public class ToolFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public object Parameters { get; set; } = new { type = "object", properties = new Dictionary<string, object>() };
    }

    /// <summary>
    /// AI 返回的工具调用（非流式或完整积累后）
    /// </summary>
    public class ToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public ToolCallFunction Function { get; set; } = new();
    }

    /// <summary>
    /// 工具调用中的函数调用详情
    /// </summary>
    public class ToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;
    }

    /// <summary>
    /// 流式传输中增量返回的工具调用片段。
    /// DeepSeek 流式返回时 tool_calls 以增量方式下发：
    /// - 第一个 chunk: { "index": 0, "id": "call_xxx", "type": "function", "function": { "name": "xxx", "arguments": "" } }
    /// - 后续 chunks: { "index": 0, "function": { "arguments": "..." } }
    /// </summary>
    public class ToolCallDelta
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("function")]
        public ToolCallFunctionDelta? Function { get; set; }
    }

    /// <summary>
    /// 流式工具调用中函数部分的增量
    /// </summary>
    public class ToolCallFunctionDelta
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }
}