using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 代码差异对比工具。基于 LCS（最长公共子序列）算法生成统一差异输出，
    /// 以 +/- 标记显示代码变更，适用于 AI 修改代码前后的对比。
    /// </summary>
    public static class CodeDiffHelper
    {
        #region Public Methods

        /// <summary>
        /// 生成两段代码的统一差异对比（带 +/- 标记）。
        /// </summary>
        /// <param name="original">修改前的原始代码。</param>
        /// <param name="modified">AI 修改后的代码。</param>
        /// <param name="filePath">可选文件路径，用于差异头部信息。</param>
        /// <returns>包含 +/- 标记和变更统计的统一差异字符串。</returns>
        public static string GenerateUnifiedDiff(string original, string modified, string filePath = "")
        {
            if (original == modified)
            {
                return "No changes detected.";
            }

            string[] originalLines = SplitLines(original);
            string[] modifiedLines = SplitLines(modified);

            List<DiffLine> diff = ComputeDiff(originalLines, modifiedLines);

            if (diff.Count == 0 || diff.TrueForAll(d => d.Type == DiffLineType.Unchanged))
            {
                return "No changes detected.";
            }

            StringBuilder sb = new();

            // Header
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                sb.AppendLine($"--- a/{filePath}");
                sb.AppendLine($"+++ b/{filePath}");
            }
            else
            {
                sb.AppendLine("--- Original");
                sb.AppendLine("+++ Modified");
            }

            sb.AppendLine();

            // Group consecutive lines for compact output
            int contextLines = 3;
            bool inChange = false;
            int unchangedCount = 0;
            StringBuilder pendingUnchanged = new();

            for (int i = 0; i < diff.Count; i++)
            {
                DiffLine line = diff[i];

                if (line.Type == DiffLineType.Unchanged)
                {
                    if (inChange)
                    {
                        unchangedCount++;
                        pendingUnchanged.AppendLine($" {line.Text}");

                        // If we've accumulated enough context after changes, flush
                        if (unchangedCount >= contextLines)
                        {
                            // Check if next line is also unchanged
                            bool nextIsChange = (i + 1 < diff.Count && diff[i + 1].Type != DiffLineType.Unchanged);

                            if (!nextIsChange)
                            {
                                sb.Append(pendingUnchanged);
                                pendingUnchanged.Clear();
                                unchangedCount = 0;
                                inChange = false;
                            }
                        }
                    }
                    else
                    {
                        // Before changes, only show limited context
                        sb.AppendLine($" {line.Text}");
                    }
                }
                else
                {
                    if (!inChange)
                    {
                        // Flush any trailing unchanged context from before the change
                        // This is handled by the contextLines logic above
                        inChange = true;
                        unchangedCount = 0;
                        pendingUnchanged.Clear();
                    }
                    else
                    {
                        // Flush pending unchanged context
                        if (pendingUnchanged.Length > 0)
                        {
                            sb.Append(pendingUnchanged);
                            pendingUnchanged.Clear();
                            unchangedCount = 0;
                        }
                    }

                    char prefix = line.Type == DiffLineType.Added ? '+' : '-';
                    sb.AppendLine($"{prefix}{line.Text}");
                }
            }

            // Summary
            int addedCount = diff.FindAll(d => d.Type == DiffLineType.Added).Count;
            int removedCount = diff.FindAll(d => d.Type == DiffLineType.Removed).Count;

            if (addedCount > 0 || removedCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"  {addedCount} addition(s), {removedCount} deletion(s)");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成紧凑的行内差异（适合短代码片段）。
        /// </summary>
        /// <param name="original">原始代码。</param>
        /// <param name="modified">修改后的代码。</param>
        /// <returns>带行内变更标记的字符串。</returns>
        public static string GenerateInlineDiff(string original, string modified)
        {
            if (original == modified)
            {
                return "No changes.";
            }

            StringBuilder sb = new();
            string[] oLines = SplitLines(original);
            string[] mLines = SplitLines(modified);

            int maxLines = Math.Max(oLines.Length, mLines.Length);

            for (int i = 0; i < maxLines; i++)
            {
                string oLine = i < oLines.Length ? oLines[i] : string.Empty;
                string mLine = i < mLines.Length ? mLines[i] : string.Empty;

                if (oLine == mLine)
                {
                    sb.AppendLine($"  {oLine}");
                }
                else
                {
                    if (!string.IsNullOrEmpty(oLine))
                    {
                        sb.AppendLine($"- {oLine}");
                    }
                    if (!string.IsNullOrEmpty(mLine))
                    {
                        sb.AppendLine($"+ {mLine}");
                    }
                }
            }

            return sb.ToString();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 将文本按行分割，保留空行。
        /// </summary>
        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new[] { string.Empty };
            }

            return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }

        /// <summary>
        /// 使用基于 LCS 的简化 Myers 算法计算逐行差异。
        /// </summary>
        private static List<DiffLine> ComputeDiff(string[] original, string[] modified)
        {
            // Compute Longest Common Subsequence (LCS) table
            int m = original.Length;
            int n = modified.Length;
            int[,] lcs = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (original[i - 1] == modified[j - 1])
                    {
                        lcs[i, j] = lcs[i - 1, j - 1] + 1;
                    }
                    else
                    {
                        lcs[i, j] = Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
                    }
                }
            }

            // Backtrack to generate diff
            List<DiffLine> diff = new();
            int oi = m;
            int mi = n;

            while (oi > 0 || mi > 0)
            {
                if (oi > 0 && mi > 0 && original[oi - 1] == modified[mi - 1])
                {
                    diff.Insert(0, new DiffLine(DiffLineType.Unchanged, original[oi - 1]));
                    oi--;
                    mi--;
                }
                else if (mi > 0 && (oi == 0 || lcs[oi, mi - 1] >= lcs[oi - 1, mi]))
                {
                    diff.Insert(0, new DiffLine(DiffLineType.Added, modified[mi - 1]));
                    mi--;
                }
                else if (oi > 0)
                {
                    diff.Insert(0, new DiffLine(DiffLineType.Removed, original[oi - 1]));
                    oi--;
                }
            }

            return diff;
        }

        #endregion

        #region Nested Types

        private enum DiffLineType
        {
            Unchanged,
            Added,
            Removed
        }

        private class DiffLine
        {
            public DiffLineType Type { get; }
            public string Text { get; }

            public DiffLine(DiffLineType type, string text)
            {
                Type = type;
                Text = text;
            }
        }

        #endregion
    }
}
