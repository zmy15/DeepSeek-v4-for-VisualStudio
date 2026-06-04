using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// list_dir 工具 — 列出目录内容。
    /// </summary>
    public class ListDirTool : BuiltInToolBase
    {
        public override string Name => "list_dir";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "list_dir",
                    Description = L["tool.list_dir.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.listDir.param.dirPath"]
                            }
                        },
                        required = new[] { "path" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string dirPath = GetStringArg(args, "path");
            return string.IsNullOrEmpty(dirPath)
                ? LocalizationService.Instance["tool.listDir.listingDir"]
                : LocalizationService.Instance.Format("tool.listDir.listingDirPath", TruncatePath(dirPath));
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;

            var dirLines = toolResult.Split('\n');
            int dirCount = dirLines.Count(l => l.TrimStart().StartsWith("- 📁"));
            int fileCount = dirLines.Count(l => l.TrimStart().StartsWith("- 📄"));
            return LocalizationService.Instance.Format("tool.listDir.complete", dirCount, fileCount);
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
            string path = GetStringArg(args, "path");

            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                {
                    path = workspaceRoot;
                }
                else
                {
                    return Task.FromResult(LocalizationService.Instance["tool.listDir.missingParam"]
                        + (string.IsNullOrEmpty(workspaceRoot) ? "" : $" 当前工作区: {workspaceRoot}"));
                }
            }

            if (!Directory.Exists(path))
            {
                string suggestion = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? $"\n💡 提示: 当前工作区根目录是 \"{workspaceRoot}\"，请使用此路径或其中的子目录。"
                    : "\n💡 提示: 请使用 Windows 绝对路径格式（如 C:\\Users\\...\\project\\src）。";
                suggestion += "\n💡 如需创建新目录，请使用 create_directory 工具。";
                return Task.FromResult(LocalizationService.Instance.Format("tool.listDir.notFound", path) + suggestion);
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"📁 目录: {path}");
                sb.AppendLine();

                var dirs = Directory.GetDirectories(path);
                if (dirs.Length > 0)
                {
                    sb.AppendLine("### 子目录");
                    foreach (var d in dirs.OrderBy(d => d).Take(100))
                    {
                        string name = Path.GetFileName(d);
                        sb.AppendLine($"- 📁 {name}/");
                    }
                    if (dirs.Length > 100)
                        sb.AppendLine($"... 还有 {dirs.Length - 100} 个子目录");
                    sb.AppendLine();
                }

                var files = Directory.GetFiles(path);
                if (files.Length > 0)
                {
                    sb.AppendLine("### 文件");
                    foreach (var f in files.OrderBy(f => f).Take(100))
                    {
                        string name = Path.GetFileName(f);
                        sb.AppendLine($"- 📄 {name}");
                    }
                    if (files.Length > 100)
                        sb.AppendLine($"... 还有 {files.Length - 100} 个文件");
                }

                if (dirs.Length == 0 && files.Length == 0)
                    sb.AppendLine("（空目录）");

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult(LocalizationService.Instance.Format("tool.listDir.failed", ex.Message));
            }
        }
    }
}
