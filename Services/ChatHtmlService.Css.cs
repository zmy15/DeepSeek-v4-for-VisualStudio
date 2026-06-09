using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    public static partial class ChatHtmlService
    {
        #region CSS & CDN Constants

        private const string HighlightJsCdnScript = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js";

        /// <summary>
        /// 获取当前主题对应的 Highlight.js CSS CDN 链接。
        /// </summary>
        private static string HighlightJsCdnStyle => ThemeService.Instance.HighlightJsCdnStyle;

        /// <summary>
        /// 获取当前主题对应的页面 CSS。
        /// </summary>
        private static string PageCss => ThemeService.Instance.PageCss;

        #endregion
    }
}
