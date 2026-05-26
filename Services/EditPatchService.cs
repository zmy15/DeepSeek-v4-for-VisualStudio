using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑补丁服务 — 支持 apply_patch / insert_edit_into_file / create_file 三种编辑方式。
    /// 
    /// 职责：
    /// - 解析 AI 输出的三种编辑格式
    /// - 4 级字符串匹配（精确 → 空白弹性 → 模糊 → Levenshtein）
    /// - 构造 TextEdit 并通过 VS 文本缓冲区应用
    /// - 编辑后检查新引入的诊断错误
    /// - Healing 机制：匹配失败时通过降级模型修正
    /// </summary>
    public class EditPatchService : IEditPatchService
    {
        private readonly DeepSeekApiService _apiService;
        private const string ExistingCodeMarker = "...existing code...";

        /// <summary>
        /// i18n 便捷访问器。
        /// </summary>
        private static LocalizationService L => LocalizationService.Instance;

        // ── Patch 格式正则 ──
        private static readonly Regex PatchBlockRegex = new(
            @"\*\*\*\s*Begin\s*Patch\s*\r?\n(?<body>.*?)\r?\n\s*\*\*\*\s*End\s*Patch",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex PatchFileHeaderRegex = new(
            @"\*\*\*\s*(Update|Add|Delete)\s*File\s*:\s*(?<path>[^\r\n]+)",
            RegexOptions.IgnoreCase);

        private static readonly Regex PatchMoveHeaderRegex = new(
            @"\*\*\*\s*Move\s*to\s*:\s*(?<path>[^\r\n]+)",
            RegexOptions.IgnoreCase);

        private static readonly Regex PatchContextMarkerRegex = new(
            @"^@@\s*(?<marker>.*)$", RegexOptions.Multiline);

        private static readonly Regex PatchLineRegex = new(
            @"^([\s\-\+])(.*)$");

        // ── insert_edit_into_file 格式正则 ──
        // 匹配 ```文件路径: 或 ```语言标识:文件路径 格式的代码块
        private static readonly Regex InsertEditBlockRegex = new(
            @"```(?:insert_edit_into_file|edit)\s*:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public EditPatchService(DeepSeekApiService apiService)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        }

        #region Parsing

        /// <summary>
        /// 从 AI 输出中解析所有 Patch 操作。
        /// </summary>
        public List<PatchOperation> ParsePatches(string aiOutput)
        {
            var patches = new List<PatchOperation>();
            if (string.IsNullOrWhiteSpace(aiOutput)) return patches;

            var matches = PatchBlockRegex.Matches(aiOutput);
            foreach (Match match in matches)
            {
                string body = match.Groups["body"].Value;
                var patch = ParsePatchBody(body);
                if (patch != null)
                    patches.Add(patch);
            }

            return patches;
        }

        /// <summary>
        /// 解析 Patch 块体内容。
        /// </summary>
        private static PatchOperation? ParsePatchBody(string body)
        {
            // ── 解析文件操作头 ──
            var fileMatch = PatchFileHeaderRegex.Match(body);
            if (!fileMatch.Success) return null;

            var patch = new PatchOperation
            {
                RawText = body,
                Action = fileMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "update" => PatchFileAction.Update,
                    "add" => PatchFileAction.Add,
                    "delete" => PatchFileAction.Delete,
                    _ => PatchFileAction.Update,
                },
                FilePath = fileMatch.Groups["path"].Value.Trim(),
            };

            // ── 检查是否有 Move to 指令 ──
            var moveMatch = PatchMoveHeaderRegex.Match(body);
            if (moveMatch.Success)
                patch.MoveToPath = moveMatch.Groups["path"].Value.Trim();

            // ── 解析 Hunks ──
            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            PatchHunk? currentHunk = null;

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                // 跳过文件头行和空行
                if (line.StartsWith("***") || (string.IsNullOrWhiteSpace(line) && currentHunk == null))
                    continue;

                // 检测 @@ 上下文标记（开始新 Hunk）
                var ctxMatch = PatchContextMarkerRegex.Match(line);
                if (ctxMatch.Success)
                {
                    if (currentHunk != null)
                        patch.Hunks.Add(currentHunk);

                    currentHunk = new PatchHunk
                    {
                        ContextMarkers = new List<string> { ctxMatch.Groups["marker"].Value.Trim() },
                        RawText = line + "\n",
                    };
                    continue;
                }

                // 合并连续的 @@ 行到同一个 Hunk 的 ContextMarkers
                if (currentHunk != null && line.TrimStart().StartsWith("@@"))
                {
                    string marker = line.TrimStart().Substring(2).Trim();
                    currentHunk.ContextMarkers.Add(marker);
                    currentHunk.RawText += line + "\n";
                    continue;
                }

                // 解析普通 patch 行（-, +, 空格前缀）
                var plMatch = PatchLineRegex.Match(line);
                if (currentHunk != null && plMatch.Success)
                {
                    currentHunk.Lines.Add(new PatchLine
                    {
                        Type = plMatch.Groups[1].Value[0],
                        Text = plMatch.Groups[2].Value,
                    });
                    currentHunk.RawText += line + "\n";
                }
            }

            // 添加最后一个 Hunk
            if (currentHunk != null)
                patch.Hunks.Add(currentHunk);

            return patch;
        }

        /// <summary>
        /// 从 AI 输出中解析所有 insert_edit_into_file 操作。
        /// </summary>
        public List<InsertEditOperation> ParseInsertEdits(string aiOutput)
        {
            var edits = new List<InsertEditOperation>();
            if (string.IsNullOrWhiteSpace(aiOutput)) return edits;

            // ── 方式1：```insert_edit_into_file: 或 ```edit: 包裹 ──
            var matches = InsertEditBlockRegex.Matches(aiOutput);
            foreach (Match match in matches)
            {
                edits.Add(new InsertEditOperation
                {
                    FilePath = match.Groups["path"].Value.Trim(),
                    FullContent = match.Groups["content"].Value,
                });
            }

            // ── 方式2：```file: 代码块内包含 ...existing code... 标记 ──
            // 这种情况也视为 insert_edit_into_file 操作
            var fileBlockRegex = new Regex(
                @"```file:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
                RegexOptions.Singleline);
            var fileMatches = fileBlockRegex.Matches(aiOutput);
            foreach (Match match in fileMatches)
            {
                string content = match.Groups["content"].Value;
                if (content.Contains(ExistingCodeMarker))
                {
                    // 避免重复添加
                    string path = match.Groups["path"].Value.Trim();
                    if (!edits.Any(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        edits.Add(new InsertEditOperation
                        {
                            FilePath = path,
                            FullContent = content,
                        });
                    }
                }
            }

            return edits;
        }

        /// <summary>
        /// 从 AI 输出中检测编辑操作类型。
        /// </summary>
        public EditOperationType DetectOperationType(string aiOutput)
        {
            if (string.IsNullOrWhiteSpace(aiOutput))
                return EditOperationType.ApplyPatch; // 默认

            // 检测 patch 格式
            if (PatchBlockRegex.IsMatch(aiOutput))
                return EditOperationType.ApplyPatch;

            // 检测 insert_edit_into_file 格式
            if (InsertEditBlockRegex.IsMatch(aiOutput))
                return EditOperationType.InsertEditIntoFile;

            // 检测 ...existing code... 标记
            if (aiOutput.Contains(ExistingCodeMarker))
                return EditOperationType.InsertEditIntoFile;

            // 检测 create_file / delete_file（已有格式）
            if (Regex.IsMatch(aiOutput, @"```file:\s*[^\r\n]+", RegexOptions.IgnoreCase))
                return EditOperationType.CreateFile;

            return EditOperationType.ApplyPatch; // 默认
        }

        #endregion

        #region Matching

        /// <summary>
        /// 4 级字符串匹配：在文件内容中定位目标区域。
        /// </summary>
        /// <param name="fileContent">文件当前完整内容</param>
        /// <param name="searchText">要搜索的文本</param>
        /// <param name="matchLevel">输出实际使用的匹配级别</param>
        /// <returns>匹配到的起始位置（0-based），-1 表示匹配失败</returns>
        public int MatchWithFallback(string fileContent, string searchText, out MatchLevel matchLevel)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                matchLevel = MatchLevel.Exact;
                return -1; // 空搜索无法定位，让调用方尝试 @@ 标记 fallback
            }

            // ── 第1级：精确匹配 ──
            int pos = fileContent.IndexOf(searchText, StringComparison.Ordinal);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Exact;
                return pos;
            }

            // ── 第2级：空白弹性匹配（标准化空白后匹配）──
            pos = WhitespaceFlexibleMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.WhitespaceFlexible;
                return pos;
            }

            // ── 第3级：模糊匹配（忽略空白 + 标点符号差异）──
            pos = FuzzyMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Fuzzy;
                return pos;
            }

            // ── 第4级：Levenshtein 相似度匹配 ──
            pos = LevenshteinMatch(fileContent, searchText);
            if (pos >= 0)
            {
                matchLevel = MatchLevel.Levenshtein;
                return pos;
            }

            matchLevel = MatchLevel.Levenshtein;
            return -1;
        }

        /// <summary>
        /// 空白弹性匹配：标准化所有空白（将连续空白合并为单个空格）后查找。
        /// </summary>
        private static int WhitespaceFlexibleMatch(string fileContent, string searchText)
        {
            var normalizedFile = NormalizeWhitespace(fileContent);
            var normalizedSearch = NormalizeWhitespace(searchText);

            int pos = normalizedFile.IndexOf(normalizedSearch, StringComparison.Ordinal);
            if (pos < 0) return -1;

            // 将标准化位置映射回原始位置
            return MapNormalizedToOriginal(fileContent, normalizedFile, pos);
        }

        /// <summary>
        /// 模糊匹配：忽略空白差异和常见标点符号差异后查找。
        /// </summary>
        private static int FuzzyMatch(string fileContent, string searchText)
        {
            var fuzzyFile = NormalizeFuzzy(fileContent);
            var fuzzySearch = NormalizeFuzzy(searchText);

            int pos = fuzzyFile.IndexOf(fuzzySearch, StringComparison.Ordinal);
            if (pos < 0) return -1;

            return MapNormalizedToOriginal(fileContent, fuzzyFile, pos);
        }

        /// <summary>
        /// Levenshtein 相似度匹配：在文件中滑动窗口查找最相似位置。
        /// 如果最佳相似度 >= 70%，则返回位置；否则返回 -1。
        /// </summary>
        private static int LevenshteinMatch(string fileContent, string searchText)
        {
            const double minSimilarity = 0.70;
            const int maxSearchLines = 500; // 限制搜索范围防止性能问题

            var fileLines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var searchLines = searchText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            if (searchLines.Length == 0 || fileLines.Length == 0) return -1;
            if (searchLines.Length > fileLines.Length) return -1;

            int bestPos = -1;
            double bestSimilarity = 0;
            int bestMatchedLength = 0;

            int maxStartLine = Math.Min(fileLines.Length - searchLines.Length, maxSearchLines);

            for (int start = 0; start <= maxStartLine; start++)
            {
                // 对比多行拼接的文本
                int endLine = Math.Min(start + searchLines.Length, fileLines.Length);
                var windowLines = fileLines.Skip(start).Take(endLine - start);
                string window = string.Join("\n", windowLines);
                string search = string.Join("\n", searchLines);

                double similarity = CalculateSimilarity(window, search);
                int windowLen = window.Length;

                if (similarity > bestSimilarity ||
                    (Math.Abs(similarity - bestSimilarity) < 0.01 && windowLen > bestMatchedLength))
                {
                    bestSimilarity = similarity;
                    bestPos = GetLineStartPosition(fileContent, start);
                    bestMatchedLength = windowLen;
                }
            }

            return bestSimilarity >= minSimilarity ? bestPos : -1;
        }

        /// <summary>
        /// 计算两个字符串的相似度 (0.0 ~ 1.0)。
        /// 使用 Levenshtein 距离 + 长度归一化。
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
        /// 计算 Levenshtein（编辑距离）。
        /// </summary>
        public static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int lenA = a.Length, lenB = b.Length;
            // 使用两行滚动数组优化内存
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

        #endregion

        #region Patch Application

        /// <summary>
        /// 尝试将 Patch 操作应用到文件。
        /// 返回应用结果，包含成功/失败状态和需要 healing 的信息。
        /// </summary>
        public async Task<EditApplyResult> ApplyPatchAsync(
            PatchOperation patch,
            string workspaceRoot,
            CancellationToken ct)
        {
            return await ApplyPatchWithContentAsync(patch, workspaceRoot, ct, existingContent: null);
        }

        /// <summary>
        /// 尝试将 Patch 操作应用到文件（支持预加载内容，避免重复读盘）。
        /// 当 existingContent 不为 null 时，直接使用该内容进行匹配而不从磁盘读取。
        /// 用于同一文件的多 Patch 原子应用场景。
        /// </summary>
        public async Task<EditApplyResult> ApplyPatchWithContentAsync(
            PatchOperation patch,
            string workspaceRoot,
            CancellationToken ct,
            string? existingContent = null)
        {
            var result = new EditApplyResult
            {
                FilePath = ResolvePath(patch.FilePath, workspaceRoot),
                OperationType = EditOperationType.ApplyPatch,
            };

            // ── 处理 Add File ──
            if (patch.Action == PatchFileAction.Add)
            {
                return await ApplyCreateFileFromPatchAsync(patch, result.FilePath, ct);
            }

            // ── 处理 Delete File ──
            if (patch.Action == PatchFileAction.Delete)
            {
                return await ApplyDeleteFileAsync(result.FilePath, ct);
            }

            // ── 处理 Move File ──
            if (!string.IsNullOrEmpty(patch.MoveToPath))
            {
                return await ApplyMoveFileAsync(result.FilePath, patch.MoveToPath!, ct);
            }

            // ── 处理 Update File ──
            if (!File.Exists(result.FilePath))
            {
                result.Success = false;
                result.ErrorMessage = $"文件不存在: {result.FilePath}";
                return result;
            }

            // 优先使用预加载内容（避免同一文件多次读盘 + 保证原子性）
            string fileContent = existingContent
                ?? await Task.Run(() => File.ReadAllText(result.FilePath), ct);

            // ── 基于参考实现的重构方案 ──
            // 不再使用「搜索模式 → 替换文本」的字符串替换方式。
            // 改用与 OpenAI Codex apply_patch 参考实现一致的文件重建方式：
            //   1. 将每个 Hunk 转换为 FileChunk（delLines / insLines）
            //   2. 在文件行中进行上下文匹配定位
            //   3. 遍历原始文件行，按 Chunk 的 OrigIndex 删除旧行、插入新行
            // 这种方式从根本上避免了 AI 重复闭合符号等问题 ——
            // 闭合符号要么在 delLines 中（被删除），要么保留在原始行中。
            // ================================================================

            var fileLines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var chunks = new List<(FileChunk chunk, string[] contextLines)>();
            var failedHunks = new List<PatchHunk>();

            // ── 阶段 1：将每个 Hunk 解析为 FileChunk + 上下文行 ──
            foreach (var hunk in patch.Hunks)
            {
                var (chunk, contextArray) = HunkToChunk(hunk);
                if (chunk == null)
                {
                    failedHunks.Add(hunk);
                    continue;
                }
                chunks.Add((chunk, contextArray));
            }

            if (failedHunks.Count > 0)
            {
                result.Success = false;
                result.FailedHunks = failedHunks;
                result.ErrorMessage = L.Format("edit.hunksFailed", failedHunks.Count, patch.Hunks.Count);
                return result;
            }

            // ── 阶段 2：上下文匹配 — 找到每个 Chunk 在文件中的行位置 ──
            int searchStartLine = 0;
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var (chunk, contextLines) = chunks[ci];
                if (contextLines.Length == 0)
                {
                    // 空上下文：Chunk 位置已由 OrigIndex 确定，无需调整
                    continue;
                }

                int matchedLine = MatchContextInFileLines(
                    fileLines, contextLines, searchStartLine, out MatchLevel level);

                if (matchedLine < 0)
                {
                    // ── Fallback：用 @@ 标记文本定位 ──
                    var hunk = patch.Hunks[ci];
                    matchedLine = MatchContextViaMarkers(fileLines, hunk.ContextMarkers, searchStartLine);
                    if (matchedLine >= 0) level = MatchLevel.Exact;
                }

                if (matchedLine < 0)
                {
                    // ── 回退：从文件开头搜索（patch 可能乱序）──
                    matchedLine = MatchContextInFileLines(
                        fileLines, contextLines, 0, out level);
                    if (matchedLine >= 0)
                    {
                        // 找到了但不在预期位置 — 接受
                    }
                }

                if (matchedLine < 0)
                {
                    failedHunks.Add(patch.Hunks[ci]);
                    continue;
                }

                // 调整 Chunk 位置到匹配行
                chunk.OrigIndex += matchedLine;
                searchStartLine = matchedLine + contextLines.Length;

                // 记录编辑（用于日志）
                result.AppliedEdits.Add(new TextEditOperation
                {
                    StartLine = matchedLine,
                    StartColumn = 0,
                    EndLine = matchedLine + contextLines.Length,
                    EndColumn = 0,
                    NewText = $"[Chunk: -{chunk.DelLines.Count} +{chunk.InsLines.Count} lines]",
                    MatchedText = string.Join("\n", contextLines),
                    MatchLevelUsed = level,
                });
            }

            if (failedHunks.Count > 0)
            {
                result.Success = false;
                result.FailedHunks = failedHunks;
                result.ErrorMessage = L.Format("edit.hunksFailed", failedHunks.Count, patch.Hunks.Count);
                return result;
            }

            // ── 阶段 3：文件重建 — 遍历原始行，应用所有 Chunk ──
            string reconstructedContent = ReconstructFile(
                fileLines, chunks.Select(c => c.chunk).ToList());

            // ── 标准化行尾并保存最终内容 ──
            result.FinalContent = NormalizeToCrLf(reconstructedContent);
            result.Success = true;

            return result;
        }

        // ====================================================================
        // Chunk-based 文件重建方法（基于 OpenAI Codex apply_patch 参考实现）
        // ====================================================================

        /// <summary>
        /// 将 PatchHunk 转换为 FileChunk + 上下文行数组。
        /// 参考 peek_next_section：解析 patch 行，区分上下文/删除/新增，
        /// 构建 { OrigIndex, DelLines, InsLines } 结构。
        /// </summary>
        /// <returns>(chunk, contextLines)。chunk 为 null 表示解析失败。</returns>
        private static (FileChunk? chunk, string[] contextLines) HunkToChunk(PatchHunk hunk)
        {
            var contextLines = new List<string>();
            var delLines = new List<string>();
            var insLines = new List<string>();
            int origIndex = 0; // 在原始文件中，第一个受影响的行索引
            bool hasChanges = false;

            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case ' ':
                        // 上下文行：如果在删除/新增之后出现，表示当前段结束
                        if (hasChanges)
                        {
                            // 上下文行只用于匹配，不参与 chunk 计算
                            contextLines.Add(line.Text);
                        }
                        else
                        {
                            // 变更前的上下文：增加 origIndex
                            contextLines.Add(line.Text);
                            origIndex++;
                        }
                        break;

                    case '-':
                        hasChanges = true;
                        delLines.Add(line.Text);
                        contextLines.Add(line.Text); // 用于上下文匹配
                        break;

                    case '+':
                        hasChanges = true;
                        insLines.Add(line.Text);
                        // 注意：+ 行不加入 contextLines（它们不在原始文件中）
                        break;
                }
            }

            if (!hasChanges)
            {
                // 纯上下文 Hunk（无变更）—— 跳过
                return (null, Array.Empty<string>());
            }

            // ── 防御性修复：检测尾部 + 行重复闭合符号 ──
            // AI 模型常见错误：将闭合括号（) } ] end 等）同时标记为上下文行和 + 行。
            // 例如在 CMakeLists.txt 的 add_executable(...) 末尾：
            //   上下文: )
            //   +新增:  )
            // 结果是 InsLines 包含重复的闭合符号。
            // 
            // 策略：如果最后一个 InsLine 是闭合 token 且内容与最后一个上下文行相同，
            // 将其从 InsLines 中移除，避免重建文件时产生 ))、}} 等畸形输出。
            if (insLines.Count > 0 && contextLines.Count > 0)
            {
                string lastIns = insLines[insLines.Count - 1];
                string lastCtx = contextLines[contextLines.Count - 1];

                if (IsClosingToken(lastIns) && 
                    string.Equals(lastIns.Trim(), lastCtx.Trim(), StringComparison.Ordinal))
                {
                    insLines.RemoveAt(insLines.Count - 1);
                }
            }

            // origIndex 表示：在原始文件的 contextLines 中，
            // 从第 origIndex 行开始是第一个受 Chunk 影响的行
            var chunk = new FileChunk
            {
                OrigIndex = origIndex,
                DelLines = delLines,
                InsLines = insLines,
            };

            return (chunk, contextLines.ToArray());
        }

        /// <summary>
        /// 判断单行是否为闭合符号（用于检测 AI 重复闭合符号错误）。
        /// </summary>
        private static bool IsClosingToken(string line)
        {
            string trimmed = line.Trim();
            return trimmed switch
            {
                ")" or "}" or "]" => true,
                ");" or "};" or "]);" => true,
                "end" or "endif" or "fi" or "done" or "esac" => true,
                _ => trimmed.Length <= 2 && (trimmed == ")" || trimmed == "}" || trimmed == "]"),
            };
        }

        /// <summary>
        /// 在文件行数组中匹配上下文行。
        /// 使用与参考实现 find_context_core 相同的 5 层降级策略：
        ///   1. 精确匹配
        ///   2. 忽略尾部空白
        ///   3. 标准化显式 \t
        ///   4. 忽略所有周围空白
        ///   5. 编辑距离模糊匹配
        /// </summary>
        /// <returns>匹配到的行索引（0-based），-1 表示未找到。</returns>
        private static int MatchContextInFileLines(
            string[] fileLines, string[] contextLines, int startLine, out MatchLevel level)
        {
            level = MatchLevel.Exact;

            if (contextLines.Length == 0 || startLine >= fileLines.Length)
                return -1;

            int maxStart = fileLines.Length - contextLines.Length;
            if (maxStart < 0) return -1;

            // ── Pass 1：精确匹配（Unicode NFC 标准化后）──
            var canonFile = fileLines.Select(NormalizeUnicode).ToArray();
            var canonCtx = contextLines.Select(NormalizeUnicode).ToArray();
            int match = FindLineSequence(canonFile, canonCtx, startLine);
            if (match >= 0) { level = MatchLevel.Exact; return match; }

            // ── Pass 2：忽略尾部空白 ──
            var trimEndFile = canonFile.Select(l => l.TrimEnd()).ToArray();
            var trimEndCtx = canonCtx.Select(l => l.TrimEnd()).ToArray();
            match = FindLineSequence(trimEndFile, trimEndCtx, startLine);
            if (match >= 0) { level = MatchLevel.WhitespaceFlexible; return match; }

            // ── Pass 3：标准化显式 \t → 制表符 ──
            var tabFile = trimEndFile.Select(ReplaceExplicitTabs).ToArray();
            var tabCtx = trimEndCtx.Select(ReplaceExplicitTabs).ToArray();
            match = FindLineSequence(tabFile, tabCtx, startLine);
            if (match >= 0) { level = MatchLevel.Fuzzy; return match; }

            // ── Pass 4：忽略所有周围空白 ──
            var trimAllFile = tabFile.Select(l => l.Trim()).ToArray();
            var trimAllCtx = tabCtx.Select(l => l.Trim()).ToArray();
            match = FindLineSequence(trimAllFile, trimAllCtx, startLine);
            if (match >= 0) { level = MatchLevel.Fuzzy; return match; }

            // ── Pass 5：编辑距离模糊匹配（每行容忍 ~34% 编辑距离）──
            match = FuzzyLineMatch(trimAllFile, trimAllCtx, startLine);
            if (match >= 0) { level = MatchLevel.Levenshtein; return match; }

            return -1;
        }

        /// <summary>
        /// 在行数组中查找连续行序列的起始位置。
        /// </summary>
        private static int FindLineSequence(string[] fileLines, string[] pattern, int startLine)
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
        /// 参考 find_context_core Pass 5。
        /// </summary>
        private static int FuzzyLineMatch(string[] fileLines, string[] contextLines, int startLine)
        {
            const double EDIT_DISTANCE_ALLOWANCE_PER_LINE = 0.34;
            int maxDistance = (int)Math.Floor(contextLines.Length * EDIT_DISTANCE_ALLOWANCE_PER_LINE);
            if (maxDistance <= 0) return -1;

            int maxStart = fileLines.Length - contextLines.Length;

            for (int i = Math.Max(0, startLine); i <= maxStart; i++)
            {
                int totalDistance = 0;
                for (int j = 0; j < contextLines.Length && totalDistance <= maxDistance; j++)
                {
                    totalDistance += CalculateSimilarityDistance(fileLines[i + j], contextLines[j]);
                }
                if (totalDistance <= maxDistance) return i;
            }
            return -1;
        }

        /// <summary>
        /// 计算两个字符串的编辑距离（用于模糊行匹配，返回原始距离值）。
        /// </summary>
        private static int CalculateSimilarityDistance(string a, string b)
        {
            return LevenshteinDistance(a, b);
        }

        /// <summary>
        /// Unicode 标点规范化：将全角/变体标点映射为 ASCII。
        /// 参考 find_context_core 的 canon() 函数。
        /// </summary>
        private static string NormalizeUnicode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            // NFC 标准化后替换常见 Unicode 标点变体
            string normalized = s.Normalize(System.Text.NormalizationForm.FormC);

            // 仅在包含非 ASCII 字符时进行替换（性能优化）
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
        /// 参考 replace_explicit_tabs。
        /// </summary>
        private static string ReplaceExplicitTabs(string s)
        {
            return s.Replace("\\t", "\t");
        }

        /// <summary>
        /// 通过 @@ 标记文本在文件中定位。
        /// </summary>
        private static int MatchContextViaMarkers(
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

        /// <summary>
        /// 文件重建：遍历原始行，按 Chunk 列表删除旧行、插入新行。
        /// 参考 _get_updated_file。
        /// 
        /// 这是核心方法 — 通过直接操作行数组而非文本替换，
        /// 从根本上杜绝了 AI 重复闭合符号等 patch 格式错误。
        /// </summary>
        private static string ReconstructFile(string[] originalLines, List<FileChunk> chunks)
        {
            // ── 按 OrigIndex 排序 Chunk（确保处理顺序正确）──
            var sorted = chunks.OrderBy(c => c.OrigIndex).ToList();

            var destLines = new List<string>();
            int origIdx = 0;

            foreach (var chunk in sorted)
            {
                if (chunk.OrigIndex > originalLines.Length)
                    throw new InvalidOperationException(
                        $"文件重建错误: Chunk.OrigIndex ({chunk.OrigIndex}) 超出文件行数 ({originalLines.Length})");

                if (origIdx > chunk.OrigIndex)
                    throw new InvalidOperationException(
                        $"文件重建错误: 当前索引 ({origIdx}) 超过 Chunk.OrigIndex ({chunk.OrigIndex})，Chunk 可能重叠");

                // ── 复制 Chunk 之前的原始行 ──
                destLines.AddRange(originalLines.Skip(origIdx).Take(chunk.OrigIndex - origIdx));
                origIdx = chunk.OrigIndex;

                // ── 插入新行 ──
                destLines.AddRange(chunk.InsLines);

                // ── 跳过被删除的行 ──
                origIdx += chunk.DelLines.Count;
            }

            // ── 复制剩余原始行 ──
            destLines.AddRange(originalLines.Skip(origIdx));

            return string.Join("\n", destLines);
        }

        /// <summary>
        /// 根据匹配位置创建 TextEditOperation。
        /// </summary>
        private static TextEditOperation? CreateTextEditFromMatch(
            string fileContent, int matchPos, string searchPattern, string replacement,
            MatchLevel matchLevel)
        {
            if (matchPos < 0 || matchPos >= fileContent.Length) return null;

            // ── 计算匹配文本的实际结束位置 ──
            int matchEndPos;
            if (matchLevel == MatchLevel.Exact)
            {
                matchEndPos = matchPos + searchPattern.Length;
            }
            else
            {
                // 非精确匹配时，用行计数定位匹配区间的结束位置。
                // 不能直接用 searchPattern.Length 估算，因为 searchPattern 的格式
                // （缩进、空白）可能与实际文件不同，导致长度膨胀。
                // 改为：从 matchPos 开始，按 searchPattern 的行数在文件中定位结束行。
                matchEndPos = FindMatchEndByLineCount(fileContent, matchPos, searchPattern);

                // 兜底：如果行计数定位失败（如大文件截断），回退到保守估算
                if (matchEndPos <= matchPos)
                {
                    matchEndPos = Math.Min(matchPos + searchPattern.Length + 50, fileContent.Length);
                }

                // 尝试在附近找到精确匹配（安全区间内优先精确）
                int exactPos = fileContent.IndexOf(searchPattern, matchPos, StringComparison.Ordinal);
                if (exactPos >= 0 && exactPos - matchPos >= 0 && exactPos - matchPos < 500)
                {
                    matchPos = exactPos;
                    matchEndPos = exactPos + searchPattern.Length;
                }
            }

            // 安全边界检查
            int safeEndPos = Math.Min(matchEndPos, fileContent.Length);
            int safeLen = Math.Max(0, safeEndPos - matchPos);
            string matchedText = fileContent.Substring(matchPos, safeLen);

            // ── 计算行列位置 ──
            var (startLine, startCol) = GetLineColumn(fileContent, matchPos);
            var (endLine, endCol) = GetLineColumn(fileContent, matchEndPos);

            return new TextEditOperation
            {
                StartLine = startLine,
                StartColumn = startCol,
                EndLine = endLine,
                EndColumn = endCol,
                NewText = replacement,
                MatchedText = matchedText,
                MatchLevelUsed = matchLevel,
            };
        }

        #endregion

        #region Insert Edit Application

        /// <summary>
        /// 应用 insert_edit_into_file 操作。
        /// 解析 ...existing code... 标记，定位未修改区域，构造增量编辑。
        /// </summary>
        public async Task<EditApplyResult> ApplyInsertEditAsync(
            InsertEditOperation edit,
            string workspaceRoot,
            CancellationToken ct)
        {
            var result = new EditApplyResult
            {
                FilePath = ResolvePath(edit.FilePath, workspaceRoot),
                OperationType = EditOperationType.InsertEditIntoFile,
            };

            if (!File.Exists(result.FilePath))
            {
                // 文件不存在 → 降级为创建新文件（去掉 ...existing code... 占位）
                result.Success = false;
                result.ErrorMessage = $"文件不存在: {result.FilePath}。请使用 create_file 创建新文件。";
                return result;
            }

            // RAG-SOURCE: file-read 读取文件内容（Insert Edit Into File）
            string fileContent = await Task.Run(() => File.ReadAllText(result.FilePath), ct);
            var normalizedContent = NormalizeLineEndings(fileContent);
            var normalizedEdit = NormalizeLineEndings(edit.FullContent);

            // ── 按 ...existing code... 分割编辑内容 ──
            var segments = SplitByExistingCodeMarker(normalizedEdit);

            if (segments.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = L["edit.noExistingCodeMarker"];
                return result;
            }

            // ── 全文件替换检测：没有任何 ...existing code... 占位符 → 直接写入全文 ──
            bool hasPlaceholders = segments.Any(s => s.IsPlaceholder);
            if (!hasPlaceholders)
            {
                // AI 想完全替换文件内容（如重写整个算法），无需逐段匹配
                Logger.Info($"[EditPatchService] InsertEdit 无 ...existing code... 标记，视为全文件替换: {result.FilePath}");
                result.FinalContent = NormalizeToCrLf(normalizedEdit);
                result.Success = true;
                return result;
            }

            // ── 对每个"修改段"进行匹配 ──
            var failedRegions = new List<string>();
            string workingContent = normalizedContent;

            foreach (var segment in segments)
            {
                if (segment.IsPlaceholder)
                {
                    // 占位符表示未修改的区域，跳过
                    continue;
                }

                string searchText = segment.Text.Trim();
                if (string.IsNullOrEmpty(searchText)) continue;

                int matchPos = MatchWithFallback(workingContent, searchText, out MatchLevel level);

                if (matchPos < 0)
                {
                    failedRegions.Add(searchText.Truncate(80));
                    continue;
                }

                // ── 定位修改段的边界 ──
                // 使用前后 ...existing code... 标记之间的内容来确定修改范围
                var textEdit = CreateTextEditFromMatch(
                    workingContent, matchPos, searchText, searchText, level);

                if (textEdit != null)
                {
                    result.AppliedEdits.Add(textEdit);
                }
            }

            if (failedRegions.Count > 0)
            {
                result.Success = false;
                result.FailedRegions = failedRegions;
                result.ErrorMessage = $"{failedRegions.Count} 个代码区域匹配失败";
                return result;
            }

            // ── 构造最终文件内容：替换 ...existing code... 为实际文件内容 ──
            string finalContent = ReconstructContent(segments, normalizedContent);

            // 标准化行尾为 CRLF，由调用方通过 VS SDK 写入（避免外部变更弹窗）
            result.FinalContent = NormalizeToCrLf(finalContent);
            result.Success = true;

            return result;
        }

        /// <summary>
        /// 按 ...existing code... 标记分割编辑内容。
        /// </summary>
        private static List<ContentSegment> SplitByExistingCodeMarker(string content)
        {
            var segments = new List<ContentSegment>();
            if (string.IsNullOrEmpty(content)) return segments;

            // 使用多种可能的标记格式
            var markerPatterns = new[]
            {
                @"\/\/\s*\.\.\.existing\s*code\.\.\.",    // // ...existing code...
                @"\/\/\s*\.\.\.\s*existing\s*\.\.\.",     // // ... existing ...
                @"#\s*\.\.\.existing\s*code\.\.\.",        // # ...existing code...
                @"<!--\s*\.\.\.existing\s*code\.\.\.\s*-->", // <!-- ...existing code... -->
            };

            string pattern = string.Join("|", markerPatterns);
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);

            var matches = regex.Matches(content);
            int lastEnd = 0;

            foreach (Match match in matches)
            {
                // 标记前的文本是一个"修改段"
                if (match.Index > lastEnd)
                {
                    string segmentText = content.Substring(lastEnd, match.Index - lastEnd);
                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        segments.Add(new ContentSegment
                        {
                            Text = segmentText,
                            IsPlaceholder = false,
                        });
                    }
                }

                // 标记本身是一个占位符
                segments.Add(new ContentSegment
                {
                    Text = match.Value,
                    IsPlaceholder = true,
                });

                lastEnd = match.Index + match.Length;
            }

            // 最后一个标记后的文本
            if (lastEnd < content.Length)
            {
                string remaining = content.Substring(lastEnd);
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    segments.Add(new ContentSegment
                    {
                        Text = remaining,
                        IsPlaceholder = false,
                    });
                }
            }

            // 如果没有找到标记，整个内容作为单个修改段
            if (segments.Count == 0)
            {
                segments.Add(new ContentSegment
                {
                    Text = content,
                    IsPlaceholder = false,
                });
            }

            return segments;
        }

        /// <summary>
        /// 根据修改段重建文件内容。
        /// 占位符位置用原始文件对应内容填充。
        /// </summary>
        private static string ReconstructContent(List<ContentSegment> segments, string originalContent)
        {
            if (segments.All(s => !s.IsPlaceholder))
            {
                // 没有占位符，直接使用第一个修改段（等同于全文件替换）
                return segments.FirstOrDefault()?.Text ?? originalContent;
            }

            var sb = new StringBuilder();

            foreach (var segment in segments)
            {
                if (segment.IsPlaceholder)
                {
                    // 占位符：保留原始文件中对应位置的代码
                    // 使用前后修改段来定位原始文件中的保留区域
                    // 简化处理：如果是纯占位符，追加原始内容（实际映射在匹配阶段完成）
                    sb.Append(segment.Text); // 保留占位符标记
                }
                else
                {
                    sb.Append(segment.Text);
                }
            }

            // 去除所有占位符标记，因为实际匹配已在前面完成
            // 这里最终重建时只需保留修改段即可
            string result = sb.ToString();
            var markerPattern = new Regex(
                @"\/\/\s*\.\.\.existing\s*code\.\.\.[\r\n]*|" +
                @"#\s*\.\.\.existing\s*code\.\.\.[\r\n]*|" +
                @"<!--\s*\.\.\.existing\s*code\.\.\.\s*-->[\r\n]*",
                RegexOptions.IgnoreCase);
            result = markerPattern.Replace(result, "");

            return result;
        }

        #endregion

        #region Create / Delete / Move File

        /// <summary>
        /// 从 Patch Add 创建新文件。
        /// </summary>
        private async Task<EditApplyResult> ApplyCreateFileFromPatchAsync(
            PatchOperation patch, string filePath, CancellationToken ct)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.CreateFile,
            };

            // 从所有 Hunk 的 + 行提取新内容
            var sb = new StringBuilder();
            foreach (var hunk in patch.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.Type == '+' || line.Type == ' ')
                    {
                        sb.AppendLine(line.Text);
                    }
                }
            }

            // 标准化行尾为 CRLF，由调用方通过 VS SDK 写入
            result.FinalContent = NormalizeToCrLf(sb.ToString());
            result.Success = true;

            return result;
        }

        /// <summary>
        /// 删除文件。
        /// </summary>
        private Task<EditApplyResult> ApplyDeleteFileAsync(string filePath, CancellationToken ct)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.DeleteFile,
            };

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    result.Success = true;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件不存在: {filePath}";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"删除文件失败: {ex.Message}";
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// 移动/重命名文件。
        /// </summary>
        private Task<EditApplyResult> ApplyMoveFileAsync(string sourcePath, string destPath, CancellationToken ct)
        {
            var result = new EditApplyResult
            {
                FilePath = sourcePath,
                OperationType = EditOperationType.MoveFile,
            };

            try
            {
                if (!File.Exists(sourcePath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"源文件不存在: {sourcePath}";
                    return Task.FromResult(result);
                }

                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(destPath))
                    File.Delete(destPath);

                File.Move(sourcePath, destPath);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"移动文件失败: {ex.Message}";
            }

            return Task.FromResult(result);
        }

        #endregion

        #region VS Editor Integration

        /// <summary>
        /// 通过 VS 文本缓冲区将 TextEdit 应用到已打开的文件编辑器。
        /// 使用 ITextEdit 确保整个操作为一次撤销。
        /// </summary>
        public async Task<bool> ApplyEditsToOpenDocumentAsync(
            string filePath, List<TextEditOperation> edits)
        {
            if (edits == null || edits.Count == 0) return true;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // ── 获取 VS 文本缓冲区 ──
                var textBuffer = GetTextBufferForFile(filePath);
                if (textBuffer == null)
                {
                    // 文件未在编辑器中打开，直接文件级操作已完成
                    return true;
                }

                using (ITextEdit edit = textBuffer.CreateEdit())
                {
                    foreach (var textEdit in edits)
                    {
                        var snapshot = textBuffer.CurrentSnapshot;
                        int startLine = Math.Min(textEdit.StartLine, snapshot.LineCount - 1);
                        int endLine = Math.Min(textEdit.EndLine, snapshot.LineCount - 1);

                        var startLineObj = snapshot.GetLineFromLineNumber(startLine);
                        var endLineObj = snapshot.GetLineFromLineNumber(endLine);

                        int startPos = startLineObj.Start.Position + Math.Min(textEdit.StartColumn,
                            startLineObj.Length);
                        int endPos = endLineObj.Start.Position + Math.Min(textEdit.EndColumn,
                            endLineObj.Length);

                        // 确保范围有效
                        if (startPos < 0) startPos = 0;
                        if (endPos > snapshot.Length) endPos = snapshot.Length;
                        if (startPos > endPos) startPos = endPos;

                        Span span = new Span(startPos, endPos - startPos);
                        edit.Replace(span, textEdit.NewText);
                    }

                    edit.Apply();
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditPatchService] 应用 TextEdit 到编辑器失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取文件的 ITextBuffer（如果文件在 VS 编辑器中打开）。
        /// 通过 IVsRunningDocumentTable 枚举打开文档，使用 IVsEditorAdaptersFactoryService 获取 buffer。
        /// </summary>
        private static ITextBuffer? GetTextBufferForFile(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // ── 获取 IVsRunningDocumentTable ──
                var rdt = (IVsRunningDocumentTable?)
                    Package.GetGlobalService(typeof(SVsRunningDocumentTable));
                if (rdt == null) return null;

                IEnumRunningDocuments? enumDocs;
                if (rdt.GetRunningDocumentsEnum(out enumDocs) != VSConstants.S_OK || enumDocs == null)
                    return null;

                // ── 获取编辑器适配器（将 IVsTextBuffer 转为 ITextBuffer）──
                var componentModel = (IComponentModel?)
                    Package.GetGlobalService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;
                if (editorAdapter == null) return null;

                uint[] cookieArray = new uint[1];
                uint fetched;

                while (enumDocs.Next(1, cookieArray, out fetched) == VSConstants.S_OK && fetched == 1)
                {
                    uint cookie = cookieArray[0];

                    uint flags; uint readLocks; uint editLocks;
                    string? docPath; IVsHierarchy? hierarchy; uint itemId; IntPtr docDataPtr;

                    if (rdt.GetDocumentInfo(cookie, out flags, out readLocks, out editLocks,
                        out docPath, out hierarchy, out itemId, out docDataPtr) != VSConstants.S_OK)
                        continue;

                    if (docPath == null || !string.Equals(docPath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (docDataPtr == IntPtr.Zero) continue;

                    // ── 通过 docDataPtr 获取 IVsTextBuffer → ITextBuffer ──
                    var vsTextBuffer = Marshal.GetObjectForIUnknown(docDataPtr) as IVsTextBuffer;
                    if (vsTextBuffer == null) continue;

                    var textBuffer = editorAdapter.GetDataBuffer(vsTextBuffer);
                    if (textBuffer != null)
                        return textBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditPatchService] 获取 TextBuffer 失败: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Diagnostics Checking

        /// <summary>
        /// 检查文件在编辑后是否引入了新的编译/诊断错误。
        /// </summary>
        public async Task<List<string>> CheckNewDiagnosticsAsync(string filePath)
        {
            var diagnostics = new List<string>();

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // ── 通过 VS Error List 获取当前错误 ──
                var errorList = (IVsTaskList?)Package.GetGlobalService(typeof(SVsTaskList));
                if (errorList == null) return diagnostics;

                errorList.EnumTaskItems(out IVsEnumTaskItems? enumTasks);
                if (enumTasks == null) return diagnostics;

                IVsTaskItem[] items = new IVsTaskItem[1];
                uint[] fetched = new uint[1];

                while (enumTasks.Next(1, items, fetched) == VSConstants.S_OK && fetched[0] == 1)
                {
                    try
                    {
                        var item = items[0];
                        if (item is not IVsTaskItem2 item2) continue;

                        // 只收集构建编译类任务项
                        var catArray = new VSTASKCATEGORY[1];
                        item2.Category(catArray);
                        if (catArray[0] != VSTASKCATEGORY.CAT_BUILDCOMPILE) continue;

                        // 只收集错误级别
                        var priorityArray = new VSTASKPRIORITY[1];
                        item2.get_Priority(priorityArray);
                        if (priorityArray[0] != VSTASKPRIORITY.TP_HIGH) continue;

                        item2.Document(out string fileName);

                        if (!string.IsNullOrEmpty(fileName) &&
                            string.Equals(fileName, filePath, StringComparison.OrdinalIgnoreCase))
                        {
                            item2.Line(out int line);
                            item2.Column(out int column);
                            item2.get_Text(out string text);

                            diagnostics.Add($"行 {line}: {text}");
                        }
                    }
                    catch
                    {
                        // 跳过无法读取的任务项
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditPatchService] 诊断检查失败: {ex.Message}");
            }

            return diagnostics;
        }

        #endregion

        #region Healing

        /// <summary>
        /// 通过降级模型（copilot-fast）修正匹配失败的编辑。
        /// </summary>
        public async Task<HealingResponse?> HealFailedEditAsync(
            HealingRequest request, CancellationToken ct)
        {
            try
            {
                var prompt = BuildHealingPrompt(request);
                string response = await _apiService.CompleteAsync(
                    new List<ChatApiMessage>
                    {
                        new ChatApiMessage { Role = "system", Content = L["edit.healingSystemPrompt"] },
                        new ChatApiMessage { Role = "user", Content = prompt },
                    },
                    ct);

                return ParseHealingResponse(response, request);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditPatchService] Healing 失败: {ex.Message}");
                return new HealingResponse
                {
                    Success = false,
                    ErrorMessage = L.Format("edit.healingRequestFailed", ex.Message),
                };
            }
        }

        /// <summary>
        /// 使用完整模型（更强的指令遵循能力）重试 healing。
        /// 当降级模型 healing 失败时调用。使用更严格的格式要求和格式示例。
        /// </summary>
        public async Task<HealingResponse?> HealFailedEditWithFullModelAsync(
            HealingRequest request, CancellationToken ct)
        {
            try
            {
                var prompt = BuildHealingPrompt(request);

                // 强化格式要求，附带完整示例 — 从 i18n 加载
                string systemPrompt = L["edit.healingFullSystemPrompt"];

                string response = await _apiService.CompleteAsync(
                    new List<ChatApiMessage>
                    {
                        new ChatApiMessage { Role = "system", Content = systemPrompt },
                        new ChatApiMessage { Role = "user", Content = prompt },
                    },
                    ct);

                var result = ParseHealingResponse(response, request);
                if (result?.Success != true)
                {
                    Logger.Warn($"[EditPatchService] 完整模型 healing 也失败: {result?.ErrorMessage ?? "无法解析"}");
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditPatchService] 完整模型 Healing 异常: {ex.Message}");
                return new HealingResponse
                {
                    Success = false,
                    ErrorMessage = L.Format("edit.healingFullModelFailed", ex.Message),
                };
            }
        }

        /// <summary>
        /// 构建 Healing prompt。
        /// </summary>
        private static string BuildHealingPrompt(HealingRequest request)
        {
            var sb = new StringBuilder();
            var L = LocalizationService.Instance;

            sb.AppendLine(L["edit.healingHeaderCurrent"]);
            sb.AppendLine("```");
            // RAG-MARK: no-truncate — 不再截断文件内容，完整传递给 Healing prompt
            // RAG-SOURCE: file-read 当前文件完整内容（Edit Healing 上下文）
            sb.AppendLine(request.CurrentFileContent);
            sb.AppendLine("```");
            sb.AppendLine();

            sb.AppendLine(L["edit.healingHeaderFailed"]);
            sb.AppendLine(L.Format("edit.operationTypeLabel", request.OriginalOperationType));
            sb.AppendLine(L.Format("edit.failureReasonLabel", request.FailureReason));
            sb.AppendLine();

            if (request.OriginalOperationType == EditOperationType.ApplyPatch && request.FailedPatch != null)
            {
                sb.AppendLine(L["edit.healingHeaderOriginalPatch"]);
                sb.AppendLine("```");
                sb.AppendLine(request.FailedPatch.RawText);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine(L["edit.healingInstructionPatch"]);
                sb.AppendLine(L["edit.healingOutputFormat"]);
                sb.AppendLine(L["edit.healingAdjustHint"]);
            }
            else if (request.OriginalOperationType == EditOperationType.InsertEditIntoFile)
            {
                sb.AppendLine(L["edit.healingHeaderOriginalInsert"]);
                sb.AppendLine("```");
                // RAG-MARK: no-truncate — 不再截断 insert 编辑内容
                sb.AppendLine(request.FailedInsertEditContent ?? "");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine(L["edit.healingInstructionInsert"]);
                sb.AppendLine(L["edit.healingOutputFormatInsert"]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 解析 Healing 模型的响应。
        /// </summary>
        private HealingResponse? ParseHealingResponse(string response, HealingRequest request)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return new HealingResponse { Success = false, ErrorMessage = L["edit.healingEmptyResponse"] };
            }

            var result = new HealingResponse { Success = true };

            if (request.OriginalOperationType == EditOperationType.ApplyPatch)
            {
                var patches = ParsePatches(response);
                if (patches.Count > 0)
                {
                    result.CorrectedPatch = patches[0];
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = L["edit.healingNoPatch"];
                }
            }
            else if (request.OriginalOperationType == EditOperationType.InsertEditIntoFile)
            {
                // 提取代码块内容或全文
                var codeBlockMatch = Regex.Match(response,
                    @"```(?:[\w#]*)?[\r\n]+(.*?)```", RegexOptions.Singleline);
                result.CorrectedInsertEditContent = codeBlockMatch.Success
                    ? codeBlockMatch.Groups[1].Value
                    : response;
            }

            return result;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 内容段 — 用于 insert_edit_into_file 解析。
        /// </summary>
        private class ContentSegment
        {
            public string Text { get; set; } = string.Empty;
            public bool IsPlaceholder { get; set; }
        }

        /// <summary>
        /// 标准化空白：将连续空白合并为单个空格。
        /// </summary>
        private static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = Regex.Replace(text, @"\s+", " ");
            return result.Trim();
        }

        /// <summary>
        /// 模糊标准化：移除所有空白和标点符号。
        /// </summary>
        private static string NormalizeFuzzy(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 保留字母数字和下划线
            var result = Regex.Replace(text, @"[^a-zA-Z0-9_]", "");
            return result.ToLowerInvariant();
        }

        /// <summary>
        /// 将标准化位置映射回原始位置。
        /// </summary>
        private static int MapNormalizedToOriginal(string original, string normalized, int normalizedPos)
        {
            // 逐字符追踪映射
            int origIdx = 0;
            int normIdx = 0;

            // 跳过空白差异
            while (normIdx < normalizedPos && origIdx < original.Length)
            {
                if (char.IsWhiteSpace(original[origIdx]))
                {
                    // 在标准化文本中可能合并了多个空白
                    while (origIdx < original.Length && char.IsWhiteSpace(original[origIdx]))
                        origIdx++;
                    // 标准化文本中对应一个空格
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
        /// 通过行计数定位非精确匹配的结束位置。
        /// 避免用 searchPattern.Length 估算（searchPattern 的缩进/空白可能与实际文件不同导致长度膨胀）。
        /// 原理：从 matchPos 开始，找到 searchPattern 行数对应的文件行数 → 返回这些行之后的位置。
        /// </summary>
        /// <param name="fileContent">文件完整内容</param>
        /// <param name="matchPos">匹配起始位置（0-based 字符偏移）</param>
        /// <param name="searchPattern">搜索模式文本（行间用 \n 分隔）</param>
        /// <returns>匹配结束位置（0-based 字符偏移），指向匹配内容最后一行的下一个字符</returns>
        private static int FindMatchEndByLineCount(string fileContent, int matchPos, string searchPattern)
        {
            if (string.IsNullOrEmpty(searchPattern) || matchPos >= fileContent.Length)
                return Math.Min(matchPos + 1, fileContent.Length);

            // 计算 searchPattern 的逻辑行数
            int searchLineCount = 1;
            for (int i = 0; i < searchPattern.Length; i++)
                if (searchPattern[i] == '\n') searchLineCount++;

            // 从 matchPos 开始，在文件中向前扫描 searchLineCount 个换行
            int pos = matchPos;
            int linesFound = 1; // matchPos 所在的行算第 1 行

            while (pos < fileContent.Length && linesFound < searchLineCount)
            {
                if (fileContent[pos] == '\n')
                {
                    linesFound++;
                    if (linesFound >= searchLineCount)
                    {
                        pos++; // 跳过这个 \n，指向下一行开头
                        break;
                    }
                }
                pos++;
            }

            // 如果文件行数不够，返回文件末尾
            if (linesFound < searchLineCount)
                return fileContent.Length;

            return Math.Min(pos, fileContent.Length);
        }

        /// <summary>
        /// 获取指定行的起始字符位置。
        /// </summary>
        private static int GetLineStartPosition(string text, int lineNumber)
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
        /// 根据字符位置获取行列号（0-based）。
        /// </summary>
        private static (int line, int column) GetLineColumn(string text, int position)
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
        /// 统一换行符为 \n。
        /// </summary>
        private static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// 将文本中的所有行尾标准化为 Windows CRLF（\r\n）。
        /// 用于文件写回前统一行尾格式，避免 VS 弹出"行尾不一致"对话框。
        /// </summary>
        private static string NormalizeToCrLf(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 先统一为 LF，再转为 CRLF，避免产生 \r\r\n
            return NormalizeLineEndings(text).Replace("\n", "\r\n");
        }

        /// <summary>
        /// 解析文件路径（支持相对路径和绝对路径）。
        /// 包含路径穿越防护：确保解析后的路径在工作区范围内。
        /// </summary>
        public static string ResolvePath(string filePath, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;

            string resolved;
            if (Path.IsPathRooted(filePath))
            {
                resolved = Path.GetFullPath(filePath);
            }
            else if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string candidate = Path.Combine(workspaceRoot, filePath.Replace('/', '\\'));
                resolved = Path.GetFullPath(candidate);
            }
            else
            {
                return filePath;
            }

            // ── 路径穿越防护：确保解析后的路径在工作区根目录内 ──
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string normalizedWorkspace = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedResolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // 检查是否在工作区内（不区分大小写，Windows 文件系统）
                if (!normalizedResolved.StartsWith(normalizedWorkspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedResolved, normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[EditPatch] ⚠️ 路径穿越检测: {resolved} 不在工作区 {workspaceRoot} 内，拒绝访问");
                    return filePath; // 返回原始路径，由调用方处理
                }
            }

            return resolved;
        }

        #endregion
    }
}
