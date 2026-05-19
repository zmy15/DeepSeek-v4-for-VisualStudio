using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 集中管理所有发送给 AI 的 Prompt 文本。
    /// 支持多语言：根据 LocalizationService 当前语言自动切换中英文。
    /// 所有 Prompt 通过 LocalizationService 获取，便于统一修改、多语言支持和 A/B 测试。
    /// </summary>
    public static class AiPrompts
    {
        /// <summary>
        /// 便捷访问器。
        /// </summary>
        private static LocalizationService L => LocalizationService.Instance;

        #region System Prompts

        /// <summary>
        /// 默认系统提示词，定义 AI 助手的行为角色。
        /// 用户可在 工具 → 选项 → DeepSeek Chat → API Settings → System Prompt 中自定义覆盖。
        /// </summary>
        public static string DefaultSystemPrompt => L["system.defaultSystemPrompt"];

        #endregion

        #region UI Messages

        /// <summary>
        /// 首次启动时的欢迎语，当已配置 API Key 时显示。
        /// </summary>
        public static string WelcomeMessage => L["ui.welcomeMessage"];

        /// <summary>
        /// 未配置 API Key 时显示的警告消息。
        /// </summary>
        public static string ApiKeyMissingMessage => L["ui.apiKeyMissingMessage"];

        #endregion

        #region Skill Prompts

        /// <summary>
        /// Skill 系统提示词片段 — 注入到主系统提示中，告知 AI 如何使用技能。
        /// 包含详细的调用时机说明和语义匹配指导。
        /// </summary>
        public static string SkillSystemPromptFragment => L["system.skillSystemPromptFragment"];

        /// <summary>
        /// 技能路由判断 — 系统提示词。
        /// 用于在用户提问时，先让 AI 判断是否应调用某个技能。
        /// </summary>
        public static string SkillRoutingSystemPrompt => L["system.skillRoutingSystemPrompt"];

        /// <summary>
        /// 技能路由判断 — 用户提示词模板。
        /// {0} = 技能总结
        /// {1} = 用户问题 + 上下文
        /// </summary>
        public static string SkillRoutingUserPrompt => L["system.skillRoutingUserPrompt"];

        /// <summary>
        /// 内置示例技能：代码审查 (code-review)。
        /// 演示 SKILL.md 的标准格式。
        /// </summary>
        public static string BuiltInSkill_CodeReview => L["system.builtInSkillCodeReview"];

        #endregion

        #region Search Optimization Prompts

        /// <summary>
        /// 百度搜索优化 - 系统提示词。
        /// </summary>
        public static string SearchOptimizationSystem_Baidu => L["system.searchOptimizationSystemBaidu"];

        /// <summary>
        /// 百度搜索优化 - 用户提示词模板。
        /// {0} = 对话上下文 + 用户问题
        /// </summary>
        public static string SearchOptimizationUser_Baidu => L["system.searchOptimizationUserBaidu"];

        /// <summary>
        /// DuckDuckGo 搜索优化 - 系统提示词。
        /// </summary>
        public static string SearchOptimizationSystem_DuckDuckGo => L["system.searchOptimizationSystemDuckDuckGo"];

        /// <summary>
        /// DuckDuckGo 搜索优化 - 用户提示词模板。
        /// {0} = 对话上下文 + 用户问题
        /// </summary>
        public static string SearchOptimizationUser_DuckDuckGo => L["system.searchOptimizationUserDuckDuckGo"];

        #endregion

        #region File Extraction Prompts

        /// <summary>
        /// 附件关键信息提取 - 系统提示词。
        /// </summary>
        public static string FileExtractionSystem => L["system.fileExtractionSystem"];

        /// <summary>
        /// 附件关键信息提取 - 用户提示词模板。
        /// {0} = 用户问题
        /// {1} = 文件内容
        /// </summary>
        public static string FileExtractionUser => L["system.fileExtractionUser"];

        #endregion

        #region Multi-Agent Prompts

        /// <summary>
        /// 多 Agent 系统提示词片段 — 告知 AI 当前处于多 Agent 协作环境。
        /// </summary>
        public static string MultiAgentSystemPromptFragment => L["system.multiAgentSystemPromptFragment"];

        /// <summary>
        /// Agent 路由分析 — 系统提示词。
        /// </summary>
        public static string AgentRoutingSystemPrompt => L["system.agentRoutingSystemPrompt"];

        /// <summary>
        /// Agent 路由分析 — 用户提示词模板。
        /// {0} = 用户消息
        /// </summary>
        public static string AgentRoutingUserPrompt => L["system.agentRoutingUserPrompt"];

        #endregion

        #region Helper Methods

        /// <summary>
        /// 构建搜索优化提示词（用户消息部分）。
        /// </summary>
        /// <param name="contextLine">包含上下文摘要和用户问题的字符串。</param>
        /// <param name="isBaiduSearch">true=百度 JSON 格式，false=DuckDuckGo 纯文本格式。</param>
        /// <returns>优化后的用户提示词。</returns>
        public static string BuildSearchOptimizationPrompt(string contextLine, bool isBaiduSearch)
        {
            string template = isBaiduSearch
                ? SearchOptimizationUser_Baidu
                : SearchOptimizationUser_DuckDuckGo;

            return string.Format(template, contextLine);
        }

        /// <summary>
        /// 获取搜索优化对应的系统提示词。
        /// </summary>
        public static string GetSearchOptimizationSystemPrompt(bool isBaiduSearch)
        {
            return isBaiduSearch
                ? SearchOptimizationSystem_Baidu
                : SearchOptimizationSystem_DuckDuckGo;
        }

        /// <summary>
        /// 构建文件提取提示词（用户消息部分）。
        /// </summary>
        /// <param name="userQuestion">用户原始问题。</param>
        /// <param name="fileContent">从附件提取的文件内容。</param>
        /// <returns>文件提取用户提示词。</returns>
        public static string BuildFileExtractionPrompt(string userQuestion, string fileContent)
        {
            return string.Format(FileExtractionUser, userQuestion, fileContent);
        }

        /// <summary>
        /// 获取格式化的 Skill 路由用户提示词。
        /// </summary>
        public static string BuildSkillRoutingPrompt(string skillSummary, string userQuestion)
        {
            return string.Format(SkillRoutingUserPrompt, skillSummary, userQuestion);
        }

        /// <summary>
        /// 获取格式化的 Agent 路由用户提示词。
        /// </summary>
        public static string BuildAgentRoutingPrompt(string userMessage)
        {
            return string.Format(AgentRoutingUserPrompt, userMessage);
        }

        #endregion
    }
}
