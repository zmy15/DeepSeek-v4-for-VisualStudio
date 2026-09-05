using System;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 工具结果类别（P2-B，序号 21：内部结构化 / 外部兼容字符串）。
    /// </summary>
    public enum ToolResultKind
    {
        /// <summary>执行成功</summary>
        Success = 0,

        /// <summary>工具级失败（Error: 前缀约定）</summary>
        ToolError = 1,

        /// <summary>超时被终止（Timeout: 前缀约定，归入 System 故障族）</summary>
        Timeout = 2,

        /// <summary>被硬拒绝（[BLOCKED] 前缀约定，如 Git 危险操作、RunInTerminal 危险命令）</summary>
        Blocked = 3,
    }

    /// <summary>
    /// 工具执行结果的类型化包装（P2-B 渐进迁移第一步）：
    /// 对外契约仍是 <see cref="Output"/> 字符串（喂给 LLM 的内容不变），
    /// 内部消费方（遥测 / 未来 Benchmark / UI 汇总）通过 <see cref="Kind"/> 与
    /// <see cref="Success"/> 获得机器可读语义。
    /// </summary>
    public sealed class ToolExecutionOutcome
    {
        public ToolResultKind Kind { get; init; }

        public bool Success => Kind == ToolResultKind.Success;

        /// <summary>原始结果字符串（与既有 LLM 契约完全一致）</summary>
        public string Output { get; init; } = string.Empty;

        public string ToolName { get; init; } = string.Empty;

        public long DurationMs { get; init; }

        public static ToolExecutionOutcome FromRaw(string toolName, string raw, long durationMs)
            => new()
            {
                Kind = Classify(raw),
                Output = raw ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                DurationMs = durationMs,
            };

        /// <summary>
        /// 结果字符串约定的唯一权威解析点：
        /// Error: = 工具错误；Timeout: = 超时；[BLOCKED] = 硬拒绝；其余 = 成功。
        /// </summary>
        public static ToolResultKind Classify(string? output)
        {
            if (string.IsNullOrEmpty(output)) return ToolResultKind.Success;
            if (output.StartsWith("Timeout: ", StringComparison.Ordinal)) return ToolResultKind.Timeout;
            if (output.StartsWith("[BLOCKED] ", StringComparison.Ordinal)) return ToolResultKind.Blocked;
            if (output.StartsWith("Error: ", StringComparison.Ordinal)) return ToolResultKind.ToolError;
            return ToolResultKind.Success;
        }

        /// <summary>
        /// 判断内容是否以任一契约前缀开头（Error: / Timeout: / [BLOCKED] ）。
        /// 供内容型工具（read_file 等）在返回原始文件内容前做防碰撞检查：
        /// 内容恰好以契约前缀开头时必须包裹（如 &lt;file&gt; 信封），否则会被
        /// Classify / BaseAgent 连续错误检测误判为工具失败，累计后提前终止工具循环。
        /// </summary>
        public static bool StartsWithContractMarker(string? content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            return content.StartsWith("Error: ", StringComparison.Ordinal)
                || content.StartsWith("Timeout: ", StringComparison.Ordinal)
                || content.StartsWith("[BLOCKED] ", StringComparison.Ordinal);
        }
    }
}
