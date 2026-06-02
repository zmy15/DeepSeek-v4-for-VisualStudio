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
    /// run_in_terminal 工具 — 在终端中运行命令。
    /// ⚠️ 编译/构建命令会被拦截，提示使用 build_solution 工具。
    /// </summary>
    public class RunInTerminalTool : BuiltInToolBase
    {
        /// <summary>同步模式最大等待时间（防止进程僵死导致 Agent 永久卡住）</summary>
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);
        public override string Name => "run_in_terminal";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "run_in_terminal",
                    Description = L["tool.run_in_terminal.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = "要执行的 shell 命令" },
                            explanation = new { type = "string", description = "命令用途的简短说明（这条命令做什么）" },
                            purpose = new { type = "string", description = "操作目的——为什么要执行此命令，要达成什么目标（如：验证编译是否通过、检查文件是否存在）" },
                            mode = new
                            {
                                type = "string",
                                description = "执行模式：sync（等待完成）或 async（后台运行）。默认 sync。",
                                @enum = new[] { "sync", "async" }
                            }
                        },
                        required = new[] { "command", "explanation" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string cmd = GetStringArg(args, "command");
            string expl = GetStringArg(args, "explanation");
            if (!string.IsNullOrEmpty(expl))
                return $"💻 执行终端命令: {TruncateText(expl, 80)}";
            else if (!string.IsNullOrEmpty(cmd))
                return $"💻 执行终端命令: `{TruncateText(cmd, 60)}`";
            return "💻 执行终端命令";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌") || toolResult.StartsWith("⛔")) return toolResult;
            if (toolResult.Contains("exit code: 0") || toolResult.Contains("ExitCode: 0"))
                return "✅ 终端命令执行成功";
            return "💻 终端命令已执行";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string command = GetStringArg(args, "command");
            string mode = GetStringArg(args, "mode");

            if (string.IsNullOrEmpty(command))
                return "❌ run_in_terminal: 缺少 command 参数";

            if (IsBuildCommand(command))
            {
                return $"⛔ 禁止在终端中运行编译命令。请改用 build_solution 工具，它通过 VS SDK 原生接口编译，" +
                    $"编译错误也会自动进入 VS Error List，可通过 get_errors 工具获取详细错误信息。\n\n" +
                    $"被拦截的命令: {command}";
            }

            // ── Unix 风格命令检测与修正（安全网：即使 AI prompt 已要求 PowerShell，仍有概率输出 Unix 命令）──
            string? unixWarning = DetectUnixStyleCommand(command);
            command = NormalizeUnixToPowerShell(command);

            // 如果命令被修正过，构建警告前缀（附加到输出开头提醒 AI 下次注意）
            string warningPrefix = unixWarning != null
                ? unixWarning + "\n修正后的命令: " + command + "\n\n"
                : string.Empty;

            bool isAsync = string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };

                var process = Process.Start(psi);
                if (process == null)
                    return "❌ run_in_terminal: 无法启动进程";

                if (isAsync)
                {
                    string pid = process.Id.ToString();
                    // 不等待进程退出，直接返回。进程由 OS 管理，VS 退出时自动清理。
                    // 注意：不能 using/dispose process，因为 fire-and-forget 任务还需要它。
                    _ = Task.Run(() =>
                    {
                        try { process.WaitForExit(); }
                        catch { }
                        finally { process.Dispose(); }
                    });
                    return warningPrefix + $"🚀 终端命令已启动 (PID: {pid}, 模式: async)\n命令: {command}";
                }
                else
                {
                    // ── 并发读取 stdout 和 stderr，防止管道缓冲区满导致死锁 ──
                    // 经典问题：若先读 stdout 再读 stderr，当 stderr 缓冲区先满时，
                    // 进程会阻塞等待 stderr 被读取，但 stdout 永远等不到进程退出 → 死锁。
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var readTask = Task.WhenAll(stdoutTask, stderrTask);

                    // ── 超时保护：防止进程僵死（如后台进程未正确关闭管道）──
                    var timeoutTask = Task.Delay(SyncTimeout);
                    var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

                    if (completed == timeoutTask)
                    {
                        // 超时：强制杀死进程并返回部分输出
                        try { process.Kill(); } catch { }
                        string partialStdout = stdoutTask.IsCompleted ? stdoutTask.Result : "(超时截断)";
                        string partialStderr = stderrTask.IsCompleted ? stderrTask.Result : "(超时截断)";
                        process.Dispose();

                        var timeoutSb = new StringBuilder();
                        if (!string.IsNullOrEmpty(warningPrefix))
                            timeoutSb.Append(warningPrefix);
                        timeoutSb.AppendLine($"⏱️ 终端命令超时 ({SyncTimeout.TotalMinutes:F0} 分钟)，已强制终止");
                        timeoutSb.AppendLine($"命令: {command}");
                        if (!string.IsNullOrWhiteSpace(partialStdout))
                            timeoutSb.AppendLine(partialStdout);
                        if (!string.IsNullOrWhiteSpace(partialStderr))
                        {
                            timeoutSb.AppendLine("--- STDERR ---");
                            timeoutSb.AppendLine(partialStderr);
                        }
                        return timeoutSb.ToString().TrimEnd();
                    }

                    // 正常完成
                    string stdout = stdoutTask.Result;
                    string stderr = stderrTask.Result;

                    // 流已关闭，进程应已退出；WaitForExit 确保退出码可用
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    int exitCode = process.ExitCode;
                    process.Dispose();

                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(warningPrefix))
                        sb.Append(warningPrefix);
                    sb.AppendLine($"📟 终端输出 (退出码: {exitCode}):");
                    if (!string.IsNullOrWhiteSpace(stdout))
                        sb.AppendLine(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        sb.AppendLine("--- STDERR ---");
                        sb.AppendLine(stderr);
                    }
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                return $"❌ run_in_terminal 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 检测命令是否为编译/构建命令。
        /// </summary>
        private static bool IsBuildCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            string normalized = command.Trim();
            if (normalized.StartsWith("&"))
                normalized = normalized.Substring(1).Trim();

            if (normalized.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet msbuild", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet publish", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet restore", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("dotnet pack", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("msbuild", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("MSBuild.exe", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("cl.exe", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" link.exe", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("cl ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("\"cl\"", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.StartsWith("gcc ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("g++ ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("clang", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" gcc ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" g++ ", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("cmake --build", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("make ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("ninja", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(" make ", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("cargo build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("go build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("npm run build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("yarn build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("pnpm build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("gradle build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("gradlew build", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("mvn ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("mvnw ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("pip install", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("nuget restore", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// 检测命令中是否包含 Unix/Linux 风格的语法，返回警告信息。
        /// 如果命令是有效的 PowerShell，返回 null。
        /// </summary>
        private static string? DetectUnixStyleCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            var issues = new List<string>();

            // ── 检测 `&&` 命令链（应使用 `;`）──
            if (command.Contains("&&"))
                issues.Add("使用了 `&&` 连接命令（应用 `;` 替代）");

            // ── 检测 Unix 命令 ──
            var unixCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["grep "] = "Select-String",
                ["| grep"] = "| Select-String",
                ["cat "] = "Get-Content",
                ["rm -rf"] = "Remove-Item -Recurse -Force",
                ["rm -r"] = "Remove-Item -Recurse",
                ["rm "] = "Remove-Item",
                ["ls -la"] = "Get-ChildItem -Force",
                ["ls -l"] = "Get-ChildItem",
                ["ls "] = "Get-ChildItem",
                ["chmod "] = "(不支持 chmod，Windows 使用 icacls 或 attrib)",
                ["sed "] = "(不支持 sed，使用 -replace 运算符或 Select-String)",
                ["awk "] = "(不支持 awk，使用 Select-String 或 ForEach-Object)",
                ["touch "] = "New-Item",
                ["which "] = "Get-Command",
                ["cp -r"] = "Copy-Item -Recurse",
                ["cp "] = "Copy-Item",
                ["mv "] = "Move-Item",
                ["mkdir -p"] = "New-Item -ItemType Directory -Force",
                ["mkdir "] = "New-Item -ItemType Directory",
                ["wget "] = "Invoke-WebRequest",
                ["curl "] = "Invoke-WebRequest",
                ["tail -f"] = "Get-Content -Wait -Tail",
                ["tail "] = "Get-Content -Tail",
                ["head "] = "Get-Content -Head",
                ["./"] = "应使用 `.\\` 运行脚本",
                ["export "] = "应使用 `$env:` 设置环境变量",
            };

            foreach (var kvp in unixCommands)
            {
                if (command.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    issues.Add($"检测到 Unix 命令 `{kvp.Key.Trim()}` → 应使用 `{kvp.Value}`");
                    break; // 只报告第一个问题，避免消息过长
                }
            }

            if (issues.Count == 0) return null;

            return "⚠️ 终端命令包含 Unix/Linux 风格语法，已自动修正：\n" + string.Join("\n", issues);
        }

        /// <summary>
        /// 将常见 Unix 风格命令修正为 Windows PowerShell 等价命令。
        /// 此方法是安全网——AI 应通过 system prompt 直接输出 PowerShell 命令。
        /// </summary>
        private static string NormalizeUnixToPowerShell(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return command;

            string result = command;

            // `&&` → `;`（PowerShell 不支持 && 命令链）
            result = result.Replace("&&", ";");

            // 常见 Unix 命令替换（整词匹配，避免误替换变量名等）
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 注意：顺序很重要！长匹配必须放在短匹配前面
                ["rm -rf"] = "Remove-Item -Recurse -Force",
                ["rm -r"] = "Remove-Item -Recurse",
                ["cp -r"] = "Copy-Item -Recurse",
                ["cp -R"] = "Copy-Item -Recurse",
                ["mkdir -p"] = "New-Item -ItemType Directory -Force",
                ["ls -la"] = "Get-ChildItem -Force",
                ["ls -l"] = "Get-ChildItem",
                ["tail -f"] = "Get-Content -Wait -Tail",
                ["tail "] = "Get-Content -Tail ",
                ["head "] = "Get-Content -Head ",
            };

            foreach (var kvp in replacements)
            {
                result = ReplaceCommandWord(result, kvp.Key, kvp.Value);
            }

            // 简单命令映射（整词）
            var simpleReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["grep"] = "Select-String",
                ["cat"] = "Get-Content",
                ["chmod"] = "icacls",
                ["touch"] = "New-Item",
                ["which"] = "Get-Command",
                ["wget"] = "Invoke-WebRequest",
                ["curl"] = "Invoke-WebRequest",
            };

            foreach (var kvp in simpleReplacements)
            {
                result = ReplaceCommandWord(result, kvp.Key + " ", kvp.Value + " ");
            }

            // 路径分隔符 `/` → `\`（仅对已知路径模式，避免破坏 URL 等）
            // 匹配类似 ./path/to/file 或 /absolute/path 的模式
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"(?<![a-zA-Z])(\./)([^\s;|]+)", @".\$2");
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"(?<![a-zA-Z:\)\(])(/[a-zA-Z0-9_\-\.]+)+", m =>
                    m.Value.Replace('/', '\\'));

            // `./script` → `.\script`
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"(?<![a-zA-Z\\])\./([^\s;|]+)", @".\$1");

            return result;
        }

        /// <summary>
        /// 在命令字符串中替换命令词（仅当出现在命令起始位置或管道/分隔符后时替换）。
        /// 避免替换文件路径或参数中包含的关键词。
        /// </summary>
        private static string ReplaceCommandWord(string command, string oldWord, string newWord)
        {
            if (!command.Contains(oldWord)) return command;

            // 只在命令起始位置或 `|`、`;` 后替换
            var pattern = $@"(^|\||;\s*){System.Text.RegularExpressions.Regex.Escape(oldWord)}";
            return System.Text.RegularExpressions.Regex.Replace(
                command, pattern, $"$1{newWord}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }
}
