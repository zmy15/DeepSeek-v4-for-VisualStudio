using DeepSeek_v4_for_VisualStudio.Commands;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.View;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
    /// VS 包入口点，对标共享项目 VisuallChatGPTStudioPackage。
    /// 使用传统 VS SDK (AsyncPackage + ToolWindowPane)。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [Guid(DeepSeek_v4_for_VisualStudioPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(DeepSeekChatWindowPane), Style = VsDockStyle.Tabbed)]
    [ProvideOptionPage(typeof(DeepSeekOptionsPage), "DeepSeek Chat", "General", 0, 0, true)]
    public sealed class DeepSeek_v4_for_VisualStudioPackage : AsyncPackage
    {
        /// <summary>
        /// DeepSeek_v4_for_VisualStudioPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "7a5b2771-22cb-4337-b445-8d97e3189b64";

        /// <summary>
        /// GUID as a static ref for use with LoadPackage.
        /// </summary>
        public static readonly Guid PackageGuid = new(PackageGuidString);

        public DeepSeekOptionsPage Options => (DeepSeekOptionsPage)GetDialogPage(typeof(DeepSeekOptionsPage));

        #region Package Members

        /// <summary>
        /// 初始化包；VS 加载包后立即调用此方法。
        /// 不在初始化阶段直接显示工具窗口，避免 LoadPackageWithContext 冲突 (HRESULT: 0x80049283)。
        /// 改为延迟到 VS Shell 初始化完成后再显示。
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // 注册菜单命令（视图 → 其他窗口 → DeepSeek Chat）
            await ShowChatWindowCommand.InitializeAsync(this);

            // 延迟显示工具窗口，避免在包初始化期间调用 ShowToolWindowAsync
            // 导致 COMException (0x80049283): LoadPackageWithContext 冲突
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                // 等待初始化完成后再切换到主线程
                await Task.Delay(200, DisposalToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

                try
                {
                    await ShowToolWindowAsync(typeof(DeepSeekChatWindowPane), 0, create: true, cancellationToken: DisposalToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DeepSeek] Failed to show tool window: {ex.Message}");
                }
            });
        }

        #endregion
    }

    /// <summary>
    /// 扩展版本信息常量。
    /// </summary>
    internal static class Vsix
    {
        public const string Name = "DeepSeek Chat for Visual Studio";
        public const string Description = "DeepSeek AI chat integration for Visual Studio 2022.";
        public const string Version = "1.1.0";
    }
}
