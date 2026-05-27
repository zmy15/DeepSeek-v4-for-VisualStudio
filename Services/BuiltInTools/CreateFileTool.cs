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
        private static LocalizationService L => LocalizationService.Instance;

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
            return $"📝 创建文件 `{createFile}`";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                return "✅ 文件已创建";
            return "📝 文件操作完成";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string content = GetStringArg(args, "content");

            if (string.IsNullOrEmpty(filePath))
                return "❌ create_file: 缺少 filePath 参数";

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
                    return $"❌ create_file 被拒绝: `{Path.GetFileName(filePath)}` 的内容不像是合法的 {lang} 源代码。" +
                        "\n请写入实际可编译的代码，严禁用自然语言描述、TODO 注释、文档摘要或功能说明替代代码。";
                }

                bool existed = File.Exists(filePath);
                await Task.Run(() => File.WriteAllText(filePath, normalizedContent, Encoding.UTF8));

                return existed
                    ? $"✅ 已覆盖文件: {Path.GetFileName(filePath)}"
                    : $"✅ 已创建文件: {Path.GetFileName(filePath)}";
            }
            catch (Exception ex)
            {
                return $"❌ create_file 失败: {ex.Message}";
            }
        }
    }
}
