using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// memory 工具 — AI 管理持久化记忆。
    /// 
    /// 支持六个操作：view、create、str_replace、insert、delete、rename。
    /// 三个作用域：user（跨解决方案）、session（当前对话）、repo（当前解决方案）。
    /// </summary>
    public class MemoryTool : BuiltInToolBase
    {
        private readonly IMemoryService _memoryService;
        private readonly Func<string?> _getSessionId;
        private readonly Func<string?> _getSolutionPath;

        public MemoryTool(IMemoryService memoryService, Func<string?> getSessionId, Func<string?> getSolutionPath)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _getSessionId = getSessionId ?? throw new ArgumentNullException(nameof(getSessionId));
            _getSolutionPath = getSolutionPath ?? throw new ArgumentNullException(nameof(getSolutionPath));
        }

        public override string Name => "memory";

        #region Tool Definition

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "memory",
                    Description = L["tool.memory.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new
                            {
                                type = "string",
                                @enum = new[] { "view", "create", "str_replace", "insert", "delete", "rename" },
                                description = L["tool.memory.param.command"]
                            },
                            path = new
                            {
                                type = "string",
                                description = L["tool.memory.param.path"]
                            },
                            file_text = new
                            {
                                type = "string",
                                description = L["tool.memory.param.file_text"]
                            },
                            old_str = new
                            {
                                type = "string",
                                description = L["tool.memory.param.old_str"]
                            },
                            new_str = new
                            {
                                type = "string",
                                description = L["tool.memory.param.new_str"]
                            },
                            insert_line = new
                            {
                                type = "integer",
                                description = L["tool.memory.param.insert_line"]
                            },
                            insert_text = new
                            {
                                type = "string",
                                description = L["tool.memory.param.insert_text"]
                            },
                            old_path = new
                            {
                                type = "string",
                                description = L["tool.memory.param.old_path"]
                            },
                            new_path = new
                            {
                                type = "string",
                                description = L["tool.memory.param.new_path"]
                            },
                            view_range = new
                            {
                                type = "array",
                                items = new { type = "integer" },
                                minItems = 2,
                                maxItems = 2,
                                description = L["tool.memory.param.view_range"]
                            }
                        },
                        required = new[] { "command" }
                    }
                }
            };
        }

        #endregion

        #region Execution

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            try
            {
                string command = GetStringArg(args, "command");
                if (string.IsNullOrEmpty(command))
                    return LocalizationService.Instance["tool.memory.missingCommand"];

                // ── 解析解决方案路径：优先使用 ExecuteAsync 传入的 workspaceRoot，
                //     回退到构造函数注入的 _getSolutionPath（兼容旧路径和测试）──
                string? resolvedSolutionPath = workspaceRoot ?? _getSolutionPath();

                return command switch
                {
                    "view" => await ExecuteViewAsync(args, resolvedSolutionPath),
                    "create" => await ExecuteCreateAsync(args, resolvedSolutionPath),
                    "str_replace" => await ExecuteStrReplaceAsync(args, resolvedSolutionPath),
                    "insert" => await ExecuteInsertAsync(args, resolvedSolutionPath),
                    "delete" => await ExecuteDeleteAsync(args, resolvedSolutionPath),
                    "rename" => await ExecuteRenameAsync(args, resolvedSolutionPath),
                    _ => $"❌ memory: 未知命令 '{command}'。可用命令: view, create, str_replace, insert, delete, rename"
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"[MemoryTool] 执行异常", ex);
                return LocalizationService.Instance.Format("tool.memory.error", ex.Message);
            }
        }

        #endregion

        #region Command Implementations

        private async Task<string> ExecuteViewAsync(Dictionary<string, JsonElement> args, string? solutionPath)
        {
            string rawPath = GetStringArg(args, "path");
            var (scope, path) = ParseMemoryPath(rawPath);

            int? startLine = null, endLine = null;
            if (args.TryGetValue("view_range", out var rangeEl) && rangeEl.ValueKind == JsonValueKind.Array)
            {
                var range = JsonSerializer.Deserialize<int[]>(rangeEl.GetRawText());
                if (range?.Length == 2)
                {
                    startLine = range[0];
                    endLine = range[1];
                }
            }

            var result = await _memoryService.ViewAsync(scope, path, _getSessionId(), solutionPath, startLine, endLine);

            if (result.IsDirectoryListing && result.Entries != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine(LocalizationService.Instance.Format("tool.memory.dirInfo", result.Path, scope));
                sb.AppendLine();
                foreach (var entry in result.Entries)
                {
                    if (entry.IsDirectory)
                        sb.AppendLine($"  📁 {entry.Name}");
                    else
                        sb.AppendLine($"  📄 {entry.Name} ({FormatSize(entry.SizeBytes)}, {entry.LastModified:yyyy-MM-dd HH:mm})");
                }
                if (result.Entries.Count == 0)
                    sb.AppendLine("  （空目录）");
                return sb.ToString();
            }

            // 文件内容
            var header = result.TotalLines.HasValue
                ? LocalizationService.Instance.Format("tool.memory.fileInfoRange", result.Path, scope, result.ViewStartLine, result.ViewEndLine, result.TotalLines)
                : LocalizationService.Instance.Format("tool.memory.fileInfo", result.Path, scope);

            return header + "\n\n" + (result.Content ?? LocalizationService.Instance["tool.memory.empty"]);
        }

        private async Task<string> ExecuteCreateAsync(Dictionary<string, JsonElement> args, string? solutionPath)
        {
            string rawPath = GetStringArg(args, "path");
            string content = GetStringArg(args, "file_text");

            if (string.IsNullOrEmpty(rawPath))
                return LocalizationService.Instance["tool.memory.createMissingPath"];
            if (content == null)
                return LocalizationService.Instance["tool.memory.createMissingText"];

            var (scope, path) = ParseMemoryPath(rawPath);
            return await _memoryService.CreateAsync(scope, path, content, _getSessionId(), solutionPath);
        }

        private async Task<string> ExecuteStrReplaceAsync(Dictionary<string, JsonElement> args, string? solutionPath)
        {
            string rawPath = GetStringArg(args, "path");
            string oldStr = GetStringArg(args, "old_str");
            string newStr = GetStringArg(args, "new_str");

            if (string.IsNullOrEmpty(rawPath))
                return LocalizationService.Instance["tool.memory.strReplaceMissingPath"];
            if (oldStr == null)
                return LocalizationService.Instance["tool.memory.strReplaceMissingOld"];
            if (newStr == null)
                return LocalizationService.Instance["tool.memory.strReplaceMissingNew"];

            var (scope, path) = ParseMemoryPath(rawPath);
            return await _memoryService.StrReplaceAsync(scope, path, oldStr, newStr, _getSessionId(), solutionPath);
        }

        private async Task<string> ExecuteInsertAsync(Dictionary<string, JsonElement> args, string? solutionPath)
        {
            string rawPath = GetStringArg(args, "path");
            string text = GetStringArg(args, "insert_text");

            if (string.IsNullOrEmpty(rawPath))
                return LocalizationService.Instance["tool.memory.insertMissingPath"];
            if (text == null)
                return LocalizationService.Instance["tool.memory.insertMissingText"];

            int lineNumber = 0;
            if (args.TryGetValue("insert_line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number)
                lineNumber = lineEl.GetInt32();

            var (scope, path) = ParseMemoryPath(rawPath);
            return await _memoryService.InsertAsync(scope, path, lineNumber, text, _getSessionId(), solutionPath);
        }

        private async Task<string> ExecuteDeleteAsync(Dictionary<string, JsonElement> args, string? solutionPath)
        {
            string rawPath = GetStringArg(args, "path");
            if (string.IsNullOrEmpty(rawPath))
                return LocalizationService.Instance["tool.memory.deleteMissingPath"];

            var (scope, path) = ParseMemoryPath(rawPath);
            return await _memoryService.DeleteAsync(scope, path, _getSessionId(), solutionPath);
        }

        private async Task<string> ExecuteRenameAsync(Dictionary<string, JsonElement> args, string? solutionPath)
        {
            string rawOldPath = GetStringArg(args, "old_path");
            string rawNewPath = GetStringArg(args, "new_path");

            if (string.IsNullOrEmpty(rawOldPath))
                return LocalizationService.Instance["tool.memory.renameMissingOld"];
            if (string.IsNullOrEmpty(rawNewPath))
                return LocalizationService.Instance["tool.memory.renameMissingNew"];

            var (oldScope, oldPath) = ParseMemoryPath(rawOldPath);
            var (newScope, newPath) = ParseMemoryPath(rawNewPath);

            if (oldScope != newScope)
                return LocalizationService.Instance["tool.memory.renameCrossScope"];

            return await _memoryService.RenameAsync(oldScope, oldPath, newPath, _getSessionId(), solutionPath);
        }

        #endregion

        #region Display & Summary

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string command = GetStringArg(args, "command");
            string path = GetStringArg(args, "path") ?? GetStringArg(args, "old_path") ?? "?";

            return command switch
            {
                "view" => LocalizationService.Instance.Format("tool.memory.viewMemory", path),
                "create" => LocalizationService.Instance.Format("tool.memory.createMemory", path),
                "str_replace" => LocalizationService.Instance.Format("tool.memory.editMemory", path),
                "insert" => LocalizationService.Instance.Format("tool.memory.insertMemory", path),
                "delete" => LocalizationService.Instance.Format("tool.memory.deleteMemory", path),
                "rename" => LocalizationService.Instance.Format("tool.memory.renameMemory", path),
                _ => LocalizationService.Instance.Format("tool.memory.operation", command)
            };
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            // 截取结果的前80字符作为摘要
            return toolResult.Length > 80
                ? toolResult.Substring(0, 80) + "..."
                : toolResult;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 解析记忆路径格式：/memories/...  → user, /memories/session/... → session, /memories/repo/... → repo
        /// </summary>
        private static (MemoryScope scope, string path) ParseMemoryPath(string rawPath)
        {
            rawPath = rawPath?.Trim() ?? string.Empty;

            if (rawPath.StartsWith("/memories/session/", StringComparison.OrdinalIgnoreCase))
                return (MemoryScope.Session, rawPath.Substring("/memories/session/".Length).TrimStart('/'));

            if (rawPath.StartsWith("/memories/session", StringComparison.OrdinalIgnoreCase))
                return (MemoryScope.Session, rawPath.Substring("/memories/session".Length).TrimStart('/'));

            if (rawPath.StartsWith("/memories/repo/", StringComparison.OrdinalIgnoreCase))
                return (MemoryScope.Repo, rawPath.Substring("/memories/repo/".Length).TrimStart('/'));

            if (rawPath.StartsWith("/memories/repo", StringComparison.OrdinalIgnoreCase))
                return (MemoryScope.Repo, rawPath.Substring("/memories/repo".Length).TrimStart('/'));

            if (rawPath.StartsWith("/memories/", StringComparison.OrdinalIgnoreCase))
                return (MemoryScope.User, rawPath.Substring("/memories/".Length).TrimStart('/'));

            if (rawPath.StartsWith("/memories", StringComparison.OrdinalIgnoreCase))
                return (MemoryScope.User, rawPath.Substring("/memories".Length).TrimStart('/'));

            // 默认视为 user 作用域
            return (MemoryScope.User, rawPath.TrimStart('/'));
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        #endregion
    }
}
