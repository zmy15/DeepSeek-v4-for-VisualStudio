using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// run_in_terminal 工具 — 在终端中运行命令。
    /// ⚠️ 编译/构建命令会被拦截，提示使用 build_solution 工具。
    /// </summary>
    public class RunInTerminalTool : BuiltInToolBase
    {
        private static LocalizationService L => LocalizationService.Instance;

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
                            explanation = new { type = "string", description = "命令用途的简短说明" },
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

                using var process = Process.Start(psi);
                if (process == null)
                    return "❌ run_in_terminal: 无法启动进程";

                if (isAsync)
                {
                    string pid = process.Id.ToString();
                    _ = Task.Run(() => { process.WaitForExit(); });
                    return $"🚀 终端命令已启动 (PID: {pid}, 模式: async)\n命令: {command}";
                }
                else
                {
                    string stdout = await process.StandardOutput.ReadToEndAsync();
                    string stderr = await process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit());

                    var sb = new StringBuilder();
                    sb.AppendLine($"📟 终端输出 (退出码: {process.ExitCode}):");
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
    }
}
