using System;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 字符串扩展方法。
    /// </summary>
    internal static class StringExtensions
    {
        /// <summary>
        /// 安全截断字符串到指定长度，超出部分用 "…" 替代。
        /// 不会在 Unicode 代理对中间截断。
        /// </summary>
        public static string Truncate(this string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            int cutPoint = maxLength - 1;
            if (cutPoint > 0 && cutPoint < text.Length
                && char.IsHighSurrogate(text[cutPoint - 1])
                && char.IsLowSurrogate(text[cutPoint]))
            {
                cutPoint--;
            }

            return text.Substring(0, cutPoint) + "…";
        }

        #region 输入安全净化（防工具注入）

        // DeepSeek 工具调用标记格式：
        //   <|tool_calls|>...content...</|tool_calls|>
        //   或 <|DSML|function_calls|>...content...</|DSML|function_calls|>
        // 攻击者可在聊天输入中注入这些标记，诱导 AI 将用户输入误识别为工具调用。

        private static readonly Regex ToolCallBlockRegex = new(
            @"<\|[^>]*?(?:tool_calls?|function_calls?|DSML)[^>]*?\|>.*?</\|[^>]*?(?:tool_calls?|function_calls?|DSML)[^>]*?\|>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ToolCallTagRegex = new(
            @"</?\|[^>]*?(?:tool_calls?|function_calls?|DSML)[^>]*?\|>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 净化用户输入，移除 DeepSeek 工具调用注入标记。
        /// 防御 <|tool_calls|>、<|DSML|>、<|function_calls|> 等格式的注入攻击。
        /// </summary>
        /// <param name="userInput">原始用户输入</param>
        /// <returns>净化后的安全文本</returns>
        public static string SanitizeUserInput(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput))
                return userInput ?? string.Empty;

            // 步骤1：移除完整的工具调用块（含内容）
            string sanitized = ToolCallBlockRegex.Replace(userInput, string.Empty);

            // 步骤2：移除残留的孤立工具调用标签
            sanitized = ToolCallTagRegex.Replace(sanitized, string.Empty);

            // 步骤3：转义残留的 <| 和 |> 分隔符（防止部分注入）
            // 使用全角字符替代，保留可读性
            sanitized = sanitized.Replace("<|", "〈|");
            sanitized = sanitized.Replace("|>", "|〉");

            return sanitized.Trim();
        }

        #endregion
    }
}
