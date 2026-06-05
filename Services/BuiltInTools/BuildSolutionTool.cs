using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// build_solution 工具 — 构建/编译当前解决方案。
    /// 委托给 IBuildService 执行实际的 VS SDK 构建交互。
    /// </summary>
    public class BuildSolutionTool : BuiltInToolBase
    {
        private readonly IBuildService? _buildService;

        public BuildSolutionTool(IBuildService? buildService = null)
        {
            _buildService = buildService;
        }

        public override string Name => "build_solution";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "build_solution",
                    Description = L["tool.build_solution.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            configuration = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.buildSolution.param.configuration"]
                            }
                        },
                        required = new string[] { }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string config = GetStringArg(args, "configuration");
            return string.IsNullOrEmpty(config)
                ? LocalizationService.Instance["tool.buildSolution.displayText"]
                : LocalizationService.Instance.Format("tool.buildSolution.displayTextWithConfig", config);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.Contains(LocalizationService.Instance["tool.common.buildSuccess"]) || toolResult.Contains("Build succeeded"))
                return LocalizationService.Instance["tool.buildSolution.success"];
            if (toolResult.Contains(LocalizationService.Instance["tool.common.buildFailed"]) || toolResult.Contains("Build failed"))
                return LocalizationService.Instance["tool.buildSolution.failed"];
            return LocalizationService.Instance["tool.buildSolution.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            if (_buildService == null)
                return LocalizationService.Instance["tool.buildSolution.serviceNotInit"];

            try
            {
                Logger.Info($"[BuiltInTool] build_solution 开始 (workspaceRoot={workspaceRoot ?? "(null)"})");
                // ── 使用 10 分钟超时 CTS，避免构建无限挂起 ──
                // 内部 BuildService 各路径也有各自的超时保护（5分钟），
                // 此处作为外层兜底，同时使取消操作可被外部令牌触发。
                using var buildCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                string result = await _buildService.BuildAsync(workspaceRoot, buildCts.Token);
                // 只输出一行摘要：构建成功/失败 + 结果长度（完整结果由 Agent 层日志输出）
                string oneLine = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                Logger.Info($"[BuiltInTool] build_solution 完成: {oneLine.Truncate(120)}");

                // ── 构建失败时附加一次性读取提示，避免 AI 逐轮试探 ──
                if (result.Contains("❌") || result.Contains("Build FAILED") || result.Contains("error CS") || result.Contains("error C"))
                {
                    result += "\n\n> 💡 **构建失败提示**: 请在单次调用中同时读取**所有**报错文件及行号范围。"
                        + "不要逐个文件试探。使用并行 read_file 调用（一次发送多个 read_file 请求）可大幅减少轮次。";
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[BuiltInTool] build_solution 已取消");
                return LocalizationService.Instance["tool.buildSolution.cancelled"];
            }
            catch (Exception ex)
            {
                Logger.Error($"[BuiltInTool] build_solution 异常: {ex.Message}", ex);
                return LocalizationService.Instance.Format("tool.buildSolution.failedWithError", ex.Message);
            }
        }
    }
}
