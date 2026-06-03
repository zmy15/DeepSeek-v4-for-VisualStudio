using System;
using System.ComponentModel;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 本地化的 CategoryAttribute，通过 LocalizationService 在运行时解析分类名称。
    /// 用法: [LocalizedCategory("settings.category.api")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedCategoryAttribute : CategoryAttribute
    {
        private readonly string _key;

        public LocalizedCategoryAttribute(string key)
        {
            _key = key ?? string.Empty;
        }

        /// <summary>
        /// 重写此方法以返回本地化后的分类名。VS 属性窗口通过此方法获取显示文本。
        /// </summary>
        protected override string GetLocalizedString(string value)
        {
            // value 是构造时传入的原始 key，这里忽略它，直接用 _key 查表
            return LocalizationService.Instance[_key];
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
                    // 订阅语言变更以清除缓存
                    LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
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
                    LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
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
