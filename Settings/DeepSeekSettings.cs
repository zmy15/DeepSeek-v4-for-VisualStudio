using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;
using System.Runtime.Serialization;

#pragma warning disable VSEXTPREVIEW_SETTINGS // The settings API is currently in preview and marked as experimental

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    internal static class DeepSeekSettings
    {
        [VisualStudioContribution]
        public static SettingCategory SettingsCategory { get; } = new("deepSeekSettings", "DeepSeek Settings")
        {
            Description = "Settings for DeepSeek Visual Studio Extension",
            GenerateObserverClass = true,
        };

        [VisualStudioContribution]
        public static Setting.String ApiKeySetting { get; } = new("apiKey", "API Key", SettingsCategory, defaultValue: "")
        {
            Description = "API Key for DeepSeek",
        };

        [VisualStudioContribution]
        public static Setting.String SystemPromptSetting { get; } = new("systemPrompt", "System Prompt", SettingsCategory, defaultValue:
            "你是 DeepSeek Chat，一个深度集成在 Visual Studio 中的 AI 编程助手。" +
            "你的核心能力包括：解释代码逻辑、定位并修复 Bug、重构优化代码、生成单元测试、回答各类技术问题。" +
            "请遵循以下准则：\n" +
            "- 回答应简洁、准确、直接，优先给出可运行的代码方案。\n" +
            "- 涉及代码修改时，明确指出文件路径和具体行号。\n" +
            "- 优先使用用户项目已有的框架和库，不引入不必要的依赖。\n" +
            "- 如果用户的问题模糊不清，先追问澄清再给出建议。\n" +
            "- 使用中文回答，代码中的注释也使用中文。")
        {
            Description = "System prompt that defines the AI assistant's behavior and role",
        };
    }
}