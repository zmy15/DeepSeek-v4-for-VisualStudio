using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 集中管理所有发送给 AI 的 Prompt 文本。
    /// 便于统一修改、多语言支持和 A/B 测试。
    /// 所有 Prompt 遵循 C# 原始字符串或常量定义，避免硬编码散落各处。
    /// </summary>
    public static class AiPrompts
    {
        #region System Prompts

        /// <summary>
        /// 默认系统提示词，定义 AI 助手的行为角色。
        /// 用户可在 工具 → 选项 → DeepSeek Chat → API Settings → System Prompt 中自定义覆盖。
        /// </summary>
        public const string DefaultSystemPrompt =
            "你是 DeepSeek Chat，一个深度集成在 Visual Studio 中的 AI 编程助手。" +
            "你的核心能力包括：解释代码逻辑、定位并修复 Bug、重构优化代码、生成单元测试、回答各类技术问题。" +
            "请遵循以下准则：\n" +
            "- 回答应简洁、准确、直接，优先给出可运行的代码方案。\n" +
            "- 涉及代码修改时，明确指出文件路径和具体行号。\n" +
            "- 优先使用用户项目已有的框架和库，不引入不必要的依赖。\n" +
            "- 如果用户的问题模糊不清，先追问澄清再给出建议。\n" +
            "- 使用中文回答，代码中的注释也使用中文。\n" +
            "- 当用户需要获取实时信息、操作文件系统或执行特定任务时，积极使用可用的工具（tools）来完成任务。";

        #endregion

        #region UI Messages

        /// <summary>
        /// 首次启动时的欢迎语，当已配置 API Key 时显示。
        /// </summary>
        public const string WelcomeMessage =
            "你好！我是 DeepSeek Chat，你的 AI 编程助手。\n\n" +
            "我可以帮你：\n- 解释代码\n- 修复 Bug\n- 重构代码\n- 生成测试\n- 回答技术问题\n\n开始提问吧！";

        /// <summary>
        /// 未配置 API Key 时显示的警告消息。
        /// </summary>
        public const string ApiKeyMissingMessage =
            "⚠️ **未配置 API 密钥**\n\n" +
            "请通过菜单 **工具 → 选项 → DeepSeek Chat** 配置你的 DeepSeek API 密钥。\n\n" +
            "获取密钥：https://platform.deepseek.com/api_keys";

        #endregion

        #region Search Optimization Prompts

        /// <summary>
        /// 百度搜索优化 - 系统提示词。
        /// </summary>
        public const string SearchOptimizationSystem_Baidu =
            "你只返回 JSON，不返回任何其他内容。";

        /// <summary>
        /// 百度搜索优化 - 用户提示词模板。
        /// {0} = 对话上下文 + 用户问题
        /// </summary>
        public const string SearchOptimizationUser_Baidu =
            "你是一个搜索查询优化助手。根据用户的问题和对话上下文，生成优化的联网搜索关键词。\n\n" +
            "规则：\n" +
            "1. 提取核心搜索意图，去除无关词汇\n" +
            "2. 关键词应简洁精准，不超过72个字符（一个汉字=2字符）\n" +
            "3. 如需时效性信息，设置 search_recency 为 week/month/semiyear/year\n" +
            "4. 如果用户只是聊天/问候/代码问题（不需要联网），设置 need_search 为 false\n" +
            "5. 如果内容携带时间信息，请不要移除\n" +
            "6. 严格返回 JSON 格式，不要包含任何其他文本\n\n" +
            "JSON 格式：\n" +
            "{{\"search_query\":\"优化后的关键词\",\"search_recency\":null,\"need_search\":true}}\n\n" +
            "{0}\n\n" +
            "请返回优化后的搜索 JSON：";

        /// <summary>
        /// DuckDuckGo 搜索优化 - 系统提示词。
        /// </summary>
        public const string SearchOptimizationSystem_DuckDuckGo =
            "你只返回优化后的搜索关键词，不返回任何其他内容。";

        /// <summary>
        /// DuckDuckGo 搜索优化 - 用户提示词模板。
        /// {0} = 对话上下文 + 用户问题
        /// </summary>
        public const string SearchOptimizationUser_DuckDuckGo =
            "你是一个搜索查询优化助手。根据用户的问题，生成优化的联网搜索关键词。\n\n" +
            "规则：\n" +
            "1. 提取核心搜索意图，去除无关词汇\n" +
            "2. 关键词应简洁精准，不超过72个字符（一个汉字=2字符）\n" +
            "3. 如果用户不需要联网搜索，回复 NO_SEARCH\n" +
            "4. 只返回优化后的关键词本身，不要任何解释、标点或格式\n\n" +
            "5. 如果内容携带时间信息，请不要移除\n" +
            "{0}\n\n" +
            "优化后的关键词：";

        #endregion

        #region File Extraction Prompts

        /// <summary>
        /// 附件关键信息提取 - 系统提示词。
        /// </summary>
        public const string FileExtractionSystem =
            "你只返回从文档中提取的关键信息，不返回任何其他内容。如果无法提取有效信息，只回复 NO_INFO。";

        /// <summary>
        /// 附件关键信息提取 - 用户提示词模板。
        /// {0} = 用户问题
        /// {1} = 文件内容
        /// </summary>
        public const string FileExtractionUser =
            "你是一个信息提取助手。请从以下用户上传的文件内容中提取关键信息，用于优化联网搜索查询。\n\n" +
            "提取规则：\n" +
            "1. 提取文档中的核心主题、技术名词、版本号、API 名称、错误码等可搜索的关键信息\n" +
            "2. 忽略代码中的冗余细节（如变量赋值、注释），关注概念性、可检索的内容\n" +
            "3. 如果用户的问题指明了方向，优先提取与问题相关的信息\n" +
            "4. 输出格式：纯文本，简洁列出关键信息点，不超过 300 字\n" +
            "5. 如果文档内容与用户问题无关或无法提取有效信息，回复 NO_INFO\n" +
            "6. 只返回提取的信息，不要任何解释或格式标记\n\n" +
            "用户问题：{0}\n\n" +
            "文件内容：\n{1}\n\n" +
            "请提取关键信息：";

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
        /// 构建附件关键信息提取提示词。
        /// </summary>
        /// <param name="userQuestion">用户原始问题。</param>
        /// <param name="fileContent">已截断的文件内容（建议 ≤8000 字符）。</param>
        /// <returns>提取提示词。</returns>
        public static string BuildFileExtractionPrompt(string userQuestion, string fileContent)
        {
            return string.Format(FileExtractionUser, userQuestion, fileContent);
        }

        #endregion
    }
}
