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
    /// delete_file 工具 — 删除指定文件。
    /// 审批检查在 BaseAgent.ExecuteToolAsync 中完成，此方法仅执行实际删除。
    /// </summary>
    public class DeleteFileTool : BuiltInToolBase
    {
        private static LocalizationService L => LocalizationService.Instance;

        public override string Name => "delete_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "delete_file",
                    Description = L["tool.delete_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string", description = "要删除的文件的绝对路径（Windows 格式）" },
                            explanation = new { type = "string", description = "删除原因的简短说明" }
                        },
                        required = new[] { "filePath" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string deletePath = GetStringArg(args, "filePath");
            string deleteFile = string.IsNullOrEmpty(deletePath) ? "?" : Path.GetFileName(deletePath);
            return $"🗑️ 删除文件 `{deleteFile}`";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;
            return "🗑️ 文件已删除";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");

            if (string.IsNullOrEmpty(filePath))
                return "❌ delete_file: 缺少 filePath 参数";

            filePath = ResolvePath(filePath, workspaceRoot);

            try
            {
                if (!File.Exists(filePath))
                    return $"⚠️ 文件不存在: {Path.GetFileName(filePath)}";

                string fileName = Path.GetFileName(filePath);
                await Task.Run(() => File.Delete(filePath));

                return $"✅ 已删除文件: {fileName}";
            }
            catch (Exception ex)
            {
                return $"❌ delete_file 失败: {ex.Message}";
            }
        }
    }
}
