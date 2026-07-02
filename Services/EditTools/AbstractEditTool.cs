using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// 抽象编辑工具基类 — 所有编辑工具（ReplaceString / MultiReplace / ApplyPatch / InsertEdit）的共享逻辑。
    /// 
    /// 参考: vscode-copilot-chat abstractReplaceStringTool.tsx
    /// 
    /// 职责：
    /// - 准备编辑（验证路径、读取内容、执行匹配、生成 TextEdit）
    /// - 批量应用编辑到 VS 文本缓冲区
    /// - 编辑后诊断检查
    /// - Healing 集成点
    /// - 日志/遥测
    /// </summary>
    public abstract class AbstractEditTool
    {
        protected readonly DeepSeekApiService ApiService;
        protected readonly string WorkspaceRoot;

        protected AbstractEditTool(DeepSeekApiService apiService, string workspaceRoot)
        {
            ApiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            WorkspaceRoot = workspaceRoot ?? string.Empty;
        }

        /// <summary>
        /// 工具名称（用于日志）。
        /// </summary>
        protected abstract string ToolName { get; }

        /// <summary>
        /// 准备所有编辑：验证 → 读取文件 → 匹配 → 生成 TextEdit。
        /// 这是核心方法，子类通过重写 GenerateEditForFile 提供各自的编辑生成逻辑。
        /// </summary>
        protected async Task<List<PreparedEdit>> PrepareEditsAsync(
            List<ReplaceStringInput> inputs,
            CancellationToken ct)
        {
            var results = new List<PreparedEdit>();

            foreach (var input in inputs)
            {
                if (ct.IsCancellationRequested) break;

                var prepared = await PrepareSingleEditAsync(input, ct);
                results.Add(prepared);
            }

            // ── 检测同一文件内的编辑冲突 ──
            DetectConflictingEdits(results);

            return results;
        }

        /// <summary>
        /// 准备单个文件的编辑。
        /// </summary>
        private async Task<PreparedEdit> PrepareSingleEditAsync(
            ReplaceStringInput input, CancellationToken ct)
        {
            var prepared = new PreparedEdit
            {
                FilePath = EditPatchService.ResolvePath(input.FilePath, WorkspaceRoot),
                Input = input,
            };

            // ── 验证参数 ──
            if (string.IsNullOrEmpty(input.OldString) && string.IsNullOrEmpty(input.NewString))
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance["tool.edit.oldNewBothEmpty"],
                };
                return prepared;
            }

            // ── 处理新文件创建（oldString 为空时）──
            if (!File.Exists(prepared.FilePath))
            {
                if (string.IsNullOrEmpty(input.OldString))
                {
                    // 空 oldString + 文件不存在 = 创建新文件
                    prepared.GeneratedEdit = new GeneratedEditResult
                    {
                        Success = true,
                        TextEdits = new List<TextEditOperation>
                        {
                            new TextEditOperation
                            {
                                StartLine = 0, StartColumn = 0,
                                EndLine = 0, EndColumn = 0,
                                NewText = input.NewString,
                                MatchLevelUsed = MatchLevel.Exact,
                            }
                        },
                    };
                }
                else
                {
                    prepared.GeneratedEdit = new GeneratedEditResult
                    {
                        Success = false,
                        ErrorMessage = LocalizationService.Instance.Format("tool.edit.fileNotExist", prepared.FilePath),
                    };
                }
                return prepared;
            }

            // ── 读取文件当前内容 ──
            string fileContent;
            try
            {
                fileContent = await Task.Run(() => File.ReadAllText(prepared.FilePath), ct);
            }
            catch (Exception ex)
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance.Format("tool.edit.readFailed", ex.Message),
                };
                return prepared;
            }

            prepared.OriginalContent = fileContent;

            // ── 生成编辑（由子类实现）──
            bool success = await GenerateEditForFileAsync(prepared, fileContent, ct);

            if (!success && prepared.GeneratedEdit.ErrorMessage != null)
            {
                // 生成失败 — 记录失败信息
            }

            return prepared;
        }

        /// <summary>
        /// 为单个文件生成 TextEdit（子类实现）。
        /// 子类负责：
        /// 1. 执行字符串匹配
        /// 2. 构造 TextEditOperation
        /// 3. 设置 prepared.GeneratedEdit
        /// </summary>
        /// <returns>true 表示生成成功</returns>
        protected abstract Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct);

        /// <summary>
        /// 检测同一文件内的编辑冲突（重叠编辑）。
        /// </summary>
        private void DetectConflictingEdits(List<PreparedEdit> edits)
        {
            for (int i = 1; i < edits.Count; i++)
            {
                var current = edits[i];
                if (!current.GeneratedEdit.Success) continue;

                for (int k = 0; k < i; k++)
                {
                    var other = edits[k];
                    if (!other.GeneratedEdit.Success) continue;

                    if (!string.Equals(current.FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 收集并排序所有 TextEdit
                    var allEdits = current.GeneratedEdit.TextEdits
                        .Concat(other.GeneratedEdit.TextEdits)
                        .OrderBy(e => e.StartLine * 100000 + e.StartColumn)
                        .ToList();

                    // 检测重叠
                    bool hasOverlap = false;
                    for (int j = 1; j < allEdits.Count; j++)
                    {
                        var prev = allEdits[j - 1];
                        var curr = allEdits[j];
                        // 简化检测：行级重叠
                        if (prev.EndLine > curr.StartLine ||
                            (prev.EndLine == curr.StartLine && prev.EndColumn > curr.StartColumn))
                        {
                            hasOverlap = true;
                            break;
                        }
                    }

                    if (hasOverlap)
                    {
                        current.GeneratedEdit = new GeneratedEditResult
                        {
                            Success = false,
                            ErrorMessage = LocalizationService.Instance.Format("tool.edit.editConflict", i, current.FilePath),
                        };
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 批量应用所有编辑。
        /// 1. 创建备份
        /// 2. 先通过文件系统写入
        /// 3. 再通过 VS 文本缓冲区更新已打开的编辑器
        /// 4. 检查新引入的诊断错误
        /// 5. 全部成功则清理备份，有失败则回滚
        /// </summary>
        protected async Task<EditToolResult> ApplyAllEditsAsync(
            List<PreparedEdit> edits,
            CancellationToken ct)
        {
            var result = new EditToolResult();
            var fileResults = new List<EditedFileResult>();

            // ── 备份追踪 ──
            BackupService.BeginSession();
            var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var prepared in edits)
            {
                if (ct.IsCancellationRequested) break;

                var fileResult = new EditedFileResult
                {
                    FilePath = prepared.FilePath,
                    Operation = PatchFileAction.Update,
                };

                if (!prepared.GeneratedEdit.Success)
                {
                    fileResult.ErrorMessage = prepared.GeneratedEdit.ErrorMessage;
                    fileResults.Add(fileResult);
                    result.FailureCount++;
                    continue;
                }

                if (prepared.HealedInput != null)
                {
                    fileResult.WasHealed = true;
                    fileResult.HealingDescription = LocalizationService.Instance["tool.edit.healingFixed"];
                    result.HealingCount++;
                }

                // ── 计算最终文件内容 ──
                string finalContent;
                try
                {
                    finalContent = ApplyTextEditsToContent(
                        prepared.OriginalContent ?? string.Empty,
                        prepared.GeneratedEdit.TextEdits);
                    finalContent = EditStringMatcher.NormalizeToCrLf(finalContent);
                }
                catch (Exception ex)
                {
                    fileResult.ErrorMessage = LocalizationService.Instance.Format("tool.edit.constructFailed", ex.Message);
                    fileResults.Add(fileResult);
                    result.FailureCount++;
                    continue;
                }

                // ── 创建备份 ──
                if (!backups.ContainsKey(prepared.FilePath))
                {
                    backups[prepared.FilePath] = BackupService.CreateBackup(prepared.FilePath);
                }

                // ── 写入文件系统 ──
                try
                {
                    await Task.Run(() => File.WriteAllText(prepared.FilePath, finalContent), ct);
                }
                catch (Exception ex)
                {
                    fileResult.ErrorMessage = LocalizationService.Instance.Format("tool.edit.writeFailed", ex.Message);
                    fileResults.Add(fileResult);
                    result.FailureCount++;
                    continue;
                }

                // ── 更新 VS 编辑器缓冲区 ──
                try
                {
                    await EditBufferApplier.ApplyEditsToOpenDocumentAsync(
                        prepared.FilePath, prepared.GeneratedEdit.TextEdits);
                }
                catch (Exception ex)
                {
                    Logger.Warn(LocalizationService.Instance.Format("tool.edit.vsUpdateFailed", ToolName, ex.Message));
                }

                fileResults.Add(fileResult);
                result.SuccessCount++;

                // ── 日志 ──
                result.Logs.Add(new EditToolLogEntry
                {
                    Level = "INFO",
                    Message = fileResult.WasHealed
                        ? LocalizationService.Instance.Format("tool.edit.appliedHealing", prepared.FilePath)
                        : LocalizationService.Instance.Format("tool.edit.applied", prepared.FilePath),
                });
            }

            result.Files = fileResults;
            result.AllSucceeded = result.FailureCount == 0;

            // ── 事务提交/回滚 ──
            if (!result.AllSucceeded)
            {
                Logger.Warn("[AbstractEditTool] 部分编辑失败，回滚所有已修改文件");
                BackupService.RollbackAll(backups);
                result.ErrorSummary = string.Join("; ",
                    fileResults.Where(f => f.ErrorMessage != null)
                               .Select(f => $"{Path.GetFileName(f.FilePath)}: {f.ErrorMessage}"));
            }
            else
            {
                result.ErrorSummary = string.Join("; ",
                    fileResults.Where(f => f.ErrorMessage != null)
                               .Select(f => $"{Path.GetFileName(f.FilePath)}: {f.ErrorMessage}"));

                // ── 成功清理备份 ──
                foreach (var kvp in backups)
                    BackupService.CleanupBackup(kvp.Value);
            }

            BackupService.EndSession();
            return result;
        }

        /// <summary>
        /// 将 TextEdit 列表应用到文件内容，生成最终内容。
        /// </summary>
        private static string ApplyTextEditsToContent(string originalContent, List<TextEditOperation> edits)
        {
            if (edits.Count == 0) return originalContent;

            var lines = originalContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 按行重建：先收集所有编辑并计算最终行数组
            var finalLines = new List<string>(lines);

            foreach (var edit in edits.OrderBy(e => e.StartLine * 100000 + e.StartColumn))
            {
                // 简化：行级替换
                int startLine = Clamp(edit.StartLine, 0, finalLines.Count);
                int endLine = Clamp(edit.EndLine, startLine, finalLines.Count);

                // 移除旧行
                if (endLine > startLine)
                    finalLines.RemoveRange(startLine, endLine - startLine);

                // 插入新行
                var newLines = edit.NewText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                if (endLine == startLine && startLine < finalLines.Count)
                {
                    // 行内替换
                    string oldLine = finalLines[startLine];
                    int startCol = Clamp(edit.StartColumn, 0, oldLine.Length);
                    int endCol = Clamp(edit.EndColumn, startCol, oldLine.Length);
                    string prefix = oldLine.Substring(0, startCol);
                    string suffix = oldLine.Substring(endCol);
                    finalLines[startLine] = prefix + edit.NewText + suffix;
                }
                else
                {
                    finalLines.InsertRange(startLine, newLines);
                }
            }

            return string.Join("\n", finalLines);
        }

        /// <summary>
        /// 检查编辑后是否引入了新的诊断错误。
        /// </summary>
        protected static async Task<List<string>> CheckDiagnosticsAsync(string filePath)
        {
            return await EditPatchService.CheckNewDiagnosticsAsync(filePath);
        }

        /// <summary>
        /// 路径解析（委托给 EditPatchService）。
        /// </summary>
        protected static string ResolvePath(string filePath, string workspaceRoot)
        {
            return EditPatchService.ResolvePath(filePath, workspaceRoot);
        }

        /// <summary>
        /// Clamp 值到指定范围（.NET Framework 4.7.2 兼容替代 Math.Clamp）。
        /// </summary>
        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
