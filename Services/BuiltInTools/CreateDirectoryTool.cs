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
        private static LocalizationService L => LocalizationService.Instance;

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
            return $"📁 创建目录 `{mkdirName}`";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;
            return "📁 目录操作完成";
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string dirPath = GetStringArg(args, "dirPath");

            if (string.IsNullOrEmpty(dirPath))
                return Task.FromResult("❌ create_directory: 缺少 dirPath 参数。请提供 Windows 绝对路径。");

            try
            {
                if (Directory.Exists(dirPath))
                    return Task.FromResult($"📁 目录已存在: {dirPath}");

                Directory.CreateDirectory(dirPath);
                return Task.FromResult($"✅ 已创建目录: {dirPath}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ create_directory 失败: {ex.Message}");
            }
        }
    }
}
