using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
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
        /// <summary>
        /// StagedEditWorkspace 引用（可选注入）。
        /// 设置后，create_file 写入 Workspace 而非磁盘（由 Agent 结束统一提交）。
        /// </summary>
        public Services.Editing.StagedEditWorkspace? Workspace { get; set; }

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
                            filePath = new { type = "string", description = LocalizationService.Instance["tool.createFile.param.filePath"] },
                            content = new { type = "string", description = LocalizationService.Instance["tool.createFile.param.content"] }
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
            return LocalizationService.Instance.Format("tool.createFile.displayText", createFile);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            if (toolResult.Contains("成功") || toolResult.Contains("success"))
                return LocalizationService.Instance["tool.createFile.created"];
            return LocalizationService.Instance["tool.createFile.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string content = GetStringArg(args, "content");

            if (string.IsNullOrEmpty(filePath))
                return LocalizationService.Instance["tool.createFile.missingParam"];

            filePath = ResolvePath(filePath, workspaceRoot);

            bool existedBefore = File.Exists(filePath);
            // ── 备份路径上提到 try 外：失败路径（含覆盖场景）需要用它回滚 ──
            string? backupPath = null;

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
                    return LocalizationService.Instance.Format("tool.createFile.rejected", Path.GetFileName(filePath), lang);
                }

                bool existed = File.Exists(filePath);

                // ── Workspace 模式：写入 Workspace，不创建备份 ──
                if (Workspace != null)
                {
                    Workspace.WriteFile(filePath, normalizedContent);
                    return existed
                        ? LocalizationService.Instance.Format("tool.createFile.overwritten", Path.GetFileName(filePath))
                        : LocalizationService.Instance.Format("tool.createFile.createdNew", Path.GetFileName(filePath));
                }

                // ── 覆盖已存在文件前创建备份 ──
                if (existed)
                {
                    backupPath = BackupService.CreateBackup(filePath);
                }

                // ── 覆盖已打开文档 → buffer+编辑器 Save；未打开（含新建）→ 裸写盘 ──
                bool writtenViaBuffer = await EditBufferApplier.TryWriteOpenDocumentAsync(
                    filePath, normalizedContent);
                if (!writtenViaBuffer)
                    await Task.Run(() => File.WriteAllText(filePath, normalizedContent, Encoding.UTF8));

                // ── 写入成功 → 清理备份 ──
                if (backupPath != null)
                    BackupService.CleanupBackup(backupPath);

                return existed
                    ? LocalizationService.Instance.Format("tool.createFile.overwritten", Path.GetFileName(filePath))
                    : LocalizationService.Instance.Format("tool.createFile.createdNew", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                // ── 覆盖失败：文件可能已被部分写坏，回滚到备份（P1-1：此前的备份既不恢复又泄漏）──
                if (existedBefore && backupPath != null)
                {
                    BackupService.RestoreFromBackup(filePath, backupPath);
                }
                // ── 新建文件写入失败时清理残留（回滚到"不存在"状态）──
                else if (!existedBefore && File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        Logger.Warn($"[CreateFile] 写入失败，已清理新建文件: {filePath}");
                    }
                    catch { }
                }
                return LocalizationService.Instance.Format("tool.createFile.failed", ex.Message);
            }
        }
    }
}
