using System;
using System.ComponentModel;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 本地化的 CategoryAttribute，通过 LocalizationService 在运行时解析分类名称。
    /// .NET Framework 中 Category 属性非 virtual 且 GetLocalizedString() 仅首次调用。
    /// 解决方案：重写 GetLocalizedString() 提供初值，语言切换时用反射
    /// 直接改写基类内部 categoryValue 字段，绕过 localized 缓存标志。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedCategoryAttribute : CategoryAttribute
    {
        private readonly string _key;

        public LocalizedCategoryAttribute(string key) : base(key)
        {
            _key = key ?? string.Empty;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        }

        protected override string GetLocalizedString(string value)
        {
            return LocalizationService.Instance[_key];
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            // 直接改写基类 categoryValue 为当前语言的值，
            // 绕过 localized 标志位，无需 TypeDescriptor.Refresh
            try
            {
                var field = typeof(CategoryAttribute).GetField("categoryValue",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(this, LocalizationService.Instance[_key]);
                }
            }
            catch
            {
                // 静默忽略
            }
        }
    }

    /// <summary>
    /// 本地化的 DisplayNameAttribute，通过 LocalizationService 在运行时解析显示名称。
    /// 用法: [LocalizedDisplayName("settings.apiKey.displayName")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedDisplayNameAttribute : DisplayNameAttribute
    {
        private readonly string _key;
        private string? _cachedDisplayName;
        private bool _subscribed;

        public LocalizedDisplayNameAttribute(string key)
        {
            _key = key ?? string.Empty;
        }

        /// <summary>
        /// 重写 DisplayName 以返回本地化后的显示名称。
        /// </summary>
        public override string DisplayName
        {
            get
            {
                if (_cachedDisplayName == null)
                {
                    _cachedDisplayName = LocalizationService.Instance[_key];
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
                    }
                }
                return _cachedDisplayName;
            }
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            _cachedDisplayName = null;
        }
    }

    /// <summary>
    /// 本地化的 DescriptionAttribute，通过 LocalizationService 在运行时解析描述文本。
    /// 用法: [LocalizedDescription("settings.apiKey.description")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedDescriptionAttribute : DescriptionAttribute
    {
        private readonly string _key;
        private string? _cachedDescription;
        private bool _subscribed;

        public LocalizedDescriptionAttribute(string key)
        {
            _key = key ?? string.Empty;
        }

        /// <summary>
        /// 重写 Description 以返回本地化后的描述文本。
        /// </summary>
        public override string Description
        {
            get
            {
                if (_cachedDescription == null)
                {
                    _cachedDescription = LocalizationService.Instance[_key];
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
                    }
                }
                return _cachedDescription;
            }
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            _cachedDescription = null;
        }
    }
}
