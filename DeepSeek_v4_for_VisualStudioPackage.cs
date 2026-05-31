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
            // JetBrains ReSharper Platform会预加载不同版本的 Microsoft.Web.WebView2.Wpf/Core。扩展打包的版本若直接加载会导致
            // InvalidCastException（同名类型来自两个不同版本程序集）。
            // 解决方案：优先重用 VS 已加载的版本，仅当 VS 环境没有时才回退到扩展自带的。
            "Microsoft.Web.WebView2.Wpf",
            "Microsoft.Web.WebView2.Core",
        };

        private static Assembly? ResolveSystemAssembly(object sender, ResolveEventArgs args)
        {
            var requestName = new AssemblyName(args.Name);

            // 只处理已知的程序集
            if (Array.IndexOf(SystemAssemblyNames, requestName.Name) < 0)
                return null;

            // 优先检查 AppDomain 中是否已加载同名程序集（任意版本）
            // 解决 JetBrains ReSharper 已加载不同版本 WebView2 的冲突问题
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, requestName.Name, StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Write(
                        $"[DeepSeek AR] Reuse loaded: {requestName.Name} v{loaded.GetName().Version} (requested v{requestName.Version})");
                    return loaded;
                }
            }

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
            // ═══ 步骤 1/7：基类初始化 ═══
            try
            {
                await base.InitializeAsync(cancellationToken, progress);
                DiagnosticLog.Write("[DeepSeek Init] Step 1/7: base.InitializeAsync OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 1/7 base.InitializeAsync: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // ═══ 步骤 2/7：选项页 ═══
            try
            {
                DeepSeekOptionsPage.Instance = Options;
                DiagnosticLog.Write("[DeepSeek Init] Step 2/7: Options page OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 2/7 Options: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // ═══ 步骤 3/7：日志系统 ═══
            try
            {
                Logger.Initialize(this);
                DiagnosticLog.Write("[DeepSeek Init] Step 3/7: Logger OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] Step 3/7 Logger.Initialize (non-fatal): {ex.GetType().Name}: {ex.Message}");
                // 非致命：日志不可用时继续
            }

            // ═══ 步骤 4/7：国际化 ═══
            try
            {
                InitializeLocalization();
                DiagnosticLog.Write("[DeepSeek Init] Step 4/7: Localization OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 4/7 Localization: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Init] Stack: {ex.StackTrace}");
                throw;
            }

            // ═══ 步骤 5/7：设置变更订阅 ═══
            try
            {
                DeepSeekOptionsPage.SettingsChanged += OnSettingsChanged;
                DiagnosticLog.Write("[DeepSeek Init] Step 5/7: SettingsChanged OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 5/7 SettingsChanged: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // ═══ 步骤 6/7：DI 容器 ═══
            try
            {
                CompositionRoot.Build();
                DiagnosticLog.Write("[DeepSeek Init] Step 6/7: DI container OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 6/7 CompositionRoot.Build: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Init] Stack: {ex.StackTrace}");
                throw;
            }

            // ═══ 步骤 7/7：注册菜单命令 ═══
            try
            {
                await ShowChatWindowCommand.InitializeAsync(this);
                DiagnosticLog.Write("[DeepSeek Init] Step 7/7: Commands registered OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 7/7 ShowChatWindowCommand.InitializeAsync: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Init] Stack: {ex.StackTrace}");
                throw;
            }

            DiagnosticLog.Write("[DeepSeek Init] All 7 steps completed successfully");

            // 延迟显示工具窗口，避免在包初始化期间调用 ShowToolWindowAsync
            // 导致 COMException (0x80049283): LoadPackageWithContext 冲突
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                // 等待初始化完成后再切换到主线程
                await Task.Delay(200, DisposalToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

                try
                {
                    DiagnosticLog.Write("[DeepSeek Init] Auto-show: calling ShowToolWindowAsync...");
                    await ShowToolWindowAsync(typeof(DeepSeekChatWindowPane), 0, create: true, cancellationToken: DisposalToken);
                    DiagnosticLog.Write("[DeepSeek Init] Auto-show: tool window shown OK");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[DeepSeek Init] Auto-show FAILED: {ex.GetType().Name}: {ex.Message}");
                    DiagnosticLog.Write($"[DeepSeek Init] Auto-show stack: {ex.StackTrace}");
                    if (ex.InnerException != null)
                        DiagnosticLog.Write($"[DeepSeek Init] Auto-show inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
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
                DiagnosticLog.Write($"[I18n] Failed to reload language: {ex.Message}");
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
        public const string Version = "1.1.6";
    }
}
