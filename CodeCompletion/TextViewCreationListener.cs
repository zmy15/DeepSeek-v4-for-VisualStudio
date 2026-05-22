using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

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

            Logger.Info(LocalizationService.Instance["autocomplete.viewCreated"]);

            // 使用 JoinableTaskFactory.Run 替代 .Result，避免 UI 线程死锁
            // VsTextViewCreated 本身已在 UI 线程，JTF.Run 允许安全的同步等待
            DeepSeek_v4_for_VisualStudioPackage package =
                ThreadHelper.JoinableTaskFactory.Run(async () => await GetPackageAsync());

            if (package == null || package.Options == null)
            {
                Logger.Warn(LocalizationService.Instance["autocomplete.packageNotFound"]);
                return;
            }

            string status = package.Options.AutoCompleteEnabled
                ? LocalizationService.Instance["autocomplete.statusEnabled"]
                : LocalizationService.Instance["autocomplete.statusDisabled"];
            Logger.Info(string.Format(LocalizationService.Instance["autocomplete.status"], status));

            // Store the text view adapter for later use (e.g., formatting)
            view.Properties.GetOrCreateSingletonProperty(
                typeof(IVsTextView), () => textViewAdapter);

            // Create and store the inline prediction manager
            ITextStructureNavigator structureNavigator = NavigatorSelectorService.GetTextStructureNavigator(view.TextBuffer);
            InlinePredictionManager manager = new(package.Options, view, structureNavigator);
            view.Properties.GetOrCreateSingletonProperty(
                typeof(InlinePredictionManager), () => manager);

            // Attach command filter for Tab/Escape handling
            _ = new CommandFilter(view, textViewAdapter);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 异步获取 <see cref="DeepSeek_v4_for_VisualStudioPackage"/> 实例。
        /// </summary>
        private async Task<DeepSeek_v4_for_VisualStudioPackage> GetPackageAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            IVsShell shell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));

            if (shell != null)
            {
                Guid packageGuid = DeepSeek_v4_for_VisualStudioPackage.PackageGuid;
                shell.LoadPackage(ref packageGuid, out IVsPackage package);

                return package as DeepSeek_v4_for_VisualStudioPackage;
            }

            return null;
        }

        #endregion
    }
}
