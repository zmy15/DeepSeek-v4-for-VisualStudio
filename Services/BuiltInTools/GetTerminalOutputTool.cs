using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// get_terminal_output 工具 — 获取异步终端执行输出。
    /// </summary>
    public class GetTerminalOutputTool : BuiltInToolBase
    {
        public override string Name => "get_terminal_output";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "get_terminal_output",
                    Description = L["tool.get_terminal_output.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.getTerminalOutput.param.id"]
                            }
                        },
                        required = new[] { "id" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            return LocalizationService.Instance["tool.getTerminalOutput.displayText"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            return LocalizationService.Instance.Format("tool.getTerminalOutput.result", toolResult.Length);
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string id = GetStringArg(args, "id");
            if (string.IsNullOrEmpty(id))
            {
                return LocalizationService.Instance["tool.getTerminalOutput.missingId"];
            }

            // 原子领取作业：同一个 ID 只允许一次 get_terminal_output。
            // 这能防止模型在等待期间重复调用同一作业。
            if (!RunInTerminalTool.AsyncJobs.TryRemove(id, out var job))
            {
                return LocalizationService.Instance.Format("tool.getTerminalOutput.notFound", id);
            }

            try
            {
                // 关键点：这里 await 作业完成事件。命令仍在运行时工具调用保持 pending，
                // Agent 循环不会发起下一轮模型请求，因此不需要 get_terminal_output 轮询。
                var completionTask = job.Completion;
                var cancellationTask = Task.Delay(Timeout.Infinite, CancellationToken);
                var completed = await Task.WhenAny(completionTask, cancellationTask)
                    .ConfigureAwait(false);

                if (completed != completionTask)
                {
                    return LocalizationService.Instance["tool.getTerminalOutput.cancelled"];
                }

                var result = await completionTask.ConfigureAwait(false);

                var sb = new StringBuilder();
                sb.AppendLine(result.TimedOut
                    ? LocalizationService.Instance["tool.runInTerminal.timeoutReached"]
                    : LocalizationService.Instance.Format(
                        "tool.getTerminalOutput.completed", result.ExitCode));
                if (!string.IsNullOrWhiteSpace(result.Stdout))
                    sb.AppendLine(result.Stdout);
                if (!string.IsNullOrWhiteSpace(result.Stderr))
                {
                    sb.AppendLine("--- STDERR ---");
                    sb.AppendLine(result.Stderr);
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.runTerminal.failed", ex.Message);
            }
        }
    }
}
