using System;

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
    }
}
