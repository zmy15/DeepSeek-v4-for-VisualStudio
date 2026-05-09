using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：为所有代码内容类型创建 <see cref="DiffMarkerTagger"/> 实例，
    /// 在编辑器中渲染 diff 红绿行标记。
    /// </summary>
    [Export(typeof(ITaggerProvider))]
    [ContentType("code")]
    [TagType(typeof(ITextMarkerTag))]
    internal sealed class DiffMarkerTaggerProvider : ITaggerProvider
    {
        #region Public Methods

        /// <summary>
        /// 为指定文本缓冲区创建或获取 <see cref="DiffMarkerTagger"/> 实例。
        /// 每个 buffer 只会创建一个 tagger（通过 Properties 单例模式）。
        /// </summary>
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            DiffMarkerTagger tagger = buffer.Properties.GetOrCreateSingletonProperty(
                typeof(DiffMarkerTagger),
                () => new DiffMarkerTagger(buffer));

            return tagger as ITagger<T>;
        }

        #endregion
    }
}
