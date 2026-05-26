using DeepSeek_v4_for_VisualStudio.Models;
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
    /// multi_replace_string_in_file 工具实现。
    /// 批量执行多个文件的字符串替换，原子操作（全部成功或报告失败）。
    /// 支持编辑冲突检测和 Healing。
    /// 
    /// 参考: vscode-copilot-chat multiReplaceStringTool.tsx
    /// </summary>
    public class MultiReplaceStringTool : AbstractEditTool
    {
        private readonly EditFileHealing? _healing;

        protected override string ToolName => "multi_replace_string_in_file";

        public MultiReplaceStringTool(DeepSeekApiService apiService, string workspaceRoot)
            : base(apiService, workspaceRoot)
        {
            _healing = new EditFileHealing(apiService);
        }

        /// <summary>
        /// 执行批量字符串替换。
        /// </summary>
        public async Task<EditToolResult> ExecuteAsync(
            MultiReplaceStringParams parameters, CancellationToken ct)
        {
            if (parameters.Replacements == null || parameters.Replacements.Count == 0)
            {
                return new EditToolResult
                {
                    AllSucceeded = false,
                    ErrorSummary = "replacements 数组为空",
                };
            }

            // ── 阶段 1：准备所有编辑（验证 + 匹配）──
            var edits = await PrepareEditsAsync(parameters.Replacements, ct);

            // ── 阶段 2：Healing 失败的编辑 ──
            if (_healing != null)
            {
                await HealFailedEditsAsync(edits, ct);
            }

            // ── 阶段 3：批量应用所有成功的编辑 ──
            return await ApplyAllEditsAsync(edits, ct);
        }

        /// <summary>
        /// MultiReplace 的每文件编辑生成：与 ReplaceStringTool 相同。
        /// </summary>
        protected override async Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct)
        {
            var input = prepared.HealedInput ?? prepared.Input;

            if (input.OldString == input.NewString)
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = "oldString 和 newString 相同，跳过。",
                };
                return false;
            }

            int matchPos = EditStringMatcher.MatchWithFallback(
                fileContent, input.OldString, out MatchLevel matchLevel);

            if (matchPos < 0)
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = $"无法找到 oldString（已尝试 4 级匹配）",
                };
                return false;
            }

            int matchEndPos;
            if (matchLevel == MatchLevel.Exact)
            {
                matchEndPos = matchPos + input.OldString.Length;
            }
            else
            {
                matchEndPos = EditStringMatcher.FindMatchEndByLineCount(
                    fileContent, matchPos, input.OldString);
                if (matchEndPos <= matchPos)
                    matchEndPos = Math.Min(matchPos + input.OldString.Length + 50, fileContent.Length);

                int exactPos = fileContent.IndexOf(input.OldString, matchPos, StringComparison.Ordinal);
                if (exactPos >= 0 && exactPos - matchPos < 500)
                {
                    matchPos = exactPos;
                    matchEndPos = exactPos + input.OldString.Length;
                }
            }

            var (startLine, startCol) = EditStringMatcher.GetLineColumn(fileContent, matchPos);
            var (endLine, endCol) = EditStringMatcher.GetLineColumn(fileContent, matchEndPos);

            prepared.GeneratedEdit = new GeneratedEditResult
            {
                Success = true,
                TextEdits = new List<TextEditOperation>
                {
                    new TextEditOperation
                    {
                        StartLine = startLine,
                        StartColumn = startCol,
                        EndLine = endLine,
                        EndColumn = endCol,
                        NewText = input.NewString,
                        MatchedText = fileContent.Substring(matchPos,
                            Math.Min(matchEndPos - matchPos, fileContent.Length - matchPos)),
                        MatchLevelUsed = matchLevel,
                    }
                },
            };

            return true;
        }

        /// <summary>
        /// 对匹配失败的编辑执行 Healing。
        /// </summary>
        private async Task HealFailedEditsAsync(List<PreparedEdit> edits, CancellationToken ct)
        {
            for (int i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (edit.GeneratedEdit.Success) continue;
                if (_healing == null) continue;

                string fileContent;
                try
                {
                    fileContent = await Task.Run(() => File.ReadAllText(edit.FilePath), ct);
                }
                catch
                {
                    continue;
                }

                var healingRequest = new HealingRequest
                {
                    FilePath = edit.FilePath,
                    CurrentFileContent = fileContent,
                    OriginalOperationType = EditOperationType.ApplyPatch, // 使用 ApplyPatch 类型（复用 healing 逻辑）
                    FailedReplaceInput = edit.Input,
                    FailureReason = edit.GeneratedEdit.ErrorMessage ?? "oldString 匹配失败",
                    FailedContextDetails = new List<string> { edit.Input.OldString.Truncate(80) },
                };

                var healingResponse = await _healing.HealAsync(healingRequest, ct);

                if (healingResponse?.Success == true && healingResponse.CorrectedReplaceInput != null)
                {
                    Logger.Info($"[MultiReplace] Healing 成功: {edit.FilePath}");
                    edit.HealedInput = healingResponse.CorrectedReplaceInput;

                    // 用修正后的输入重新生成编辑
                    string updatedContent;
                    try
                    {
                        updatedContent = await Task.Run(() => File.ReadAllText(edit.FilePath), ct);
                    }
                    catch
                    {
                        continue;
                    }

                    await GenerateEditForFileAsync(edit, updatedContent, ct);
                }
                else
                {
                    Logger.Warn($"[MultiReplace] Healing 失败: {edit.FilePath} - {healingResponse?.ErrorMessage}");
                }
            }
        }
    }
}
