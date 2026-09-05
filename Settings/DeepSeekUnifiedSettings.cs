using DeepSeek_v4_for_VisualStudio.Models;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;
#pragma warning disable VSEXTPREVIEW_SETTINGS // Settings API is experimental

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
    /// Declares the non-sensitive DeepSeek settings shown by VS2026 Unified Settings.
    /// API keys are stored in Visual Studio Credential Storage and intentionally stay
    /// out of Unified Settings.
    /// </summary>
    [VisualStudioContribution]
    internal static class DeepSeekUnifiedSettings
    {
        [VisualStudioContribution]
        internal static SettingCategory GeneralCategory { get; } =
            new("deepseekGeneral", "%DeepSeek.Chat.Settings.CategoryTitle%")
            {
                Description = "%DeepSeek.Chat.Settings.CategoryDescription%",
                GenerateObserverClass = true,
            };

        [VisualStudioContribution]
        internal static Setting.String ApiKeyConfigurationGuide { get; } =
            new(
                "deepseekApiKeyGuide",
                "%DeepSeek.Chat.Settings.ApiKeyGuideTitle%",
                GeneralCategory,
                defaultValue: "工具 → 选项 → DeepSeek Chat → General")
            {
                Description = "%DeepSeek.Chat.Settings.ApiKeyGuideDescription%",
                SearchKeywords = new[] { "API", "密钥", "ApiKey", "Key", "Options" },
                Messages = new[]
                {
                    new SettingMessage("%DeepSeek.Chat.Settings.ApiKeyGuideMessage%"),
                },
                EnabledWhen = SettingRule.FeatureFlag("DeepSeek.ApiKeyGuideReadOnly", true),
            };

        [VisualStudioContribution]
        internal static Setting.String SystemPrompt { get; } =
            new("deepseekSystemPrompt", "%DeepSeek.Chat.settings.systemPrompt.displayName%", GeneralCategory, defaultValue: string.Empty)
            {
                Description = "%DeepSeek.Chat.settings.systemPrompt.description%",
            };

        [VisualStudioContribution]
        internal static Setting.String SystemPromptEn { get; } =
            new("deepseekSystemPromptEn", "%DeepSeek.Chat.settings.systemPromptEn.displayName%", GeneralCategory, defaultValue: string.Empty)
            {
                Description = "%DeepSeek.Chat.settings.systemPromptEn.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Enum SelectedModel { get; } =
            new(
                "deepseekModel",
                "%DeepSeek.Chat.settings.selectedModel.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry(DeepSeekModelCatalog.Pro, DeepSeekModelCatalog.Pro),
                    new EnumSettingEntry(DeepSeekModelCatalog.Flash, DeepSeekModelCatalog.Flash),
                    new EnumSettingEntry(DeepSeekModelCatalog.FlashVisionExp, DeepSeekModelCatalog.FlashVisionExp),
                },
                defaultValue: DeepSeekModelCatalog.Pro)
            {
                Description = "%DeepSeek.Chat.settings.selectedModel.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ThinkingEnabled { get; } =
            new("deepseekThinking", "%DeepSeek.Chat.settings.enableThinking.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.enableThinking.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Enum ReasoningEffort { get; } =
            new(
                "deepseekReasoningEffort",
                "%DeepSeek.Chat.settings.reasoningEffort.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("high", "High"),
                    new EnumSettingEntry("max", "Max"),
                },
                defaultValue: "high")
            {
                Description = "%DeepSeek.Chat.settings.reasoningEffort.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableWebSearch { get; } =
            new("deepseekWebSearch", "%DeepSeek.Chat.chat.html.webSearchLabel%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.enableWebSearch.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Enum SearchProvider { get; } =
            new(
                "deepseekSearchProvider",
                "%DeepSeek.Chat.settings.searchProvider.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("Baidu", "Baidu"),
                    new EnumSettingEntry("Bing", "Bing"),
                    new EnumSettingEntry("DuckDuckGo", "%DeepSeek.Chat.websearch.searchEngine.duckduckgo%"),
                },
                defaultValue: "DuckDuckGo")
            {
                Description = "%DeepSeek.Chat.settings.searchProvider.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ShowDiffMarkers { get; } =
            new("deepseekShowDiffMarkers", "%DeepSeek.Chat.settings.showDiffMarkers.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.showDiffMarkers.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Enum OcrEngine { get; } =
            new(
                "deepseekOcrEngine",
                "%DeepSeek.Chat.settings.ocrEngine.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("Windows Built-in", "%DeepSeek.Chat.Settings.OcrWindowsBuiltIn%"),
                    new EnumSettingEntry("PaddleOCR-Sharp", "%DeepSeek.Chat.Settings.OcrPaddleLocal%"),
                },
                defaultValue: "Windows Built-in")
            {
                Description = "%DeepSeek.Chat.settings.ocrEngine.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean AutoCompleteEnabled { get; } =
            new("deepseekAutoCompleteEnabled", "%DeepSeek.Chat.settings.autocompleteEnabled.displayName%", GeneralCategory, defaultValue: false)
            {
                Description = "%DeepSeek.Chat.settings.autocompleteEnabled.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer AutoCompleteDelay { get; } =
            new("deepseekAutoCompleteDelay", "%DeepSeek.Chat.settings.autocompleteDelay.displayName%", GeneralCategory, defaultValue: 800)
            {
                Description = "%DeepSeek.Chat.settings.autocompleteDelay.description%",
                Minimum = 100,
                Maximum = 5000,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean AutoCompleteContinueAfterAccept { get; } =
            new("deepseekAutoCompleteContinueAfterAccept", "%DeepSeek.Chat.settings.autocompleteContinueAfterAccept.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.autocompleteContinueAfterAccept.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer TokenBudget { get; } =
            new("deepseekTokenBudget", "%DeepSeek.Chat.settings.tokenBudget.displayName%", GeneralCategory, defaultValue: 900_000)
            {
                Description = "%DeepSeek.Chat.settings.tokenBudget.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableAutoCompression { get; } =
            new("deepseekAutoCompression", "%DeepSeek.Chat.settings.enableAutoCompression.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.enableAutoCompression.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer CompressionThreshold { get; } =
            new("deepseekCompressionThreshold", "%DeepSeek.Chat.settings.compressionThreshold.displayName%", GeneralCategory, defaultValue: 85)
            {
                Description = "%DeepSeek.Chat.settings.compressionThreshold.description%",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Integer PreserveRecentTurns { get; } =
            new("deepseekPreserveRecentTurns", "%DeepSeek.Chat.settings.preserveRecentTurns.displayName%", GeneralCategory, defaultValue: 3)
            {
                Description = "%DeepSeek.Chat.settings.preserveRecentTurns.description%",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableRag { get; } =
            new("deepseekEnableRag", "%DeepSeek.Chat.settings.enableRag.displayName%", GeneralCategory, defaultValue: false)
            {
                Description = "%DeepSeek.Chat.settings.enableRag.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer RagTopK { get; } =
            new("deepseekRagTopK", "%DeepSeek.Chat.settings.ragTopK.displayName%", GeneralCategory, defaultValue: 5)
            {
                Description = "%DeepSeek.Chat.settings.ragTopK.description%",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ShowContextStats { get; } =
            new("deepseekContextStats", "%DeepSeek.Chat.settings.showContextStats.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.showContextStats.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableIdeContextInjection { get; } =
            new("deepseekIdeContext", "%DeepSeek.Chat.settings.enableIdeContextInjection.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.enableIdeContextInjection.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableTelemetryExport { get; } =
            new("deepseekTelemetryExport", "%DeepSeek.Chat.settings.enableTelemetryExport.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.enableTelemetryExport.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer LlmTimeoutSeconds { get; } =
            new("deepseekLlmTimeoutSeconds", "%DeepSeek.Chat.settings.llmTimeoutSeconds.displayName%", GeneralCategory, defaultValue: 300)
            {
                Description = "%DeepSeek.Chat.settings.llmTimeoutSeconds.description%",
                Minimum = 10,
                Maximum = 3600,
            };

        [VisualStudioContribution]
        internal static Setting.Enum Language { get; } =
            new(
                "deepseekLanguage",
                "%DeepSeek.Chat.settings.language.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("auto", "%DeepSeek.Chat.skills.help.typeAuto%"),
                    new EnumSettingEntry("zh-CN", "%DeepSeek.Chat.Settings.LanguageChineseSimplified%"),
                    new EnumSettingEntry("en", "English"),
                },
                defaultValue: "auto")
            {
                Description = "%DeepSeek.Chat.settings.language.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer MaxToolCallRounds { get; } =
            new("deepseekMaxToolCallRounds", "%DeepSeek.Chat.settings.maxToolCallRounds.displayName%", GeneralCategory, defaultValue: 200)
            {
                Description = "%DeepSeek.Chat.settings.maxToolCallRounds.description%",
                Minimum = 1,
                Maximum = 1000,
            };

        [VisualStudioContribution]
        internal static Setting.Integer MaxRepeatedSameCall { get; } =
            new("deepseekMaxRepeatedSameCall", "%DeepSeek.Chat.settings.maxRepeatedSameCall.displayName%", GeneralCategory, defaultValue: 5)
            {
                Description = "%DeepSeek.Chat.settings.maxRepeatedSameCall.description%",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Integer MaxConsecutiveErrors { get; } =
            new("deepseekMaxConsecutiveErrors", "%DeepSeek.Chat.settings.maxConsecutiveErrors.displayName%", GeneralCategory, defaultValue: 5)
            {
                Description = "%DeepSeek.Chat.settings.maxConsecutiveErrors.description%",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableAutoBuild { get; } =
            new("deepseekEnableAutoBuild", "%DeepSeek.Chat.settings.enableAutoBuild.displayName%", GeneralCategory, defaultValue: true)
            {
                Description = "%DeepSeek.Chat.settings.enableAutoBuild.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Enum ApprovalMode { get; } =
            new(
                "deepseekApprovalMode",
                "%DeepSeek.Chat.settings.approvalMode.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("BlockAll", "%DeepSeek.Chat.approval.blockAll%"),
                    new EnumSettingEntry("AllowAll", "%DeepSeek.Chat.approval.allowAll%"),
                    new EnumSettingEntry("SmartBlock", "%DeepSeek.Chat.approval.smartBlock%"),
                },
                defaultValue: "SmartBlock")
            {
                Description = "%DeepSeek.Chat.settings.approvalMode.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Enum ThemeMode { get; } =
            new(
                "deepseekThemeMode",
                "%DeepSeek.Chat.settings.themeMode.displayName%",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("Auto", "%DeepSeek.Chat.Settings.ThemeFollowVS%"),
                    new EnumSettingEntry("Dark", "%DeepSeek.Chat.theme.dark%"),
                    new EnumSettingEntry("Light", "%DeepSeek.Chat.theme.light%"),
                },
                defaultValue: "Auto")
            {
                Description = "%DeepSeek.Chat.settings.themeMode.description%",
            };

        [VisualStudioContribution]
        internal static Setting.Integer InputBoxHeight { get; } =
            new("deepseekInputBoxHeight", "%DeepSeek.Chat.settings.inputBoxHeight.displayName%", GeneralCategory, defaultValue: Settings.DeepSeekOptionsPage.DefaultInputBoxHeight)
            {
                Description = "%DeepSeek.Chat.settings.inputBoxHeight.description%",
                Minimum = Settings.DeepSeekOptionsPage.MinInputBoxHeight,
                Maximum = Settings.DeepSeekOptionsPage.MaxInputBoxHeight,
            };

        [VisualStudioContribution]
        internal static Setting.Integer BottomAreaScalePercent { get; } =
            new("deepseekBottomAreaScalePercent", "%DeepSeek.Chat.settings.bottomAreaScale.displayName%", GeneralCategory, defaultValue: Settings.DeepSeekOptionsPage.DefaultBottomAreaScalePercent)
            {
                Description = "%DeepSeek.Chat.settings.bottomAreaScale.description%",
                Minimum = Settings.DeepSeekOptionsPage.MinBottomAreaScalePercent,
                Maximum = Settings.DeepSeekOptionsPage.MaxBottomAreaScalePercent,
            };

        [VisualStudioContribution]
        internal static Setting.Integer WebView2ZoomPercent { get; } =
            new("deepseekWebView2ZoomPercent", "%DeepSeek.Chat.Settings.WebView2ZoomTitle%", GeneralCategory, defaultValue: Settings.DeepSeekOptionsPage.DefaultWebView2ZoomPercent)
            {
                Description = "%DeepSeek.Chat.Settings.WebView2ZoomDescription%",
                Minimum = Settings.DeepSeekOptionsPage.MinWebView2ZoomPercent,
                Maximum = Settings.DeepSeekOptionsPage.MaxWebView2ZoomPercent,
            };
    }
}
