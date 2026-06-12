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

        /// <summary>
        /// 英文版默认系统提示词（始终返回英文，不受当前语言设置影响）。
        /// 用于在选项页中展示英文版 System Prompt。
        /// </summary>
        public static string DefaultSystemPromptEn => L.GetValueForLocale("system.defaultSystemPrompt", "en");

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
        /// 记忆系统提示词片段 — 告知 AI 何时应主动使用 memory 工具存储偏好、项目知识等。
        /// </summary>
        public static string MemoryInstructionsFragment => L["system.memoryInstructions"];

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
        #region Code Completion Prompts

        /// <summary>
        /// 代码补全 — 系统提示词。
        /// </summary>
        public static string CodeCompletionSystemPrompt => L["system.codeCompletionSystemPrompt"];

        /// <summary>
        /// 代码补全 — 用户提示词（有光标位置）。
        /// {0} = 光标前的代码
        /// {1} = 光标后的代码
        /// </summary>
        public static string CodeCompletionUserPromptWithCursor => L["system.codeCompletionUserPromptWithCursor"];

        /// <summary>
        /// 代码补全 — 用户提示词（追加模式，无光标位置）。
        /// {0} = 上下文代码
        /// </summary>
        public static string CodeCompletionUserPromptAppend => L["system.codeCompletionUserPromptAppend"];

        #endregion

        #region Explore Agent Prompts

        /// <summary>
        /// 搜索关键词专家 — 系统提示词。
        /// </summary>
        public static string SearchKeywordsExpertSystemPrompt => L["system.searchKeywordsExpertSystemPrompt"];

        /// <summary>
        /// Explore Agent 搜索指令 — 注入到探索任务的末尾，指导 AI 如何系统性地搜索代码库。
        /// </summary>
        public static string ExploreAgentInstructions => L["system.exploreAgentInstructions"];

        #endregion

        #region Change Summary Prompts

        /// <summary>
        /// 代码变更总结 — 系统提示词（根据当前语言自动切换中英文）。
        /// </summary>
        public static string ChangeSummarySystemPrompt => L["system.changeSummarySystemPrompt"];

        /// <summary>
        /// 代码变更总结 — 用户指令（根据当前语言自动切换中英文）。
        /// </summary>
        public static string ChangeSummaryUserInstruction => L["system.changeSummaryUserInstruction"];

        #endregion

        #region AI Sub-prompts (summary polish, memory, auto-split, web search, workspace warning)

        /// <summary>摘要润色 — 系统提示词</summary>
        public static string SummaryPolishSystemPrompt => L["system.aiPrompt.summaryPolishSystem"];

        /// <summary>摘要润色 — 用户提示词模板。{0}=directSummary</summary>
        public static string SummaryPolishUserPrompt => L["system.aiPrompt.summaryPolishUser"];

        /// <summary>自动记忆记录 — 系统提示词</summary>
        public static string MemoryAutoRecordSystemPrompt => L["system.aiPrompt.memoryAutoRecordSystem"];

        /// <summary>自动记忆记录 — 用户提示词模板。{0}=userMessage, {1}=assistantResponse</summary>
        public static string MemoryAutoRecordUserPrompt => L["system.aiPrompt.memoryAutoRecordUser"];

        /// <summary>步骤自动拆分 — 系统提示词。{0}=maxFiles, {1}=maxLines, {2}=userMessage</summary>
        public static string AutoSplitSystemPrompt => L["system.aiPrompt.autoSplitSystem"];

        /// <summary>联网搜索上下文指令</summary>
        public static string WebSearchContextInstruction => L["system.aiPrompt.webSearchContext"];

        /// <summary>网页抓取上下文指令</summary>
        public static string WebFetchContextInstruction => L["system.aiPrompt.webFetchContext"];

        /// <summary>项目外路径拒绝警告。{0}=targetPath, {1}=workspaceRoot</summary>
        public static string OutOfWorkspaceWarning => L["system.aiPrompt.outOfWorkspaceWarning"];

        #endregion

        #region Plan Agent Prompts

        /// <summary>
        /// Plan Agent 对齐检查 — 用户提示词模板。
        /// {0} = 用户消息
        /// {1} = 代码库发现上下文
        /// </summary>
        public static string PlanAlignmentCheckPrompt => L["system.planAlignmentCheckPrompt"];

        /// <summary>
        /// Plan Agent 对齐阶段 — 用户提示词模板。
        /// {0} = 用户消息
        /// </summary>
        public static string PlanAlignmentUserPrompt => L["system.planAlignmentUserPrompt"];

        #endregion

        #region Edit Agent Prompts

        /// <summary>
        /// Edit Agent 格式恢复提示 — 在上次输出格式不正确时追加。
        /// </summary>
        public static string EditFormatRecoveryPrompt => L["system.editFormatRecoveryPrompt"];

        /// <summary>
        /// Edit Agent 步骤提示词前缀。
        /// {0} = 计划标题
        /// </summary>
        public static string EditStepPromptPrefix => L["system.editStepPromptPrefix"];

        #endregion

        #region Handoff & Context Prompts

        /// <summary>
        /// Handoff 上下文提示 — 告知接手 Agent 优先从对话历史获取上下文。
        /// </summary>
        public static string HandoffContextPrompt => L["system.handoffContextPrompt"];

        /// <summary>
        /// 上下文压缩提示词模板。
        /// {0} = 被压缩的对话内容
        /// </summary>
        public static string CompressionPromptTemplate => L["system.compressionPromptTemplate"];

        /// <summary>
        /// 可导航引用格式化规则 — 追加到 SharedImmutablePrefix 末尾。
        /// </summary>
        public static string NavigableReferenceRule => L["system.navigableReferenceRule"];

        #endregion

        #region Common System Prompt Prefix

        /// <summary>
        /// 所有 Agent 共享的 System Prompt 前缀核心（不含语言指令和工具说明）。
        /// 放在 messages[0]，确保跨 Agent 切换时 DeepSeek Prefix Cache 仍能命中。
        /// </summary>
        public static string CommonSystemPromptPrefixCore => L["system.agent.commonSystemPromptPrefixCore"];

        /// <summary>
        /// 所有 Agent 共享的不可变前缀（含语言指令）。
        /// 始终放在 messages[0]，确保跨 Agent 切换时 DeepSeek Prefix Cache 永远命中。
        /// Agent 专属行为指令不在此前缀中，而是作为最后一条 system 消息注入。
        /// 
        /// 内容 = CommonSystemPromptPrefixCore（角色定义 + 工具说明 + 文件规则 + 终端规则 +
        /// Handoff 规则）+ 语言指令。
        /// 
        /// 此属性同时被 BaseAgent.BuildContextAwareMessages（Agent 内部调用）和
        /// ConversationContextManager.BuildApiMessages（主流程 API 调用）使用，
        /// 确保两个路径的 messages[0] 完全一致，消除前缀漂移。
        /// </summary>
        public static string SharedImmutablePrefix => CommonSystemPromptPrefixCore + L["system.agent.languageInstruction"] + "\n"
            + L["system.navigableReferenceRule"] + "\n";

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
            // 使用 Replace 代替 string.Format 以避免模板中的 JSON 大括号被误解析为格式项
            return AgentRoutingUserPrompt.Replace("{0}", userMessage);
        }

        #endregion
    }
}
