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

                if (line.StartsWith("***") || (string.IsNullOrWhiteSpace(line) && currentHunk == null))
                    continue;

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

                if (currentHunk != null && line.TrimStart().StartsWith("@@"))
                {
                    string marker = line.TrimStart().Substring(2).Trim();
                    currentHunk.ContextMarkers.Add(marker);
                    currentHunk.RawText += line + "\n";
                    continue;
                }

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
                if (contextLines.Length == 0) continue;

                int matchedLine = EditStringMatcher.MatchContextInFileLines(
                    fileLines, contextLines, searchStartLine, out MatchLevel level);

                if (matchedLine < 0)
                {
                    // Fallback: @@ 标记定位
                    matchedLine = EditStringMatcher.MatchContextViaMarkers(
                        fileLines, patch.Hunks[ci].ContextMarkers, searchStartLine);
                }

                if (matchedLine < 0)
                {
                    // 从文件开头回退搜索
                    matchedLine = EditStringMatcher.MatchContextInFileLines(
                        fileLines, contextLines, 0, out level);
                }

                if (matchedLine < 0)
                {
                    failedHunks.Add(patch.Hunks[ci]);
                    continue;
                }

                chunk.OrigIndex += matchedLine;
                searchStartLine = matchedLine + contextLines.Length;
            }

            if (failedHunks.Count > 0)
            {
                result.Success = false;
                result.FailedHunks = failedHunks;
                result.ErrorMessage = $"{failedHunks.Count}/{patch.Hunks.Count} 个 Hunk 匹配失败";
                return result;
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
        /// 文件重建：遍历原始行，按 Chunk 列表删除旧行、插入新行。
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

            return string.Join("\n", destLines);
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
