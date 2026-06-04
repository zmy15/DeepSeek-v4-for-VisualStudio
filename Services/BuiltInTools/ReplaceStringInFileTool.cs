using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// replace_string_in_file 工具 — 在文件中精确替换字符串。
    /// </summary>
    public class ReplaceStringInFileTool : BuiltInToolBase
    {
        public override string Name => "replace_string_in_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "replace_string_in_file",
                    Description = L["tool.replace_string_in_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string", description = "要修改的文件的绝对路径（Windows 格式）" },
                            oldString = new { type = "string", description = "要替换的原始文本（必须精确匹配，包括所有空白和缩进）" },
                            newString = new { type = "string", description = "替换后的新文本" }
                        },
                        required = new[] { "filePath", "oldString", "newString" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string editPath = GetStringArg(args, "filePath");
            string editFile = string.IsNullOrEmpty(editPath) ? "?" : Path.GetFileName(editPath);
            return LocalizationService.Instance.Format("tool.replaceString.displayText", editFile);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                return LocalizationService.Instance["tool.replaceString.editComplete"];
            return LocalizationService.Instance["tool.replaceString.editDone"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string oldString = GetStringArg(args, "oldString");
            string newString = GetStringArg(args, "newString");

            if (string.IsNullOrEmpty(filePath))
                return LocalizationService.Instance["tool.replaceString.missingFilePath"];
            if (string.IsNullOrEmpty(oldString))
                return LocalizationService.Instance["tool.replaceString.missingOldString"];

            filePath = ResolvePath(filePath, workspaceRoot);

            if (!File.Exists(filePath))
                return LocalizationService.Instance.Format("tool.replaceString.fileNotFound", filePath);

            try
            {
                string content = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8));
                string normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedOld = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedNew = newString.Replace("\r\n", "\n").Replace("\r", "\n");

                int index = normalizedContent.IndexOf(normalizedOld, StringComparison.Ordinal);
                if (index < 0)
                    return LocalizationService.Instance.Format("tool.replaceString.textNotFound", Path.GetFileName(filePath));

                int secondIndex = normalizedContent.IndexOf(normalizedOld, index + 1, StringComparison.Ordinal);
                if (secondIndex >= 0)
                    return LocalizationService.Instance.Format("tool.replaceString.multipleMatches", index, secondIndex);

                string newContent = normalizedContent.Substring(0, index)
                    + normalizedNew
                    + normalizedContent.Substring(index + normalizedOld.Length);

                newContent = newContent.Replace("\n", "\r\n");

                await Task.Run(() => File.WriteAllText(filePath, newContent, Encoding.UTF8));
                return LocalizationService.Instance.Format("tool.replaceString.replaced", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.replaceString.failed", ex.Message);
            }
        }
    }
}
