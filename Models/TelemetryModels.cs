using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 失败分类（P0 可观测性 / Benchmark 基础）。
    /// 核心分析保持 Model / Context / Host 三分类，System 仅用于工程故障归档。
    /// </summary>
    public enum AgentFailureCategory
    {
        /// <summary>未失败或尚未标注</summary>
        None = 0,

        /// <summary>Model Failure：Context 正确、工具正确、VS 正常，LLM 决策错误</summary>
        Model = 1,

        /// <summary>Context Failure：模型可能有能力，但缺少必要信息（缺文件/缺符号/缺诊断）</summary>
        Context = 2,

        /// <summary>Host Failure：LLM 决策正确、工具意图正确，VS Adapter 执行错误</summary>
        Host = 3,

        /// <summary>System：timeout / cancellation / network / 模型不可用 / VS 崩溃等环境故障</summary>
        System = 4,
    }

    /// <summary>
    /// 单次会话结果。
    /// </summary>
    public enum AgentSessionResult
    {
        /// <summary>会话进行中或未完成</summary>
        Running = 0,

        /// <summary>成功完成</summary>
        Success = 1,

        /// <summary>失败（配合 FailureCategory 使用）</summary>
        Failure = 2,

        /// <summary>用户主动取消</summary>
        Cancelled = 3,
    }

    /// <summary>
    /// 单次工具调用指标。
    /// </summary>
    public sealed class ToolCallMetric
    {
        /// <summary>所属轮次</summary>
        [JsonPropertyName("turn")]
        public int Turn { get; set; }

        /// <summary>工具名</summary>
        [JsonPropertyName("tool")]
        public string ToolName { get; set; } = string.Empty;

        /// <summary>执行耗时（毫秒，含超时等待）</summary>
        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        /// <summary>是否成功（按结果约定判定：非 Error: /Timeout: 前缀视为成功）</summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>失败时的错误片段（截断至 160 字符）</summary>
        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorSnippet { get; set; }
    }

    /// <summary>
    /// 单轮 LLM 请求指标（一次 API 调用 = 一轮工具循环迭代）。
    /// </summary>
    public sealed class AgentTurnMetrics
    {
        /// <summary>轮次编号（从 1 开始；非工具循环路径为 1）</summary>
        [JsonPropertyName("turn")]
        public int Turn { get; set; }

        /// <summary>Time To First Token（毫秒）。null = 未收到任何 token</summary>
        [JsonPropertyName("ttft_ms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? TtftMs { get; set; }

        /// <summary>本轮总耗时（毫秒，从请求发起到 usage 返回）</summary>
        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        /// <summary>输入 token（usage.prompt_tokens）</summary>
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        /// <summary>输出 token（usage.completion_tokens）</summary>
        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }

        /// <summary>Prompt Cache 命中 token</summary>
        [JsonPropertyName("cache_hit_tokens")]
        public int CacheHitTokens { get; set; }

        /// <summary>Prompt Cache 未命中 token</summary>
        [JsonPropertyName("cache_miss_tokens")]
        public int CacheMissTokens { get; set; }

        /// <summary>本轮流断点续传重试次数</summary>
        [JsonPropertyName("stream_retries")]
        public int StreamRetries { get; set; }

        /// <summary>循环终止原因（safety_limit / loop_detected / consecutive_errors / whitelist_rejection），null = 正常结束</summary>
        [JsonPropertyName("terminated_reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TerminatedReason { get; set; }

        /// <summary>本轮工具调用明细</summary>
        [JsonPropertyName("tools")]
        public List<ToolCallMetric> Tools { get; set; } = new();

        [JsonIgnore]
        public int ToolCallCount => Tools.Count;
    }

    /// <summary>
    /// 会话级指标汇总 —— Benchmark 的最小数据单元。
    /// 一次 RunAgentWorkflowAsync（含 Handoff 链）产生一条 Session 记录，
    /// 完成后自动导出 JSON 到 %LocalAppData%\DeepSeekVS\telemetry\。
    /// </summary>
    public sealed class AgentSessionMetrics
    {
        /// <summary>会话唯一 ID（短随机 ID）</summary>
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        /// <summary>开始时间（ISO 8601 本地时间）</summary>
        [JsonPropertyName("started_at")]
        public DateTime StartedAt { get; set; }

        /// <summary>结束时间（null = 进行中）</summary>
        [JsonPropertyName("completed_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? CompletedAt { get; set; }

        /// <summary>总耗时（毫秒）</summary>
        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        /// <summary>模型名</summary>
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        /// <summary>参与执行的 Agent 链（含 Handoff 目标，如 ["Plan","Edit"]）</summary>
        [JsonPropertyName("agents")]
        public List<string> Agents { get; set; } = new();

        /// <summary>用户输入摘要（截断至 200 字符）</summary>
        [JsonPropertyName("user_prompt_snippet")]
        public string? UserPromptSnippet { get; set; }

        /// <summary>扩展版本</summary>
        [JsonPropertyName("extension_version")]
        public string? ExtensionVersion { get; set; }

        /// <summary>逐轮指标</summary>
        [JsonPropertyName("turns")]
        public List<AgentTurnMetrics> Turns { get; set; } = new();

        /// <summary>结果</summary>
        [JsonPropertyName("result")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AgentSessionResult Result { get; set; } = AgentSessionResult.Running;

        /// <summary>失败分类（result=failure 时有意义；None 表示待人工标注）</summary>
        [JsonPropertyName("failure_category")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AgentFailureCategory FailureCategory { get; set; } = AgentFailureCategory.None;

        /// <summary>失败细节（错误消息/异常消息，截断）</summary>
        [JsonPropertyName("failure_detail")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FailureDetail { get; set; }

        /// <summary>
        /// Benchmark 任务类别（compile_fix / inline_edit / cross_file）。
        /// 日常使用为 null；仅在跑基准任务集时由运行方标注。
        /// </summary>
        [JsonPropertyName("task_category")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TaskCategory { get; set; }

        /// <summary>Benchmark 任务 ID（对应 benchmark/tasks.json 的 id）</summary>
        [JsonPropertyName("task_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TaskId { get; set; }

        /// <summary>
        /// 会话开始时的上下文构成快照（P2 Context Debugger 数据面，camelCase JSON 字符串）。
        /// 回答报告 §16 的问题："这些 Context 为什么被加入" —— 失败复盘时对照
        /// failure_category 即可判定缺了哪类上下文。
        /// </summary>
        [JsonPropertyName("context_debug")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ContextDebug { get; set; }

        // ═══════════════ 聚合统计 ═══════════════

        [JsonPropertyName("turn_count")]
        public int TurnCount => Turns.Count;

        [JsonPropertyName("tool_call_count")]
        public int ToolCallCount => Turns.Sum(t => t.ToolCallCount);

        [JsonPropertyName("input_tokens")]
        public int InputTokens => Turns.Sum(t => t.InputTokens);

        [JsonPropertyName("output_tokens")]
        public int OutputTokens => Turns.Sum(t => t.OutputTokens);

        /// <summary>聚合 Cache 命中率（0~1；无可缓存数据时为 null）</summary>
        [JsonPropertyName("cache_hit_rate")]
        public double? CacheHitRate
        {
            get
            {
                long hit = Turns.Sum(t => (long)t.CacheHitTokens);
                long miss = Turns.Sum(t => (long)t.CacheMissTokens);
                return hit + miss > 0 ? (double)hit / (hit + miss) : null;
            }
        }

        /// <summary>首轮 TTFT（毫秒）；null = 无数据</summary>
        [JsonPropertyName("first_turn_ttft_ms")]
        public long? FirstTurnTtftMs => Turns.FirstOrDefault(t => t.TtftMs.HasValue)?.TtftMs;

        /// <summary>序列化为缩进 JSON（camelCase 字段名）</summary>
        public string ToJson()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            return JsonSerializer.Serialize(this, options);
        }
    }
}
