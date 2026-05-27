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
    /// multi_replace_string_in_file 工具 — 批量字符串替换。
    /// 委托给 ReplaceStringInFileTool 逐条执行。
    /// </summary>
    public class MultiReplaceStringInFileTool : BuiltInToolBase
    {
        private static LocalizationService L => LocalizationService.Instance;
        private readonly ReplaceStringInFileTool _singleReplacer = new();

        public override string Name => "multi_replace_string_in_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "multi_replace_string_in_file",
                    Description = L["tool.multi_replace_string_in_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            replacements = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        filePath = new { type = "string", description = "要修改的文件的绝对路径" },
                                        oldString = new { type = "string", description = "要替换的原始文本" },
                                        newString = new { type = "string", description = "替换后的新文本" }
                                    },
                                    required = new[] { "filePath", "oldString", "newString" }
                                },
                                description = "替换操作数组"
                            }
                        },
                        required = new[] { "replacements" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            int count = 0;
            if (args.TryGetValue("replacements", out var repsElement)
                && repsElement.ValueKind == JsonValueKind.Array)
                count = repsElement.GetArrayLength();
            return count > 0
                ? $"✏️ 批量编辑 ({count} 处替换)"
                : "✏️ 批量编辑文件";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                return "✅ 编辑完成";
            return "✏️ 编辑完成";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            if (!args.TryGetValue("replacements", out var element) ||
                element.ValueKind != JsonValueKind.Array)
                return "❌ multi_replace_string_in_file: 缺少 replacements 数组参数";

            var results = new List<string>();
            int successCount = 0;
            int failCount = 0;

            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var singleArgs = new Dictionary<string, JsonElement>();
                foreach (var prop in item.EnumerateObject())
                    singleArgs[prop.Name] = prop.Value;

                string filePath = GetStringArg(singleArgs, "filePath");
                string oldStr = GetStringArg(singleArgs, "oldString");

                if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(oldStr))
                {
                    failCount++;
                    continue;
                }

                string result = await _singleReplacer.ExecuteAsync(singleArgs, workspaceRoot);
                results.Add($"{Path.GetFileName(filePath)}: {result}");
                if (result.StartsWith("✅")) successCount++;
                else failCount++;
            }

            string summary = $"🔧 multi_replace_string_in_file: 成功 {successCount}, 失败 {failCount}";
            return summary + "\n" + string.Join("\n", results);
        }
    }
}
