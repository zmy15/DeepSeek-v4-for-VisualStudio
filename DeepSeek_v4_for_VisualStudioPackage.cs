using DeepSeek_v4_for_VisualStudio.Commands;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
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
        /// 
        /// 同时预加载 WebView2/Markdig 程序集，确保在 ReSharper 等第三方扩展之前加载
        /// 扩展自带的兼容版本，避免版本冲突导致聊天窗口无法打开 (issue #18)。
        /// </summary>
        static DeepSeek_v4_for_VisualStudioPackage()
        {
            // ── 预加载关键程序集 ──
            // 在 ReSharper 等第三方扩展可能加载不同版本之前，先将扩展自带的
            // WebView2/Markdig 程序集加载到 AppDomain。预加载失败不阻止包初始化，
            // AssemblyResolve 处理器会作为后备路径再次尝试。
            PreloadCriticalAssemblies();

            AppDomain.CurrentDomain.AssemblyResolve += ResolveSystemAssembly;
        }

        /// <summary>
        /// 预加载关键程序集，确保扩展自带的兼容版本在 ReSharper 等
        /// 第三方扩展之前加载。预加载失败不抛异常，AssemblyResolve 作为后备。
        /// </summary>
        private static void PreloadCriticalAssemblies()
        {
            try
            {
                var extensionDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (extensionDir == null) return;

                foreach (var name in CriticalAssemblyNames)
                {
                    var dllPath = Path.Combine(extensionDir, name + ".dll");
                    if (File.Exists(dllPath))
                    {
                        try
                        {
                            Assembly.LoadFrom(dllPath);
                            DiagnosticLog.Write($"[DeepSeek AR] Preload OK: {name} from {dllPath}");
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Write($"[DeepSeek AR] Preload FAILED: {name} — {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    else
                    {
                        DiagnosticLog.Write($"[DeepSeek AR] Preload SKIP: {name}.dll not found at {dllPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek AR] PreloadCriticalAssemblies error: {ex.GetType().Name}: {ex.Message}");
            }
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
            // JetBrains ReSharper Platform会预加载不同版本的 Microsoft.Web.WebView2.Wpf/Core。
            // 扩展若直接加载不同版本会导致 InvalidCastException（同名类型来自两个版本的程序集）。
            //
            // 策略变更 (2026-06-05):
            // 旧策略"优先重用已加载版本"导致 ReSharper WebView2 vA 被用于 DeepSeek 编译目标 vB，
            // API 不兼容时引发 XamlParseException / MissingMethodException，聊天窗口打不开 (issue #18)。
            // 新策略: 对 WebView2 程序集始终优先加载扩展自带版本，跳过已加载版本检测。
            // ReSharper 和 DeepSeek 各自使用自己的版本，避免跨版本 API 不兼容。
            "Microsoft.Web.WebView2.Wpf",
            "Microsoft.Web.WebView2.Core",
            "Markdig",
        };

        /// <summary>
        /// 关键程序集名称。对这些程序集，不重用 AppDomain 中已加载的版本，
        /// 而应始终加载扩展自带的兼容版本，避免 ReSharper 等第三方扩展预加载的
        /// 不同版本造成 API 不兼容。
        /// </summary>
        private static readonly HashSet<string> CriticalAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.Web.WebView2.Wpf",
            "Microsoft.Web.WebView2.Core",
            "Markdig",
        };

        private static Assembly? ResolveSystemAssembly(object sender, ResolveEventArgs args)
        {
            var requestName = new AssemblyName(args.Name);

            // 只处理已知的程序集
            if (Array.IndexOf(SystemAssemblyNames, requestName.Name) < 0)
                return null;

            // ── 关键程序集特殊处理 ──
            // 不重用 AppDomain 中已加载的版本（可能来自 ReSharper 等第三方扩展）。
            // ReSharper 2026.2 预加载的版本与 DeepSeek 编译目标 (1.0.3912.50) 可能
            // API 不兼容，重用会导致 XamlParseException (BAML 类型解析失败) 或
            // MissingMethodException，造成聊天窗口无法打开 (GitHub issue #18)。
            // 对于 WebView2/Markdig，始终从扩展目录加载自带版本。
            bool isCriticalAssembly = CriticalAssemblyNames.Contains(requestName.Name);

            if (!isCriticalAssembly)
            {
                // 非关键程序集：优先复用已加载版本（解决 System.Memory 等桥接程序集版本冲突）
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
            }
            else
            {
                DiagnosticLog.Write(
                    $"[DeepSeek AR] Critical assembly requested: {requestName.Name} v{requestName.Version} — loading bundled version (skip reuse)");
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

        /// <summary>
        /// 获取运行时配置。Package 初始化阶段只提供内存默认值；
        /// 持久化配置由 LoadPersistedOptionsAsync 在 Shell 空闲后加载。
        /// </summary>
        public DeepSeekOptionsPage Options => DeepSeekOptionsPage.Instance ??= new DeepSeekOptionsPage();

        private readonly object _persistedOptionsLock = new();
        private Task<DeepSeekOptionsPage>? _persistedOptionsLoadTask;

        /// <summary>
        /// Loads the persisted DialogPage after package initialization has unwound.
        /// GetDialogPage synchronously queries Unified Settings and deadlocks if called
        /// while VS is still blocked waiting for InitializeAsync to complete.
        /// </summary>
        public Task<DeepSeekOptionsPage> LoadPersistedOptionsAsync()
        {
            lock (_persistedOptionsLock)
            {
                _persistedOptionsLoadTask ??= LoadPersistedOptionsCoreAsync();
                return _persistedOptionsLoadTask;
            }
        }

        private async Task<DeepSeekOptionsPage> LoadPersistedOptionsCoreAsync()
        {
            if (DisposalToken.IsCancellationRequested)
            {
                return Options;
            }

            try
            {
                // Leave the current package-load/command-execution stack before
                // touching DialogPage so AsyncPackage initialization can finish.
                await Task.Yield();
                await KnownUIContexts.ShellInitializedContext;
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

                DiagnosticLog.Write("[DeepSeek Init] Loading persisted options after package initialization...");
                var persistedOptions = (DeepSeekOptionsPage)GetDialogPage(typeof(DeepSeekOptionsPage));
                DeepSeekOptionsPage.Instance = persistedOptions;
                InitializeLocalization();
                ThemeService.Instance.UserThemeMode = persistedOptions.ThemeMode;
                DiagnosticLog.Write("[DeepSeek Init] Persisted options loaded OK");
                return persistedOptions;
            }
            catch (OperationCanceledException)
            {
                return Options;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] Persisted options load deferred/failed: {ex.GetType().Name}: {ex.Message}");
                return Options;
            }
        }

        #region Package Members

        /// <summary>
        /// 初始化包；VS 加载包后立即调用此方法。
        /// 不在初始化阶段直接显示工具窗口，避免 LoadPackageWithContext 冲突 (HRESULT: 0x80049283)。
        /// 改为延迟到 VS Shell 初始化完成后再显示。
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // ═══ 步骤 1/8：基类初始化 ═══
            try
            {
                await base.InitializeAsync(cancellationToken, progress);
                DiagnosticLog.Write("[DeepSeek Init] Step 1/8: base.InitializeAsync OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 1/8 base.InitializeAsync: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // ═══ 步骤 2/8：国际化（提前初始化，避免选项页加载时拿到默认语言）═══
            try
            {
                // 先自动检测系统语言，确保选项页属性描述符构建时使用正确语言
                LocalizationService.Instance.Initialize(null);
                DiagnosticLog.Write("[DeepSeek Init] Step 2/8: Localization (auto-detect) OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 2/8 Localization: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // ═══ 步骤 3/8：选项页默认值 ═══
            // GetDialogPage must not run here: restoring its settings synchronously
            // queries Unified Settings while VS is waiting for this package to finish
            // initializing, which creates a UI-thread deadlock.
            DeepSeekOptionsPage.Instance ??= new DeepSeekOptionsPage();
            DiagnosticLog.Write("[DeepSeek Init] Step 3/8: in-memory defaults ready; persisted options deferred");

            // ═══ 步骤 4/8：日志系统 ═══
            try
            {
                Logger.Initialize(this);
                DiagnosticLog.Write("[DeepSeek Init] Step 4/8: Logger OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] Step 4/8 Logger.Initialize (non-fatal): {ex.GetType().Name}: {ex.Message}");
                // 非致命：日志不可用时继续
            }

            // ═══ 步骤 5/8：根据用户设置细化语言 ═══
            try
            {
                InitializeLocalization();
                DiagnosticLog.Write("[DeepSeek Init] Step 5/8: Localization (user override) OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 5/8 Localization: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Init] Stack: {ex.StackTrace}");
                throw;
            }

            // ═══ 步骤 6/8：设置变更订阅 ═══
            try
            {
                DeepSeekOptionsPage.SettingsChanged += OnSettingsChanged;
                DiagnosticLog.Write("[DeepSeek Init] Step 6/8: SettingsChanged OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 6/8 SettingsChanged: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // ═══ 步骤 7/9：主题服务（UI 线程）═══
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                ThemeService.Initialize();
                // 从设置恢复用户主题偏好
                var savedTheme = Options?.ThemeMode ?? Models.ThemeMode.Auto;
                ThemeService.Instance.UserThemeMode = savedTheme;
                DiagnosticLog.Write("[DeepSeek Init] Step 7/9: ThemeService OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] Step 7/9 ThemeService (non-fatal)：{ex.GetType().Name}: {ex.Message}");
            }

            // ═══ 步骤 8/9：DI 容器 ═══
            try
            {
                CompositionRoot.Build();
                DiagnosticLog.Write("[DeepSeek Init] Step 8/9: DI container OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 8/9 CompositionRoot.Build: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Init] Stack: {ex.StackTrace}");
                throw;
            }

            // ═══ Toast 通知点击 → 激活 VS 窗口并打开工具窗口 ═══
            ToastNotificationService.ToastActivated += () =>
            {
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    try
                    {
                        // 1. 将 VS 主窗口带到前台（最小化时恢复）
                        DiagnosticLog.Write("[DeepSeek Init] Toast 点击：正在激活 VS 主窗口...");
                        var dte = (EnvDTE.DTE?)GetService(typeof(EnvDTE.DTE));
                        if (dte != null)
                        {
                            dte.MainWindow.Activate();
                            DiagnosticLog.Write("[DeepSeek Init] Toast 点击：VS 主窗口已激活");
                        }
                        else
                        {
                            // 备用方案：通过 IVsUIShell 激活主窗口
                            var uiShell = (IVsUIShell?)GetService(typeof(SVsUIShell));
                            if (uiShell != null)
                            {
                                var guid = Guid.Empty;
                                uiShell.GetDialogOwnerHwnd(out var hwnd);
                                if (hwnd != IntPtr.Zero)
                                {
                                    ShowWindow(hwnd, SW_RESTORE);
                                    SetForegroundWindow(hwnd);
                                }
                            }
                        }

                        // 2. 打开 DeepSeek Chat 工具窗口
                        DiagnosticLog.Write("[DeepSeek Init] Toast 点击：正在打开工具窗口...");
                        await LoadPersistedOptionsAsync();
                        await ShowToolWindowAsync(
                            typeof(DeepSeekChatWindowPane),
                            0,
                            create: true,
                            cancellationToken: DisposalToken);
                        DiagnosticLog.Write("[DeepSeek Init] Toast 点击：工具窗口已打开");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write($"[DeepSeek Init] Toast 点击打开窗口失败: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            };

            // ═══ 步骤 9/9：注册菜单命令 ═══
            try
            {
                await ShowChatWindowCommand.InitializeAsync(this);
                DiagnosticLog.Write("[DeepSeek Init] Step 9/9: Commands registered OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] FATAL Step 9/9 ShowChatWindowCommand.InitializeAsync: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Init] Stack: {ex.StackTrace}");
                throw;
            }

            DiagnosticLog.Write("[DeepSeek Init] All 9 steps completed successfully");

            // 延迟显示工具窗口，避免在包初始化期间调用 ShowToolWindowAsync
            // 导致 COMException (0x80049283): LoadPackageWithContext 冲突
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                // 等待初始化完成后再切换到主线程
                await Task.Delay(200, DisposalToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

                try
                {
                    DiagnosticLog.Write("[DeepSeek Init] Auto-show: loading persisted options...");
                    await LoadPersistedOptionsAsync();
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

        #region Native Methods (Toast 通知点击 → 激活 VS 窗口)

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        #endregion
    }

    /// <summary>
    /// 扩展版本信息常量。
    /// </summary>
    internal static class Vsix
    {
        public const string Name = "DeepSeek Chat for Visual Studio";
        public const string Description = "DeepSeek AI chat integration for Visual Studio 2022.";
        public const string Version = "1.1.13";
    }
}
