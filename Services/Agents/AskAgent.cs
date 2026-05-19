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

        /// <summary>
        /// ExploreAgent 引用，由 AgentDispatcher 注入。
        /// 用于在需要时委托代码库探索任务。
        /// </summary>
        public ExploreAgent? ExploreAgent { get; set; }

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
                Description = LocalizationService.Instance["agent.ask.description"],
                ArgumentHint = LocalizationService.Instance["agent.ask.argumentHint"],
                UserInvocable = true,
                DisableModelInvocation = false,
                AllowedTools = new List<string>(AskTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>
                {
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.ask.handoffEditLabel"],
                        TargetAgent = AgentType.Edit,
                        Prompt = LocalizationService.Instance["agent.ask.handoffEditPrompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.ask.handoffPlanLabel"],
                        TargetAgent = AgentType.Plan,
                        Prompt = LocalizationService.Instance["agent.ask.handoffPlanPrompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return CommonSystemPromptPrefix + LocalizationService.Instance["agent.ask.systemPromptFragment"];
        }

        #endregion

        #region Execute

        /// <summary>
        /// Ask Agent 执行入口。
        /// 直接将用户问题发送给 AI 并返回回答。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.askStarted"], userMessage.Truncate(100)));

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
                if (context.ConversationHistory.Count > 0)
                {
                    int historyStart = Math.Max(0, context.ConversationHistory.Count - 20);
                    for (int i = historyStart; i < context.ConversationHistory.Count; i++)
                    {
                        var histMsg = context.ConversationHistory[i];
                        // 无工具调用的 assistant 消息不带 reasoning_content（API 会忽略）
                        var apiMsg = new ChatApiMessage
                        {
                            Role = histMsg.Role,
                            Content = histMsg.Content,
                        };
                        messages.Add(apiMsg);
                    }
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

                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.askDone"], aiResponse.Length));
                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance["agent.log.askCancelled"];
                AddLog("WARN", LocalizationService.Instance["agent.log.askCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(LocalizationService.Instance["agent.log.askFailed"], ex.Message));
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
