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
        /// 为单个文件生成 TextEdit：执行 4 级字符串匹配。
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
                    ErrorMessage = "oldString 和 newString 相同，无需修改。",
                };
                return false;
            }

            // ── 执行 4 级匹配 ──
            int matchPos = EditStringMatcher.MatchWithFallback(
                fileContent, input.OldString, out MatchLevel matchLevel);

            if (matchPos < 0)
            {
                // ── 匹配失败 → 标记需要 Healing ──
                prepared.GeneratedEdit = new GeneratedEditResult
                {
                    Success = false,
                    ErrorMessage = $"无法在文件中找到 oldString。\n" +
                        "请确保 oldString 与文件中的文本完全一致（包括空白、缩进、换行符）。",
                };
                return false;
            }

            // ── 构造 TextEdit ──
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

                // 尝试在附近精确匹配
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
    }
}
