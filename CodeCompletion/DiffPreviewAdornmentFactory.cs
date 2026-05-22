using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：为每个 WPF 文本视图创建 <see cref="View.DiffPreviewAdornment"/> 实例，
    /// 在 diff 预览激活时显示确认/撤销工具栏。
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("code")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class DiffPreviewAdornmentFactory : IWpfTextViewCreationListener
    {
        /// <summary>
        /// MEF 导出：将 "DeepSeekDiffPreviewAdornment" 层名注册到 VS 编辑器。
        /// 必须在获取 adornment layer 之前存在此导出，否则 GetAdornmentLayer 抛出 ArgumentOutOfRangeException。
        /// </summary>
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(View.DiffPreviewAdornment.AdornmentLayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        internal static AdornmentLayerDefinition DeepSeekDiffPreviewAdornmentLayer = null!;

        #region Public Methods

        /// <summary>
        /// 文本视图创建时，新建 <see cref="View.DiffPreviewAdornment"/> 并挂载，
        /// 同时检查是否有为此文件注册的待处理 diff。
        /// 每个视图绑定一个独立的装饰器实例。
        /// </summary>
        public void TextViewCreated(IWpfTextView textView)
        {
            if (textView == null)
                return;

            // ── 检查是否有待处理 diff，如有则激活预览 ──
            bool activated = EditorDiffMarkerService.Instance.TryActivatePendingDiff(textView);

            // 装饰器在构造函数中自动订阅服务事件和视图布局事件
            textView.Properties.GetOrCreateSingletonProperty(
                typeof(View.DiffPreviewAdornment),
                () => new View.DiffPreviewAdornment(textView));

            if (activated)
                Logger.Info("[DiffPreview] 已为文件激活待处理 diff 预览");
            else
                Logger.Info("[DiffPreview] 装饰器已挂载到文本视图");
        }

        #endregion
    }
}
