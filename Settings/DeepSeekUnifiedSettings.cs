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
            new("deepseekGeneral", "DeepSeek Chat")
            {
                Description = "DeepSeek Chat model, thinking mode, web search and agent behavior settings.",
                GenerateObserverClass = true,
            };

        [VisualStudioContribution]
        internal static Setting.String ApiKeyConfigurationGuide { get; } =
            new(
                "deepseekApiKeyGuide",
                "API Key Configuration",
                GeneralCategory,
                defaultValue: "工具 → 选项 → DeepSeek Chat → General")
            {
                Description = "Configure your API key in Tools > Options > DeepSeek Chat > General.",
                SearchKeywords = new[] { "API", "密钥", "ApiKey", "Key", "Options" },
                Messages = new[]
                {
                    new SettingMessage("API keys are stored in Visual Studio Credential Storage and intentionally stay out of Unified Settings to avoid cloud sync/export leaks."),
                },
                EnabledWhen = SettingRule.FeatureFlag("DeepSeek.ApiKeyGuideReadOnly", true),
            };

        [VisualStudioContribution]
        internal static Setting.String SystemPrompt { get; } =
            new("deepseekSystemPrompt", "System Prompt", GeneralCategory, defaultValue: string.Empty)
            {
                Description = "System prompt that defines the AI assistant's behavior and role",
            };

        [VisualStudioContribution]
        internal static Setting.String SystemPromptEn { get; } =
            new("deepseekSystemPromptEn", "System Prompt (English)", GeneralCategory, defaultValue: string.Empty)
            {
                Description = "English version of the system prompt. Used when UI language is set to English.",
            };

        [VisualStudioContribution]
        internal static Setting.Enum SelectedModel { get; } =
            new(
                "deepseekModel",
                "Selected Model",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry(DeepSeekModelCatalog.Pro, DeepSeekModelCatalog.Pro),
                    new EnumSettingEntry(DeepSeekModelCatalog.Flash, DeepSeekModelCatalog.Flash),
                    new EnumSettingEntry(DeepSeekModelCatalog.FlashVisionExp, DeepSeekModelCatalog.FlashVisionExp),
                },
                defaultValue: DeepSeekModelCatalog.Pro)
            {
                Description = "The DeepSeek model to use",
            };

        [VisualStudioContribution]
        internal static Setting.String ApiBaseUrl { get; } =
            new("deepseekApiBaseUrl", "API Endpoint (Base URL)", GeneralCategory, defaultValue: string.Empty)
            {
                Description = "Compatible with any OpenAI chat/completions protocol endpoint. Leave empty to use the official DeepSeek endpoint (https://api.deepseek.com).",
                SearchKeywords = new[] { "URL", "endpoint", "baseUrl", "端点", "地址" },
            };

        [VisualStudioContribution]
        internal static Setting.String CustomModelName { get; } =
            new("deepseekCustomModelName", "Custom Model Name", GeneralCategory, defaultValue: string.Empty)
            {
                Description = "When non-empty, overrides the selected model above. Used for connecting to models on any chat/completions compatible endpoint.",
                SearchKeywords = new[] { "model", "custom", "模型", "自定义" },
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ThinkingEnabled { get; } =
            new("deepseekThinking", "Enable Deep Thinking", GeneralCategory, defaultValue: true)
            {
                Description = "Enable deep thinking mode (Reasoning)",
            };

        [VisualStudioContribution]
        internal static Setting.Enum ReasoningEffort { get; } =
            new(
                "deepseekReasoningEffort",
                "Reasoning Effort",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("high", "High"),
                    new EnumSettingEntry("max", "Max"),
                },
                defaultValue: "high")
            {
                Description = "Reasoning intensity: high or max",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableWebSearch { get; } =
            new("deepseekWebSearch", "Web Search", GeneralCategory, defaultValue: true)
            {
                Description = "Enable web search functionality. When enabled, a web search toggle will appear in the chat window.",
            };

        [VisualStudioContribution]
        internal static Setting.Enum SearchProvider { get; } =
            new(
                "deepseekSearchProvider",
                "Search Provider",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("Baidu", "Baidu"),
                    new EnumSettingEntry("Bing", "Bing"),
                    new EnumSettingEntry("DuckDuckGo", "DuckDuckGo"),
                },
                defaultValue: "DuckDuckGo")
            {
                Description = "Select search engine: Baidu (Baidu Qianfan, requires API Key, 1500 free/month), Bing (Azure, requires API Key, 1000 free/month), or DuckDuckGo (completely free)",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ShowDiffMarkers { get; } =
            new("deepseekShowDiffMarkers", "Show Diff Markers In Editor", GeneralCategory, defaultValue: true)
            {
                Description = "After AI writes code, show red/green line markers in the editor (green=added, red=deleted) with confirm/revert buttons. When disabled, changes take effect directly without preview.",
            };

        [VisualStudioContribution]
        internal static Setting.Enum OcrEngine { get; } =
            new(
                "deepseekOcrEngine",
                "OCR Engine",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("Windows Built-in", "Windows Built-in"),
                    new EnumSettingEntry("PaddleOCR-Sharp", "PaddleOCR-Sharp (local, offline)"),
                },
                defaultValue: "Windows Built-in")
            {
                Description = "Select image OCR engine:   • Windows Built-in — System built-in, no configuration needed, moderate accuracy   • PaddleOCR-Sharp — Local offline recognition with higher Chinese accuracy   • Remote OCR service via MCP protocol",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean AutoCompleteEnabled { get; } =
            new("deepseekAutoCompleteEnabled", "Enable Code Completion", GeneralCategory, defaultValue: false)
            {
                Description = "Enable inline code completion (ghost text) in the editor. When enabled, DeepSeek will provide code completion suggestions as you type. Press Tab to accept, Escape to cancel.",
            };

        [VisualStudioContribution]
        internal static Setting.Integer AutoCompleteDelay { get; } =
            new("deepseekAutoCompleteDelay", "Completion Delay (ms)", GeneralCategory, defaultValue: 800)
            {
                Description = "How many milliseconds to wait after you stop typing before requesting completion suggestions.",
                Minimum = 100,
                Maximum = 5000,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean AutoCompleteContinueAfterAccept { get; } =
            new("deepseekAutoCompleteContinueAfterAccept", "Continue Completion After Accept", GeneralCategory, defaultValue: true)
            {
                Description = "When enabled, accepting a completion immediately triggers a new prediction.",
            };

        [VisualStudioContribution]
        internal static Setting.Integer TokenBudget { get; } =
            new("deepseekTokenBudget", "Token Budget Limit", GeneralCategory, defaultValue: 900_000)
            {
                Description = "DeepSeek V4 has a 1M token context window. This setting controls the maximum tokens sent to the API (reserving 100K for model output). Default is 900,000. Reduce to lower API costs; increase for more context.",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableAutoCompression { get; } =
            new("deepseekAutoCompression", "Enable Auto Compression", GeneralCategory, defaultValue: true)
            {
                Description = "When context approaches the token budget, automatically compress early conversation into summaries rather than deleting old messages. When disabled, falls back to the old truncation behavior.",
            };

        [VisualStudioContribution]
        internal static Setting.Integer CompressionThreshold { get; } =
            new("deepseekCompressionThreshold", "Compression Trigger Threshold (%)", GeneralCategory, defaultValue: 85)
            {
                Description = "Trigger auto-compression when context usage reaches this percentage. Default 85%, i.e., triggers at ~765K tokens of a 900K budget.",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Integer PreserveRecentTurns { get; } =
            new("deepseekPreserveRecentTurns", "Preserve Recent Turns", GeneralCategory, defaultValue: 3)
            {
                Description = "During compression, keep the most recent N turns of conversation uncompressed. Default 3 turns. Increase to preserve more immediate context.",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableRag { get; } =
            new("deepseekEnableRag", "Enable RAG", GeneralCategory, defaultValue: false)
            {
                Description = "Enable Retrieval-Augmented Generation (RAG) to automatically retrieve relevant documents from the knowledge base before conversation. Requires configuring a RAG provider (such as a local vector database).",
            };

        [VisualStudioContribution]
        internal static Setting.Integer RagTopK { get; } =
            new("deepseekRagTopK", "RAG Retrieval Count", GeneralCategory, defaultValue: 5)
            {
                Description = "Maximum number of documents to retrieve from the knowledge base per query. Default 5.",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ShowContextStats { get; } =
            new("deepseekContextStats", "Context Stats Indicator", GeneralCategory, defaultValue: true)
            {
                Description = "Show current token usage in the status bar (used/budget).",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableIdeContextInjection { get; } =
            new("deepseekIdeContext", "Inject Editor Context", GeneralCategory, defaultValue: true)
            {
                Description = "On each message, automatically provide the AI with the active file, cursor position, selected code and a summary of current-file errors/warnings (injected as volatile context; does not affect prefix cache hits). Deep queries remain available via tools like get_errors.",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableTelemetryExport { get; } =
            new("deepseekTelemetryExport", "Export Session Metrics", GeneralCategory, defaultValue: true)
            {
                Description = "After each agent session, export metrics (TTFT, turns, tokens, tool calls) as a JSON file to %LocalAppData%\\\\DeepSeekVS\\\\telemetry\\\\ for performance analysis and benchmarking.",
            };

        [VisualStudioContribution]
        internal static Setting.Integer LlmTimeoutSeconds { get; } =
            new("deepseekLlmTimeoutSeconds", "LLM request timeout (seconds)", GeneralCategory, defaultValue: 300)
            {
                Description = "Timeout for a single LLM API request. Default 300 seconds; streaming has a separate 120-second no-data disconnect detection that is unaffected by this setting.",
                Minimum = 10,
                Maximum = 3600,
            };

        [VisualStudioContribution]
        internal static Setting.Enum Language { get; } =
            new(
                "deepseekLanguage",
                "Display Language / 显示语言",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("auto", "Auto"),
                    new EnumSettingEntry("zh-CN", "Chinese (Simplified)"),
                    new EnumSettingEntry("en", "English"),
                },
                defaultValue: "auto")
            {
                Description = "Choose display language. Select 'Auto' to follow system language. 选择显示语言。「自动」则跟随系统语言。",
            };

        [VisualStudioContribution]
        internal static Setting.Integer MaxToolCallRounds { get; } =
            new("deepseekMaxToolCallRounds", "Max Tool Call Rounds", GeneralCategory, defaultValue: 200)
            {
                Description = "Maximum number of tool call rounds allowed in a single agent session. The conversation will be forced to end with a warning when this limit is reached. Default: 200.",
                Minimum = 1,
                Maximum = 1000,
            };

        [VisualStudioContribution]
        internal static Setting.Integer MaxRepeatedSameCall { get; } =
            new("deepseekMaxRepeatedSameCall", "Repeat Call Detection Threshold", GeneralCategory, defaultValue: 5)
            {
                Description = "When the same tool is called with the same arguments more than this many times AND returns the same result each time, it is treated as an infinite loop and the conversation is terminated. Default: 5.",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Integer MaxConsecutiveErrors { get; } =
            new("deepseekMaxConsecutiveErrors", "Consecutive Error Termination Threshold", GeneralCategory, defaultValue: 5)
            {
                Description = "Terminate when all tool calls in N consecutive rounds return errors. Default: 5.",
                Minimum = 1,
                Maximum = 100,
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableAutoBuild { get; } =
            new("deepseekEnableAutoBuild", "Auto-build after code changes", GeneralCategory, defaultValue: true)
            {
                Description = "Whether to automatically invoke Build Agent to compile and verify after Edit Agent completes code modifications. When disabled, the build step appears as a button for you to trigger manually. You can also use phrases like 'don't build' or 'skip build' in your prompt to temporarily skip the build. Default: enabled.",
            };

        [VisualStudioContribution]
        internal static Setting.Enum ApprovalMode { get; } =
            new(
                "deepseekApprovalMode",
                "Approval Mode",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("BlockAll", "Block all"),
                    new EnumSettingEntry("AllowAll", "Allow all"),
                    new EnumSettingEntry("SmartBlock", "Smart block"),
                },
                defaultValue: "SmartBlock")
            {
                Description = "Controls the approval behavior for tool operations (terminal commands, file deletion, etc.): • SmartBlock — Only dangerous commands require approval; safe commands auto-approved • BlockAll — All terminal commands and file operations require user approval • AllowAll — Auto-approve all operations without asking ( use with caution)",
            };

        [VisualStudioContribution]
        internal static Setting.Enum ThemeMode { get; } =
            new(
                "deepseekThemeMode",
                "Theme",
                GeneralCategory,
                new[]
                {
                    new EnumSettingEntry("Auto", "Follow Visual Studio"),
                    new EnumSettingEntry("Dark", "Dark"),
                    new EnumSettingEntry("Light", "Light"),
                },
                defaultValue: "Auto")
            {
                Description = "Interface theme: Auto (follow VS), Dark, or Light",
            };

        [VisualStudioContribution]
        internal static Setting.Integer InputBoxHeight { get; } =
            new("deepseekInputBoxHeight", "Input Box Height", GeneralCategory, defaultValue: Settings.DeepSeekOptionsPage.DefaultInputBoxHeight)
            {
                Description = "Fixed height for the chat input box (50-500). Default: 50.",
                Minimum = Settings.DeepSeekOptionsPage.MinInputBoxHeight,
                Maximum = Settings.DeepSeekOptionsPage.MaxInputBoxHeight,
            };

        [VisualStudioContribution]
        internal static Setting.Integer BottomAreaScalePercent { get; } =
            new("deepseekBottomAreaScalePercent", "Bottom Area Scale", GeneralCategory, defaultValue: Settings.DeepSeekOptionsPage.DefaultBottomAreaScalePercent)
            {
                Description = "Scale all text and controls below WebView2 by percentage (50-300). Default: 100%.",
                Minimum = Settings.DeepSeekOptionsPage.MinBottomAreaScalePercent,
                Maximum = Settings.DeepSeekOptionsPage.MaxBottomAreaScalePercent,
            };

        [VisualStudioContribution]
        internal static Setting.Integer WebView2ZoomPercent { get; } =
            new("deepseekWebView2ZoomPercent", "WebView2 Zoom", GeneralCategory, defaultValue: Settings.DeepSeekOptionsPage.DefaultWebView2ZoomPercent)
            {
                Description = "Zoom level of the chat window (50% - 300%).",
                Minimum = Settings.DeepSeekOptionsPage.MinWebView2ZoomPercent,
                Maximum = Settings.DeepSeekOptionsPage.MaxWebView2ZoomPercent,
            };
    }
}
