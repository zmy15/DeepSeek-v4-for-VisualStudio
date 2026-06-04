using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// create_directory 工具 — 递归创建目录（类似 mkdir -p）。
    /// </summary>
    public class CreateDirectoryTool : BuiltInToolBase
    {
        public override string Name => "create_directory";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "create_directory",
                    Description = L["tool.create_directory.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            dirPath = new
                            {
                                type = "string",
                                description = "要创建的目录的绝对路径（Windows 格式，如 C:\\Users\\...\\newfolder）"
                            }
                        },
                        required = new[] { "dirPath" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string mkdirPath = GetStringArg(args, "dirPath");
            string mkdirName = string.IsNullOrEmpty(mkdirPath) ? "?" : Path.GetFileName(mkdirPath);
            return LocalizationService.Instance.Format("tool.createDirectory.displayText", mkdirName);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            return LocalizationService.Instance["tool.createDirectory.complete"];
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string dirPath = GetStringArg(args, "dirPath");

            if (string.IsNullOrEmpty(dirPath))
                return Task.FromResult(LocalizationService.Instance["tool.createDirectory.missingParam"]);

            try
            {
                if (Directory.Exists(dirPath))
                    return Task.FromResult(LocalizationService.Instance.Format("tool.createDirectory.alreadyExists", dirPath));

                Directory.CreateDirectory(dirPath);
                return Task.FromResult(LocalizationService.Instance.Format("tool.createDirectory.created", dirPath));
            }
            catch (Exception ex)
            {
                return Task.FromResult(LocalizationService.Instance.Format("tool.createDirectory.failed", ex.Message));
            }
        }
    }
}
