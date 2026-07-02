using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
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
    /// insert_edit_into_file 工具实现 — 完整文件重写，用 ...existing code... 标记未改区域。
    /// 适合需要修改多处代码，但不想用 diff 格式的场景。
    /// 
    /// 参考: vscode-copilot-chat insertEditTool.tsx (EditFileTool)
    /// </summary>
    public class InsertEditTool : AbstractEditTool
    {
        private readonly EditFileHealing? _healing;
        private const string ExistingCodeMarker = "...existing code...";

        protected override string ToolName => "insert_edit_into_file";

        // ── insert_edit_into_file 格式正则 ──
        private static readonly Regex InsertEditBlockRegex = new(
            @"```(?:insert_edit_into_file|edit)\s*:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public InsertEditTool(DeepSeekApiService apiService, string workspaceRoot)
            : base(apiService, workspaceRoot)
        {
            _healing = new EditFileHealing(apiService);
        }

        /// <summary>
        /// 从 AI 输出中解析所有 insert_edit_into_file 操作（静态方法）。
        /// </summary>
        public static List<InsertEditOperation> ParseInsertEdits(string aiOutput)
        {
            var edits = new List<InsertEditOperation>();
            if (string.IsNullOrWhiteSpace(aiOutput)) return edits;

            // 方式1: ```insert_edit_into_file: 或 ```edit: 包裹
            var matches = InsertEditBlockRegex.Matches(aiOutput);
            foreach (Match match in matches)
            {
                edits.Add(new InsertEditOperation
                {
                    FilePath = match.Groups["path"].Value.Trim(),
                    FullContent = match.Groups["content"].Value,
                });
            }

            // 方式2: ```file: 代码块内包含 ...existing code... 标记
            var fileBlockRegex = new Regex(
                @"```file:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
                RegexOptions.Singleline);
            var fileMatches = fileBlockRegex.Matches(aiOutput);
            foreach (Match match in fileMatches)
            {
                string content = match.Groups["content"].Value;
                if (content.Contains(ExistingCodeMarker))
                {
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
        /// 执行 InsertEdit 操作。
        /// </summary>
        public async Task<List<EditApplyResult>> ExecuteInsertEditsAsync(
            List<InsertEditOperation> edits, CancellationToken ct)
        {
            var results = new List<EditApplyResult>();

            // ── 备份追踪 ──
            BackupService.BeginSession();
            var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var edit in edits)
            {
                if (ct.IsCancellationRequested) break;

                string resolvedPath = EditPatchService.ResolvePath(edit.FilePath, WorkspaceRoot);

                // ── 首次接触文件时创建备份 ──
                if (!backups.ContainsKey(resolvedPath))
                {
                    backups[resolvedPath] = BackupService.CreateBackup(resolvedPath);
                }

                var result = await ApplySingleInsertEditAsync(edit, resolvedPath, ct);

                // ── Healing ──
                if (!result.Success && result.FailedRegions != null && result.FailedRegions.Count > 0)
                {
                    string fileContent = File.Exists(resolvedPath)
                        ? await Task.Run(() => File.ReadAllText(resolvedPath), ct)
                        : string.Empty;

                    var healingRequest = new HealingRequest
                    {
                        FilePath = resolvedPath,
                        CurrentFileContent = fileContent,
                        OriginalOperationType = EditOperationType.InsertEditIntoFile,
                        FailedInsertEditContent = edit.FullContent,
                        FailureReason = result.ErrorMessage ?? LocalizationService.Instance["tool.edit.applyPatch.unknownReason"],
                        FailedContextDetails = result.FailedRegions,
                    };

                    var healingResponse = await _healing!.HealAsync(healingRequest, ct);

                    if (healingResponse?.Success == true && healingResponse.CorrectedInsertEditContent != null)
                    {
                        Logger.Info(LocalizationService.Instance.Format("tool.edit.insert.healingSuccess", resolvedPath));
                        var correctedEdit = new InsertEditOperation
                        {
                            FilePath = edit.FilePath,
                            FullContent = healingResponse.CorrectedInsertEditContent,
                        };
                        result = await ApplySingleInsertEditAsync(correctedEdit, resolvedPath, ct);

                        // ── 兜底：Healing 后仍失败 → create_file ──
                        if (!result.Success)
                        {
                            Logger.Warn(LocalizationService.Instance.Format("tool.edit.insert.healingRetryFailed", resolvedPath));
                            try
                            {
                                await Task.Run(() => File.WriteAllText(resolvedPath,
                                    EditStringMatcher.NormalizeToCrLf(edit.FullContent)), ct);
                                result.Success = true;
                                result.FinalContent = edit.FullContent;
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(LocalizationService.Instance.Format("tool.edit.insert.createFileFallbackFailed", ex.Message));
                            }
                        }
                    }
                }

                if (result.Success && !string.IsNullOrEmpty(result.FinalContent))
                {
                    await Task.Run(() => File.WriteAllText(resolvedPath,
                        EditStringMatcher.NormalizeToCrLf(result.FinalContent)), ct);
                }

                results.Add(result);
            }

            // ── 事务提交/回滚 ──
            bool anyFailed = results.Any(r => !r.Success);
            if (anyFailed)
            {
                Logger.Warn("[InsertEditTool] 部分编辑失败，回滚所有已修改文件");
                BackupService.RollbackAll(backups);
            }
            else
            {
                foreach (var kvp in backups)
                    BackupService.CleanupBackup(kvp.Value);
            }

            BackupService.EndSession();
            return results;
        }

        /// <summary>
        /// 应用单个 InsertEdit 操作。
        /// </summary>
        private static async Task<EditApplyResult> ApplySingleInsertEditAsync(
            InsertEditOperation edit, string filePath, CancellationToken ct)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.InsertEditIntoFile,
            };

            if (!File.Exists(filePath))
            {
                // 文件不存在 → 创建新文件（去掉占位符）
                string newContent = RemoveExistingCodeMarkers(edit.FullContent);
                result.FinalContent = EditStringMatcher.NormalizeToCrLf(newContent);
                result.Success = true;
                return result;
            }

            string fileContent = await Task.Run(() => File.ReadAllText(filePath), ct);
            var normalizedContent = EditStringMatcher.NormalizeLineEndings(fileContent);
            var normalizedEdit = EditStringMatcher.NormalizeLineEndings(edit.FullContent);

            var segments = SplitByExistingCodeMarker(normalizedEdit);

            if (segments.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance["tool.edit.insert.noEditContent"];
                return result;
            }

            bool hasPlaceholders = segments.Any(s => s.IsPlaceholder);
            if (!hasPlaceholders)
            {
                // 没有 ...existing code... 标记 → 全文件替换
                result.FinalContent = EditStringMatcher.NormalizeToCrLf(normalizedEdit);
                result.Success = true;
                return result;
            }

            // ── 磁盘内容一致性校验：检测文件是否在 AI 读取后被修改 ──
            if (hasPlaceholders)
            {
                var contextSegments = segments
                    .Where(s => !s.IsPlaceholder && !string.IsNullOrWhiteSpace(s.Text))
                    .Select(s => s.Text.Trim())
                    .ToArray();
                if (!EditStringMatcher.VerifyContentFreshness(normalizedContent, contextSegments))
                {
                    result.Success = false;
                    result.ErrorMessage = string.Format(
                        "File '{0}' has been modified since you last read it. Please re-read the file with read_file and try your edit again.",
                        Path.GetFileName(filePath));
                    return result;
                }
            }

            // ── 对每个修改段进行匹配 ──
            var failedRegions = new List<string>();
            string workingContent = normalizedContent;

            foreach (var segment in segments)
            {
                if (segment.IsPlaceholder) continue;

                string searchText = segment.Text.Trim();
                if (string.IsNullOrEmpty(searchText)) continue;

                int matchPos = EditStringMatcher.MatchWithFallback(
                    workingContent, searchText, out MatchLevel level);

                if (matchPos < 0)
                {
                    failedRegions.Add(searchText.Truncate(80));
                    continue;
                }

                // 记录匹配到的编辑
                var (startLine, startCol) = EditStringMatcher.GetLineColumn(workingContent, matchPos);
                result.AppliedEdits.Add(new TextEditOperation
                {
                    StartLine = startLine,
                    StartColumn = startCol,
                    MatchLevelUsed = level,
                    MatchedText = searchText,
                });
            }

            if (failedRegions.Count > 0)
            {
                result.Success = false;
                result.FailedRegions = failedRegions;
                result.ErrorMessage = LocalizationService.Instance.Format("tool.edit.insert.regionMatchFailed", failedRegions.Count);
                return result;
            }

            // ── 重建最终内容 ──
            string finalContent = ReconstructFromSegments(segments, normalizedContent);
            result.FinalContent = EditStringMatcher.NormalizeToCrLf(finalContent);
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
                if (match.Index > lastEnd)
                {
                    string segmentText = content.Substring(lastEnd, match.Index - lastEnd);
                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        segments.Add(new ContentSegment { Text = segmentText, IsPlaceholder = false });
                    }
                }

                segments.Add(new ContentSegment { Text = match.Value, IsPlaceholder = true });
                lastEnd = match.Index + match.Length;
            }

            if (lastEnd < content.Length)
            {
                string remaining = content.Substring(lastEnd);
                if (!string.IsNullOrWhiteSpace(remaining))
                    segments.Add(new ContentSegment { Text = remaining, IsPlaceholder = false });
            }

            if (segments.Count == 0)
            {
                segments.Add(new ContentSegment { Text = content, IsPlaceholder = false });
            }

            return segments;
        }

        /// <summary>
        /// 根据修改段重建文件内容。
        /// </summary>
        private static string ReconstructFromSegments(List<ContentSegment> segments, string originalContent)
        {
            if (segments.All(s => !s.IsPlaceholder))
                return segments.FirstOrDefault()?.Text ?? originalContent;

            var sb = new StringBuilder();
            foreach (var segment in segments)
            {
                sb.Append(segment.Text);
            }

            string result = sb.ToString();
            var markerPattern = new Regex(
                @"\/\/\s*\.\.\.existing\s*code\.\.\.[\r\n]*|" +
                @"#\s*\.\.\.existing\s*code\.\.\.[\r\n]*|" +
                @"<!--\s*\.\.\.existing\s*code\.\.\.\s*-->[\r\n]*",
                RegexOptions.IgnoreCase);
            result = markerPattern.Replace(result, "");

            return result;
        }

        /// <summary>
        /// 移除所有 ...existing code... 占位符。
        /// </summary>
        private static string RemoveExistingCodeMarkers(string content)
        {
            var markerPattern = new Regex(
                @"\/\/\s*\.\.\.existing\s*code\.\.\.[\r\n]*|" +
                @"#\s*\.\.\.existing\s*code\.\.\.[\r\n]*|" +
                @"<!--\s*\.\.\.existing\s*code\.\.\.\s*-->[\r\n]*",
                RegexOptions.IgnoreCase);
            return markerPattern.Replace(content, "");
        }

        /// <summary>
        /// 内容段（内部类）。
        /// </summary>
        private class ContentSegment
        {
            public string Text { get; set; } = string.Empty;
            public bool IsPlaceholder { get; set; }
        }

        #region AbstractEditTool 实现

        protected override Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct)
        {
            throw new NotSupportedException("InsertEditTool 使用 ExecuteInsertEditsAsync 而非 GenerateEditForFileAsync");
        }

        #endregion
    }
}
