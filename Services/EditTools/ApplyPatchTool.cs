using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// apply_patch 工具实现 — OpenAI 风格的 *** Begin Patch / *** End Patch 格式。
    /// 支持 Add / Delete / Update / Move 四种操作。
    /// 
    /// 核心流程（参考 OpenAI Codex apply_patch.py）:
    /// 1. 解析 patch 文本 → PatchOperation（含 Hunks）
    /// 2. 每个 Hunk → FileChunk（delLines / insLines）
    /// 3. 行级上下文匹配定位
    /// 4. 文件重建（遍历原始行，应用 Chunks 删除/插入）
    /// 
    /// 参考: vscode-copilot-chat applyPatchTool.tsx + applyPatch/parser.ts
    /// </summary>
    public class ApplyPatchTool : AbstractEditTool
    {
        private readonly EditFileHealing? _healing;

        protected override string ToolName => "apply_patch";

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

        public ApplyPatchTool(DeepSeekApiService apiService, string workspaceRoot)
            : base(apiService, workspaceRoot)
        {
            _healing = new EditFileHealing(apiService);
        }

        /// <summary>
        /// 从 AI 输出中解析所有 Patch 操作（静态方法，无需实例）。
        /// </summary>
        public static List<PatchOperation> ParsePatches(string aiOutput)
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

            var moveMatch = PatchMoveHeaderRegex.Match(body);
            if (moveMatch.Success)
                patch.MoveToPath = moveMatch.Groups["path"].Value.Trim();

            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            PatchHunk? currentHunk = null;

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                // ── 跳过文件头行（已在上面解析）──
                if (line.StartsWith("*** Update") || line.StartsWith("*** Add") ||
                    line.StartsWith("*** Delete") || line.StartsWith("*** Move"))
                    continue;

                // ── *** End of File 标记：标记当前 Hunk 为文件末尾 ──
                if (line.TrimStart().StartsWith("*** End of File"))
                {
                    if (currentHunk != null)
                        currentHunk.IsEof = true;
                    continue;
                }

                // ── 空行且尚未开始 Hunk → 跳过 ──
                if (string.IsNullOrWhiteSpace(line) && currentHunk == null)
                    continue;

                // ── @@ 主标记：新 Hunk 开始 ──
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

                // ── 次级 @@ 标记 ──
                if (currentHunk != null && line.TrimStart().StartsWith("@@"))
                {
                    string marker = line.TrimStart().Substring(2).Trim();
                    currentHunk.ContextMarkers.Add(marker);
                    currentHunk.RawText += line + "\n";
                    continue;
                }

                // ── 标准 Patch 行（- / + / 空格前缀）──
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
                // ── FuzzMerge: 行缺少有效前缀（AI 偶尔漏写 -/+/空格）──
                // 参考: parser.ts peek_next_section 的 tolerate invalid lines 逻辑
                // 当作上下文行处理，确保 patch 不因格式小错而中断
                else if (currentHunk != null && !string.IsNullOrWhiteSpace(line))
                {
                    currentHunk.Lines.Add(new PatchLine
                    {
                        Type = ' ',
                        Text = line,
                    });
                    currentHunk.RawText += line + "\n";
                }
            }

            if (currentHunk != null)
                patch.Hunks.Add(currentHunk);

            return patch;
        }

        /// <summary>
        /// 执行 Patch 编辑（支持 Add / Update / Delete / Move）。
        /// 返回每个 Patch 操作的应用结果。
        /// </summary>
        public async Task<List<EditApplyResult>> ExecutePatchesAsync(
            List<PatchOperation> patches, CancellationToken ct)
        {
            var results = new List<EditApplyResult>();

            // ── 跟踪每个文件的内存内容（原子性）──
            var fileState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var patch in patches)
            {
                if (ct.IsCancellationRequested) break;

                string resolvedPath = EditPatchService.ResolvePath(patch.FilePath, WorkspaceRoot);

                // ── 获取当前内容 ──
                if (!fileState.ContainsKey(resolvedPath))
                {
                    string original = File.Exists(resolvedPath)
                        ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                        : string.Empty;
                    fileState[resolvedPath] = original;
                }

                string currentContent = fileState[resolvedPath];

                var result = await ApplySinglePatchAsync(patch, resolvedPath, currentContent, ct);

                // ── Healing ──
                if (!result.Success && result.FailedHunks != null && result.FailedHunks.Count > 0)
                {
                    var healingRequest = new HealingRequest
                    {
                        FilePath = resolvedPath,
                        CurrentFileContent = currentContent,
                        OriginalOperationType = EditOperationType.ApplyPatch,
                        FailedPatch = patch,
                        FailureReason = result.ErrorMessage ?? "未知原因",
                        FailedContextDetails = result.FailedHunks
                            .Select(h => $"Hunk ({string.Join(", ", h.ContextMarkers.Take(3))})")
                            .ToList(),
                    };

                    var healingResponse = await _healing!.HealAsync(healingRequest, ct);

                    if (healingResponse?.Success == true && healingResponse.CorrectedPatch != null)
                    {
                        Logger.Info($"[ApplyPatch] Healing 成功: {resolvedPath}");
                        result = await ApplySinglePatchAsync(
                            healingResponse.CorrectedPatch, resolvedPath, currentContent, ct);

                        // ── 兜底：Healing 后仍失败 → 尝试 create_file ──
                        if (!result.Success && !string.IsNullOrEmpty(result.FinalContent))
                        {
                            Logger.Warn($"[ApplyPatch] Healing 修正后仍失败，启用 create_file 兜底: {resolvedPath}");
                            try
                            {
                                await Task.Run(() => File.WriteAllText(resolvedPath,
                                    EditStringMatcher.NormalizeToCrLf(result.FinalContent!)), ct);
                                result.Success = true;
                                fileState[resolvedPath] = result.FinalContent!;
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[ApplyPatch] create_file 兜底失败: {ex.Message}");
                            }
                        }
                    }
                }

                if (result.Success && !string.IsNullOrEmpty(result.FinalContent))
                {
                    // ── 更新内存状态 + 写入磁盘 ──
                    fileState[resolvedPath] = result.FinalContent;
                    await Task.Run(() => File.WriteAllText(resolvedPath,
                        EditStringMatcher.NormalizeToCrLf(result.FinalContent)), ct);
                }

                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// 应用单个 Patch 到文件（基于 Chunk 重建）。
        /// </summary>
        private EditApplyResult ApplySinglePatch(
            PatchOperation patch, string filePath, string fileContent)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.ApplyPatch,
            };

            // ── Add / Delete / Move ──
            if (patch.Action == PatchFileAction.Add)
                return ApplyCreateFromPatch(patch, filePath);
            if (patch.Action == PatchFileAction.Delete)
                return ApplyDeleteFile(filePath);
            if (!string.IsNullOrEmpty(patch.MoveToPath))
                return ApplyMoveFile(filePath, patch.MoveToPath);

            if (!File.Exists(filePath))
            {
                result.Success = false;
                result.ErrorMessage = $"文件不存在: {filePath}";
                return result;
            }

            var fileLines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var chunks = new List<(FileChunk chunk, string[] contextLines)>();
            var failedHunks = new List<PatchHunk>();

            // ── 阶段 1：Hunk → FileChunk ──
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
                result.ErrorMessage = $"{failedHunks.Count}/{patch.Hunks.Count} 个 Hunk 解析失败";
                return result;
            }

            // ── 阶段 2：上下文匹配 ──
            int searchStartLine = 0;
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var (chunk, contextLines) = chunks[ci];
                var hunkMarkers = patch.Hunks[ci].ContextMarkers;

                // ── 处理空上下文（全部为 + 行，无实际上下文）──
                if (contextLines.Length == 0)
                {
                    // 回退到 @@ 标记定位
                    int markerMatch = EditStringMatcher.MatchContextViaMarkers(
                        fileLines, hunkMarkers, searchStartLine);
                    if (markerMatch >= 0)
                    {
                        // 插入到标记行之后（+1），因为标记行本身不应被覆盖
                        chunk.OrigIndex += markerMatch + 1;
                        searchStartLine = markerMatch + 1;
                    }
                    else
                    {
                        // 既无上下文也无标记 → 无法定位
                        failedHunks.Add(patch.Hunks[ci]);
                    }
                    continue;
                }

                int matchedLine = EditStringMatcher.MatchContextInFileLines(
                    fileLines, contextLines, searchStartLine, out MatchLevel level);

                // ── @@ 标记校验：上下文匹配成功后，验证标记是否在匹配位置附近 ──
                // 防止因上下文过于通用而在错误位置匹配（如多个 struct 有相同字段）
                if (matchedLine >= 0 && hunkMarkers.Count > 0)
                {
                    if (!IsMarkerNearPosition(fileLines, hunkMarkers, matchedLine, contextLines.Length))
                    {
                        // 标记不在附近 → 搜索下一个上下文匹配位置
                        int nextMatch = EditStringMatcher.MatchContextInFileLines(
                            fileLines, contextLines, matchedLine + 1, out level);
                        if (nextMatch >= 0)
                        {
                            matchedLine = nextMatch;
                        }
                        // 下一个匹配也不满足标记约束 → 保留原匹配（允许标记不精确）
                    }
                }

                if (matchedLine < 0)
                {
                    // Fallback: @@ 标记定位
                    matchedLine = EditStringMatcher.MatchContextViaMarkers(
                        fileLines, hunkMarkers, searchStartLine);
                }

                if (matchedLine < 0)
                {
                    // 从文件开头回退搜索
                    matchedLine = EditStringMatcher.MatchContextInFileLines(
                        fileLines, contextLines, 0, out level);

                    // @@ 标记校验（回退搜索也要验证）
                    if (matchedLine >= 0 && hunkMarkers.Count > 0)
                    {
                        if (!IsMarkerNearPosition(fileLines, hunkMarkers, matchedLine, contextLines.Length))
                        {
                            int nextMatch = EditStringMatcher.MatchContextInFileLines(
                                fileLines, contextLines, matchedLine + 1, out level);
                            if (nextMatch >= 0) matchedLine = nextMatch;
                        }
                    }
                }

                if (matchedLine < 0 && patch.Hunks[ci].IsEof)
                {
                    // ── EOF Hunk: 优先从文件末尾附近匹配 ──
                    // 参考: parser.ts find_context 的 eof 优先逻辑
                    int eofStart = Math.Max(0, fileLines.Length - contextLines.Length);
                    matchedLine = EditStringMatcher.MatchContextInFileLines(
                        fileLines, contextLines, eofStart, out level);
                }

                if (matchedLine < 0)
                {
                    failedHunks.Add(patch.Hunks[ci]);
                    continue;
                }

                chunk.OrigIndex += matchedLine;
                searchStartLine = matchedLine + contextLines.Length;

                // ── 缩进适配：AI 输出的缩进可能与目标文件不一致 ──
                // 参考: parser.ts transformIndentation + additionalIndentation 逻辑
                AdaptChunkIndentation(chunk, contextLines, fileLines, matchedLine);
            }

            if (failedHunks.Count > 0)
            {
                result.Success = false;
                result.FailedHunks = failedHunks;
                result.ErrorMessage = $"{failedHunks.Count}/{patch.Hunks.Count} 个 Hunk 匹配失败";
                return result;
            }

            // ── 填充 AppliedEdits（每个成功匹配的 chunk 计为 1 个编辑点）──
            // 之前 ApplySinglePatch 从未填充此列表，导致日志中始终显示 "0 个编辑点"
            foreach (var (chunk, contextLines) in chunks)
            {
                result.AppliedEdits.Add(new TextEditOperation
                {
                    StartLine = chunk.OrigIndex,
                    StartColumn = 0,
                    EndLine = chunk.OrigIndex + chunk.DelLines.Count,
                    EndColumn = 0,
                    NewText = string.Join("\n", chunk.InsLines),
                    MatchedText = contextLines.Length > 0
                        ? string.Join("\n", contextLines.Take(3)) + (contextLines.Length > 3 ? "..." : "")
                        : string.Empty,
                    MatchLevelUsed = MatchLevel.Exact,
                });
            }

            // ── 阶段 3：文件重建 ──
            string reconstructed = ReconstructFile(fileLines, chunks.Select(c => c.chunk).ToList());
            result.FinalContent = EditStringMatcher.NormalizeToCrLf(reconstructed);
            result.Success = true;

            return result;
        }

        private async Task<EditApplyResult> ApplySinglePatchAsync(
            PatchOperation patch, string filePath, string fileContent, CancellationToken ct)
        {
            return await Task.Run(() => ApplySinglePatch(patch, filePath, fileContent), ct);
        }

        #region Chunk-based 文件重建

        /// <summary>
        /// 将 PatchHunk 转换为 FileChunk + 上下文行数组。
        /// </summary>
        private static (FileChunk? chunk, string[] contextLines) HunkToChunk(PatchHunk hunk)
        {
            var contextLines = new List<string>();
            var delLines = new List<string>();
            var insLines = new List<string>();
            int origIndex = 0;
            bool hasChanges = false;

            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case ' ':
                        if (hasChanges)
                            contextLines.Add(line.Text);
                        else
                        {
                            contextLines.Add(line.Text);
                            origIndex++;
                        }
                        break;
                    case '-':
                        hasChanges = true;
                        delLines.Add(line.Text);
                        contextLines.Add(line.Text);
                        break;
                    case '+':
                        hasChanges = true;
                        insLines.Add(line.Text);
                        break;
                }
            }

            if (!hasChanges)
                return (null, Array.Empty<string>());

            // ── 防御：检测尾部重复闭合符号 ──
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

            var chunk = new FileChunk
            {
                OrigIndex = origIndex,
                DelLines = delLines,
                InsLines = insLines,
            };

            return (chunk, contextLines.ToArray());
        }

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
        /// 检查 @@ 标记文本是否出现在匹配位置附近（±MarkerSearchWindow 行内）。
        /// 用于验证上下文匹配结果是否与标记一致，防止在错误位置匹配。
        /// </summary>
        private static bool IsMarkerNearPosition(
            string[] fileLines, List<string> contextMarkers,
            int matchedLine, int contextLength, int searchWindow = 10)
        {
            if (contextMarkers == null || contextMarkers.Count == 0) return true; // 无标记 → 不校验

            int windowStart = Math.Max(0, matchedLine - searchWindow);
            int windowEnd = Math.Min(fileLines.Length, matchedLine + contextLength + searchWindow);

            foreach (var marker in contextMarkers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                string normalized = EditStringMatcher.NormalizeUnicode(marker);
                for (int i = windowStart; i < windowEnd; i++)
                {
                    if (EditStringMatcher.NormalizeUnicode(fileLines[i])
                        .Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 文件重建：遍历原始行，按 Chunk 列表删除旧行、插入新行。
        /// 保留原始文件的尾部空行数。
        /// 参考: parser.ts _get_updated_file + applyPatchTool.tsx trailing empty line preservation
        /// </summary>
        private static string ReconstructFile(string[] originalLines, List<FileChunk> chunks)
        {
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

                destLines.AddRange(originalLines.Skip(origIdx).Take(chunk.OrigIndex - origIdx));
                origIdx = chunk.OrigIndex;

                destLines.AddRange(chunk.InsLines);
                origIdx += chunk.DelLines.Count;
            }

            destLines.AddRange(originalLines.Skip(origIdx));

            // ── 保留原始文件尾部空行数 ──
            // 参考: applyPatchTool.tsx generateUpdateTextDocumentEdit 的 trailing empty line 逻辑
            int origTrailingEmpty = CountTrailingEmptyLines(originalLines);
            int newTrailingEmpty = CountTrailingEmptyLines(destLines);
            for (int i = newTrailingEmpty; i < origTrailingEmpty; i++)
                destLines.Add(string.Empty);

            return string.Join("\n", destLines);
        }

        /// <summary>
        /// 统计字符串数组尾部的空行数。
        /// </summary>
        private static int CountTrailingEmptyLines(IList<string> lines)
        {
            int count = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(lines[i].Trim()))
                    count++;
                else
                    break;
            }
            return count;
        }

        /// <summary>
        /// 统计字符串数组尾部的空行数（string[] 重载）。
        /// </summary>
        private static int CountTrailingEmptyLines(string[] lines) => CountTrailingEmptyLines((IList<string>)lines);

        /// <summary>
        /// 缩进适配：将 AI 输出的缩进调整为与目标文件一致。
        /// 参考: parser.ts computeIndentLevel2 + transformIndentation + additionalIndentation
        /// </summary>
        private static void AdaptChunkIndentation(
            FileChunk chunk, string[] contextLines, string[] fileLines, int matchedLine)
        {
            if (chunk.InsLines.Count == 0 || contextLines.Length == 0 || matchedLine >= fileLines.Length)
                return;

            // ── 计算 AI 输出的缩进层级（基于第一个上下文行）──
            string firstCtx = contextLines[0];
            int srcIndent = GetIndentLevel(firstCtx);

            // ── 计算目标文件的缩进层级（基于匹配行）──
            string matchedFileLine = fileLines[matchedLine];
            int targetIndent = GetIndentLevel(matchedFileLine);

            // ── 缩进差值：目标比 AI 多出的缩进量 ──
            int indentDelta = targetIndent - srcIndent;
            if (indentDelta <= 0) return; // AI 缩进已足够或更多

            // ── 推断缩进字符（空格 vs 制表符）──
            string indentChar = GetIndentChar(matchedFileLine);
            string additionalIndent = new string(indentChar[0], indentDelta);

            // ── 对每个插入行增加缩进 ──
            for (int i = 0; i < chunk.InsLines.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(chunk.InsLines[i]))
                    chunk.InsLines[i] = additionalIndent + chunk.InsLines[i];
            }
        }

        /// <summary>
        /// 计算行的缩进级别（空格数，制表符按4空格计）。
        /// </summary>
        private static int GetIndentLevel(string line)
        {
            int level = 0;
            foreach (char c in line)
            {
                if (c == ' ') level++;
                else if (c == '\t') level += 4;
                else break;
            }
            return level;
        }

        /// <summary>
        /// 推断行的缩进字符（优先制表符，否则空格）。
        /// </summary>
        private static string GetIndentChar(string line)
        {
            if (line.Length > 0 && line[0] == '\t') return "\t";
            return " ";
        }

        #endregion

        #region Add / Delete / Move

        private static EditApplyResult ApplyCreateFromPatch(PatchOperation patch, string filePath)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.CreateFile,
            };

            var sb = new StringBuilder();
            foreach (var hunk in patch.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.Type == '+' || line.Type == ' ')
                        sb.AppendLine(line.Text);
                }
            }

            result.FinalContent = EditStringMatcher.NormalizeToCrLf(sb.ToString());
            result.Success = true;
            return result;
        }

        private static EditApplyResult ApplyDeleteFile(string filePath)
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
                result.ErrorMessage = $"删除失败: {ex.Message}";
            }

            return result;
        }

        private static EditApplyResult ApplyMoveFile(string sourcePath, string destPath)
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
                    return result;
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
                result.ErrorMessage = $"移动失败: {ex.Message}";
            }

            return result;
        }

        #endregion

        #region AbstractEditTool 实现

        protected override Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct)
        {
            // apply_patch 不使用此方法（直接使用 ExecutePatchesAsync）
            throw new NotSupportedException("ApplyPatchTool 使用 ExecutePatchesAsync 而非 GenerateEditForFileAsync");
        }

        #endregion
    }
}
