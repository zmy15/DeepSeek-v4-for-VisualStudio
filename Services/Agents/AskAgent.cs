using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Ask Agent — 纯问答代理。
    /// 
    /// 职责：
    /// - 回答技术问题
    /// - 解释代码逻辑
    /// - 讨论方案和架构
    /// - 绝不修改任何文件
    /// 
    /// 这是默认的 fallback Agent，当用户意图不是代码修改时使用。
    /// </summary>
    public class AskAgent : BaseAgent
    {
        public AskAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask) { }

        #region Agent Definition

        /// <summary>
        /// Ask Agent 工具集 — 只读工具 + 联网搜索。
        /// </summary>
        public static readonly string[] AskTools = new[]
        {
            "read_file",
            "file_search",
            "grep_search",
            "list_dir",
            "get_errors",
            "fetch_webpage",
            "github_repo",
            "semantic_search",
            "memory",
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                Description = "回答技术问题、解释代码、讨论方案。纯问答模式，不修改代码。",
                ArgumentHint = "输入你的技术问题",
                UserInvocable = true,
                DisableModelInvocation = false,
                AllowedTools = new List<string>(AskTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>
                {
                    new AgentHandoff
                    {
                        Label = "修改代码",
                        TargetAgent = AgentType.Edit,
                        Prompt = "用户需要修改代码。请根据以下上下文执行代码变更：\n\n",
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                    new AgentHandoff
                    {
                        Label = "制定计划",
                        TargetAgent = AgentType.Plan,
                        Prompt = "用户需要详细的实现计划。请研究代码库并制定方案：\n\n",
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return
                "你是 DeepSeek Chat 的 Ask Agent——一个专注技术问答的 AI 编程助手。\n\n" +
                "## 核心能力\n" +
                "- 解释代码逻辑和架构设计\n" +
                "- 分析技术问题和 Bug 根因\n" +
                "- 讨论实现方案和最佳实践\n" +
                "- 回答各类编程和技术问题\n\n" +
                "## 行为准则\n" +
                "- 回答简洁、准确、直接\n" +
                "- 优先给出可运行的代码示例\n" +
                "- 涉及代码时明确指出文件路径和行号\n" +
                "- 优先使用用户项目已有的框架和库\n" +
                "- 如果问题模糊，先追问澄清再回答\n" +
                "- 使用中文回答（代码注释也使用中文）\n\n" +
                "## 重要限制\n" +
                "- 你只能回答问题，不能修改任何项目文件\n" +
                "- 如果用户需要修改代码，建议他们切换到 Edit 模式\n" +
                "- 如果需要详细的实现计划，建议使用 Plan 模式";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Ask Agent 执行入口。
        /// 直接将用户问题发送给 AI 并返回回答。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            AddLog("INFO", $"Ask Agent 开始回答: \"{userMessage.Truncate(100)}\"");

            var result = new AgentResult
            {
                AgentType = AgentType.Ask,
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 构建包含历史的对话消息 ──
                var messages = new List<ChatApiMessage>();
                messages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = Definition.SystemPrompt
                });

                // 添加会话历史（最近 10 轮）
                int historyStart = Math.Max(0, context.ConversationHistory.Count - 20);
                for (int i = historyStart; i < context.ConversationHistory.Count; i++)
                {
                    messages.Add(context.ConversationHistory[i]);
                }

                // 添加上下文信息
                string contextualPrompt = BuildContextualPrompt(userMessage, context);
                messages.Add(new ChatApiMessage
                {
                    Role = "user",
                    Content = contextualPrompt
                });

                // ── 调用 AI ──
                string aiResponse = await CallAiWithHistoryAsync(messages, ct, maxTokens: 4096);
                result.Content = aiResponse;

                AddLog("INFO", $"Ask Agent 完成 ({aiResponse.Length} 字符)");
                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "回答已取消";
                AddLog("WARN", "回答已取消");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", $"回答失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建包含文件和项目上下文的 prompt。
        /// </summary>
        private static string BuildContextualPrompt(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"[当前解决方案: {context.SolutionPath}]");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine("[用户提供的文件内容]");
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            sb.AppendLine("[用户问题]");
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        #endregion
    }
}
