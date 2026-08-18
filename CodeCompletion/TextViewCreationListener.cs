using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：为每个可编辑代码视图绑定内联预测管理器和命令过滤器。
    /// 这是代码补全功能的入口点。
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("code")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal class TextViewCreationListener : IVsTextViewCreationListener
    {
        #region Properties

        /// <summary>
        /// 适配器服务，用于从旧版 <see cref="IVsTextView"/> 获取 WPF 文本视图。
        /// </summary>
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService { get; set; }

        /// <summary>
        /// 文本结构导航器选择器，用于获取方法/块级别的结构导航器。
        /// </summary>
        [Import]
        internal ITextStructureNavigatorSelectorService NavigatorSelectorService { get; set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// VS 文本视图创建时调用：绑定 InlinePredictionManager 和 CommandFilter。
        /// </summary>
        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            IWpfTextView view = AdapterService.GetWpfTextView(textViewAdapter);

            if (view == null)
            {
                return;
            }

            if (view.Roles.Contains(DifferenceViewerRoles.DiffTextViewRole) ||
                view.Roles.Contains(DifferenceViewerRoles.UbiquitousDiffTextViewRole))
            {
                return;
            }

            Logger.Info(LocalizationService.Instance["autocomplete.viewCreated"]);

            // 修复 VS 挂起 (perfwatsonhang ~700 hits)：
            // 旧实现通过 ThreadHelper.JoinableTaskFactory.Run(...) → LoadPackage 同步加载包。
            // 但 VsTextViewCreated 本身运行在 UI 线程，JTF.Run 阻塞等待的同时，
            // LoadPackage 又需要 UI 线程完成包初始化 → 双向等待 → 死锁/挂起。
            //
            // 方案 A+B：
            //   A) 绝不在此扩展点同步 LoadPackage。包在 InitializeAsync 中已将
            //      Options 缓存到静态 DeepSeekOptionsPage.Instance；若包尚未初始化
            //      则 Instance 为 null，本次直接跳过绑定（后续视图重建会重试），
            //      而不是强制同步加载引发死锁。
            //   B) 不再切换线程上下文，直接在 UI 线程读取缓存，消除等待环节。
            DeepSeekOptionsPage? options = DeepSeekOptionsPage.Instance;
            if (options == null)
            {
                Logger.Warn(LocalizationService.Instance["autocomplete.packageNotFound"]);
                return;
            }

            string status = options.AutoCompleteEnabled
                ? LocalizationService.Instance["autocomplete.statusEnabled"]
                : LocalizationService.Instance["autocomplete.statusDisabled"];
            Logger.Info(string.Format(LocalizationService.Instance["autocomplete.status"], status));

            // Store the text view adapter for later use (e.g., formatting)
            view.Properties.GetOrCreateSingletonProperty(
                typeof(IVsTextView), () => textViewAdapter);

            // Create and store the inline prediction manager
            ITextStructureNavigator structureNavigator = NavigatorSelectorService.GetTextStructureNavigator(view.TextBuffer);
            InlinePredictionManager manager = new(options, view, structureNavigator);
            view.Properties.GetOrCreateSingletonProperty(
                typeof(InlinePredictionManager), () => manager);

            // Attach command filter for Tab/Escape handling
            _ = new CommandFilter(view, textViewAdapter);
        }

        #endregion
    }
}
