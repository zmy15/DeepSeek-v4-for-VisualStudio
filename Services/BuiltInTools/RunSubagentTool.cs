using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// runSubagent 工具 — 将任务委派给子 Agent（ExploreAgent）执行。
    /// 
    /// 这是 Agent 间移交的专用工具。当 Agent 需要只读代码库探索
    /// （搜索文件、读取代码、分析结构）时，通过此工具委派给 ExploreAgent，
    /// 而非直接使用读取/搜索工具。
    /// 
    /// 移交格式（JSON）:
    /// {
    ///   "agentName": "Explore",
    ///   "prompt": "要执行的探索任务描述",
    ///   "description": "简短任务摘要"
    /// }
    /// </summary>
    public class RunSubagentTool : BuiltInToolBase
    {
        private readonly Func<ExplorationContext, Task<string>> _exploreHandler;

        /// <summary>
        /// 创建 RunSubagentTool 实例。
        /// </summary>
        /// <param name="exploreHandler">
        /// 探索处理器：接收 ExplorationContext，返回探索结果文本。
        /// 由 BaseAgent 注入，桥接 Agent 的 ExploreAgent 引用。
        /// </param>
        public RunSubagentTool(Func<ExplorationContext, Task<string>> exploreHandler)
        {
            _exploreHandler = exploreHandler ?? throw new ArgumentNullException(nameof(exploreHandler));
        }

        public override string Name => "runSubagent";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "runSubagent",
                    Description = L["tool.runSubagent.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            agentName = new
                            {
                                type = "string",
                                description = "子 Agent 名称。使用 \"Explore\" 进行代码库探索和搜索。",
                                @enum = new[] { "Explore" }
                            },
                            prompt = new
                            {
                                type = "string",
                                description = "委派给子 Agent 的完整任务描述，包括搜索目标、期望的详细程度（quick/medium/thorough）等。"
                            },
                            description = new
                            {
                                type = "string",
                                description = "简短（3-5 词）任务摘要，用于日志和追踪。"
                            }
                        },
                        required = new[] { "agentName", "prompt" }
                    }
                }
            };
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string agentName = GetStringArg(args, "agentName");
            string prompt = GetStringArg(args, "prompt");
            string description = GetStringArg(args, "description");

            if (string.IsNullOrWhiteSpace(agentName))
                return "❌ runSubagent: 缺少 agentName 参数。请指定 \"Explore\"。";

            if (string.IsNullOrWhiteSpace(prompt))
                return "❌ runSubagent: 缺少 prompt 参数。请提供委派给子 Agent 的任务描述。";

            // 目前仅支持 Explore 子 Agent
            if (!string.Equals(agentName, "Explore", StringComparison.OrdinalIgnoreCase))
                return $"❌ runSubagent: 未知的子 Agent \"{agentName}\"。当前仅支持 \"Explore\"。";

            string logDesc = string.IsNullOrWhiteSpace(description) ? prompt.Truncate(60) : description;
            Logger.Info($"[RunSubagent] → ExploreAgent: {logDesc}");

            try
            {
                var context = new ExplorationContext
                {
                    Prompt = prompt,
                    Description = description ?? prompt.Truncate(60),
                    WorkspaceRoot = workspaceRoot,
                };

                string result = await _exploreHandler(context);

                Logger.Info($"[RunSubagent] ExploreAgent 返回 {result.Length} 字符");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"[RunSubagent] ExploreAgent 执行失败: {ex.Message}", ex);
                return $"❌ ExploreAgent 执行异常: {ex.Message}";
            }
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string agentName = GetStringArg(args, "agentName");
            string desc = GetStringArg(args, "description");
            string displayDesc = string.IsNullOrWhiteSpace(desc)
                ? GetStringArg(args, "prompt")?.Truncate(50) ?? "探索代码库"
                : desc;

            return $"🤖 **{agentName ?? "Explore"}** — {displayDesc}";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "无结果";
            if (toolResult.StartsWith("❌")) return toolResult;
            // 统计探索到的关键信息量
            int lines = toolResult.Split('\n').Length;
            return $"✅ 探索完成（{toolResult.Length} 字符, {lines} 行）";
        }
    }

    /// <summary>
    /// 传递给 ExploreAgent 的探索上下文。
    /// </summary>
    public class ExplorationContext
    {
        /// <summary>探索任务描述</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>简短任务摘要</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>工作区根目录</summary>
        public string? WorkspaceRoot { get; set; }
    }
}
