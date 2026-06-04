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
    /// create_file 工具 — 创建或覆盖文件（自动创建父目录）。
    /// </summary>
    public class CreateFileTool : BuiltInToolBase
    {
        public override string Name => "create_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "create_file",
                    Description = L["tool.create_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string", description = "要创建/覆盖的文件的绝对路径（Windows 格式）" },
                            content = new { type = "string", description = "文件的完整内容" }
                        },
                        required = new[] { "filePath", "content" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string createPath = GetStringArg(args, "filePath");
            string createFile = string.IsNullOrEmpty(createPath) ? "?" : Path.GetFileName(createPath);
            return LocalizationService.Instance.Format("tool.createFile.displayText", createFile);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                return LocalizationService.Instance["tool.createFile.created"];
            return LocalizationService.Instance["tool.createFile.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string content = GetStringArg(args, "content");

            if (string.IsNullOrEmpty(filePath))
                return LocalizationService.Instance["tool.createFile.missingParam"];

            filePath = ResolvePath(filePath, workspaceRoot);

            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string normalizedContent = (content ?? string.Empty)
                    .Replace("\r\n", "\n").Replace("\r", "\n")
                    .Replace("\n", "\r\n");

                if (!string.IsNullOrEmpty(normalizedContent)
                    && !Utils.CodeContentValidator.IsProbablySourceCode(filePath, normalizedContent))
                {
                    string lang = Utils.CodeContentValidator.GetLanguageDescription(filePath);
                    return LocalizationService.Instance.Format("tool.createFile.rejected", Path.GetFileName(filePath), lang);
                }

                bool existed = File.Exists(filePath);
                await Task.Run(() => File.WriteAllText(filePath, normalizedContent, Encoding.UTF8));

                return existed
                    ? LocalizationService.Instance.Format("tool.createFile.overwritten", Path.GetFileName(filePath))
                    : LocalizationService.Instance.Format("tool.createFile.createdNew", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.createFile.failed", ex.Message);
            }
        }
    }
}
