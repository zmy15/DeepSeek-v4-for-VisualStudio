using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
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
    /// 解析和应用委托给 EditTools.ApplyPatchTool（OpenAI Codex 兼容的 Chunk 重建引擎）。
    /// 当 ApiService 注入后，支持完整的 Healing 流程（降级模型 → 完整模型 → create_file 兜底）。
    /// </summary>
    public class ApplyPatchTool : BuiltInToolBase
    {
        public override string Name => "apply_patch";

        /// <summary>
        /// API 服务引用（可选注入）。设置后启用 AI Healing 修复失败 Hunk。
        /// </summary>
        public DeepSeekApiService? ApiService { get; set; }

        /// <summary>
        /// StagedEditWorkspace 引用（可选注入）。
        /// 设置后，工具循环中的 apply_patch 写入 Workspace 而非磁盘，
        /// 由 Agent 结束后统一提交和 diff 预览。
        /// </summary>
        public Services.Editing.StagedEditWorkspace? Workspace { get; set; }

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
                            },
                            expected = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.editVerify.expectedDescription"]
                            }
                        },
                        required = new[] { "patch", "expected" }
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
            if (toolResult.StartsWith("Error: ") || toolResult.StartsWith("Timeout: ")) return toolResult;
            return LocalizationService.Instance["tool.applyPatch.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string patchText = GetStringArg(args, "patch");
            string expectedText = GetStringArg(args, "expected");

            if (string.IsNullOrEmpty(patchText))
                return LocalizationService.Instance["tool.applyPatch.missingParam"];

            if (string.IsNullOrEmpty(expectedText))
                return LocalizationService.Instance["tool.editVerify.missingExpected"];

            bool expectDeleted = IsDeletedExpectation(expectedText);
            if (!expectDeleted)
            {
                var expectedParse = ParseExpectedLineNumberedContent(expectedText);
                if (!expectedParse.Success)
                    return expectedParse.Error;
            }

            // ── 日志：原始补丁内容 ──
            LogRawPatchContent(patchText);

            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);

            try
            {
                // ── 统一使用 EditTools 版的解析器（OpenAI Codex 兼容，正则前缀检测）──
                var patches = EditTools.ApplyPatchTool.ParsePatches(patchText);

                // ── 日志：解析后的补丁详情 ──
                LogParsedPatches(patches, patchText);

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

                if (patches.Count != 1)
                {
                    return LocalizationService.Instance["tool.editVerify.verificationSingleTarget"];
                }

                var targetPath = GetVerificationTargetPath(patches[0], workspaceRoot);

                // ── 有 ApiService → 使用完整 Healing 流程 ──
                if (ApiService != null)
                {
                    var editTool = new EditTools.ApplyPatchTool(ApiService, workspaceRoot ?? string.Empty)
                    {
                        Workspace = Workspace,
                    };
                    var editResults = await editTool.ExecutePatchesAsync(patches, CancellationToken);

                    var results = new List<string>();
                    for (int i = 0; i < editResults.Count && i < patches.Count; i++)
                    {
                        var result = editResults[i];
                        if (result.Success)
                        {
                            results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied",
                                Path.GetFileName(result.FilePath), patches[i].Hunks.Count));
                        }
                        else
                        {
                            string errorMsg = result.ErrorMessage ?? "Error: " + LocalizationService.Instance["tool.applyPatch.hunkFail"];
                            results.Add(errorMsg);
                        }
                    }

                    string appliedSummary = results.Count > 0
                        ? string.Join("\n", results)
                        : LocalizationService.Instance["tool.applyPatch.noAction"];
                    bool allSucceeded = editResults.Count == patches.Count
                        && editResults.All(result => result.Success);

                    if (!allSucceeded)
                    {
                        return appliedSummary + "\n" +
                            await BuildCurrentStateSnapshotAsync(targetPath);
                    }

                    return await VerifyAppliedResultAsync(
                        expectedText, targetPath, expectDeleted);
                }

                // ── 无 ApiService → 降级到静态 ApplySinglePatch（无 Healing）──
                {
                    var results = new List<string>();
                    var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    bool anyFailed = false;

                    // ── 记录操作前已存在的文件（用于区分"新建"与"修改"，回滚时新建文件应删除而非恢复）──
                    // Move 目的文件同样记录：已存在的目的文件在回滚时不应当被误删。
                    var existedBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in patches)
                    {
                        foreach (var fp in EnumerateRollbackPaths(p, workspaceRoot))
                        {
                            if (File.Exists(fp)) existedBefore.Add(fp);
                        }
                    }

                    try
                    {
                        foreach (var patch in patches)
                        {
                            string filePath = ResolvePath(patch.FilePath, workspaceRoot);

                            // ── Workspace 模式：读取暂存内容，不创建备份 ──
                            string sourceContent;
                            if (Workspace != null)
                            {
                                sourceContent = Workspace.ReadFile(filePath);
                            }
                            else
                            {
                                // ── 首次接触此文件时创建备份 ──
                                if (File.Exists(filePath) && !backups.ContainsKey(filePath))
                                {
                                    backups[filePath] = BackupService.CreateBackup(filePath);
                                }
                                sourceContent = File.Exists(filePath)
                                    ? await Task.Run(() => File.ReadAllText(filePath))
                                    : string.Empty;
                            }

                            var result = EditTools.ApplyPatchTool.ApplySinglePatch(
                                patch, filePath, sourceContent);

                            if (result.Success && !string.IsNullOrEmpty(result.FinalContent))
                            {
                                string normalized = EditStringMatcher.NormalizeToCrLf(result.FinalContent);
                                if (Workspace != null)
                                {
                                    // WriteFile 内部对已打开文档走 buffer+编辑器 Save
                                    Workspace.WriteFile(filePath, normalized);
                                }
                                else
                                {
                                    // 已打开文档 → buffer+编辑器 Save；未打开 → 裸写盘
                                    bool writtenViaBuffer = await EditBufferApplier.TryWriteOpenDocumentAsync(
                                        filePath, normalized);
                                    if (!writtenViaBuffer)
                                        await Task.Run(() => File.WriteAllText(filePath, normalized));
                                }
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied",
                                    Path.GetFileName(filePath), patch.Hunks.Count));
                            }
                            else if (result.Success)
                            {
                                results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied",
                                    Path.GetFileName(filePath), patch.Hunks.Count));
                            }
                            else
                            {
                                string errorMsg = result.ErrorMessage ?? "Error: " + LocalizationService.Instance["tool.applyPatch.hunkFail"];
                                results.Add(errorMsg);
                                anyFailed = true;
                            }
                        }

                        // ── 失败回滚（仅直接写盘模式）──
                        if (anyFailed)
                        {
                            if (Workspace == null)
                            {
                                RollbackStaticPath(patches, workspaceRoot, backups, existedBefore);
                                Logger.Warn("[Backup] 静态降级路径：部分 patch 失败，已回滚所有文件");
                            }
                            return string.Join("\n", results) + "\n" +
                                await BuildCurrentStateSnapshotAsync(targetPath);
                        }
                        if (Workspace == null)
                        {
                            // ── 成功清理 ──
                            foreach (var kv in backups)
                                BackupService.CleanupBackup(kv.Value);
                        }

                        return await VerifyAppliedResultAsync(
                            expectedText, targetPath, expectDeleted);
                    }
                    catch
                    {
                        // ── 异常回滚：与 anyFailed 分支同一实现（P1-2：此前不删新建文件，残留 AI 半成品）──
                        RollbackStaticPath(patches, workspaceRoot, backups, existedBefore);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.applyPatch.failed", ex.Message);
            }
        }

        internal static bool IsDeletedExpectation(string expectedText)
            => ExpectedContentVerifier.IsDeletedExpectation(expectedText);

        /// <summary>
        /// 解析 "1|code"、"2: code"、"3→code" 或 "4. code" 形式的带行号内容。
        /// </summary>
        internal static (bool Success, List<string> Lines, string Error)
            ParseExpectedLineNumberedContent(string expectedText)
            => ExpectedContentVerifier.ParseLineNumberedContent(expectedText);

        internal static string FormatLineNumberedContent(IReadOnlyList<string> lines)
            => ExpectedContentVerifier.FormatLineNumberedContent(lines);

        internal static string VerifyExpectedContent(
            string expectedText,
            string? actualContent,
            string filePath,
            bool expectDeleted)
            => ExpectedContentVerifier.VerifyExpectedContent(
                expectedText, actualContent, filePath, expectDeleted);

        private static string GetVerificationTargetPath(
            PatchOperation patch,
            string? workspaceRoot)
        {
            string targetPath = !string.IsNullOrEmpty(patch.MoveToPath)
                ? patch.MoveToPath
                : patch.FilePath;
            return ResolvePath(targetPath, workspaceRoot);
        }

        private async Task<string> VerifyAppliedResultAsync(
            string expectedText,
            string targetPath,
            bool expectDeleted)
        {
            string? actualContent = await ReadActualContentAsync(targetPath);
            return VerifyExpectedContent(
                expectedText, actualContent, targetPath, expectDeleted);
        }

        private async Task<string> BuildCurrentStateSnapshotAsync(string targetPath)
        {
            string? actualContent = await ReadActualContentAsync(targetPath);
            return ExpectedContentVerifier.BuildCurrentStateMessage(actualContent);
        }

        private async Task<string?> ReadActualContentAsync(string targetPath)
        {
            if (Workspace != null)
            {
                string stagedContent = Workspace.ReadFile(targetPath);
                if (File.Exists(targetPath) || stagedContent.Length > 0)
                    return stagedContent;
                return null;
            }

            return File.Exists(targetPath)
                ? await Task.Run(() => File.ReadAllText(targetPath))
                : null;
        }

        /// <summary>
        /// 静态降级路径的统一回滚：恢复所有备份 + 删除操作前不存在的新建文件（含 Move 目的文件）。
        /// anyFailed 分支与异常分支共用，保证 all-or-nothing 语义一致。
        /// </summary>
        private static void RollbackStaticPath(
            List<PatchOperation> patches, string? workspaceRoot,
            Dictionary<string, string?> backups, HashSet<string> existedBefore)
        {
            foreach (var kv in backups)
                BackupService.RestoreFromBackup(kv.Key, kv.Value);

            // 新建文件：操作前不存在 → 回滚时应删除（Move 目的文件同样纳入）
            foreach (var p in patches)
            {
                foreach (var fp in EnumerateRollbackPaths(p, workspaceRoot))
                {
                    if (!existedBefore.Contains(fp) && File.Exists(fp))
                    {
                        try
                        {
                            File.Delete(fp);
                            Logger.Info($"[Backup] 已回滚新建文件: {fp}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Backup] 回滚新建文件失败: {fp} — {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 枚举一个 patch 操作在回滚时需要检查的路径：源文件 + Move 目的文件（若声明）。
        /// </summary>
        private static IEnumerable<string> EnumerateRollbackPaths(PatchOperation p, string? workspaceRoot)
        {
            yield return ResolvePath(p.FilePath, workspaceRoot);
            if (!string.IsNullOrEmpty(p.MoveToPath))
                yield return ResolvePath(p.MoveToPath, workspaceRoot);
        }

        #region ApplyPatch 日志

        /// <summary>
        /// 记录原始补丁内容（截断过长内容）。
        /// </summary>
        private static void LogRawPatchContent(string patchText)
        {
            Logger.LogToFile("applypatch", $"[ApplyPatch]  原始补丁内容 ({patchText.Length} 字符):\n{patchText}");
        }

        /// <summary>
        /// 记录解析后的补丁详情（每个 Patch 的文件、操作类型、Hunk 数）。
        /// </summary>
        private static void LogParsedPatches(List<PatchOperation> patches, string rawPatchText)
        {
            if (patches.Count == 0)
            {
                Logger.LogToFile("applypatch", $"[ApplyPatch]  未能从补丁文本中解析出任何 Patch 操作。原始文本长度: {rawPatchText.Length}");
                return;
            }

            Logger.LogToFile("applypatch", $"[ApplyPatch]  解析出 {patches.Count} 个 Patch 操作:");
            for (int i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                string actionLabel = p.Action switch
                {
                    Models.PatchFileAction.Add => "Add  File",
                    Models.PatchFileAction.Delete => "Del  File",
                    Models.PatchFileAction.Update => "Update",
                    _ => p.Action.ToString()
                };
                string moveInfo = !string.IsNullOrEmpty(p.MoveToPath) ? $" → {p.MoveToPath}" : "";
                Logger.LogToFile("applypatch", $"[ApplyPatch]   [{i + 1}] {actionLabel}: {p.FilePath}{moveInfo} | {p.Hunks.Count} hunks | raw={p.RawText.Length}chars");
            }
        }

        #endregion
    }
}
