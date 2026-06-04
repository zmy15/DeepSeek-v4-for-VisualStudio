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
                                description = LocalizationService.Instance["tool.applyPatch.description"]
                            }
                        },
                        required = new[] { "patch" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            return LocalizationService.Instance["tool.applyPatch.displayText"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌") || toolResult.StartsWith("⚠️")) return toolResult;
            return LocalizationService.Instance["tool.applyPatch.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string patchText = GetStringArg(args, "patch");

            if (string.IsNullOrEmpty(patchText))
                return LocalizationService.Instance["tool.applyPatch.missingParam"];

            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);

            try
            {
                var results = new List<string>();
                var patches = PatchParser.ParseBlocks(patchText);

                if (patches.Count == 0)
                {
                    return LocalizationService.Instance["tool.applyPatch.noPatchBlock"] + "\n"
                        + LocalizationService.Instance["tool.applyPatch.formatHint"] + "\n"
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
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.deleted", Path.GetFileName(filePath)));
                            }
                            else
                            {
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.skipNotExist", Path.GetFileName(filePath)));
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
                            results.Add(LocalizationService.Instance.Format("tool.applyPatch.created", Path.GetFileName(filePath), newContent.Split('\n').Length));
                            break;

                        case "update file":
                        case "update":
                        default:
                            if (!File.Exists(filePath))
                            {
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.fileNotExist", filePath) + "\n" + LocalizationService.Instance["tool.applyPatch.createHint"]);
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
                                    results.Add(LocalizationService.Instance.Format("tool.applyPatch.hunkFail", string.Join(", ", hunk.ContextLines.Take(2)), Path.GetFileName(filePath)));
                                }
                            }

                            if (anyApplied)
                            {
                                string finalContent = string.Join(Environment.NewLine, newLines);
                                await Task.Run(() => File.WriteAllText(filePath, finalContent));
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied", Path.GetFileName(filePath), patch.Hunks.Count));
                            }
                            else if (results.All(r => !r.StartsWith("✅") && !r.StartsWith("⚠️")))
                            {
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.allHunksFailed", Path.GetFileName(filePath)) + "\n" + LocalizationService.Instance["tool.applyPatch.allHunksFailedHint"]);
                            }
                            break;
                    }
                }

                return results.Count > 0
                    ? string.Join("\n", results)
                    : LocalizationService.Instance["tool.applyPatch.noAction"];
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.applyPatch.failed", ex.Message);
            }
        }
    }
}
