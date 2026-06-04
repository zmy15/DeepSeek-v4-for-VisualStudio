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
                                        filePath = new { type = "string", description = LocalizationService.Instance["tool.multiReplace.param.filePath"] },
                                        oldString = new { type = "string", description = LocalizationService.Instance["tool.multiReplace.param.oldString"] },
                                        newString = new { type = "string", description = LocalizationService.Instance["tool.multiReplace.param.newString"] }
                                    },
                                    required = new[] { "filePath", "oldString", "newString" }
                                },
                                description = LocalizationService.Instance["tool.multiReplace.param.replacements"]
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
                ? LocalizationService.Instance.Format("tool.multiReplace.batchEditCount", count)
                : LocalizationService.Instance["tool.multiReplace.batchEdit"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                return LocalizationService.Instance["tool.multiReplace.editComplete"];
            return LocalizationService.Instance["tool.multiReplace.editDone"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            if (!args.TryGetValue("replacements", out var element) ||
                element.ValueKind != JsonValueKind.Array)
                return LocalizationService.Instance["tool.multiReplace.missingReplacements"];

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

            string summary = $"multi_replace_string_in_file: success {successCount}, fail {failCount}";
            return summary + "\n" + string.Join("\n", results);
        }
    }
}
