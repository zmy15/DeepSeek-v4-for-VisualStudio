using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：为可编辑文本视图创建 <see cref="GhostTextTagger"/> 实例，
    /// 启用内联幽灵文本代码补全功能。
    /// </summary>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("code")]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class GhostTextTaggerProvider : IViewTaggerProvider
    {
        #region Public Methods

        /// <summary>
        /// 为给定文本视图创建或获取 <see cref="GhostTextTagger"/> 实例。
        /// </summary>
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView is not IWpfTextView wpfView)
            {
                return null;
            }

            if (buffer != textView.TextBuffer)
            {
                return null;
            }

            GhostTextTagger tagger = wpfView.Properties.GetOrCreateSingletonProperty(
                GhostTextTagger.TaggerKey,
                () => new GhostTextTagger(wpfView));

            return tagger as ITagger<T>;
        }

        #endregion
    }
}
