using System;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// Levenshtein 编辑距离扩展方法。
    /// </summary>
    internal static class LevenshteinDistanceExtensions
    {
        /// <summary>
        /// 计算两个字符串的相似度 (0.0 ~ 1.0)。
        /// </summary>
        public static double CalculateSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

            int distance = LevenshteinDistance(a, b);
            int maxLen = Math.Max(a.Length, b.Length);
            return 1.0 - (double)distance / maxLen;
        }

        /// <summary>
        /// 计算 Levenshtein 编辑距离（两行滚动数组优化）。
        /// </summary>
        public static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int lenA = a.Length, lenB = b.Length;
            var prev = new int[lenB + 1];
            var curr = new int[lenB + 1];

            for (int j = 0; j <= lenB; j++) prev[j] = j;

            for (int i = 1; i <= lenA; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= lenB; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                var temp = prev;
                prev = curr;
                curr = temp;
            }

            return prev[lenB];
        }
    }
}
