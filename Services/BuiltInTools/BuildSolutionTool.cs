using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
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
        private static LocalizationService L => LocalizationService.Instance;
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
                                description = "构建配置（如 Debug 或 Release）。省略则使用当前活动配置。"
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
                ? "🔨 构建解决方案"
                : $"🔨 构建解决方案 ({config})";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;
            if (toolResult.Contains("构建成功") || toolResult.Contains("Build succeeded"))
                return "✅ 构建成功";
            if (toolResult.Contains("构建失败") || toolResult.Contains("Build failed"))
                return "⚠️ 构建失败";
            return "🔨 构建完成";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            if (_buildService == null)
                return "❌ build_solution: 构建服务未初始化。请在 VS 中打开解决方案后重试。";

            try
            {
                Logger.Info($"[BuiltInTool] build_solution 开始 (workspaceRoot={workspaceRoot ?? "(null)"})");
                string result = await _buildService.BuildAsync(workspaceRoot, CancellationToken.None);
                Logger.Info($"[BuiltInTool] build_solution 完成: {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"[BuiltInTool] build_solution 异常: {ex.Message}", ex);
                return $"❌ 构建失败: {ex.Message}";
            }
        }
    }
}
