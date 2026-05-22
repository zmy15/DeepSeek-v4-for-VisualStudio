using DeepSeek_v4_for_VisualStudio.Commands;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Reflection;
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

        /// <summary>
        /// 静态构造函数：注册 AssemblyResolve 以处理 VS2022 中 System.Memory 等
        /// .NET Standard 桥接程序集的版本绑定问题。
        /// Markdig 编译时引用 System.Memory 4.0.5.0，但实际部署的是 NuGet 版本
        /// (4.5.5, 程序集版本 4.0.1.2)，需要通过此处理器完成运行时重定向。
        /// </summary>
        static DeepSeek_v4_for_VisualStudioPackage()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSystemAssembly;
        }

        private static readonly string[] SystemAssemblyNames = new[]
        {
            "System.Memory",
            "System.Buffers",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Numerics.Vectors",
            "System.Threading.Tasks.Extensions",
            "System.ValueTuple",
            "System.Diagnostics.DiagnosticSource",
        };

        private static Assembly? ResolveSystemAssembly(object sender, ResolveEventArgs args)
        {
            var requestName = new AssemblyName(args.Name);

            // 只处理已知的 System.* 桥接程序集
            if (Array.IndexOf(SystemAssemblyNames, requestName.Name) < 0)
                return null;

            // 首先尝试按简单名称加载（已加载的程序集）
            try
            {
                return Assembly.Load(requestName.Name);
            }
            catch (FileNotFoundException)
            {
                // 未加载，尝试从扩展目录加载 DLL
            }

            // 从扩展安装目录加载
            try
            {
                var extensionDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (extensionDir != null)
                {
                    var dllPath = Path.Combine(extensionDir, requestName.Name + ".dll");
                    if (File.Exists(dllPath))
                    {
                        return Assembly.LoadFrom(dllPath);
                    }
                }
            }
            catch
            {
                // 静默失败，返回 null 让 CLR 走默认流程
            }

            return null;
        }

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

            // 设置全局 Options 实例，供静态工具类读取设置
            DeepSeekOptionsPage.Instance = Options;

            // 初始化日志系统（输出窗口窗格、日志目录）
            Logger.Initialize(this);

            // 初始化国际化服务（根据用户设置或系统语言自动选择语言）
            InitializeLocalization();

            // 订阅设置变更事件，当用户切换语言时热更新
            DeepSeekOptionsPage.SettingsChanged += OnSettingsChanged;

            // 初始化 DI 容器
            CompositionRoot.Build();

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

        #region Localization

        /// <summary>
        /// 初始化国际化服务。
        /// 根据用户选项中的语言设置或系统 UI 语言自动选择语言。
        /// </summary>
        private void InitializeLocalization()
        {
            string? languageOverride = Options?.Language;
            if (string.IsNullOrEmpty(languageOverride) ||
                string.Equals(languageOverride, "auto", StringComparison.OrdinalIgnoreCase))
            {
                languageOverride = null; // 自动检测系统语言
            }

            LocalizationService.Instance.Initialize(languageOverride);
        }

        /// <summary>
        /// 设置变更回调：当用户在选项页修改语言设置时热更新。
        /// </summary>
        private void OnSettingsChanged()
        {
            try
            {
                string? language = Options?.Language;
                if (!string.IsNullOrEmpty(language) &&
                    !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    LocalizationService.Instance.SetLanguage(language);
                }
                else
                {
                    // 重新自动检测
                    LocalizationService.Instance.Initialize(null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[I18n] Failed to reload language: {ex.Message}");
            }
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
