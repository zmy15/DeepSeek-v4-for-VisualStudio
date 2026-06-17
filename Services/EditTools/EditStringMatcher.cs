using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// 编辑字符串匹配器 — 从 EditPatchService 提取的 5 级匹配算法。
    /// 支持单文本匹配和行数组上下文匹配两种模式。
    /// 
    /// 参考: vscode-copilot-chat applyPatch/parser.ts (find_context_core, fuzzy matching)
    /// </summary>
    public static class EditStringMatcher
    {
        // ── 匹配常量 ──
        private const double MinSimilarityThreshold = 0.70;
        private const int MaxSearchLines = 500;
        private const double EditDistanceAllowancePerLine = 0.34;

        #region 单文本匹配（4 级降级）

        /// <summary>
        /// 快速验证 AI 提供的上下文是否仍在当前文件内容中。
        /// 用于检测文件是否在 AI 读取后被外部修改（如前面的编辑步骤），
        /// 避免基于过时内容应用编辑导致插入到错误位置。
        /// </summary>
        /// <param name="fileContent">文件当前完整内容</param>
        /// <param name="contextLines">AI 提供的上下文行（用于定位的代码行）</param>
        /// <returns>true 表示至少部分上下文仍可找到，文件内容未过时；false 表示关键上下文全部丢失</returns>
        public static bool VerifyContentFreshness(string fileContent, string[] contextLines)
        {
            if (contextLines == null || contextLines.Length == 0) return true; // 无上下文 → 通过（新文件等场景）
            if (string.IsNullOrEmpty(fileContent)) return false;

            // ── 取前几个非空上下文行作为采样 ──
            var sampleLines = contextLines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(3)
                .ToArray();

            if (sampleLines.Length == 0) return true;

            // ── 快速检查：每个采样行是否能在文件中找到（忽略首尾空白）──
            int foundCount = 0;
            foreach (var line in sampleLines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length < 5) continue; // 太短的行无意义，跳过

                // 尝试在文件中找到（原始 + 去空白两种方式）
                if (fileContent.Contains(trimmed, StringComparison.Ordinal)
                    || fileContent.Contains(line, StringComparison.Ordinal))
                {
                    foundCount++;
                }
            }

            // ── 至少一半的采样行能被找到 → 内容未过时 ──
            int effectiveSampleSize = sampleLines.Count(l => l.Trim().Length >= 5);
            if (effectiveSampleSize == 0) return true;

            return foundCount >= Math.Max(1, effectiveSampleSize / 2);
        }

        /// <summary>
        /// 4 级字符串匹配：在文件内容中定位目标区域。
        /// </summary>
        /// <param name="fileContent">文件当前完整内容</param>
        /// <param name="searchText">要搜索的文本</param>
        /// <param name="matchLevel">输出实际使用的匹配级别</param>
        /// <returns>匹配到的起始位置（0-based），-1 表示匹配失败</returns>
        public static int MatchWithFallback(string fileContent, string searchText, out MatchLevel matchLevel)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                matchLevel = MatchLevel.Exact;
                return -1;
            }

            // 第1级：精确匹配
            int pos = fileContent.IndexOf(searchText, StringComparison.Ordinal);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Exact;
                return pos;
            }

            // 第2级：空白弹性匹配
            pos = WhitespaceFlexibleMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.WhitespaceFlexible;
                return pos;
            }

            // 第3级：模糊匹配
            pos = FuzzyMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Fuzzy;
                return pos;
            }

            // 第4级：Levenshtein 相似度匹配
            pos = LevenshteinMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Levenshtein;
                return pos;
            }

            // 第5级：行级滑动窗口相似度匹配（95% 阈值，最后兜底）
            // 参考: editFileToolUtils.tsx trySimilarityMatch
            pos = SimilarityMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Levenshtein;
                return pos;
            }

            matchLevel = MatchLevel.Levenshtein;
            return -1;
        }

        private static int WhitespaceFlexibleMatch(string fileContent, string searchText)
        {
            var normalizedFile = NormalizeWhitespace(fileContent);
            var normalizedSearch = NormalizeWhitespace(searchText);

            int pos = normalizedFile.IndexOf(normalizedSearch, StringComparison.Ordinal);
            if (pos < 0) return -1;

            return MapNormalizedToOriginal(fileContent, normalizedFile, pos);
        }

        private static int FuzzyMatch(string fileContent, string searchText)
        {
            var fuzzyFile = NormalizeFuzzy(fileContent);
            var fuzzySearch = NormalizeFuzzy(searchText);

            int pos = fuzzyFile.IndexOf(fuzzySearch, StringComparison.Ordinal);
            if (pos < 0) return -1;

            return MapNormalizedToOriginal(fileContent, fuzzyFile, pos);
        }

        private static int LevenshteinMatch(string fileContent, string searchText)
        {
            var fileLines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var searchLines = searchText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            if (searchLines.Length == 0 || fileLines.Length == 0) return -1;
            if (searchLines.Length > fileLines.Length) return -1;

            int bestPos = -1;
            double bestSimilarity = 0;
            int bestMatchedLength = 0;

            int maxStartLine = Math.Min(fileLines.Length - searchLines.Length, MaxSearchLines);

            for (int start = 0; start <= maxStartLine; start++)
            {
                int endLine = Math.Min(start + searchLines.Length, fileLines.Length);
                var windowLines = fileLines.Skip(start).Take(endLine - start);
                string window = string.Join("\n", windowLines);
                string search = string.Join("\n", searchLines);

                double similarity = LevenshteinDistanceExtensions.CalculateSimilarity(window, search);
                int windowLen = window.Length;

                if (similarity > bestSimilarity ||
                    (Math.Abs(similarity - bestSimilarity) < 0.01 && windowLen > bestMatchedLength))
                {
                    bestSimilarity = similarity;
                    bestPos = GetLineStartPosition(fileContent, start);
                    bestMatchedLength = windowLen;
                }
            }

            return bestSimilarity >= MinSimilarityThreshold ? bestPos : -1;
        }

        #endregion

        #region 行级上下文匹配（5 级降级，参考 find_context_core）

        /// <summary>
        /// 在文件行数组中匹配上下文行。
        /// 参考 find_context_core: 5 层降级策略。
        /// </summary>
        /// <returns>匹配到的行索引（0-based），-1 表示未找到。</returns>
        public static int MatchContextInFileLines(
            string[] fileLines, string[] contextLines, int startLine, out MatchLevel level)
        {
            level = MatchLevel.Exact;

            if (contextLines.Length == 0 || startLine >= fileLines.Length)
                return -1;

            int maxStart = fileLines.Length - contextLines.Length;
            if (maxStart < 0) return -1;

            // Pass 1: 精确匹配（Unicode NFC 标准化后）
            var canonFile = fileLines.Select(NormalizeUnicode).ToArray();
            var canonCtx = contextLines.Select(NormalizeUnicode).ToArray();
            int match = FindLineSequence(canonFile, canonCtx, startLine);
            if (match >= 0) { level = MatchLevel.Exact; return match; }

            // Pass 2: 忽略尾部空白
            var trimEndFile = canonFile.Select(l => l.TrimEnd()).ToArray();
            var trimEndCtx = canonCtx.Select(l => l.TrimEnd()).ToArray();
            match = FindLineSequence(trimEndFile, trimEndCtx, startLine);
            if (match >= 0) { level = MatchLevel.WhitespaceFlexible; return match; }

            // Pass 3: 标准化显式 \t → 制表符
            var tabFile = trimEndFile.Select(ReplaceExplicitTabs).ToArray();
            var tabCtx = trimEndCtx.Select(ReplaceExplicitTabs).ToArray();
            match = FindLineSequence(tabFile, tabCtx, startLine);
            if (match >= 0) { level = MatchLevel.Fuzzy; return match; }

            // Pass 3.5: 标准化显式 \n → 换行符（仅单行上下文，参考 parser.ts Pass 4）
            if (contextLines.Length == 1)
            {
                var nlFile = tabFile.Select(ReplaceExplicitNewlines).ToArray();
                var nlCtx = tabCtx.Select(ReplaceExplicitNewlines).ToArray();
                // expandFile: 如果 ctx 被 \n 拆成多行，扩展匹配窗口
                if (nlCtx.Length != contextLines.Length)
                {
                    match = FindLineSequence(nlFile, nlCtx, startLine);
                    if (match >= 0) { level = MatchLevel.Fuzzy; return match; }
                }
                else
                {
                    match = FindLineSequence(nlFile, nlCtx, startLine);
                    if (match >= 0) { level = MatchLevel.Fuzzy; return match; }
                }
            }

            // Pass 4: 忽略所有周围空白
            var trimAllFile = tabFile.Select(l => l.Trim()).ToArray();
            var trimAllCtx = tabCtx.Select(l => l.Trim()).ToArray();
            match = FindLineSequence(trimAllFile, trimAllCtx, startLine);
            if (match >= 0) { level = MatchLevel.Fuzzy; return match; }

            // Pass 5: 编辑距离模糊匹配
            match = FuzzyLineMatch(trimAllFile, trimAllCtx, startLine);
            if (match >= 0) { level = MatchLevel.Levenshtein; return match; }

            return -1;
        }

        /// <summary>
        /// 在行数组中查找连续行序列的起始位置。
        /// </summary>
        public static int FindLineSequence(string[] fileLines, string[] pattern, int startLine)
        {
            int maxStart = fileLines.Length - pattern.Length;
            for (int i = Math.Max(0, startLine); i <= maxStart; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (fileLines[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// 编辑距离模糊行匹配：累计各行编辑距离不超过阈值。
        /// 额外要求至少一半的上下文行精确匹配（距离 0），防止在结构不同但总编辑距离
        /// 相近的代码块中产生误匹配（如重复的 getter/setter 模式）。
        /// </summary>
        private static int FuzzyLineMatch(string[] fileLines, string[] contextLines, int startLine)
        {
            int maxDistance = (int)Math.Floor(contextLines.Length * EditDistanceAllowancePerLine);
            if (maxDistance <= 0) return -1;

            int maxStart = fileLines.Length - contextLines.Length;
            int minExactMatches = Math.Max(1, contextLines.Length / 2); // 至少一半行精确匹配

            for (int i = Math.Max(0, startLine); i <= maxStart; i++)
            {
                int totalDistance = 0;
                int exactMatches = 0;
                for (int j = 0; j < contextLines.Length && totalDistance <= maxDistance; j++)
                {
                    int dist = LevenshteinDistanceExtensions.LevenshteinDistance(
                        fileLines[i + j], contextLines[j]);
                    totalDistance += dist;
                    if (dist == 0) exactMatches++;
                }
                if (totalDistance <= maxDistance && exactMatches >= minExactMatches)
                    return i;
            }
            return -1;
        }

        #endregion

        #region @@ 标记定位

        /// <summary>
        /// 通过 @@ 标记文本在文件中定位。
        /// </summary>
        public static int MatchContextViaMarkers(
            string[] fileLines, List<string> contextMarkers, int startLine)
        {
            if (contextMarkers == null || contextMarkers.Count == 0) return -1;

            foreach (var marker in contextMarkers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                string normalized = NormalizeUnicode(marker);
                for (int i = Math.Max(0, startLine); i < fileLines.Length; i++)
                {
                    if (NormalizeUnicode(fileLines[i]).Contains(normalized))
                        return i;
                }
                // 忽略大小写再试
                for (int i = Math.Max(0, startLine); i < fileLines.Length; i++)
                {
                    if (NormalizeUnicode(fileLines[i]).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        #endregion

        #region 位置计算

        /// <summary>
        /// 根据字符位置获取行列号（0-based）。
        /// </summary>
        public static (int line, int column) GetLineColumn(string text, int position)
        {
            if (position <= 0 || string.IsNullOrEmpty(text)) return (0, 0);
            if (position >= text.Length) position = text.Length;

            int line = 0;
            int col = 0;

            for (int i = 0; i < position; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    col = 0;
                }
                else
                {
                    col++;
                }
            }

            return (line, col);
        }

        /// <summary>
        /// 获取指定行的起始字符位置。
        /// </summary>
        public static int GetLineStartPosition(string text, int lineNumber)
        {
            if (lineNumber <= 0) return 0;

            int currentLine = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (currentLine == lineNumber) return i;
                if (text[i] == '\n') currentLine++;
            }

            return text.Length;
        }

        /// <summary>
        /// 通过行计数定位非精确匹配的结束位置。
        /// </summary>
        public static int FindMatchEndByLineCount(string fileContent, int matchPos, string searchPattern)
        {
            if (string.IsNullOrEmpty(searchPattern) || matchPos >= fileContent.Length)
                return Math.Min(matchPos + 1, fileContent.Length);

            int searchLineCount = 1;
            for (int i = 0; i < searchPattern.Length; i++)
                if (searchPattern[i] == '\n') searchLineCount++;

            int pos = matchPos;
            int linesFound = 1;

            while (pos < fileContent.Length && linesFound < searchLineCount)
            {
                if (fileContent[pos] == '\n')
                {
                    linesFound++;
                    if (linesFound >= searchLineCount)
                    {
                        pos++;
                        break;
                    }
                }
                pos++;
            }

            if (linesFound < searchLineCount)
                return fileContent.Length;

            return Math.Min(pos, fileContent.Length);
        }

        #endregion

        #region 文本标准化

        /// <summary>
        /// Unicode 标点规范化：将全角/变体标点映射为 ASCII。
        /// </summary>
        public static string NormalizeUnicode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            string normalized = s.Normalize(NormalizationForm.FormC);

            if (normalized.All(c => c < 128)) return normalized;

            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                sb.Append(c switch
                {
                    '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => '-',
                    '\u201C' or '\u201D' or '\u201E' or '\u00AB' or '\u00BB' => '"',
                    '\u2018' or '\u2019' or '\u201B' => '\'',
                    '\u00A0' or '\u202F' => ' ',
                    _ => c,
                });
            }
            return sb.ToString();
        }

        /// <summary>
        /// 将显式的 "\t" 字符串替换为实际制表符。
        /// </summary>
        public static string ReplaceExplicitTabs(string s)
        {
            return s.Replace("\\t", "\t");
        }

        /// <summary>
        /// 将显式的 "\n" 字符串替换为实际换行符（同时处理 \t）。
        /// 参考: parser.ts replace_explicit_nl
        /// </summary>
        public static string ReplaceExplicitNewlines(string s)
        {
            return ReplaceExplicitTabs(s).Replace("\\n", "\n");
        }

        /// <summary>
        /// 标准化空白：将连续空白合并为单个空格。
        /// </summary>
        public static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = Regex.Replace(text, @"\s+", " ");
            return result.Trim();
        }

        /// <summary>
        /// 模糊标准化：移除所有空白和标点符号，仅保留字母数字和下划线。
        /// </summary>
        public static string NormalizeFuzzy(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = Regex.Replace(text, @"[^a-zA-Z0-9_]", "");
            return result.ToLowerInvariant();
        }

        /// <summary>
        /// 将标准化位置映射回原始位置。
        /// </summary>
        public static int MapNormalizedToOriginal(string original, string normalized, int normalizedPos)
        {
            int origIdx = 0;
            int normIdx = 0;

            while (normIdx < normalizedPos && origIdx < original.Length)
            {
                if (char.IsWhiteSpace(original[origIdx]))
                {
                    while (origIdx < original.Length && char.IsWhiteSpace(original[origIdx]))
                        origIdx++;
                    while (normIdx < normalized.Length && normalized[normIdx] == ' ')
                        normIdx++;
                }
                else if (char.ToLowerInvariant(original[origIdx]) ==
                         (normIdx < normalized.Length ? char.ToLowerInvariant(normalized[normIdx]) : '\0'))
                {
                    origIdx++;
                    normIdx++;
                }
                else
                {
                    origIdx++;
                }
            }

            return origIdx;
        }

        /// <summary>
        /// 统一换行符为 \n。
        /// </summary>
        public static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// 将文本中的所有行尾标准化为 Windows CRLF（\r\n）。
        /// </summary>
        public static string NormalizeToCrLf(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return NormalizeLineEndings(text).Replace("\n", "\r\n");
        }

        /// <summary>
        /// 获取 oldString 和 newString 首尾相同的字符数。
        /// 参考: editFileToolUtils.tsx getIdenticalChars
        /// 用于缩小编辑范围，只替换真正变化的部分。
        /// </summary>
        /// <returns>(leading 相同前导字符数, trailing 相同尾部字符数)</returns>
        public static (int leading, int trailing) GetIdenticalLeadingTrailingChars(
            string oldString, string newString)
        {
            int leading = 0;
            int minLen = Math.Min(oldString.Length, newString.Length);
            while (leading < minLen && oldString[leading] == newString[leading])
                leading++;

            int trailing = 0;
            while (trailing + leading < minLen &&
                   oldString[oldString.Length - 1 - trailing] == newString[newString.Length - 1 - trailing])
                trailing++;

            return (leading, trailing);
        }

        /// <summary>
        /// 去除 AI 输出中第一行的文件路径注释。
        /// 参考: markdown.ts removeLeadingFilepathComment
        /// 例如: "path/to/file.cs\n...code..." → "...code..."
        /// </summary>
        public static string RemoveLeadingFilepathComment(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 常见文件扩展名模式
            var patterns = new[]
            {
                @"^[a-zA-Z]:[\\/][^\r\n]{3,200}\r?\n",        // Windows 绝对路径
                @"^\/[^\r\n]{3,200}\r?\n",                     // Unix 绝对路径
                @"^[^\r\n]{3,200}\.(cs|cpp|h|ts|js|py|java|go|rs|tsx|jsx|vue|html|css|json|xml|yaml|yml|md)\r?\n",
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
                if (match.Success)
                {
                    string remaining = text.Substring(match.Length);
                    if (!string.IsNullOrWhiteSpace(remaining))
                        return remaining;
                }
            }

            return text;
        }

        /// <summary>
        /// 相似度匹配：基于滑动窗口的 95% 行级相似度匹配。
        /// 参考: editFileToolUtils.tsx trySimilarityMatch
        /// 作为最后一级的兜底策略。
        /// </summary>
        public static int SimilarityMatch(string fileContent, string searchText, double threshold = 0.95)
        {
            var eol = "\n";
            var fileLines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var searchLines = searchText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            if (searchLines.Length == 0 || fileLines.Length == 0) return -1;
            if (searchLines.Length > fileLines.Length) return -1;
            if (searchText.Length > 1000 || searchLines.Length > 20) return -1;
            if (fileLines.Length > 1000) return -1;

            int bestStartLine = -1;
            double bestSimilarity = threshold;
            int bestOldLength = 0;
            int startOffset = 0;

            for (int i = 0; i <= fileLines.Length - searchLines.Length; i++)
            {
                double totalSimilarity = 0;
                int oldLength = 0;

                for (int j = 0; j < searchLines.Length; j++)
                {
                    double sim = LevenshteinDistanceExtensions.CalculateSimilarity(
                        searchLines[j], fileLines[i + j]);
                    totalSimilarity += sim;
                    oldLength += fileLines[i + j].Length + eol.Length;
                }

                double avgSimilarity = totalSimilarity / searchLines.Length;
                if (avgSimilarity > bestSimilarity)
                {
                    bestSimilarity = avgSimilarity;
                    bestStartLine = i;
                    bestOldLength = oldLength;
                }

                startOffset += fileLines[i].Length + eol.Length;
            }

            if (bestStartLine < 0) return -1;

            // 计算字符偏移
            int charOffset = 0;
            for (int i = 0; i < bestStartLine; i++)
                charOffset += fileLines[i].Length + eol.Length;

            return charOffset;
        }

        #endregion

        #region 结构完整性校验

        /// <summary>
        /// 对修改区域的代码进行快速结构校验。
        /// 检查括号配对、基本语法标记等，返回问题列表。
        /// </summary>
        /// <param name="content">修改后文件完整内容</param>
        /// <param name="editStartLine">编辑起始行（0-based）</param>
        /// <param name="editEndLine">编辑结束行（0-based）</param>
        /// <param name="originalLines">原始文件行数组（用于对比基线）</param>
        /// <returns>问题描述列表，为空表示通过</returns>
        public static List<string> ValidateStructureIntegrity(
            string content, int editStartLine, int editEndLine, string[] originalLines)
        {
            var errors = new List<string>();
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            int checkStart = Math.Max(0, editStartLine - 20);
            int checkEnd = Math.Min(lines.Length - 1, editEndLine + 20);

            var bracketStack = new Stack<(char bracket, int line)>();
            for (int i = checkStart; i <= checkEnd && i < lines.Length; i++)
            {
                foreach (char ch in lines[i])
                {
                    switch (ch)
                    {
                        case '{':
                            bracketStack.Push(('{', i));
                            break;
                        case '}':
                            if (bracketStack.Count == 0 || bracketStack.Peek().bracket != '{')
                                errors.Add($"行 {i + 1}: 多余的 '}}' 闭合符号（缺少对应的 '{{'）");
                            else
                                bracketStack.Pop();
                            break;
                        case '(':
                            bracketStack.Push(('(', i));
                            break;
                        case ')':
                            if (bracketStack.Count > 0 && bracketStack.Peek().bracket == '(')
                                bracketStack.Pop();
                            else
                                errors.Add($"行 {i + 1}: 可能多余的 ')' 闭合符号");
                            break;
                        case '[':
                            bracketStack.Push(('[', i));
                            break;
                        case ']':
                            if (bracketStack.Count > 0 && bracketStack.Peek().bracket == '[')
                                bracketStack.Pop();
                            else
                                errors.Add($"行 {i + 1}: 可能多余的 ']' 闭合符号");
                            break;
                    }
                }
            }
            foreach (var (bracket, line) in bracketStack)
                errors.Add($"行 {line + 1}: '{bracket}' 没有对应的闭合符号");

            int windowBraceDelta = CountBracesInRange(lines, checkStart, checkEnd) -
                                   CountBracesInRange(originalLines,
                                       Math.Min(checkStart, originalLines.Length - 1),
                                       Math.Min(checkEnd, originalLines.Length - 1));
            if (Math.Abs(windowBraceDelta) > 3)
                errors.Add($"编辑窗口内括号数量变化较大 (Δ={windowBraceDelta})，请人工确认");

            return errors;
        }

        private static int CountBracesInRange(string[] lines, int start, int end)
        {
            int count = 0;
            for (int i = Math.Max(0, start); i <= end && i < lines.Length; i++)
                foreach (char ch in lines[i])
                    if (ch == '{' || ch == '}') count++;
            return count;
        }

        #endregion
    }
}
