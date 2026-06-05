using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// git 工具 — 支持常用 Git 操作（status/diff/log/add/commit/branch/checkout/pull/push/stash/reset）。
    /// 启动时自动检测 git 是否安装，写操作通过 BaseAgent 审批流程控制。
    /// </summary>
    public class GitTool : BuiltInToolBase
    {
        // ═══════════════════════════════════════════════════════════════
        // Git 安装检测（进程级缓存，首次调用时检测一次）
        // ═══════════════════════════════════════════════════════════════

        private static readonly Lazy<(bool Available, string Version)> _gitDetection =
            new(() => DetectGitInstallation());

        /// <summary>git 是否已安装</summary>
        public static bool IsGitAvailable => _gitDetection.Value.Available;

        /// <summary>git 版本字符串</summary>
        public static string GitVersion => _gitDetection.Value.Version;

        private static (bool, string) DetectGitInstallation()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    Logger.Info($"[GitTool] git 已安装: {output}");
                    return (true, output);
                }

                Logger.Warn("[GitTool] git 未安装或不可用");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[GitTool] git 检测失败: {ex.Message}");
                return (false, string.Empty);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 操作分类常量
        // ═══════════════════════════════════════════════════════════════

        /// <summary>只读操作 — 自动放行，无需审批</summary>
        private static readonly HashSet<string> ReadOnlyOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "diff", "log",
        };

        /// <summary>写操作 — 需要审批</summary>
        private static readonly HashSet<string> WriteOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "add", "commit", "branch", "checkout", "pull", "stash", "reset",
        };

        /// <summary>危险操作 — 需要审批 + 额外警告</summary>
        private static readonly HashSet<string> DangerousOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "push",
        };

        /// <summary>所有有效操作</summary>
        private static readonly HashSet<string> AllOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "diff", "log", "add", "commit", "branch",
            "checkout", "pull", "push", "stash", "reset",
        };

        /// <summary>同步模式超时</summary>
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 当前调用 Agent 类型（由 BaseAgent 在执行前设置，用于运行时权限校验）。
        /// ExploreAgent 只能执行只读操作。
        /// </summary>
        public static AgentType? CurrentAgentType { get; set; }

        public override string Name => "git";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "git",
                    Description = L["tool.git.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            operation = new
                            {
                                type = "string",
                                description = L["tool.git.param.operation"],
                                @enum = new[]
                                {
                                    "status", "diff", "log", "add", "commit",
                                    "branch", "checkout", "pull", "push", "stash", "reset"
                                }
                            },
                            path = new { type = "string", description = L["tool.git.param.path"] },
                            message = new { type = "string", description = L["tool.git.param.message"] },
                            branch = new { type = "string", description = L["tool.git.param.branch"] },
                            files = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = L["tool.git.param.files"]
                            },
                            staged = new { type = "boolean", description = L["tool.git.param.staged"] },
                            count = new { type = "integer", description = L["tool.git.param.count"] },
                            oneline = new { type = "boolean", description = L["tool.git.param.oneline"] },
                            delete = new { type = "boolean", description = L["tool.git.param.delete"] },
                            force = new { type = "boolean", description = L["tool.git.param.force"] },
                            remote = new { type = "string", description = L["tool.git.param.remote"] },
                            mode = new { type = "string", description = L["tool.git.param.mode"] },
                            purpose = new { type = "string", description = L["tool.git.param.purpose"] },
                        },
                        required = new[] { "operation" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string operation = GetStringArg(args, "operation");
            string desc = operation.ToLowerInvariant() switch
            {
                "status" => L["tool.git.displayStatus"],
                "diff" => L["tool.git.displayDiff"],
                "log" => L["tool.git.displayLog"],
                "add" => L["tool.git.displayAdd"],
                "commit" => L["tool.git.displayCommit"],
                "branch" => L["tool.git.displayBranch"],
                "checkout" => L["tool.git.displayCheckout"],
                "pull" => L["tool.git.displayPull"],
                "push" => L["tool.git.displayPush"],
                "stash" => L["tool.git.displayStash"],
                "reset" => L["tool.git.displayReset"],
                _ => $"git {operation}",
            };
            return desc;
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return L["tool.common.noResult"];
            if (toolResult.StartsWith("❌") || toolResult.StartsWith("⛔")) return toolResult;
            if (toolResult.Contains("exit code: 0"))
                return L["tool.git.success"];
            return L["tool.git.executed"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string operation = GetStringArg(args, "operation").ToLowerInvariant().Trim();

            // ── 参数验证 ──
            if (string.IsNullOrEmpty(operation))
                return L["tool.git.missingOperation"];

            if (!AllOps.Contains(operation))
                return string.Format(L["tool.git.unknownOperation"], operation);

            // ── git 安装检测 ──
            if (!IsGitAvailable)
                return L["tool.git.notInstalled"];

            // ── 工作目录检测（查找 .git 目录）──
            string? workingDir = NormalizeWorkspaceRoot(workspaceRoot);
            string? gitDir = FindGitDir(workingDir);
            if (gitDir == null)
                return L["tool.git.noRepo"];

            // ── 运行时 Agent 权限校验 ──
            // ExploreAgent 只能执行只读操作；EditAgent/BuildAgent 无限制
            if (CurrentAgentType == AgentType.Explore)
            {
                bool isReadOnly = ReadOnlyOps.Contains(operation)
                    // branch 无参数（list）→ 只读
                    || (operation == "branch" && string.IsNullOrEmpty(GetStringArg(args, "branch")) && !GetBoolArg(args, "delete"))
                    // stash mode=list → 只读
                    || (operation == "stash" && string.Equals(GetStringArg(args, "mode"), "list", StringComparison.OrdinalIgnoreCase))
                    // reset + path（unstage）→ 只读
                    || (operation == "reset" && !string.IsNullOrEmpty(GetStringArg(args, "path")));

                if (!isReadOnly)
                {
                    Logger.Warn($"[GitTool] ExploreAgent 尝试执行写操作被拒绝: git {operation}");
                    return string.Format(L["tool.git.agentBlocked"], operation, "Explore");
                }
            }

            // ── 构建 git 命令行 ──
            string gitCommand = BuildGitCommand(operation, args, gitDir);
            if (gitCommand.StartsWith("⛔"))
                return gitCommand; // 被硬拒绝的操作

            // ── 执行 git 命令 ──
            try
            {
                return await RunGitCommandAsync(gitCommand, gitDir);
            }
            catch (Exception ex)
            {
                Logger.Error($"[GitTool] git {operation} 执行异常: {ex.Message}", ex);
                return string.Format(L["tool.git.failed"], ex.Message);
            }
        }

        #region Git Command Builder

        /// <summary>
        /// 根据操作类型和参数构建安全的 git 命令。
        /// 返回以 "⛔" 开头的字符串表示操作被硬拒绝。
        /// </summary>
        private string BuildGitCommand(string operation, Dictionary<string, JsonElement> args, string repoDir)
        {
            switch (operation)
            {
                case "status":
                    {
                        string path = GetStringArg(args, "path");
                        return string.IsNullOrEmpty(path) ? "status --porcelain" : $"status --porcelain -- \"{EscapeArg(path)}\"";
                    }

                case "diff":
                    {
                        bool staged = GetBoolArg(args, "staged");
                        string path = GetStringArg(args, "path");
                        var sb = new StringBuilder("diff");
                        if (staged) sb.Append(" --staged");
                        if (!string.IsNullOrEmpty(path)) sb.Append($" -- \"{EscapeArg(path)}\"");
                        return sb.ToString();
                    }

                case "log":
                    {
                        int count = GetIntArg(args, "count", 10);
                        bool oneline = GetBoolArg(args, "oneline");
                        string path = GetStringArg(args, "path");
                        var sb = new StringBuilder("log");
                        if (oneline) sb.Append(" --oneline");
                        int clamped = count < 1 ? 1 : (count > 50 ? 50 : count);
                        sb.Append($" -{clamped}");
                        if (!string.IsNullOrEmpty(path)) sb.Append($" -- \"{EscapeArg(path)}\"");
                        return sb.ToString();
                    }

                case "add":
                    {
                        var files = GetStringArrayArg(args, "files");
                        if (files == null || files.Length == 0)
                            return "add .";
                        return $"add -- {string.Join(" ", Array.ConvertAll(files, EscapeArg))}";
                    }

                case "commit":
                    {
                        string message = GetStringArg(args, "message");
                        if (string.IsNullOrWhiteSpace(message))
                            return "⛔ " + L["tool.git.commitNoMessage"];
                        var files = GetStringArrayArg(args, "files");
                        string escapedMsg = EscapeArg(message);
                        if (files == null || files.Length == 0)
                            return $"commit -m \"{escapedMsg}\"";
                        return $"commit -m \"{escapedMsg}\" -- {string.Join(" ", Array.ConvertAll(files, EscapeArg))}";
                    }

                case "branch":
                    {
                        string branch = GetStringArg(args, "branch");
                        bool delete = GetBoolArg(args, "delete");
                        bool force = GetBoolArg(args, "force");

                        // 硬拒绝：强制删除分支
                        if (delete && force)
                            return $"⛔ " + L["tool.git.branchForceDeleteBlocked"];

                        if (delete)
                            return string.IsNullOrEmpty(branch)
                                ? "branch --list"
                                : $"branch -d \"{EscapeArg(branch)}\"";

                        if (!string.IsNullOrEmpty(branch))
                            return $"branch \"{EscapeArg(branch)}\"";

                        return "branch --list";
                    }

                case "checkout":
                    {
                        string branch = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(branch))
                            return "⛔ " + L["tool.git.checkoutNoBranch"];
                        return $"checkout \"{EscapeArg(branch)}\"";
                    }

                case "pull":
                    {
                        string remote = GetStringArg(args, "remote");
                        string branch = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(remote)) remote = "origin";
                        return string.IsNullOrEmpty(branch)
                            ? $"pull {EscapeArg(remote)}"
                            : $"pull {EscapeArg(remote)} \"{EscapeArg(branch)}\"";
                    }

                case "push":
                    {
                        string remote = GetStringArg(args, "remote");
                        string branch = GetStringArg(args, "branch");
                        bool force = GetBoolArg(args, "force");
                        if (string.IsNullOrEmpty(remote)) remote = "origin";

                        // 硬拒绝：force push 到 main/master
                        if (force && !string.IsNullOrEmpty(branch))
                        {
                            string lower = branch.ToLowerInvariant();
                            if (lower == "main" || lower == "master")
                                return $"⛔ " + L["tool.git.pushForceMainBlocked"];
                        }
                        // 硬拒绝：任何 force push
                        if (force)
                            return $"⛔ " + L["tool.git.pushForceBlocked"];

                        return string.IsNullOrEmpty(branch)
                            ? $"push {EscapeArg(remote)}"
                            : $"push {EscapeArg(remote)} \"{EscapeArg(branch)}\"";
                    }

                case "stash":
                    {
                        string mode = GetStringArg(args, "mode").ToLowerInvariant().Trim();
                        string message = GetStringArg(args, "message");
                        return mode switch
                        {
                            "pop" => "stash pop",
                            "list" => "stash list",
                            "apply" => "stash apply",
                            "drop" => "stash drop",
                            _ => string.IsNullOrEmpty(message)
                                ? "stash push"
                                : $"stash push -m \"{EscapeArg(message)}\"",
                        };
                    }

                case "reset":
                    {
                        string path = GetStringArg(args, "path");
                        string mode = GetStringArg(args, "mode").ToLowerInvariant().Trim();

                        // 硬拒绝：reset --hard
                        if (mode == "hard")
                            return $"⛔ " + L["tool.git.resetHardBlocked"];

                        // 如果指定了 path，为 unstage 操作（reset HEAD <path>）
                        if (!string.IsNullOrEmpty(path))
                            return $"reset HEAD -- \"{EscapeArg(path)}\"";

                        // 否则为模式 reset
                        string resetMode = mode switch
                        {
                            "soft" => "--soft",
                            "mixed" => "--mixed",
                            _ => "--mixed",
                        };
                        return $"reset {resetMode} HEAD~1";
                    }

                default:
                    return $"⛔ Unknown operation: {operation}";
            }
        }

        #endregion

        #region Git Execution

        /// <summary>
        /// 执行 git 命令并返回格式化输出。
        /// </summary>
        private async Task<string> RunGitCommandAsync(string gitArgs, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = gitArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 超时保护
            var timeoutTask = Task.Delay(SyncTimeout);
            var readTask = Task.WhenAll(stdoutTask, stderrTask);
            var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

            if (completed == timeoutTask)
            {
                try { process.Kill(); } catch { }
                process.Dispose();
                return $"⏱️ " + string.Format(L["tool.git.timeout"], SyncTimeout.TotalSeconds);
            }

            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            int exitCode = process.ExitCode;
            string stdout = stdoutTask.Result;
            string stderr = stderrTask.Result;

            var sb = new StringBuilder();
            sb.AppendLine($"📟 git 输出 (退出码: {exitCode}):");
            if (!string.IsNullOrWhiteSpace(stdout))
                sb.AppendLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                // git 常将信息性消息（如 "Switched to branch"）输出到 stderr
                if (exitCode == 0)
                    sb.AppendLine(stderr.TrimEnd());
                else
                {
                    sb.AppendLine("--- STDERR ---");
                    sb.AppendLine(stderr.TrimEnd());
                }
            }

            string result = sb.ToString().TrimEnd();

            // 截断过长输出
            const int maxOutput = 60000;
            if (result.Length > maxOutput)
                result = result.Substring(0, maxOutput) + $"\n\n...(截断，总输出 {result.Length} 字符)";

            return string.IsNullOrWhiteSpace(result)
                ? L["tool.git.noOutput"]
                : result;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 查找工作目录或其父目录中的 .git 目录。
        /// </summary>
        private static string? FindGitDir(string? startDir)
        {
            if (string.IsNullOrEmpty(startDir))
                return null;

            try
            {
                string? current = Path.GetFullPath(startDir);
                while (current != null)
                {
                    string gitPath = Path.Combine(current, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                        return current;

                    string? parent = Path.GetDirectoryName(current);
                    if (parent == current) break; // 到达文件系统根
                    current = parent;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 转义命令行参数中的特殊字符。
        /// </summary>
        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "\"\"";
            // 如果参数不含空格或特殊字符，直接返回
            if (!arg.Contains(" ") && !arg.Contains("\"") && !arg.Contains("\\"))
                return arg;
            // 转义双引号和反斜杠
            return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #endregion
    }
}
