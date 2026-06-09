using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 工具结果裁剪器接口 — 在工具结果进入对话历史前进行裁剪，防止撑爆 AI 上下文。
    /// </summary>
    public interface IToolResultCompactor
    {
        /// <summary>
        /// 裁剪工具结果为适合放入 AI 上下文的版本。
        /// </summary>
        /// <param name="toolName">工具名（用于分级限流判断）</param>
        /// <param name="rawResult">原始工具结果</param>
        /// <param name="model">当前模型标识（用于大上下文模型检测）</param>
        /// <returns>裁剪后的结果（可能等于原结果）</returns>
        string CompactToolResultForContext(string toolName, string rawResult, string model);
    }

    /// <summary>
    /// 工具结果裁剪器 — 参考 CodeWhale context.rs 的分级限流策略。
    /// 
    /// 核心设计：
    /// - 不同工具类型给予不同的保留长度
    /// - read_file 相对宽松（24K），噪音工具严格（4K）
    /// - head-tail 截断保留开头 2/3 + 结尾 1/3，帮助 AI 判断是否需要继续读取
    /// - 大上下文模型（≥500K 窗口）所有限制 ×15
    /// </summary>
    public class ToolResultCompactor : IToolResultCompactor
    {
        // ── 普通模型限制 ──
        private const int HardLimitChars = 12_000;         // 硬上限
        private const int SnippetChars = 2_000;            // 超限后保留长度
        private const int NoisySoftLimitChars = 4_000;     // 噪音工具软上限
        private const int NoisySnippetChars = 1_200;       // 噪音工具截断后保留
        private const int ReadFileLimitChars = 24_000;     // read_file 专用上限
        private const int ReadFileSnippetChars = 4_000;    // read_file 截断后保留

        // ── 大上下文模型（≥500K 窗口）缩放因子 ──
        private const int LargeContextMultiplier = 15;
        private const int LargeContextWindowTokens = 500_000;

        // ── 截断标记 ──
        private const string TruncationMarker = "\n\n[... 内容已截断以保护上下文 ...]\n\n";

        /// <inheritdoc />
        public string CompactToolResultForContext(string toolName, string rawResult, string model)
        {
            if (string.IsNullOrEmpty(rawResult))
                return rawResult ?? string.Empty;

            bool isLargeContext = IsLargeContextModel(model);
            int multiplier = isLargeContext ? LargeContextMultiplier : 1;

            if (IsReadFileTool(toolName))
            {
                return CompactWithLimits(rawResult,
                    ReadFileLimitChars * multiplier,
                    ReadFileSnippetChars * multiplier);
            }

            if (IsNoisyTool(toolName))
            {
                return CompactWithLimits(rawResult,
                    HardLimitChars * multiplier,
                    NoisySnippetChars * multiplier,
                    noisySoftLimit: NoisySoftLimitChars * multiplier);
            }

            // 普通工具
            return CompactWithLimits(rawResult,
                HardLimitChars * multiplier,
                SnippetChars * multiplier);
        }

        /// <summary>
        /// 按指定限制裁剪文本。
        /// 如果内容在 hardLimit 内则原样返回；
        /// 如果存在 noisySoftLimit 且内容超过则触发裁剪；
        /// 否则仅在超过 hardLimit 时裁剪到 snippetChars。
        /// </summary>
        private static string CompactWithLimits(
            string text, int hardLimit, int snippetChars, int? noisySoftLimit = null)
        {
            int charCount = text.Length;
            bool shouldCompact = charCount > hardLimit
                || (noisySoftLimit.HasValue && charCount > noisySoftLimit.Value);

            if (!shouldCompact)
                return text;

            return SummarizeHeadTail(text, snippetChars, charCount);
        }

        /// <summary>
        /// Head-tail 截断：保留前 2/3 + 后 1/3，中间插入截断标记。
        /// 参考 CodeWhale summarize_text_head_tail()。
        /// </summary>
        internal static string SummarizeHeadTail(string text, int limit, int? totalChars = null)
        {
            int total = totalChars ?? text.Length;
            if (total <= limit)
                return text;

            int markerLen = TruncationMarker.Length;
            if (limit <= markerLen + 20)
                return TruncateHead(text, limit);

            int remaining = limit - markerLen;
            int headLen = remaining * 2 / 3;
            int tailLen = remaining - headLen;

            string head = text.Substring(0, Math.Min(headLen, text.Length));
            string tail;
            if (tailLen >= text.Length)
            {
                tail = "";
            }
            else
            {
                int tailStart = text.Length - tailLen;
                if (tailStart < headLen)
                    tailStart = headLen;
                tail = text.Substring(tailStart, Math.Min(tailLen, text.Length - tailStart));
            }

            return head + TruncationMarker + tail;
        }

        /// <summary>
        /// 纯头部截断（备用，当 limit 太小时使用）。
        /// </summary>
        private static string TruncateHead(string text, int limit)
        {
            if (text.Length <= limit)
                return text;
            return text.Substring(0, limit - 3) + "...";
        }

        /// <summary>
        /// 判断是否为 read_file 类工具。
        /// </summary>
        private static bool IsReadFileTool(string toolName)
        {
            return string.Equals(toolName, "read_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "memory", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断是否为噪音工具（输出量大的工具，需更严格的限制）。
        /// 参考 CodeWhale tool_result_is_noisy()。
        /// </summary>
        private static bool IsNoisyTool(string toolName)
        {
            return toolName switch
            {
                "exec_shell" => true,
                "exec_shell_wait" => true,
                "exec_shell_interact" => true,
                "run_tests" => true,
                "run_verifiers" => true,
                "web_search" => true,
                "web_fetch" => true,
                "multi_tool_use.parallel" => true,
                "semantic_search" => true,
                "grep_search" => true,
                "file_search" => true,
                _ => false,
            };
        }

        /// <summary>
        /// 判断是否为超大上下文模型（窗口 ≥ 500K tokens）。
        /// DeepSeek V4 系列为 1M 窗口，属于大上下文模型。
        /// </summary>
        internal static bool IsLargeContextModel(string model)
        {
            if (string.IsNullOrEmpty(model))
                return true; // 默认按大上下文处理，保守策略

            // DeepSeek V4 系列
            if (model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
                && (model.Contains("v4", StringComparison.OrdinalIgnoreCase)
                    || model.Contains("reasoner", StringComparison.OrdinalIgnoreCase)))
                return true;

            // 显式标注大上下文的模型
            if (model.Contains("1m", StringComparison.OrdinalIgnoreCase)
                || model.Contains("1M", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
