using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// apply_patch 工具 — 应用 *** Begin Patch / *** End Patch 格式的补丁。
    /// </summary>
    public class ApplyPatchTool : BuiltInToolBase
    {
        private static LocalizationService L => LocalizationService.Instance;

        public override string Name => "apply_patch";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "apply_patch",
                    Description = L["tool.apply_patch.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            patch = new
                            {
                                type = "string",
                                description = "补丁文本，使用 *** Begin Patch / *** End Patch 格式。每行前缀：空格=上下文、-=删除行、+=新增行。@@ 用于定位（类名/函数名等）。"
                            }
                        },
                        required = new[] { "patch" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            return "🔧 应用补丁";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌") || toolResult.StartsWith("⚠️")) return toolResult;
            return "🔧 补丁应用完成";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string patchText = GetStringArg(args, "patch");

            if (string.IsNullOrEmpty(patchText))
                return "❌ apply_patch: 缺少 patch 参数。请提供 *** Begin Patch / *** End Patch 格式的补丁文本。";

            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);

            try
            {
                var results = new List<string>();
                var patches = PatchParser.ParseBlocks(patchText);

                if (patches.Count == 0)
                {
                    return "⚠️ apply_patch: 未检测到 *** Begin Patch / *** End Patch 块。\n"
                        + "请使用正确格式：\n"
                        + "*** Begin Patch\n"
                        + "*** Update File: /path/to/file\n"
                        + "@@ some context\n"
                        + " context line\n"
                        + "- old line to remove\n"
                        + "+ new line to add\n"
                        + " context line\n"
                        + "*** End Patch";
                }

                foreach (var patch in patches)
                {
                    string filePath = ResolvePath(patch.FilePath, workspaceRoot);

                    switch (patch.Operation.ToLowerInvariant())
                    {
                        case "delete file":
                        case "delete":
                            if (File.Exists(filePath))
                            {
                                await Task.Run(() => File.Delete(filePath));
                                results.Add($"✅ 已删除: {Path.GetFileName(filePath)}");
                            }
                            else
                            {
                                results.Add($"⚠️ 文件不存在，跳过删除: {Path.GetFileName(filePath)}");
                            }
                            break;

                        case "add file":
                        case "add":
                            string newContent = string.Join(Environment.NewLine,
                                patch.Hunks.SelectMany(h => h.AddLines));
                            string? newFileDir = Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrEmpty(newFileDir) && !Directory.Exists(newFileDir))
                                Directory.CreateDirectory(newFileDir);
                            await Task.Run(() => File.WriteAllText(filePath, newContent));
                            results.Add($"✅ 已创建: {Path.GetFileName(filePath)} ({newContent.Split('\n').Length} 行)");
                            break;

                        case "update file":
                        case "update":
                        default:
                            if (!File.Exists(filePath))
                            {
                                results.Add($"❌ 文件不存在: {filePath}\n💡 如需创建新文件，请使用 Add File 操作或 create_file 工具。");
                                break;
                            }

                            string originalContent = await Task.Run(() => File.ReadAllText(filePath));
                            string[] originalLines = originalContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                            string[] newLines = (string[])originalLines.Clone();
                            bool anyApplied = false;

                            foreach (var hunk in patch.Hunks)
                            {
                                int matchStart = PatchParser.FindContextMatch(originalLines, hunk.ContextLines, hunk.RemoveLines);
                                if (matchStart < 0)
                                    matchStart = PatchParser.FindLooseContextMatch(originalLines, hunk.ContextLines);
                                if (matchStart < 0 && !string.IsNullOrEmpty(hunk.ContextMarker))
                                    matchStart = PatchParser.FindContextByMarker(originalLines, hunk.ContextMarker);

                                if (matchStart >= 0)
                                {
                                    var updatedLines = new List<string>();
                                    for (int i = 0; i < matchStart; i++)
                                        updatedLines.Add(newLines[i]);
                                    foreach (var hunkLine in hunk.AllLines)
                                    {
                                        if (hunkLine.Type == PatchLineType.Context || hunkLine.Type == PatchLineType.Add)
                                            updatedLines.Add(hunkLine.Text);
                                    }
                                    int afterHunkStart = matchStart + hunk.ContextLines.Count;
                                    if (afterHunkStart < 0) afterHunkStart = 0;
                                    for (int i = afterHunkStart; i < newLines.Length; i++)
                                        updatedLines.Add(newLines[i]);

                                    newLines = updatedLines.ToArray();
                                    anyApplied = true;
                                }
                                else
                                {
                                    results.Add($"⚠️ 无法匹配 hunk (上下文: {string.Join(", ", hunk.ContextLines.Take(2))}...) → 文件: {Path.GetFileName(filePath)}");
                                }
                            }

                            if (anyApplied)
                            {
                                string finalContent = string.Join(Environment.NewLine, newLines);
                                await Task.Run(() => File.WriteAllText(filePath, finalContent));
                                results.Add($"✅ 已应用补丁: {Path.GetFileName(filePath)} ({patch.Hunks.Count} 个 hunk)");
                            }
                            else if (results.All(r => !r.StartsWith("✅") && !r.StartsWith("⚠️")))
                            {
                                results.Add($"❌ 补丁应用失败: {Path.GetFileName(filePath)} — 无法匹配任何 hunk 的上下文。\n💡 请使用 replace_string_in_file 工具进行精确替换，或使用 create_file 工具重写整个文件。");
                            }
                            break;
                    }
                }

                return results.Count > 0
                    ? string.Join("\n", results)
                    : "⚠️ apply_patch: 未执行任何操作";
            }
            catch (Exception ex)
            {
                return $"❌ apply_patch 失败: {ex.Message}";
            }
        }
    }
}
