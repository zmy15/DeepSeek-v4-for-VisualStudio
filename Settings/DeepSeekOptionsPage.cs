using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Drawing.Design;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// DeepSeek 选项页，对标共享项目 OptionPageGridGeneral。
    /// 通过 Tools → Options → DeepSeek Chat 访问。
    /// </summary>
    public class DeepSeekOptionsPage : DialogPage
    {
        /// <summary>
        /// 当用户在 Options 对话框中点击"确定"或"应用"时触发。
        /// 订阅此事件可实现设置热切换，无需重启聊天窗口。
        /// </summary>
        public static event Action? SettingsChanged;

        /// <summary>
        /// 全局实例引用，在 Package 初始化时设置，方便静态工具类读取设置。
        /// </summary>
        public static DeepSeekOptionsPage? Instance { get; set; }

        /// <summary>
        /// VS 在用户应用设置更改时调用此方法。
        /// 我们在此触发 SettingsChanged 事件以通知订阅者刷新配置。
        /// </summary>
        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                SettingsChanged?.Invoke();
            }
        }

        /// <summary>
        /// 安全加载设置存储。捕获因 VS 版本兼容性（如 IVsProfileLazyImportControl
        /// 在部分 VS 版本不可用）导致的 InvalidCastException，回退到默认值。
        /// </summary>
        public override void LoadSettingsFromStorage()
        {
            try
            {
                base.LoadSettingsFromStorage();
            }
            catch (InvalidCastException ex)
            {
                Logger.Warn($"[Settings] LoadSettingsFromStorage 失败（VS 版本兼容性）: {ex.Message}");
            }
        }
        [LocalizedCategory("settings.category.api")]
        [LocalizedDisplayName("settings.apiKey.displayName")]
        [LocalizedDescription("settings.apiKey.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ApiKey { get; set; } = string.Empty;

        [LocalizedCategory("settings.category.api")]
        [LocalizedDisplayName("settings.systemPrompt.displayName")]
        [LocalizedDescription("settings.systemPrompt.description")]
        [Editor(typeof(System.ComponentModel.Design.MultilineStringEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SystemPrompt { get; set; } = AiPrompts.DefaultSystemPrompt;

        [LocalizedCategory("settings.category.api")]
        [LocalizedDisplayName("settings.systemPromptEn.displayName")]
        [LocalizedDescription("settings.systemPromptEn.description")]
        [Editor(typeof(System.ComponentModel.Design.MultilineStringEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string SystemPromptEn { get; set; } = AiPrompts.DefaultSystemPromptEn;

        /// <summary>
        /// 根据当前语言设置获取有效的 System Prompt。
        /// - 英文模式（Language == "en"）：优先使用 SystemPromptEn，为空时回退英文默认值。
        /// - 中文/自动模式：优先使用 SystemPrompt，为空时回退当前语言默认值。
        /// </summary>
        public string GetEffectiveSystemPrompt()
        {
            bool isEnglish = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
            if (isEnglish)
            {
                string enPrompt = SystemPromptEn ?? string.Empty;
                return !string.IsNullOrWhiteSpace(enPrompt) ? enPrompt : AiPrompts.DefaultSystemPromptEn;
            }
            string prompt = SystemPrompt ?? string.Empty;
            return !string.IsNullOrWhiteSpace(prompt) ? prompt : AiPrompts.DefaultSystemPrompt;
        }

        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.selectedModel.displayName")]
        [LocalizedDescription("settings.selectedModel.description")]
        [TypeConverter(typeof(ModelListConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SelectedModel { get; set; } = "deepseek-v4-pro";

        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.enableThinking.displayName")]
        [LocalizedDescription("settings.enableThinking.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public bool IsThinkingEnabled { get; set; } = true;

        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.reasoningEffort.displayName")]
        [LocalizedDescription("settings.reasoningEffort.description")]
        [TypeConverter(typeof(ReasoningEffortConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ReasoningEffort { get; set; } = "high";

        [LocalizedCategory("settings.category.webSearch")]
        [LocalizedDisplayName("settings.enableWebSearch.displayName")]
        [LocalizedDescription("settings.enableWebSearch.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableWebSearch { get; set; } = true;

        [LocalizedCategory("settings.category.webSearch")]
        [LocalizedDisplayName("settings.searchProvider.displayName")]
        [LocalizedDescription("settings.searchProvider.description")]
        [TypeConverter(typeof(SearchProviderConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string SearchProvider { get; set; } = "DuckDuckGo";

        [LocalizedCategory("settings.category.webSearch")]
        [LocalizedDisplayName("settings.baiduApiKey.displayName")]
        [LocalizedDescription("settings.baiduApiKey.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string BaiduApiKey { get; set; } = string.Empty;

        [LocalizedCategory("settings.category.editor")]
        [LocalizedDisplayName("settings.showDiffMarkers.displayName")]
        [LocalizedDescription("settings.showDiffMarkers.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool ShowDiffMarkersInEditor { get; set; } = true;

        [LocalizedCategory("settings.category.ocr")]
        [LocalizedDisplayName("settings.ocrEngine.displayName")]
        [LocalizedDescription("settings.ocrEngine.description")]
        [TypeConverter(typeof(OcrEngineConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string OcrEngine { get; set; } = "Windows Built-in";

        // ═══════════════════════════════════════════════
        //  DeepSeek 自动补全（幽灵文本）设置
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.autocomplete")]
        [LocalizedDisplayName("settings.autocompleteEnabled.displayName")]
        [LocalizedDescription("settings.autocompleteEnabled.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool AutoCompleteEnabled { get; set; } = false;

        [LocalizedCategory("settings.category.autocomplete")]
        [LocalizedDisplayName("settings.autocompleteDelay.displayName")]
        [LocalizedDescription("settings.autocompleteDelay.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int AutoCompleteDelay { get; set; } = 800;

        [LocalizedCategory("settings.category.autocomplete")]
        [LocalizedDisplayName("settings.autocompleteContinueAfterAccept.displayName")]
        [LocalizedDescription("settings.autocompleteContinueAfterAccept.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool AutoCompleteContinueAfterAccept { get; set; } = true;

        [LocalizedCategory("settings.category.autocomplete")]
        [LocalizedDisplayName("settings.autocompleteModel.displayName")]
        [LocalizedDescription("settings.autocompleteModel.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string AutoCompleteModel { get; set; } = string.Empty;

        // ═══════════════════════════════════════════════
        //  上下文管理设置（DeepSeek V4 1M 上下文窗口）
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.tokenBudget.displayName")]
        [LocalizedDescription("settings.tokenBudget.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int TokenBudget { get; set; } = 900_000;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.enableAutoCompression.displayName")]
        [LocalizedDescription("settings.enableAutoCompression.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableAutoCompression { get; set; } = true;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.compressionThreshold.displayName")]
        [LocalizedDescription("settings.compressionThreshold.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int CompressionThreshold { get; set; } = 85;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.preserveRecentTurns.displayName")]
        [LocalizedDescription("settings.preserveRecentTurns.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int PreserveRecentTurns { get; set; } = 3;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.enableRag.displayName")]
        [LocalizedDescription("settings.enableRag.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableRag { get; set; } = false;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.ragTopK.displayName")]
        [LocalizedDescription("settings.ragTopK.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int RagTopK { get; set; } = 5;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.showContextStats.displayName")]
        [LocalizedDescription("settings.showContextStats.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool ShowContextStats { get; set; } = true;

        // ═══════════════════════════════════════════════
        //  国际化 (i18n) 设置
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.i18n")]
        [LocalizedDisplayName("settings.language.displayName")]
        [LocalizedDescription("settings.language.description")]
        [TypeConverter(typeof(LanguageConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string Language { get; set; } = "auto";

        // ═══════════════════════════════════════════════
        //  审批模式设置
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.approval")]
        [LocalizedDisplayName("settings.approvalMode.displayName")]
        [LocalizedDescription("settings.approvalMode.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string ApprovalMode { get; set; } = "SmartBlock";

        // ═══════════════════════════════════════════════
        //  界面主题设置
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.appearance")]
        [LocalizedDisplayName("settings.themeMode.displayName")]
        [LocalizedDescription("settings.themeMode.description")]
        [TypeConverter(typeof(ThemeModeConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string ThemeModeString
        {
            get => _themeMode == ThemeMode.Auto ? "Auto" : _themeMode == ThemeMode.Dark ? "Dark" : "Light";
            set
            {
                _themeMode = value switch
                {
                    "Dark" => ThemeMode.Dark,
                    "Light" => ThemeMode.Light,
                    _ => ThemeMode.Auto
                };
            }
        }

        private ThemeMode _themeMode = ThemeMode.Auto;

        /// <summary>
        /// 获取/设置主题模式（强类型版本，供代码使用）。
        /// </summary>
        [System.ComponentModel.Browsable(false)]
        public ThemeMode ThemeMode
        {
            get => _themeMode;
            set
            {
                _themeMode = value;
                // 同步通知 ThemeService（可能尚未初始化，安全忽略）
                try { ThemeService.Instance.UserThemeMode = value; } catch { }
            }
        }
    }

    /// <summary>
    /// 模型列表下拉选项。
    /// </summary>
    internal class ModelListConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "deepseek-v4-pro", "deepseek-v4-flash" });
    }

    /// <summary>
    /// 推理强度下拉选项。
    /// </summary>
    internal class ReasoningEffortConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "high", "max" });
    }

    /// <summary>
    /// 搜索提供商下拉选项。
    /// </summary>
    internal class SearchProviderConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Baidu", "DuckDuckGo" });
    }

    /// <summary>
    /// OCR 引擎下拉选项（PaddleOCR-Sharp 已移除以减小包体，仍可通过 MCP 使用远程 OCR）。
    /// </summary>
    internal class OcrEngineConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Windows Built-in" });
    }

    /// <summary>
    /// 语言选择下拉选项。
    /// </summary>
    internal class LanguageConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "auto", "zh-CN", "en" });
    }

    /// <summary>
    /// 主题模式下拉选项。
    /// </summary>
    internal class ThemeModeConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Auto", "Dark", "Light" });
    }
}
