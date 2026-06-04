using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// replace_string_in_file 工具实现。
    /// 在文件中查找 oldString 并用 newString 替换（仅替换首次匹配）。
    /// 
    /// 参考: vscode-copilot-chat replaceStringTool.tsx
    /// </summary>
    public class ReplaceStringTool : AbstractEditTool
    {
        protected override string ToolName => "replace_string_in_file";

        public ReplaceStringTool(DeepSeekApiService apiService, string workspaceRoot)
            : base(apiService, workspaceRoot) { }

        /// <summary>
        /// 执行单次字符串替换。
        /// </summary>
        public async Task<EditToolResult> ExecuteAsync(
            ReplaceStringParams parameters, CancellationToken ct)
        {
            var inputs = new List<ReplaceStringInput>
            {
                new ReplaceStringInput
                {
                    FilePath = parameters.FilePath,
                    OldString = parameters.OldString,
                    NewString = parameters.NewString,
                }
            };

            var edits = await PrepareEditsAsync(inputs, ct);
            return await ApplyAllEditsAsync(edits, ct);
        }

        /// <summary>
        /// 为单个文件生成 TextEdit：执行多级字符串匹配。
        /// 包含：文件路径注释剥离、首尾相同优化、多重匹配检测。
        /// 参考: abstractReplaceStringTool.tsx + editFileToolUtils.tsx findAndReplaceOne
        /// </summary>
        protected override async Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct)
        {
            var input = prepared.HealedInput ?? prepared.Input;

            // ── 剥离 AI 可能在首行重复输出的文件路径注释 ──
            // 参考: removeLeadingFilepathComment
            string oldString = EditStringMatcher.RemoveLeadingFilepathComment(
                EditStringMatcher.NormalizeLineEndings(input.OldString));
            string newString = EditStringMatcher.RemoveLeadingFilepathComment(
                EditStringMatcher.NormalizeLineEndings(input.NewString));

            if (oldString == newString)
            {
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance["tool.edit.replaceString.sameNoChange"],
                };
                return false;
            }

            // ── 首尾相同字符修剪：缩小实际编辑范围 ──
            // 参考: editFileToolUtils.tsx getIdenticalChars
            var (leading, trailing) = EditStringMatcher.GetIdenticalLeadingTrailingChars(oldString, newString);
            string trimmedOld = oldString.Substring(leading,
                oldString.Length - leading - Math.Max(0, trailing));
            string trimmedNew = newString.Substring(leading,
                newString.Length - leading - Math.Max(0, trailing));

            // ── 执行多级匹配 ──
            int matchPos = EditStringMatcher.MatchWithFallback(
                fileContent, trimmedOld, out MatchLevel matchLevel);

            if (matchPos < 0)
            {
                // ── 匹配失败 → 标记需要 Healing ──
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = LocalizationService.Instance["tool.edit.replaceString.notFound"],
                };
                return false;
            }

            // ── 多重匹配检测 ──
            // 参考: editFileToolUtils.tsx tryExactMatch 的 multiple 检测
            if (matchLevel == MatchLevel.Exact)
            {
                int secondMatch = fileContent.IndexOf(trimmedOld, matchPos + 1, StringComparison.Ordinal);
                if (secondMatch >= 0)
                {
                    prepared.GeneratedEdit = new GeneratedEditResult
                    {
                        Success = false,
                        ErrorMessage = LocalizationService.Instance.Format("tool.edit.replaceString.multipleMatch", matchPos, secondMatch),
                    };
                    return false;
                }
            }

            // ── 构造 TextEdit ──
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
    }
}
