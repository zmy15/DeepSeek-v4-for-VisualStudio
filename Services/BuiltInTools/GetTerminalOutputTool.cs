using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
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
                                description = "终端执行 ID（由 run_in_terminal 异步模式返回）"
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
            if (toolResult.StartsWith("❌")) return toolResult;
            return $"📋 终端输出 ({toolResult.Length} 字符)";
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string id = GetStringArg(args, "id");
            if (string.IsNullOrEmpty(id))
                return Task.FromResult("❌ get_terminal_output: 缺少 id 参数");

            return Task.FromResult(
                $"📟 终端 ID: {id}\n" +
                "💡 提示：异步终端命令的输出请直接查看 VS 输出窗口或终端面板。\n" +
                "如果命令仍在运行中，请稍后重试。");
        }
    }
}
