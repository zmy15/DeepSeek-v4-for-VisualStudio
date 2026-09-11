using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// DeepSeek 选项页，对标共享项目 OptionPageGridGeneral。
    /// 通过 Tools → Options → DeepSeek Chat 访问。
    /// </summary>
    public class DeepSeekOptionsPage : DialogPage
    {
        internal const int MinInputBoxHeight = 50;
        internal const int MaxInputBoxHeight = 500;
        internal const int DefaultInputBoxHeight = 50;
        internal const int MinBottomAreaScalePercent = 50;
        internal const int MaxBottomAreaScalePercent = 300;
        internal const int DefaultBottomAreaScalePercent = 100;
        internal const int MinWebView2ZoomPercent = 50;
        internal const int MaxWebView2ZoomPercent = 300;
        internal const int DefaultWebView2ZoomPercent = 100;

        /// <summary>
        /// 静态构造：订阅语言变更，刷新属性描述符缓存。
        /// 注意：VS 选项对话框的分类标题在对话框打开期间无法热更新
        /// （VS 内部属性检查器缓存），关闭后重新打开即可生效。
        /// DisplayName 和 Description 不受此限制。
        /// </summary>
        static DeepSeekOptionsPage()
        {
            LocalizationService.Instance.LanguageChanged += (_, _) =>
            {
                TypeDescriptor.Refresh(typeof(DeepSeekOptionsPage));
            };
        }

        /// <summary>
        /// 当用户在 Options 对话框中点击"确定"或"应用"时触发。
        /// 订阅此事件可实现设置热切换，无需重启聊天窗口。
        /// </summary>
        public static event Action? SettingsChanged;
        /// <summary>
        /// 触发一次设置热更新（Unified Settings 桥接 SetValue 后调用）。
        /// </summary>
        internal void ApplyRuntimeHotUpdates()
        {
            ThemeService.Instance.UserThemeMode = ThemeMode;

            // Language affects resource loading before general subscribers refresh the UI.
            ApplyLanguageSetting();
            SettingsChanged?.Invoke();
        }

        private string _loadedApiKey = string.Empty;
        private string _loadedCustomApiKey = string.Empty;
        private string _loadedBaiduApiKey = string.Empty;
        private string _loadedBingApiKey = string.Empty;
        private bool _apiKeysDirty;
        private bool _apiKeysMigrationPending;

        /// <summary>
        /// 全局实例引用，在 Package 初始化时设置，方便静态工具类读取设置。
        /// </summary>
        public static DeepSeekOptionsPage? Instance { get; set; }

        /// <summary>解析自定义模型列表，保留输入顺序并去重（忽略大小写与首尾空白）。</summary>
        internal static IReadOnlyList<string> ParseCustomModels(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new List<string>();
            foreach (var part in value.Split(
                new[] { '\r', '\n', ';', '；', ',', '，' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                var model = part.Trim();
                if (model.Length == 0 || !seen.Add(model))
                    continue;
                models.Add(model);
            }

            return models;
        }

        internal IReadOnlyList<string> GetCustomModels()
            => ParseCustomModels(CustomModelName);

        /// <summary>解析用户手动勾选的多模态模型名单（官方与自定义模型共用）。</summary>
        internal IReadOnlyList<string> GetVisionModels()
            => ParseCustomModels(CustomVisionModels);

        /// <summary>归一化并写入用户勾选的多模态模型名单。</summary>
        internal void SetVisionModels(IEnumerable<string> models)
        {
            CustomVisionModels = string.Join(
                Environment.NewLine,
                ParseCustomModels(string.Join(Environment.NewLine, models)));
        }

        /// <summary>返回“选择模型”下拉框的统一显示文本。</summary>
        internal string GetSelectedModelChoice()
        {
            var config = DeepSeekEndpointResolver.Resolve(this);
            return config.IsCustom
                ? FormatCustomModelChoice(config.Model)
                : config.Model;
        }

        /// <summary>
        /// 写入统一模型选择：官方条目更新 SelectedModel，自定义条目更新
        /// ActiveCustomModel，并自动切换实际端点来源。
        /// </summary>
        internal void SetSelectedModelChoice(string? value)
        {
            var choice = value?.Trim() ?? string.Empty;
            string customSuffix = GetCustomModelSuffix();
            if (customSuffix.Length > 0 &&
                choice.EndsWith(customSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var model = choice.Substring(0, choice.Length - customSuffix.Length).Trim();
                if (model.Length > 0)
                {
                    var models = GetCustomModels()
                        .Union(new[] { model }, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    SetCustomModels(models);
                    ActiveCustomModel = model;
                    ActiveModelSource = "custom";
                }
                return;
            }

            SelectedModel = choice;
            ActiveModelSource = "official";
        }

        /// <summary>官方模型 + 自定义模型（自定义条目带来源后缀，避免同名冲突）。</summary>
        internal IReadOnlyList<string> GetModelChoices()
        {
            var choices = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var model in OfficialModelCatalogService.GetModels())
            {
                if (seen.Add(model))
                    choices.Add(model);
            }

            foreach (var model in GetCustomModels())
            {
                var display = FormatCustomModelChoice(model);
                if (seen.Add(display))
                    choices.Add(display);
            }

            var current = GetSelectedModelChoice();
            if (current.Length > 0 && seen.Add(current))
                choices.Add(current);

            return choices;
        }

        private static string GetCustomModelSuffix()
            => LocalizationService.Instance["chat.model.customSuffix"] ?? string.Empty;

        private static string FormatCustomModelChoice(string model)
            => model + GetCustomModelSuffix();

        /// <summary>返回当前应请求的自定义模型；激活项失效时回退列表第一项。</summary>
        internal string GetActiveCustomModel()
        {
            var models = GetCustomModels();
            var active = ActiveCustomModel?.Trim() ?? string.Empty;
            if (active.Length > 0)
            {
                var match = models.FirstOrDefault(model =>
                    string.Equals(model, active, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }

            return models.FirstOrDefault() ?? string.Empty;
        }

        /// <summary>归一化并写入模型列表；激活模型保留在列表中，否则回退到第一项。</summary>
        internal void SetCustomModels(IEnumerable<string> models)
        {
            var modelList = models?.Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .ToArray() ?? Array.Empty<string>();
            CustomModelName = string.Join(Environment.NewLine, modelList);
            ActiveCustomModel = GetActiveCustomModel();
        }

        /// <summary>
        /// VS 在用户应用设置更改时调用此方法。
        /// 我们在此触发 SettingsChanged 事件以通知订阅者刷新配置。
        /// </summary>
        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                // ── 同步静态 Instance 到被 VS 实际应用的规范 DialogPage 实例 ──
                // 避免后续通过 Options/Instance 读取到包初始化阶段的过期内存实例。
                if (!ReferenceEquals(Instance, this))
                {
                    Instance = this;
                }

                // ── 语言设置：直接读取本页被 VS 应用后的最新 Language 值，立即生效 ──
                // 不能依赖静态 DeepSeekOptionsPage.Instance（它可能是尚未同步到
                // 规范 DialogPage 的过期实例），否则用户手动选择的语言会被静默丢弃、
                // 回退到自动检测（中文），表现为"切换失效"。
                ApplyLanguageSetting();
                SettingsChanged?.Invoke();

                // ── 旧页改动 → 推送到 Unified Settings（新版设置 UI 同步）──
                UnifiedSettingsSync.PushFromPage(this);
            }
        }

        /// <summary>
        /// 将本页当前的 Language 设置应用到 LocalizationService。
        /// 在 OnApply 中调用，读取的一定是 VS 刚刚写入本页的最新值。
        /// </summary>
        private void ApplyLanguageSetting()
        {
            try
            {
                string language = Language;
                if (string.IsNullOrEmpty(language) ||
                    string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    LocalizationService.Instance.Initialize(null);
                    Logger.Info($"[I18n] 语言设置已应用: auto → {LocalizationService.Instance.CurrentLanguage}");
                }
                else
                {
                    LocalizationService.Instance.SetLanguage(language);
                    Logger.Info($"[I18n] 语言设置已应用: {language}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[I18n] 应用语言设置失败: {ex.Message}");
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
                LoadApiKeysFromCredentialStore();
            }
            catch (InvalidCastException ex)
            {
                Logger.Warn($"[Settings] LoadSettingsFromStorage 失败（VS 版本兼容性）: {ex.Message}");
            }
        }

        /// <summary>
        /// API Key 采用双持久化：优先读写 Visual Studio Credential Storage，
        /// 同时保留 DPAPI 加密备份，避免 keychain 瞬时故障导致密钥丢失。
        /// </summary>
        public override void SaveSettingsToStorage()
        {
            string apiKey = ApiKey;
            string customApiKey = CustomApiKey;
            string baiduApiKey = BaiduApiKey;
            string bingApiKey = BingApiKey;
            var credentialStore = VisualStudioApiKeyStore.Current;
            // DialogPage hosts can save before OnApply, so detect changes from the loaded
            // baseline here. OnApply is too late to influence credential writes.
            _apiKeysDirty = HasApiKeyChanges(apiKey, customApiKey, baiduApiKey, bingApiKey);
            bool shouldWriteCredentialStore = _apiKeysDirty || _apiKeysMigrationPending;
            bool credentialStoreUpdated = shouldWriteCredentialStore
                && credentialStore != null
                && SaveCredential(credentialStore, ApiKeyKind.DeepSeek, apiKey)
                && SaveCredential(credentialStore, ApiKeyKind.Custom, customApiKey)
                && SaveCredential(credentialStore, ApiKeyKind.Baidu, baiduApiKey)
                && SaveCredential(credentialStore, ApiKeyKind.Bing, bingApiKey);

            // Keep the DPAPI-encrypted DialogPage backup at all times. The VS keychain is
            // preferred at runtime, but this backup prevents a transient keychain failure
            // from turning into permanent credential loss.
            try
            {
                ApiKey = ApiKeyProtection.Protect(apiKey);
                CustomApiKey = ApiKeyProtection.Protect(customApiKey);
                BaiduApiKey = ApiKeyProtection.Protect(baiduApiKey);
                BingApiKey = ApiKeyProtection.Protect(bingApiKey);
                base.SaveSettingsToStorage();
            }
            finally
            {
                ApiKey = apiKey;
                CustomApiKey = customApiKey;
                BaiduApiKey = baiduApiKey;
                BingApiKey = bingApiKey;
            }

            if (_apiKeysDirty)
            {
                _loadedApiKey = apiKey;
                _loadedCustomApiKey = customApiKey;
                _loadedBaiduApiKey = baiduApiKey;
                _loadedBingApiKey = bingApiKey;
                _apiKeysDirty = false;
            }

            if (_apiKeysMigrationPending && credentialStoreUpdated)
            {
                _apiKeysMigrationPending = false;
            }
        }

        private void LoadApiKeysFromCredentialStore()
        {
            string legacyApiKey = ApiKeyProtection.Unprotect(ApiKey);
            string legacyCustomApiKey = ApiKeyProtection.Unprotect(CustomApiKey);
            string legacyBaiduApiKey = ApiKeyProtection.Unprotect(BaiduApiKey);
            string legacyBingApiKey = ApiKeyProtection.Unprotect(BingApiKey);

            var store = VisualStudioApiKeyStore.Current;
            if (store == null)
            {
                ApiKey = legacyApiKey;
                CustomApiKey = legacyCustomApiKey;
                BaiduApiKey = legacyBaiduApiKey;
                BingApiKey = legacyBingApiKey;
                _apiKeysMigrationPending =
                    !string.IsNullOrWhiteSpace(legacyApiKey) ||
                    !string.IsNullOrWhiteSpace(legacyCustomApiKey) ||
                    !string.IsNullOrWhiteSpace(legacyBaiduApiKey) ||
                    !string.IsNullOrWhiteSpace(legacyBingApiKey);
                _loadedApiKey = ApiKey;
                _loadedCustomApiKey = CustomApiKey;
                _loadedBaiduApiKey = BaiduApiKey;
                _loadedBingApiKey = BingApiKey;
                _apiKeysDirty = false;
                return;
            }

            ApiKey = GetCredentialOrMigrateLegacy(store, ApiKeyKind.DeepSeek, legacyApiKey);
            CustomApiKey = GetCredentialOrMigrateLegacy(store, ApiKeyKind.Custom, legacyCustomApiKey);
            BaiduApiKey = GetCredentialOrMigrateLegacy(store, ApiKeyKind.Baidu, legacyBaiduApiKey);
            BingApiKey = GetCredentialOrMigrateLegacy(store, ApiKeyKind.Bing, legacyBingApiKey);

            _loadedApiKey = ApiKey;
            _loadedCustomApiKey = CustomApiKey;
            _loadedBaiduApiKey = BaiduApiKey;
            _loadedBingApiKey = BingApiKey;
            _apiKeysDirty = false;
            _apiKeysMigrationPending =
                (!string.IsNullOrWhiteSpace(legacyApiKey) && !store.TryGet(ApiKeyKind.DeepSeek, out _)) ||
                (!string.IsNullOrWhiteSpace(legacyCustomApiKey) && !store.TryGet(ApiKeyKind.Custom, out _)) ||
                (!string.IsNullOrWhiteSpace(legacyBaiduApiKey) && !store.TryGet(ApiKeyKind.Baidu, out _)) ||
                (!string.IsNullOrWhiteSpace(legacyBingApiKey) && !store.TryGet(ApiKeyKind.Bing, out _));
        }

        private static string GetCredentialOrMigrateLegacy(
            IApiKeyStore store,
            ApiKeyKind kind,
            string legacyValue)
        {
            if (store.TryGet(kind, out string value))
            {
                // 旧版本曾把 DPAPI 备份密文同步进 Credential Storage。Keychain 是
                // 运行时主来源，因此这里必须保证返回明文；解密失败时按未配置处理。
                // 注：解密失败时直接返回空串而不回退 legacyValue —— DPAPI 备份与
                // Keychain 内的是同一密钥加密的密文，两者解密成败一致，回退无收益。
                return ApiKeyProtection.Unprotect(value);
            }

            if (string.IsNullOrWhiteSpace(legacyValue))
            {
                return string.Empty;
            }

            // Keep the legacy value usable even if the keychain write fails; Save will then
            // fall back to DPAPI instead of losing the user's key.
            store.Set(kind, legacyValue);
            return legacyValue;
        }

        private bool SaveCredential(IApiKeyStore store, ApiKeyKind kind, string value)
        {
            var runtimeValue = ApiKeyProtection.Unprotect(value);

            // An empty in-memory value can also mean "the credential store was not readable
            // during startup". Only clear it when the user explicitly edited this page.
            if (string.IsNullOrWhiteSpace(value))
            {
                return !_apiKeysDirty || store.Clear(kind);
            }

            // value 非空但解密结果为空 → DPAPI 解密失败（跨用户/凭据变更），
            // 绝不能 Clear 删除用户的密钥，视为本次未写入，保留原凭据等待下次成功。
            if (string.IsNullOrWhiteSpace(runtimeValue))
            {
                Logger.Warn($"[Settings] {kind} 解密失败，Keychain 写入中止（保留原凭据）");
                return true;
            }

            return store.Set(kind, runtimeValue);
        }

        private bool HasApiKeyChanges(string apiKey, string customApiKey, string baiduApiKey, string bingApiKey)
        {
            return !string.Equals(apiKey, _loadedApiKey, StringComparison.Ordinal) ||
                !string.Equals(customApiKey, _loadedCustomApiKey, StringComparison.Ordinal) ||
                !string.Equals(baiduApiKey, _loadedBaiduApiKey, StringComparison.Ordinal) ||
                !string.Equals(bingApiKey, _loadedBingApiKey, StringComparison.Ordinal);
        }

        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.apiKey.displayName")]
        [LocalizedDescription("settings.apiKey.description")]
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 自定义端点 API 密钥，与 DeepSeek 官方密钥分离存储。
        /// 仅当 ApiBaseUrl 非空时作为运行时密钥使用。
        /// </summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.customApiKey.displayName")]
        [LocalizedDescription("settings.customApiKey.description")]
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string CustomApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 自定义端点 (Base URL)，OpenAI chat/completions 协议兼容。
        /// 非空时启用自定义端点模式，与本分类的密钥、模型名称配套使用。
        /// 留空时使用 DeepSeek 官方服务与官方密钥。
        /// </summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.apiBaseUrl.displayName")]
        [LocalizedDescription("settings.apiBaseUrl.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// 自定义端点可用的模型列表；支持换行、英文分号/逗号和中文分号/逗号分隔。
        /// </summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.customModelName.displayName")]
        [LocalizedDescription("settings.customModelName.description")]
        [Editor(typeof(System.ComponentModel.Design.MultilineStringEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string CustomModelName { get; set; } = string.Empty;

        /// <summary>
        /// 用户手动勾选为支持图片/PDF 直传（多模态）的模型名单；
        /// 官方接口模型与自定义端点模型共用同一份名单。
        /// </summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.visionModels.displayName")]
        [LocalizedDescription("settings.visionModels.description")]
        [Editor(typeof(VisionModelPickerEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string CustomVisionModels { get; set; } = string.Empty;

        /// <summary>自定义模型列表中的当前激活模型；聊天窗口选择自定义条目时更新。</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string ActiveCustomModel { get; set; } = string.Empty;

        /// <summary>属性网格中的“从自定义端点添加模型”入口；不持久化自身值。</summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.customModelPicker.displayName")]
        [LocalizedDescription("settings.customModelPicker.description")]
        [Editor(typeof(ModelPickerEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string CustomModelPicker
        {
            get => string.Empty;
            set { /* 值由 ModelPickerEditor 写入模型列表。 */ }
        }

        /// <summary>属性网格中的“测试连接”入口；不持久化自身值。</summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.testConnection.displayName")]
        [LocalizedDescription("settings.testConnection.description")]
        [Editor(typeof(TestConnectionEditor), typeof(UITypeEditor))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string TestConnection
        {
            get => string.Empty;
            set { /* 值由 TestConnectionEditor 读取当前端点配置并执行校验。 */ }
        }

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

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string SelectedModel { get; set; } = "deepseek-v4-pro";

        /// <summary>
        /// 设置页使用的统一模型选择器；官方与自定义条目共用，
        /// 实际值分别落到 SelectedModel / ActiveCustomModel 与 ActiveModelSource。
        /// </summary>
        [LocalizedCategory("settings.category.model")]
        [LocalizedDisplayName("settings.selectedModel.displayName")]
        [LocalizedDescription("settings.selectedModel.description")]
        [TypeConverter(typeof(ModelListConverter))]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string SelectedModelChoice
        {
            get => GetSelectedModelChoice();
            set => SetSelectedModelChoice(value);
        }

        /// <summary>
        /// 模型来源：auto 跟随端点配置（填写了自定义端点即用自定义）；
        /// official 强制 DeepSeek 官方服务；custom 强制自定义端点。
        /// 聊天窗口模型下拉框选择官方/自定义条目时自动更新。
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)] // Fix for WFO1000
        public string ActiveModelSource { get; set; } = "auto";

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
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string BaiduApiKey { get; set; } = string.Empty;

        [LocalizedCategory("settings.category.webSearch")]
        [LocalizedDisplayName("settings.bingApiKey.displayName")]
        [LocalizedDescription("settings.bingApiKey.description")]
        [PasswordPropertyText(true)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string BingApiKey { get; set; } = string.Empty;

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

        /// <summary>旧实例设置迁移已完成标记（防止迁移值再次覆盖新版本中用户手动修改的设置）。</summary>
        /// P1-5b：必须加 DesignerSerializationVisibility(Visible) 才会被 DialogPage 序列化，
        /// 否则每次启动复位为 false，导致迁移反复执行、反复用旧值覆盖用户新改的设置。
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)]
        public bool LegacySettingsMigrated { get; set; } = false;

        // ═══════════════════════════════════════════════
        //  可观测性 (Telemetry) 设置 — P0
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.enableTelemetryExport.displayName")]
        [LocalizedDescription("settings.enableTelemetryExport.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableTelemetryExport { get; set; } = true;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.enableIdeContextInjection.displayName")]
        [LocalizedDescription("settings.enableIdeContextInjection.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableIdeContextInjection { get; set; } = true;

        [LocalizedCategory("settings.category.context")]
        [LocalizedDisplayName("settings.llmTimeoutSeconds.displayName")]
        [LocalizedDescription("settings.llmTimeoutSeconds.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int LlmTimeoutSeconds { get; set; } = 300;

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
        //  Agent 行为设置
        // ═══════════════════════════════════════════════

        [LocalizedCategory("settings.category.agent")]
        [LocalizedDisplayName("settings.maxToolCallRounds.displayName")]
        [LocalizedDescription("settings.maxToolCallRounds.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int MaxToolCallRounds { get; set; } = 200;

        [LocalizedCategory("settings.category.agent")]
        [LocalizedDisplayName("settings.maxRepeatedSameCall.displayName")]
        [LocalizedDescription("settings.maxRepeatedSameCall.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int MaxRepeatedSameCall { get; set; } = 5;

        [LocalizedCategory("settings.category.agent")]
        [LocalizedDisplayName("settings.maxConsecutiveErrors.displayName")]
        [LocalizedDescription("settings.maxConsecutiveErrors.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int MaxConsecutiveErrors { get; set; } = 5;

        [LocalizedCategory("settings.category.agent")]
        [LocalizedDisplayName("settings.enableAutoBuild.displayName")]
        [LocalizedDescription("settings.enableAutoBuild.description")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool EnableAutoBuild { get; set; } = true;

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
        [DefaultValue(ThemeMode.Auto)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string ThemeModeString
        {
            get => ThemeMode switch
            {
                ThemeMode.Dark => "Dark",
                ThemeMode.Light => "Light",
                _ => "Auto",
            };
            set => ThemeMode = value switch
            {
                "Dark" => ThemeMode.Dark,
                "Light" => ThemeMode.Light,
                _ => ThemeMode.Auto,
            };
        }

        private int _inputBoxHeight = DefaultInputBoxHeight;

        [LocalizedCategory("settings.category.appearance")]
        [LocalizedDisplayName("settings.inputBoxHeight.displayName")]
        [LocalizedDescription("settings.inputBoxHeight.description")]
        [DefaultValue(DefaultInputBoxHeight)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int InputBoxHeight
        {
            get => _inputBoxHeight;
            set => _inputBoxHeight = NormalizeInputBoxHeight(value);
        }

        internal static int NormalizeInputBoxHeight(int value)
        {
            if (value < MinInputBoxHeight) return MinInputBoxHeight;
            if (value > MaxInputBoxHeight) return MaxInputBoxHeight;
            return value;
        }

        private int _bottomAreaScalePercent = DefaultBottomAreaScalePercent;

        [LocalizedCategory("settings.category.appearance")]
        [LocalizedDisplayName("settings.bottomAreaScale.displayName")]
        [LocalizedDescription("settings.bottomAreaScale.description")]
        [DefaultValue(DefaultBottomAreaScalePercent)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int BottomAreaScalePercent
        {
            get => _bottomAreaScalePercent;
            set => _bottomAreaScalePercent = NormalizeBottomAreaScalePercent(value);
        }

        internal static int NormalizeBottomAreaScalePercent(int value)
        {
            if (value < MinBottomAreaScalePercent) return MinBottomAreaScalePercent;
            if (value > MaxBottomAreaScalePercent) return MaxBottomAreaScalePercent;
            return value;
        }

        private int _webView2ZoomPercent = DefaultWebView2ZoomPercent;

        /// <summary>
        /// WebView2 页面缩放百分比（50-300）。由用户在 WebView2 中缩放时自动更新，
        /// 用于页面重建/重启后恢复相同比例。
        /// </summary>
        [Browsable(false)]
        [DefaultValue(DefaultWebView2ZoomPercent)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int WebView2ZoomPercent
        {
            get => _webView2ZoomPercent;
            set => _webView2ZoomPercent = NormalizeWebView2ZoomPercent(value);
        }

        internal static int NormalizeWebView2ZoomPercent(int value)
        {
            if (value < MinWebView2ZoomPercent) return MinWebView2ZoomPercent;
            if (value > MaxWebView2ZoomPercent) return MaxWebView2ZoomPercent;
            return value;
        }

        private ThemeMode _themeMode = ThemeMode.Auto;

        /// <summary>
        /// 主题模式：Auto 跟随 VS，Dark/Light 强制扩展界面主题。
        /// </summary>
        [System.ComponentModel.Browsable(false)]
        public ThemeMode ThemeMode
        {
            get => _themeMode;
            set => _themeMode = value;
        }
    }

    /// <summary>
    /// 模型列表下拉选项。
    /// </summary>
    internal class ModelListConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(context?.Instance is DeepSeekOptionsPage page
                ? page.GetModelChoices().ToList()
                : OfficialModelCatalogService.GetModels().ToList());
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
            => new(new[] { "Baidu", "Bing", "DuckDuckGo" });
    }

    /// <summary>
    /// OCR 引擎下拉选项。PaddleOCR-Sharp 仅在 x64 完整版中提供。
    /// </summary>
    internal class OcrEngineConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(new[] { "Windows Built-in", "PaddleOCR-Sharp" });
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
