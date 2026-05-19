using System;
using System.Windows.Data;
using System.Windows.Markup;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// WPF MarkupExtension 用于 XAML 中的 i18n 本地化。
    /// 用法：Text="{utils:I18n chat.windowTitle}"
    /// 或：   Content="{utils:I18n general.ok}"
    /// 
    /// 支持自动订阅语言变更事件以刷新 UI。
    /// </summary>
    public class I18nExtension : MarkupExtension
    {
        /// <summary>i18n 资源键</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>格式化参数（可选，用逗号分隔）</summary>
        public string? Args { get; set; }

        public I18nExtension() { }

        public I18nExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key))
                return $"[missing key]";

            var localization = LocalizationService.Instance;

            // 尝试作为 Binding 提供（支持动态语言切换）
            if (serviceProvider?.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt
                && pvt.TargetObject != null)
            {
                string? value;
                if (!string.IsNullOrEmpty(Args))
                {
                    var args = Args.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    value = localization.Format(Key, args);
                }
                else
                {
                    value = localization[Key];
                }

                // 返回当前值；如需动态刷新，使用 Binding
                return value;
            }

            return localization[Key];
        }
    }
}
