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
    /// Explore Agent — 只读代码库探索子代理。
    /// 
    /// 职责：
    /// - 使用多种搜索策略（glob、grep、语义搜索）在代码库中查找信息
    /// - 回答关于代码结构、依赖关系、实现模式的问题
    /// - 被 Plan Agent 作为子代理并行调用
    /// - 绝不修改任何文件
    /// 
    /// 参考: VS Code Copilot Chat Explore Agent
    /// </summary>
    public class ExploreAgent : BaseAgent
    {
        public ExploreAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Explore) { }

        #region Agent Definition

        /// <summary>
        /// Explore Agent 默认只读工具集。
        /// 这些工具只能检查工作区，绝不能修改文件。
        /// </summary>
        public static readonly string[] DefaultReadTools = new[]
        {
            "search",           // 语义搜索
            "file_search",      // glob 文件搜索
            "grep_search",      // 文本/正则搜索
            "read_file",        // 读取文件内容
            "list_dir",         // 列出目录内容
            "get_errors",       // 获取编译错误
            "get_changed_files",// 获取变更文件
            "fetch_webpage",    // 获取网页内容
            "github_repo",      // GitHub 仓库搜索
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Explore,
                Name = "Explore",
                Description = "快速只读代码库探索和问答子代理。" +
                    "优先使用而非手动链接多个搜索和文件读取操作，避免污染主对话。" +
                    "支持并行调用。指定详细程度: quick, medium, 或 thorough。",
                ArgumentHint = "描述要搜索的内容和期望的详细程度 (quick/medium/thorough)",
                UserInvocable = false,
                AllowedTools = new List<string>(DefaultReadTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>(),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return
                "你是一个 Explore Agent——专精于快速代码库分析和高效回答问题。\n\n" +
                "## 核心原则\n" +
                "- 你只能读取代码，绝不能修改、创建或删除任何文件。\n" +
                "- 你的输出是分析报告，包含文件路径、关键符号、可复用的模式。\n" +
                "- 优先使用绝对文件路径引用（如 `src/Models/User.cs`）。\n\n" +
                "## 搜索策略\n" +
                "- **从宽到窄**: 先用 glob 或语义搜索发现相关区域，再用 grep 缩小范围\n" +
                "- **并行优先**: 同时发起多个独立的搜索和读取操作\n" +
                "- **适时停止**: 一旦获取足够上下文就停止，不做穷举式扫描\n\n" +
                "## 输出格式\n" +
                "直接以消息形式报告发现。包含：\n" +
                "- 相关文件及其绝对路径链接\n" +
                "- 可复用的具体函数、类型或模式\n" +
                "- 可作为实现模板的类似已有功能\n" +
                "- 对所提问题的明确回答，而不是全面概述\n\n" +
                "## 详细程度\n" +
                "- **quick**: 只搜索最明显的匹配，返回最相关的结果\n" +
                "- **medium**: 做适度的多维度搜索，确保覆盖主要相关区域\n" +
                "- **thorough**: 深度搜索，确保不遗漏任何相关信息";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Explore Agent 执行入口。
        /// 接收搜索任务描述，返回代码库分析结果。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            AddLog("INFO", $"Explore Agent 开始执行: \"{userMessage.Truncate(100)}\"");

            var result = new AgentResult
            {
                AgentType = AgentType.Explore,
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 构建搜索 prompt ──
                string systemPrompt = Definition.SystemPrompt;

                string userPrompt = BuildExplorePrompt(userMessage, context);
                AddLog("INFO", $"Explore prompt 已构建 ({userPrompt.Length} 字符)");

                // ── 调用 AI 进行探索 ──
                string aiResponse = await CallAiLongAsync(systemPrompt, userPrompt, ct, maxTokens: 4096);
                result.Content = aiResponse;
                result.Logs.AddRange(_logs);

                AddLog("INFO", $"Explore Agent 完成 ({aiResponse.Length} 字符)");
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "探索已取消";
                AddLog("WARN", "探索已取消");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", $"探索失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建 Explore prompt，包含 workspace 上下文和搜索指导。
        /// </summary>
        private static string BuildExplorePrompt(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 探索任务");
            sb.AppendLine(userMessage);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                sb.AppendLine($"## 工作区");
                sb.AppendLine($"解决方案路径: {context.SolutionPath}");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(context.FileContext))
            {
                // 截断过长的文件上下文（Explore 只需要概览）
                string truncated = context.FileContext.Length > 3000
                    ? context.FileContext.Substring(0, 3000) + "\n... (已截断)"
                    : context.FileContext;
                sb.AppendLine("## 附加文件上下文");
                sb.AppendLine(truncated);
                sb.AppendLine();
            }

            if (context.ActivePlan != null)
            {
                sb.AppendLine("## 当前计划信息");
                sb.AppendLine($"任务: {context.ActivePlan.Title}");
                sb.AppendLine($"当前步骤: {context.ActivePlan.CurrentStepIndex}/{context.ActivePlan.Steps.Count}");
                sb.AppendLine();
            }

            sb.AppendLine("## 搜索指令");
            sb.AppendLine("1. 使用多种搜索策略找到相关文件和代码");
            sb.AppendLine("2. 报告关键文件路径、函数、类、模式");
            sb.AppendLine("3. 识别可作为实现模板的类似已有功能");
            sb.AppendLine("4. 指出潜在的依赖关系和注意事项");
            sb.AppendLine();
            sb.AppendLine("请输出你的发现。");

            return sb.ToString();
        }

        #endregion
    }
}
