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
                    ErrorSummary = LocalizationService.Instance["tool.edit.multiReplace.arrayEmpty"],
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
        /// MultiReplace 的每文件编辑生成：包含文件路径剥离、首尾优化、多重匹配检测。
        /// 参考: abstractReplaceStringTool.tsx + editFileToolUtils.tsx
        /// </summary>
        protected override async Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct)
        {
            var input = prepared.HealedInput ?? prepared.Input;

            // ── 剥离 AI 可能在首行重复输出的文件路径注释 ──
            string oldString = EditStringMatcher.RemoveLeadingFilepathComment(
                EditStringMatcher.NormalizeLineEndings(input.OldString));
            string newString = EditStringMatcher.RemoveLeadingFilepathComment(
                EditStringMatcher.NormalizeLineEndings(input.NewString));

            if (oldString == newString)
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance["tool.edit.multiReplace.sameContent"],
                };
                return false;
            }

            // ── 首尾相同字符修剪 ──
            var (leading, trailing) = EditStringMatcher.GetIdenticalLeadingTrailingChars(oldString, newString);
            string trimmedOld = oldString.Substring(leading,
                oldString.Length - leading - Math.Max(0, trailing));
            string trimmedNew = newString.Substring(leading,
                newString.Length - leading - Math.Max(0, trailing));

            int matchPos = EditStringMatcher.MatchWithFallback(
                fileContent, trimmedOld, out MatchLevel matchLevel);

            if (matchPos < 0)
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance["tool.edit.multiReplace.notFound"],
                };
                return false;
            }

            // ── 多重匹配检测 ──
            if (matchLevel == MatchLevel.Exact)
            {
                int secondMatch = fileContent.IndexOf(trimmedOld, matchPos + 1, StringComparison.Ordinal);
                if (secondMatch >= 0)
                {
                    prepared.GeneratedEdit = new GeneratedEditResult
                    {
                        Success = false,
                        ErrorMessage = LocalizationService.Instance["tool.edit.multiReplace.multipleMatch"],
                    };
                    return false;
                }
            }

            int matchEndPos = matchPos + trimmedOld.Length;

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
                        NewText = trimmedNew,
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
                    FailureReason = edit.GeneratedEdit.ErrorMessage ?? LocalizationService.Instance["tool.edit.multiReplace.matchFailed"],
                    FailedContextDetails = new List<string> { edit.Input.OldString.Truncate(80) },
                };

                var healingResponse = await _healing.HealAsync(healingRequest, ct);

                if (healingResponse?.Success == true && healingResponse.CorrectedReplaceInput != null)
                {
                    Logger.Info(LocalizationService.Instance.Format("tool.edit.multiReplace.healingSuccess", edit.FilePath));
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
                    Logger.Warn(LocalizationService.Instance.Format("tool.edit.multiReplace.healingFailed", edit.FilePath, healingResponse?.ErrorMessage));
                }
            }
        }
    }
}
