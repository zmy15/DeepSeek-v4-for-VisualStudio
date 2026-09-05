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
    // The legacy page remains the secure editor for API keys. Keys live in Visual Studio
    // Credential Storage and intentionally do not enter Unified Settings.
    [ProvideOptionPage(typeof(DeepSeekOptionsPage), "DeepSeek Chat", "General", 0, 0, true)]
    [ProvideProfile(typeof(DeepSeekOptionsPage), "DeepSeek Chat", "General",
        16001, 16002, isToolsOptionPage: true, DescriptionResourceID = 16003)]
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

                var swTotal = System.Diagnostics.Stopwatch.StartNew();
                DiagnosticLog.Write("[DeepSeek Init] Loading persisted options after package initialization...");

                // Initialize the official VS keychain before DialogPage loads settings. This lets
                // API keys move out of the ro/exportable settings store without losing legacy values.
                Settings.VisualStudioApiKeyStore.Current =
                    await Settings.VisualStudioApiKeyStore.CreateAsync(this);

                // ── GetDialogPage：唯一必须留在 UI 线程的重步骤（VS 服务调用）──
                var swDialogPage = System.Diagnostics.Stopwatch.StartNew();
                var persistedOptions = (DeepSeekOptionsPage)GetDialogPage(typeof(DeepSeekOptionsPage));
                DeepSeekOptionsPage.Instance = persistedOptions;
                DiagnosticLog.Write($"[DeepSeek Init] GetDialogPage OK in {swDialogPage.ElapsedMilliseconds}ms");

                // ── 跨实例设置迁移（问题 2）：仅在未迁移过时执行一次，防止覆盖用户在新版本中的修改 ──
                // 两阶段拆分：RegLoadAppKey 挂载探测（慢 IO）放后台线程；
                // DialogPage 属性回填 + SaveSettingsToStorage 留在主线程（线程亲和性要求）。
                if (!persistedOptions.LegacySettingsMigrated)
                {
                    // P1-5a：一次性迁移标志只在"迁移成功"或"确无来源"时固化，
                    // 避免首启瞬时失败（hive 被锁/超时）烧掉标志导致旧设置永不再迁移。
                    try
                    {
                        var swMigrate = System.Diagnostics.Stopwatch.StartNew();
                        bool migrated = false;
                        bool definitivelyNothing = false;

                        if (string.IsNullOrWhiteSpace(persistedOptions.ApiKey))
                        {
                            var probed = await Settings.SettingsMigration.ProbeBestSourceAsync(TryGetOwnHiveName());
                            if (probed != null)
                            {
                                migrated = Settings.SettingsMigration.ApplyProbedValues(persistedOptions, probed);
                            }
                            else
                            {
                                definitivelyNothing = Settings.SettingsMigration.HasNoCandidateSource(TryGetOwnHiveName());
                            }
                        }
                        else
                        {
                            definitivelyNothing = true; // 已有 ApiKey，无需迁移
                        }

                        if (migrated || definitivelyNothing)
                        {
                            persistedOptions.LegacySettingsMigrated = true;
                            persistedOptions.SaveSettingsToStorage();
                            DiagnosticLog.Write($"[DeepSeek Init] settings migration stage done in {swMigrate.ElapsedMilliseconds}ms (migrated={migrated}, definitive={definitivelyNothing})");
                        }
                        else
                        {
                            DiagnosticLog.Write("[DeepSeek Init] settings migration deferred: transient failure, will retry next start");
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write($"[DeepSeek Init] settings migration stage failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                InitializeLocalization();
                ThemeService.Instance.UserThemeMode = persistedOptions.ThemeMode;
                DiagnosticLog.Write($"[DeepSeek Init] Persisted options loaded OK in {swTotal.ElapsedMilliseconds}ms");

                // ── Unified Settings 双向同步桥（新版设置 UI ↔ Instance）──
                // fire-and-forget：桥内含宿主激活与注册可见性等待（最长 120s），
                // 不得阻塞持久化装载完成与窗口显示。
                Settings.UnifiedSettingsSync.Host = this;
                _ = Settings.UnifiedSettingsSync.InitializeAsync(this, this);

                // ── 生效配置快照（脱敏）：用于核对"选项页所见 = 运行时所用" ──
                {
                    bool deepSeekKeyConfigured = !string.IsNullOrWhiteSpace(persistedOptions.ApiKey);
                    DiagnosticLog.Write($"[Settings] effective: model={persistedOptions.SelectedModel}, " +
                        $"deepSeekKeyConfigured={deepSeekKeyConfigured}, " +
                        $"credentialStore={Settings.VisualStudioApiKeyStore.IsAvailable}, " +
                        $"migrated={persistedOptions.LegacySettingsMigrated}");
                }
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
            // 注意：语言切换不再通过此事件处理（OnSettingsChanged 已移除），
            // 改为在 DeepSeekOptionsPage.OnApply 中直接读取本页最新 Language 值应用，
            // 避免从可能过期的静态 Instance 读取导致语言切换失效。
            try
            {
                DiagnosticLog.Write("[DeepSeek Init] Step 6/8: SettingsChanged OK (language applied in DeepSeekOptionsPage.OnApply)");
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

                // ── 在 VS UI 线程上捕获 VS 自身的 UI 语言，并据此刷新语言 ──
                // 之前步骤 2/5 在后台线程初始化，CultureInfo.CurrentUICulture
                // 拿到的是系统安装语言而非 VS 语言；这里切回主线程后重新检测，
                // 让 "auto" 跟随 VS 当前显示语言（VS 英文 → 扩展英文）。
                // 独立 try/catch：语言初始化失败不影响后续主题服务初始化。
                try
                {
                    LocalizationService.CaptureVsUiLanguage();
                    InitializeLocalization();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[DeepSeek Init] Step 7/9 VS language (non-fatal): {ex.GetType().Name}: {ex.Message}");
                }

                ThemeService.Initialize();
                // 从设置恢复用户主题偏好
                var savedTheme = Options?.ThemeMode ?? Models.ThemeMode.Auto;
                ThemeService.Instance.UserThemeMode = savedTheme;
                DiagnosticLog.Write("[DeepSeek Init] Step 7/9: ThemeService + VS language OK");
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
                        MarkChatWindowOpened(); // 用户显式打开，允许后续会话自动弹出
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

            // ═══ 步骤 9/9：注册菜单命令 ═══
            // 性能优化：InlineAiEditCommand 惰性注册（Ctrl+I 首次触发才初始化），
            // ShowChatWindowCommand 保持立即注册（菜单项需在启动时可见）。
            _ = Task.Run(async () =>
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    await Commands.InlineAiEditCommand.InitializeAsync(this);
                    DiagnosticLog.Write("[DeepSeek Init] Step 10 (deferred): InlineAiEditCommand registered OK");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[DeepSeek Init] WARN deferred InlineAiEditCommand failed: {ex.GetType().Name}: {ex.Message}");
                }
            });

            DiagnosticLog.Write("[DeepSeek Init] All steps completed successfully");

            // ── 备份保留期清扫（后台、非 UI 线程）──
            // 清理超过 14 天的历史备份会话目录，防止失败残留长期累积（对齐 DiagnosticLog 14 天惯例）。
            _ = Task.Run(() =>
            {
                try
                {
                    var removed = Services.BackupService.CleanupExpiredSessions();
                    if (removed > 0)
                        DiagnosticLog.Write($"[Backup] startup sweep removed {removed} expired session dir(s)");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[Backup] startup sweep failed: {ex.Message}");
                }
            });

            // ── 自动恢复聊天窗口（标记门控）──
            // 仅当用户此前显式打开过聊天窗口（存在标记文件）时才随启动自动弹出；
            // 空白实例 / 从未使用过的实例不再强制走"加载持久化配置 + 创建窗口"链路。
            // 历史事故（2026-08-24 卡死分析）：无条件自动弹窗使冷启动必然进入
            // UI 线程阻塞链，叠加当时同步运行的全域反射探针 → 启动即卡死。
            // 显式打开后写入标记（见 MarkChatWindowOpened），后续会话恢复弹出行为；
            // 未使用过的实例则保持安静，由用户主动触发（工具栏 / 菜单 / Ctrl+Shift+D）。
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(200, DisposalToken);
                    if (!File.Exists(AutoShowMarkerPath)) return;

                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    DiagnosticLog.Write("[DeepSeek Init] Auto-show (marker present): loading persisted options...");
                    await LoadPersistedOptionsAsync();
                    await ShowToolWindowAsync(typeof(DeepSeekChatWindowPane), 0, create: true, cancellationToken: DisposalToken);
                    DiagnosticLog.Write("[DeepSeek Init] Auto-show: tool window shown OK");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[DeepSeek Init] Auto-show FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        #endregion

        #region Auto-Show Marker & Hive Helpers

        /// <summary>
        /// "用户显式打开过聊天窗口"的持久化标记文件路径。
        /// 存在时启动阶段允许自动弹出工具窗口；空白实例无此文件，保持安静。
        /// </summary>
        internal static readonly string AutoShowMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "chat-window-opened.flag");

        /// <summary>用户显式打开聊天窗口成功后调用：写入自动弹出标记。</summary>
        internal static void MarkChatWindowOpened()
        {
            try
            {
                var dir = Path.GetDirectoryName(AutoShowMarkerPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(AutoShowMarkerPath, DateTime.Now.ToString("O"));
            }
            catch
            {
                // 标记写入失败不影响功能，仅失去下次启动自动弹窗
            }
        }

        /// <summary>
        /// 解析当前实例自身的 hive 目录名（如 "18.0_ba3bb658Exp"），
        /// 用于设置迁移探测时自排除本实例的活动 privateregistry.bin。解析失败返回 null。
        /// 扩展部署路径形如 %LOCALAPPDATA%\Microsoft\VisualStudio\&lt;hive&gt;\Extensions\...，
        /// 取 VisualStudio 目录的直接子目录名即为当前 hive。
        /// </summary>
        private static string? TryGetOwnHiveName()
        {
            try
            {
                var asmPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(asmPath)) return null;

                var vsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "VisualStudio");

                var dir = Path.GetDirectoryName(asmPath);
                while (!string.IsNullOrEmpty(dir))
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (parent != null && string.Equals(parent, vsRoot, StringComparison.OrdinalIgnoreCase))
                        return Path.GetFileName(dir);
                    dir = parent;
                }
                return null;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Init] resolve own hive failed: {ex.Message}");
                return null;
            }
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
        public const string Version = "1.2.2";
    }
}
