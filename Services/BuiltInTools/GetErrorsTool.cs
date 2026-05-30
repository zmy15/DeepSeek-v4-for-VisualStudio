using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// get_errors 工具 — 获取 VS Error List 中的编译错误。
    /// </summary>
    public class GetErrorsTool : BuiltInToolBase
    {
        private readonly IBuildService? _buildService;

        public GetErrorsTool(IBuildService? buildService = null)
        {
            _buildService = buildService;
        }

        public override string Name => "get_errors";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "get_errors",
                    Description = L["tool.get_errors.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePaths = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "可选，指定要检查的文件路径列表。不指定则获取所有文件的错误。"
                            },
                            includeSelected = new
                            {
                                type = "boolean",
                                description = "可选，设为 true 时返回用户在 Error List 窗口中当前选中的错误项信息（包含错误码、项目名、行列号等完整元数据）。默认 false。"
                            }
                        },
                        required = new string[] { }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            var filePaths = GetStringArrayArg(args, "filePaths");
            if (filePaths != null && filePaths.Length > 0)
                return $"⚠️ 检查 {filePaths.Length} 个文件的编译错误";
            return "⚠️ 检查工作区编译错误";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;

            if (toolResult.Contains("0 个错误") || toolResult.Contains("0 errors"))
                return "✅ 无编译错误";
            var errMatch = System.Text.RegularExpressions.Regex.Match(
                toolResult, @"(\d+)\s*(?:个)?\s*(?:错误|errors?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (errMatch.Success)
                return $"⚠️ 发现 {errMatch.Groups[1].Value} 个错误";
            return "✅ 编译检查完成";
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            bool includeSelected = GetBoolArg(args, "includeSelected", false);

            if (includeSelected && _buildService != null)
            {
                try
                {
                    var selectedErrors = await _buildService.GetSelectedErrorsAsync(CancellationToken.None);
                    if (selectedErrors.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("🔍 错误列表中选中的错误项");
                        sb.AppendLine();
                        sb.AppendLine($"| # | 描述 | 文件 | 行 | 列 | 错误码 | 项目 |");
                        sb.AppendLine("|---|------|------|----|----|--------|------|");
                        for (int i = 0; i < selectedErrors.Count; i++)
                        {
                            var e = selectedErrors[i];
                            string desc = e.Description.Truncate(80);
                            string file = Path.GetFileName(e.FileName ?? "");
                            string line = e.Line > 0 ? e.Line.ToString() : "-";
                            string col = e.Column > 0 ? e.Column.ToString() : "-";
                            string code = e.ErrorCode ?? "-";
                            string proj = e.Project ?? "-";
                            sb.AppendLine($"| {i + 1} | {desc} | {file} | {line} | {col} | {code} | {proj} |");
                        }
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine("### 详细错误信息");
                        sb.AppendLine();
                        foreach (var e in selectedErrors)
                        {
                            sb.AppendLine($"**{e.ErrorCode ?? "Error"}** ({e.Category}, {e.Priority}): {e.Description}");
                            if (!string.IsNullOrEmpty(e.FileName))
                                sb.AppendLine($"  📄 `{e.FileName}`" +
                                    (e.Line > 0 ? $":{e.Line}" : "") +
                                    (e.Column > 0 ? $":{e.Column}" : ""));
                            if (!string.IsNullOrEmpty(e.Project))
                                sb.AppendLine($"  📦 项目: {e.Project}");
                            if (!string.IsNullOrEmpty(e.SubCategory))
                                sb.AppendLine($"  🏷️ 子类别: {e.SubCategory}");
                            sb.AppendLine();
                        }
                        return sb.ToString().TrimEnd();
                    }
                    return "🔍 错误列表中未选中任何错误项。请在错误列表窗口中点击选中错误项后重试。";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[BuiltInTool] get_errors (selected) 异常: {ex.Message}");
                    return $"❌ 获取选中错误项失败: {ex.Message}";
                }
            }

            // ── 委托给 BuildService.CollectBuildErrors() ──
            try
            {
                // ── 检查构建是否仍在进行中（避免返回过时/不完整的错误）──
                string? buildStatus = CheckBuildInProgress();
                if (buildStatus != null)
                    return buildStatus;

                string errors = BuildService.CollectBuildErrors();

                if (!string.IsNullOrWhiteSpace(errors))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("🔧 编译错误检查");
                    sb.AppendLine();
                    sb.AppendLine(errors);
                    return sb.ToString().TrimEnd();
                }

                return "🔧 编译错误检查: 未检测到编译错误。\n\n" +
                       "💡 说明：\n" +
                       "- 如果刚通过 `build_solution` 触发了构建，请等待构建完成后再调用 `get_errors`。\n" +
                       "- 如果构建已完成且确实无错误，则代码编译通过。";
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuiltInTool] get_errors 异常: {ex.Message}");
                return $"❌ 获取编译错误失败: {ex.Message}\n💡 建议使用 build_solution 工具触发编译并获取错误详情。";
            }
        }

        /// <summary>
        /// 检查 VS 中是否正在构建。如果正在构建，返回提示消息让 AI 等待；
        /// 返回 null 表示没有构建在进行中。
        /// </summary>
        private static string? CheckBuildInProgress()
        {
            try
            {
                return ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var dte = (EnvDTE.DTE?)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                        .GetService(typeof(EnvDTE.DTE));
                    if (dte == null) return null;

                    var solutionBuild = dte.Solution?.SolutionBuild;
                    if (solutionBuild == null) return null;

                    if (solutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
                    {
                        Logger.Info("[BuiltInTool] get_errors: 构建仍在进行中，提示 AI 等待");
                        return "⏳ 构建仍在进行中，错误列表可能不完整。\n\n" +
                               "请等待构建完成后再调用 `get_errors`。\n" +
                               "💡 提示：CMake 项目构建通常需要 1-5 分钟，大型项目可能更长。";
                    }

                    return null;
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuiltInTool] 检查构建状态异常: {ex.Message}");
                return null; // 检查失败不影响正常流程
            }
        }
    }
}
