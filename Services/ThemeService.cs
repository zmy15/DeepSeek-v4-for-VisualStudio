using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 主题服务：检测 VS 主题、管理浅色/深色模式切换、生成主题感知 CSS。
    /// 单例模式，在 Package 初始化时创建。
    /// </summary>
    public class ThemeService : IDisposable
    {
        #region Singleton

        private static readonly object _lock = new();
        private static ThemeService? _instance;
        private static volatile bool _initialized;

        public static ThemeService Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new ThemeService();
                        Logger.Warn("[ThemeService] Instance lazy-created BEFORE Initialize() — VS theme cache will be unset until Initialize() runs");
                    }
                    return _instance;
                }
            }
        }

        /// <summary>
        /// 初始化单例（在 UI 线程调用，订阅 VS 主题变更事件）。
        /// 幂等：即使 Instance 已被懒加载也会补做初始化。
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new ThemeService();
                }
                if (!_initialized)
                {
                    _initialized = true;
                    _instance.SubscribeToVSThemeChanges();
                }
            }
        }

        #endregion

        #region VS Theme Detection

        #region Events

        /// <summary>
        /// 当实际渲染主题发生变化时触发（深色 ↔ 浅色切换）。
        /// 仅在 Auto 模式下响应 VS 主题变更；手动模式下仅在用户主动切换时触发。
        /// </summary>
        public event Action<bool>? ThemeChanged;

        #endregion

        #region Properties

        private ThemeMode _userThemeMode = ThemeMode.Auto;

        /// <summary>
        /// 缓存的 VS 浅色主题状态（线程安全）。
        /// 在 UI 线程初始化时设置，后续可从任意线程读取。
        /// </summary>
        private volatile bool _cachedIsVSThemeLight;

        /// <summary>
        /// 用户设置的主题模式。
        /// </summary>
        public ThemeMode UserThemeMode
        {
            get => _userThemeMode;
            set
            {
                if (_userThemeMode == value) return;
                _userThemeMode = value;
                OnThemeChanged();
            }
        }

        /// <summary>
        /// VS 当前是否为浅色主题（线程安全，使用缓存值）。
        /// 通过检查 VS 环境背景色判断（深色主题背景色接近黑色，浅色接近白色）。
        /// </summary>
        public bool IsVSThemeLight
        {
            get
            {
                // 首先尝试返回缓存值（任意线程安全）
                return _cachedIsVSThemeLight;
            }
        }

        /// <summary>
        /// 在 UI 线程上刷新 VS 主题缓存。
        /// 使用多个 VS 颜色键采样投票，比单一键更可靠。
        /// </summary>
        public void RefreshVSThemeCache()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Logger.Info($"[ThemeService] RefreshVSThemeCache START — UI thread");

                // ═══ 方法一：注册表检测（最可靠） ═══
                bool? registryResult = TryGetThemeFromRegistry();
                if (registryResult.HasValue)
                {
                    _cachedIsVSThemeLight = registryResult.Value;
                    Logger.Info($"[ThemeService] VS theme cache set from REGISTRY: IsLight={_cachedIsVSThemeLight}");
                    return;
                }

                // ═══ 方法二：API 颜色键多数投票 ═══
                Logger.Info("[ThemeService] Registry failed, trying API color keys...");
                int lightVotes = 0;
                int totalVotes = 0;

                // ToolWindow background
                try
                {
                    var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                    double b = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                    totalVotes++;
                    if (b > 128) lightVotes++;
                    Logger.Info($"[ThemeService] API ToolWindowBg: R={c.R} G={c.G} B={c.B} b={b:F0} -> {(b > 128 ? "LIGHT" : "DARK")}");
                }
                catch (Exception ex) { Logger.Warn($"[ThemeService] API ToolWindowBg FAILED: {ex.GetType().Name}: {ex.Message}"); }

                // AccentBorder
                try
                {
                    var c = VSColorTheme.GetThemedColor(EnvironmentColors.AccentBorderColorKey);
                    double b = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                    totalVotes++;
                    if (b > 128) lightVotes++;
                    Logger.Info($"[ThemeService] API AccentBorder: R={c.R} G={c.G} B={c.B} b={b:F0} -> {(b > 128 ? "LIGHT" : "DARK")}");
                }
                catch (Exception ex) { Logger.Warn($"[ThemeService] API AccentBorder FAILED: {ex.GetType().Name}: {ex.Message}"); }

                // CommandBar
                try
                {
                    var c = VSColorTheme.GetThemedColor(EnvironmentColors.CommandBarGradientBeginColorKey);
                    double b = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                    totalVotes++;
                    if (b > 128) lightVotes++;
                    Logger.Info($"[ThemeService] API CommandBar: R={c.R} G={c.G} B={c.B} b={b:F0} -> {(b > 128 ? "LIGHT" : "DARK")}");
                }
                catch (Exception ex) { Logger.Warn($"[ThemeService] API CommandBar FAILED: {ex.GetType().Name}: {ex.Message}"); }

                if (totalVotes > 0)
                {
                    _cachedIsVSThemeLight = lightVotes * 2 >= totalVotes;
                    Logger.Info($"[ThemeService] VS theme cache set from API VOTE: IsLight={_cachedIsVSThemeLight} (votes: {lightVotes}/{totalVotes})");
                }
                else
                {
                    Logger.Warn("[ThemeService] ALL detection methods FAILED — defaulting to DARK");
                    _cachedIsVSThemeLight = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThemeService] RefreshVSThemeCache CRASHED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                _cachedIsVSThemeLight = TryGetThemeFromRegistry() ?? false;
            }
        }

        /// <summary>
        /// 从 Windows 注册表读取 VS 主题设置。
        /// 返回 null 表示无法从注册表确定。
        /// </summary>
        private static bool? TryGetThemeFromRegistry()
        {
            try
            {
                const string basePath = @"Software\Microsoft\VisualStudio";
                using var vsKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(basePath);
                if (vsKey == null)
                {
                    Logger.Warn($"[ThemeService] Registry: base key '{basePath}' not found");
                    return null;
                }

                Logger.Info($"[ThemeService] Registry: scanning subkeys under {basePath}...");
                foreach (var subKeyName in vsKey.GetSubKeyNames())
                {
                    if (!subKeyName.StartsWith("17.") && !subKeyName.StartsWith("18."))
                        continue;

                    Logger.Info($"[ThemeService] Registry: trying VS version key '{subKeyName}'");

                    // 路径一：ApplicationPrivateSettings\Microsoft\VisualStudio\Theme\ColorTheme
                    var themePath = subKeyName + @"\ApplicationPrivateSettings\Microsoft\VisualStudio\Theme";
                    using var themeKey = vsKey.OpenSubKey(themePath);
                    if (themeKey != null)
                    {
                        var colorTheme = themeKey.GetValue("ColorTheme") as string;
                        var colorThemeId = themeKey.GetValue("ColorThemeId")?.ToString();
                        Logger.Info($"[ThemeService] Registry: {themePath}\\ColorTheme='{colorTheme ?? "null"}', ColorThemeId='{colorThemeId ?? "null"}'");

                        if (!string.IsNullOrEmpty(colorTheme))
                        {
                            bool? result = ParseThemeGuid(colorTheme);
                            if (result.HasValue)
                            {
                                Logger.Info($"[ThemeService] Registry RESULT: IsLight={result.Value} (from ColorTheme)");
                                return result;
                            }
                        }
                    }
                    else
                    {
                        Logger.Warn($"[ThemeService] Registry: key '{themePath}' not found");
                    }

                    // 路径二：General\CurrentTheme (integer: 0=Blue, 1=Dark, 2=Light, 3=Dark+)
                    var generalPath = subKeyName + @"\General";
                    using var generalKey = vsKey.OpenSubKey(generalPath);
                    if (generalKey != null)
                    {
                        var currentTheme = generalKey.GetValue("CurrentTheme");
                        Logger.Info($"[ThemeService] Registry: {generalPath}\\CurrentTheme='{currentTheme ?? "null"}' (type={currentTheme?.GetType().Name ?? "null"})");

                        if (currentTheme is int themeInt)
                        {
                            // VS 2022: 0=Blue(Light), 1=Dark, 2=Light, 3=Dark(extra contrast)
                            bool isLight = themeInt == 0 || themeInt == 2;
                            Logger.Info($"[ThemeService] Registry RESULT: IsLight={isLight} (from CurrentTheme={themeInt})");
                            return isLight;
                        }
                    }
                    else
                    {
                        Logger.Warn($"[ThemeService] Registry: key '{generalPath}' not found");
                    }
                }

                Logger.Warn("[ThemeService] Registry: no VS 17.x/18.x keys found");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThemeService] Registry FAILED: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 ColorTheme GUID 字符串。
        /// </summary>
        private static bool? ParseThemeGuid(string guidString)
        {
            // de3dbbcd-f642-433c-8353-8f1df4370aba = Light
            // a4d6a176-b948-4b29-8c66-53c97a1ed7d0 = Blue (also light)
            // 1ded0138-47ce-435e-84ef-9ec1f439b749 = Dark
            if (guidString.Contains("de3dbbcd") || guidString.Contains("a4d6a176"))
                return true;
            if (guidString.Contains("1ded0138"))
                return false;
            return null; // unknown GUID
        }

        /// <summary>
        /// 当前实际渲染是否为浅色模式（综合用户设置和 VS 主题）。
        /// </summary>
        public bool IsLight
        {
            get
            {
                bool result = _userThemeMode switch
                {
                    ThemeMode.Light => true,
                    ThemeMode.Dark => false,
                    _ => IsVSThemeLight
                };
                Logger.Info($"[ThemeService] IsLight accessed: Mode={_userThemeMode}, CachedVS={_cachedIsVSThemeLight}, Result={result}");
                return result;
            }
        }

        /// <summary>
        /// 当前有效的 Highlight.js CSS CDN 链接。
        /// </summary>
        public string HighlightJsCdnStyle
        {
            get
            {
                return IsLight
                    ? "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github.min.css"
                    : "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css";
            }
        }

        /// <summary>
        /// 获取当前主题对应的页面 CSS（用于 WebView2）。
        /// </summary>
        public string PageCss => IsLight ? LightPageCss : DarkPageCss;

        #endregion

        #region Constructor & VS Theme Subscription

        private ThemeService()
        {
        }

        private void SubscribeToVSThemeChanges()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Logger.Info($"[ThemeService] SubscribeToVSThemeChanges — on UI thread, refreshing cache...");
                RefreshVSThemeCache();
                VSColorTheme.ThemeChanged += OnVSThemeChanged;
                Logger.Info($"[ThemeService] Subscribed to VSColorTheme.ThemeChanged OK. Cached IsLight={_cachedIsVSThemeLight}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThemeService] SubscribeToVSThemeChanges FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void OnVSThemeChanged(ThemeChangedEventArgs e)
        {
            Logger.Info($"[ThemeService] VS theme changed.");
            // 先刷新缓存，再通知
            RefreshVSThemeCache();
            if (_userThemeMode == ThemeMode.Auto)
            {
                OnThemeChanged();
            }
        }

        private void OnThemeChanged()
        {
            bool isLight = IsLight;
            Logger.Info($"[ThemeService] Theme changed: IsLight={isLight}, Mode={_userThemeMode}");

            // 保存设置
            try
            {
                var options = DeepSeekOptionsPage.Instance;
                if (options != null)
                {
                    options.ThemeMode = _userThemeMode;
                }
            }
            catch { }

            ThemeChanged?.Invoke(isLight);
        }

        #endregion

        #region CSS Definitions

        /// <summary>深色主题 CSS（原有样式）。</summary>
        private const string DarkPageCss = @"*{box-sizing:border-box;margin:0;padding:0}
body{background-color:#1e1e1e;color:#cccccc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:13px;line-height:1.6;padding:0;overflow-wrap:break-word;word-wrap:break-word;scroll-behavior:smooth}
#chat-container{padding:12px 12px 30px 12px;max-width:100%;margin:0}
h1,h2,h3,h4,h5,h6{color:#e0e0e0;margin:16px 0 8px;font-weight:600}h1{font-size:1.3em}h2{font-size:1.15em}h3{font-size:1.05em}
p{margin:6px 0}a{color:#4fc1ff;text-decoration:none}a:hover{text-decoration:underline}strong,b{color:#e8e8e8;font-weight:600}
code{background:#333;color:#f48771;padding:1px 6px;border-radius:3px;font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:0.9em}
pre{background:#252526;border:1px solid #3c3c3c;border-radius:8px;padding:32px 14px 12px 14px;margin:10px 0;overflow-x:auto;overflow-y:auto;max-height:480px;font-size:0.88em;line-height:1.5;position:relative}
pre code{background:transparent;color:#d4d4d4;padding:0;font-size:inherit;white-space:pre;display:block}
ul,ol{padding-left:22px;margin:6px 0}li{margin:2px 0}blockquote{border-left:3px solid #4fc1ff;padding:6px 12px;margin:8px 0;background:#2a2a2a;color:#aaa}
table{border-collapse:collapse;margin:8px 0;width:100%}th,td{border:1px solid #444;padding:6px 10px;text-align:left}th{background:#333;color:#e0e0e0;font-weight:600}hr{border:none;border-top:1px solid #444;margin:12px 0}
.code-lang{position:absolute;top:6px;left:14px;color:#9cdcfe;font-size:10px;font-family:'Segoe UI',sans-serif;text-transform:uppercase}
.copy-btn{position:absolute;top:4px;right:8px;background:#3c3c3c;color:#ccc;border:1px solid #555;border-radius:4px;padding:2px 10px;font-size:11px;cursor:pointer;font-family:'Segoe UI',sans-serif;z-index:1;transition:all .15s}.copy-btn:hover{background:#505050;color:#fff}
.msg-wrapper{display:flex;gap:12px;margin-bottom:24px;padding:0 4px}.msg-wrapper.user{justify-content:flex-end;align-items:center;gap:6px}
.msg-avatar{width:28px;height:28px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:13px;flex-shrink:0}.msg-avatar.ai{background:#4ec9b0;color:#1e1e1e}.msg-avatar.user{background:#569cd6;color:#fff}
.msg-bubble{max-width:85%;min-width:0}.msg-bubble.ai{flex:1;min-width:0}
.msg-role-label{font-size:11px;font-weight:600;margin-bottom:4px;color:#999;text-transform:uppercase;letter-spacing:1px}.msg-role-label.user{align-self:flex-end;margin-bottom:6px;color:#569cd6;text-align:left}.msg-role-label.ai{color:#4ec9b0}
.msg-content{font-size:13px;line-height:1.65}.msg-content p:first-child{margin-top:0}.msg-content p:last-child{margin-bottom:0}
.msg-wrapper.user .msg-content{background:#264f78;border-radius:12px 12px 4px 12px;padding:10px 14px;color:#d4d4d4}
.msg-wrapper.user .msg-content pre{background:#1e3a5a;border-color:#2d5a8a}
.msg-wrapper.ai .msg-content{background:#2a2a2a;border:1px solid #555555;border-radius:4px 12px 12px 12px;padding:10px 14px;color:#d4d4d4}
.msg-wrapper.ai .msg-content pre{background:#1e1e1e;border-color:#333}
.agent-route-badge{display:inline-block;background:#3a2a5a;color:#c8a0f0;font-size:10px;font-weight:700;padding:2px 8px;border-radius:4px;margin-bottom:6px;letter-spacing:0.5px;text-transform:uppercase}
.reasoning-panel{margin:8px 0;border:1px solid #3a3a5a;border-radius:8px;background:#1e1e2e;overflow:hidden}
.reasoning-panel summary{cursor:pointer;padding:8px 14px;color:#9b9bd4;font-size:12px;font-weight:600;background:#252540;user-select:none;list-style:none}
.reasoning-panel summary::-webkit-details-marker{display:none}
.reasoning-panel .reasoning-content{padding:10px 14px;color:#8a8ab4;font-size:12px;font-style:italic;line-height:1.5;white-space:pre-wrap;max-height:300px;overflow-y:auto}
.search-results-card{margin:8px 0 12px;border:1px solid #3a5a8a;border-radius:8px;background:#1a2636}
.search-results-card summary{cursor:pointer;padding:8px 14px;color:#7eb8e0;font-size:12px;font-weight:600;background:#253545;list-style:none}
.search-results-card summary::-webkit-details-marker{display:none}
.search-results-card .search-result-item{padding:8px 14px;border-bottom:1px solid #2a3a4a}.search-results-card .search-result-item:last-child{border-bottom:none}
.search-results-card .search-result-title{color:#6cafd9;font-size:12px;font-weight:600;display:block}
.search-results-card .search-result-url{color:#608b4e;font-size:10px;display:block;word-break:break-all}
.search-results-card .search-result-snippet{color:#a0a0a0;font-size:11px;line-height:1.4}
.agent-plan{margin:4px 0}
.agent-step-node{display:flex;gap:6px;margin:3px 0}.agent-step-bullet-wrap{display:flex;flex-direction:column;align-items:center;width:16px;flex-shrink:0}
.agent-step-bullet{width:12px;height:12px;border-radius:50%;font-size:9px;display:flex;align-items:center;justify-content:center;font-weight:bold}
.agent-step-bullet.completed{background:#4ec9b0;color:#1e1e1e}.agent-step-bullet.in-progress{background:#e0c060;color:#1e1e1e;animation:pulse 1.5s infinite}
.agent-step-bullet.failed{background:#f48771;color:#1e1e1e}.agent-step-bullet.pending{background:#333;color:#aaa}
.agent-step-line{width:2px;flex:1;background:#333;min-height:8px;margin:1px 0}.agent-step-line.done{background:#4ec9b0}.agent-step-line.active{background:#e0c060}
.agent-step-content{flex:1;min-width:0}.agent-step-title{font-size:11px;color:#ccc}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.5}}
.cache-stat-card{display:flex;align-items:center;gap:8px;padding:8px 12px;background:#2a2a2a;border:1px solid #3c3c3c;border-radius:6px;margin:10px 0;font-size:11px}
.cache-icon{font-size:14px}.cache-rate{font-weight:700;font-size:12px}.cache-rate.high{color:#6cd96c}.cache-rate.medium{color:#e0c060}.cache-rate.low{color:#f48771}
.cache-bar-wrap{flex:1;height:4px;background:#333;border-radius:2px}.cache-bar-fill{height:100%;border-radius:2px}
.cache-bar-fill.high{background:#6cd96c}.cache-bar-fill.medium{background:#e0c060}.cache-bar-fill.low{background:#f48771}.cache-detail{color:#888}
.msg-action-btn{display:inline-flex;align-items:center;gap:4px;background:transparent;border:none;color:#888;cursor:pointer;font-size:11px;padding:2px 6px;border-radius:3px;margin-top:4px;opacity:0;transition:opacity .15s}
.msg-action-btn.retry-btn{font-size:14px;padding:6px 16px;margin-top:8px;border:1px solid #555;border-radius:6px}
.msg-action-btn.copy-msg-btn{font-size:12px;padding:6px 16px;margin-top:8px;border:1px solid #555;border-radius:6px}
.msg-action-btn.handoff-btn{opacity:1 !important}
.msg-wrapper:hover .msg-action-btn{opacity:1}.msg-action-btn:hover{background:#3c3c3c;color:#e0e0e0}.msg-action-btn.retry-btn:hover{color:#4fc1ff;border-color:#4fc1ff;background:#2a3a4a}.msg-action-btn.edit-btn:hover{color:#f48771}.msg-action-btn.copy-msg-btn:hover{color:#6cd96c;border-color:#6cd96c;background:#2a3a2a}.msg-action-btn.copy-msg-btn.copied{color:#6cd96c;opacity:1}
.inline-edit-area{margin:4px 0}.inline-edit-area textarea{box-sizing:border-box;width:100%;min-height:80px;background:#1e1e1e;color:#d4d4d4;border:1px solid #4fc1ff;border-radius:6px;padding:8px 12px;font-size:13px;font-family:inherit;resize:vertical}
.edit-actions{display:flex;gap:8px;margin-top:6px}.inline-edit-btn-save{background:#0e639c;color:#fff;border:none;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px}
.inline-edit-btn-save:hover{background:#1177bb}.inline-edit-btn-cancel{background:#3c3c3c;color:#ccc;border:1px solid #555;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px}
@keyframes blink{0%,100%{opacity:1}50%{opacity:0}}@keyframes fadeIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:translateY(0)}}.streaming-cursor{display:inline-block;width:1px;height:14px;background:#4fc1ff;margin-left:2px;animation:blink 1s infinite;vertical-align:text-bottom}
.branch-nav{display:flex;align-items:center;gap:6px;margin-top:6px;font-size:11px;color:#888}
.branch-nav-btn{background:transparent;border:1px solid #555;color:#aaa;cursor:pointer;font-size:11px;padding:2px 8px;border-radius:3px}
.branch-nav-btn:hover:not(:disabled){background:#3c3c3c;color:#e0e0e0}.branch-nav-btn:disabled{opacity:.3;cursor:default}.branch-nav-label{color:#aaa;min-width:40px;text-align:center}
.tool-call-section{border-left:2px solid #4fc1ff;padding-left:12px;margin:8px 0;font-size:12px;line-height:1.6}
.tool-call-section p{margin:2px 0}
.tool-call-section ul{padding-left:16px;margin:4px 0}
.tool-call-section li{margin:3px 0;color:#b0c8e0}
.tool-call-section code{background:#2a3a4a;color:#7eb8e0;font-size:11px}
.tool-call-section strong{color:#d0d8e0}
.tool-call-result{color:#8a8;font-size:11px;margin-left:4px}
.agent-task-panel{position:sticky;bottom:0;z-index:10;margin:8px 0 0 0;border:1px solid #3a4a5a;border-radius:6px 6px 0 0;background:#1a1e2a;overflow:hidden;box-shadow:0 -2px 12px rgba(0,0,0,.4)}
.agent-task-panel.collapsed .agent-task-panel-body{display:none}
.agent-task-panel-header{display:flex;align-items:center;gap:6px;padding:6px 10px;background:#222a3a;cursor:pointer;user-select:none;border-bottom:1px solid #2a3a4a}
.agent-task-panel-header:hover{background:#263040}
.task-icon{font-size:12px}.task-title{color:#b0c8e0;font-size:11px;font-weight:600;flex:1}.task-progress{color:#7ea8c8;font-size:10px}
.task-close{background:transparent;border:1px solid #4a5a6a;color:#8a9ab0;cursor:pointer;font-size:11px;width:20px;height:20px;border-radius:50%;display:flex;align-items:center;justify-content:center;transition:all .2s;flex-shrink:0}
.task-close:hover{background:#c0392b;color:#fff;border-color:#c0392b;transform:scale(1.1)}
.task-close.finished{background:#3C1A1A;color:#E07878;border-color:#6A3A3A}
.task-close.finished:hover{background:#c0392b;color:#fff;border-color:#c0392b}
.agent-task-panel-body{padding:6px 10px;max-height:24vh;overflow-y:auto}
.terminal-approval-card{margin:12px 0;border:1px solid #5a4a2a;border-radius:10px;background:#1e1a12;overflow:hidden;box-shadow:0 2px 12px rgba(0,0,0,.3)}
.terminal-approval-card-header{display:flex;align-items:center;gap:10px;padding:10px 14px;background:#2a2218;border-bottom:1px solid #3a2e1a;font-size:13px;font-weight:600;color:#e0c870}
.terminal-approval-card-header .icon{font-size:18px}
.terminal-approval-card-header .title{color:#e0c870}
.terminal-approval-card-body{padding:12px 14px}
.terminal-approval-card-body .warning-text{color:#c88;font-size:12px;margin-bottom:8px}
.terminal-approval-card-body .terminal-purpose{color:#CEA85C;font-size:11px;margin-bottom:10px;padding:6px 10px;background:#2A2218;border-left:3px solid #C8A84E;border-radius:4px;line-height:1.5}
.terminal-approval-card-body .cmd-block{background:#1a1a1a;border:1px solid #444;border-radius:6px;padding:10px 14px;font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:12px;color:#e0e0e0;white-space:pre-wrap;word-break:break-all;max-height:200px;overflow-y:auto;margin-bottom:8px}
.terminal-approval-card-body .cmd-explanation{color:#9a9a9a;font-size:11px;margin-top:6px}
.terminal-approval-card-footer{display:flex;gap:8px;padding:10px 14px;border-top:1px solid #3a2e1a;justify-content:flex-end}
.terminal-approval-btn-allow{background:#2a5a2a;color:#c0e8c0;border:1px solid #3a7a3a;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s}
.terminal-approval-btn-allow:hover{background:#3a7a3a;color:#fff}
.terminal-approval-btn-skip{background:#3c3c3c;color:#ccc;border:1px solid #555;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;transition:all .15s}
.terminal-approval-btn-skip:hover{background:#505050;color:#fff}
.file-delete-card{margin:12px 0;border:1px solid #6a3a3a;border-radius:10px;background:#1e1a1a;overflow:hidden;box-shadow:0 2px 12px rgba(0,0,0,.3)}
.file-delete-card-header{display:flex;align-items:center;gap:10px;padding:10px 14px;background:#2a1a1a;border-bottom:1px solid #3a1a1a;font-size:13px;font-weight:600;color:#e07878}
.file-delete-card-header .icon{font-size:18px}
.file-delete-card-header .title{color:#e07878}
.file-delete-card-body{padding:12px 14px}
.file-delete-card-body .file-delete-purpose{color:#CEA85C;font-size:11px;margin-bottom:10px;padding:6px 10px;background:#2A2218;border-left:3px solid #C8A84E;border-radius:4px;line-height:1.5}
.file-delete-card-body .warning-text{color:#c88;font-size:12px;margin-bottom:8px}
.file-delete-card-body .file-list{display:flex;flex-direction:column;gap:4px;margin-bottom:8px}
.file-delete-card-body .file-item{display:flex;align-items:center;gap:6px;padding:4px 8px;background:#1a1a1a;border:1px solid #3a2a2a;border-radius:4px}
.file-delete-card-body .file-icon{font-size:14px}
.file-delete-card-body .file-path{color:#d4d4d4;font-size:12px;font-family:'Cascadia Code','Fira Code',Consolas,monospace}
.file-delete-card-footer{display:flex;gap:8px;padding:10px 14px;border-top:1px solid #3a1a1a;justify-content:flex-end}
.file-delete-btn-confirm{background:#6a1a1a;color:#e07878;border:1px solid #8a3a3a;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s}
.file-delete-btn-confirm:hover{background:#8a2a2a;color:#fff}
.file-delete-btn-cancel{background:#3c3c3c;color:#ccc;border:1px solid #555;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;transition:all .15s}
.file-delete-btn-cancel:hover{background:#505050;color:#fff}
::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-track{background:#1e1e1e}::-webkit-scrollbar-thumb{background:#555;border-radius:4px}::-webkit-scrollbar-thumb:hover{background:#777}";

        /// <summary>浅色主题 CSS。</summary>
        private const string LightPageCss = @"*{box-sizing:border-box;margin:0;padding:0}
body{background-color:#ffffff;color:#333333;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:13px;line-height:1.6;padding:0;overflow-wrap:break-word;word-wrap:break-word;scroll-behavior:smooth}
#chat-container{padding:12px 12px 30px 12px;max-width:100%;margin:0}
h1,h2,h3,h4,h5,h6{color:#1e1e1e;margin:16px 0 8px;font-weight:600}h1{font-size:1.3em}h2{font-size:1.15em}h3{font-size:1.05em}
p{margin:6px 0}a{color:#0078d4;text-decoration:none}a:hover{text-decoration:underline}strong,b{color:#1a1a1a;font-weight:600}
code{background:#f0f0f0;color:#c7254e;padding:1px 6px;border-radius:3px;font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:0.9em}
pre{background:#f6f8fa;border:1px solid #d0d7de;border-radius:8px;padding:32px 14px 12px 14px;margin:10px 0;overflow-x:auto;overflow-y:auto;max-height:480px;font-size:0.88em;line-height:1.5;position:relative}
pre code{background:transparent;color:#24292f;padding:0;font-size:inherit;white-space:pre;display:block}
ul,ol{padding-left:22px;margin:6px 0}li{margin:2px 0}blockquote{border-left:3px solid #0078d4;padding:6px 12px;margin:8px 0;background:#f6f8fa;color:#656d76}
table{border-collapse:collapse;margin:8px 0;width:100%}th,td{border:1px solid #d0d7de;padding:6px 10px;text-align:left}th{background:#f6f8fa;color:#1e1e1e;font-weight:600}hr{border:none;border-top:1px solid #d0d7de;margin:12px 0}
.code-lang{position:absolute;top:6px;left:14px;color:#0550ae;font-size:10px;font-family:'Segoe UI',sans-serif;text-transform:uppercase}
.copy-btn{position:absolute;top:4px;right:8px;background:#e8e8e8;color:#555;border:1px solid #ccc;border-radius:4px;padding:2px 10px;font-size:11px;cursor:pointer;font-family:'Segoe UI',sans-serif;z-index:1;transition:all .15s}.copy-btn:hover{background:#d0d0d0;color:#333}
.msg-wrapper{display:flex;gap:12px;margin-bottom:24px;padding:0 4px}.msg-wrapper.user{justify-content:flex-end;align-items:center;gap:6px}
.msg-avatar{width:28px;height:28px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:13px;flex-shrink:0}.msg-avatar.ai{background:#2ea87a;color:#fff}.msg-avatar.user{background:#0078d4;color:#fff}
.msg-bubble{max-width:85%;min-width:0}.msg-bubble.ai{flex:1;min-width:0}
.msg-role-label{font-size:11px;font-weight:600;margin-bottom:4px;color:#888;text-transform:uppercase;letter-spacing:1px}.msg-role-label.user{align-self:flex-end;margin-bottom:6px;color:#0078d4;text-align:left}.msg-role-label.ai{color:#2ea87a}
.msg-content{font-size:13px;line-height:1.65}.msg-content p:first-child{margin-top:0}.msg-content p:last-child{margin-bottom:0}
.msg-wrapper.user .msg-content{background:#e3f2fd;border-radius:12px 12px 4px 12px;padding:10px 14px;color:#333}
.msg-wrapper.user .msg-content pre{background:#d6e9f8;border-color:#b8d4e8}
.msg-wrapper.ai .msg-content{background:#f8f8f8;border:1px solid #e0e0e0;border-radius:4px 12px 12px 12px;padding:10px 14px;color:#333}
.msg-wrapper.ai .msg-content pre{background:#f0f0f0;border-color:#ddd}
.agent-route-badge{display:inline-block;background:#e8e0f0;color:#6b3fa0;font-size:10px;font-weight:700;padding:2px 8px;border-radius:4px;margin-bottom:6px;letter-spacing:0.5px;text-transform:uppercase}
.reasoning-panel{margin:8px 0;border:1px solid #d0d0e8;border-radius:8px;background:#f5f5fa;overflow:hidden}
.reasoning-panel summary{cursor:pointer;padding:8px 14px;color:#5555aa;font-size:12px;font-weight:600;background:#e8e8f0;user-select:none;list-style:none}
.reasoning-panel summary::-webkit-details-marker{display:none}
.reasoning-panel .reasoning-content{padding:10px 14px;color:#6666aa;font-size:12px;font-style:italic;line-height:1.5;white-space:pre-wrap;max-height:300px;overflow-y:auto}
.search-results-card{margin:8px 0 12px;border:1px solid #c0d8ee;border-radius:8px;background:#f0f6fc}
.search-results-card summary{cursor:pointer;padding:8px 14px;color:#3a7abf;font-size:12px;font-weight:600;background:#e6f0fa;list-style:none}
.search-results-card summary::-webkit-details-marker{display:none}
.search-results-card .search-result-item{padding:8px 14px;border-bottom:1px solid #d8e4f0}.search-results-card .search-result-item:last-child{border-bottom:none}
.search-results-card .search-result-title{color:#1a6ab5;font-size:12px;font-weight:600;display:block}
.search-results-card .search-result-url{color:#3a7a3a;font-size:10px;display:block;word-break:break-all}
.search-results-card .search-result-snippet{color:#555;font-size:11px;line-height:1.4}
.agent-plan{margin:4px 0}
.agent-step-node{display:flex;gap:6px;margin:3px 0}.agent-step-bullet-wrap{display:flex;flex-direction:column;align-items:center;width:16px;flex-shrink:0}
.agent-step-bullet{width:12px;height:12px;border-radius:50%;font-size:9px;display:flex;align-items:center;justify-content:center;font-weight:bold}
.agent-step-bullet.completed{background:#2ea87a;color:#fff}.agent-step-bullet.in-progress{background:#c8a030;color:#fff;animation:pulse 1.5s infinite}
.agent-step-bullet.failed{background:#d04040;color:#fff}.agent-step-bullet.pending{background:#e0e0e0;color:#999}
.agent-step-line{width:2px;flex:1;background:#e0e0e0;min-height:8px;margin:1px 0}.agent-step-line.done{background:#2ea87a}.agent-step-line.active{background:#c8a030}
.agent-step-content{flex:1;min-width:0}.agent-step-title{font-size:11px;color:#555}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.5}}
.cache-stat-card{display:flex;align-items:center;gap:8px;padding:8px 12px;background:#f6f8fa;border:1px solid #d0d7de;border-radius:6px;margin:10px 0;font-size:11px}
.cache-icon{font-size:14px}.cache-rate{font-weight:700;font-size:12px}.cache-rate.high{color:#2a8a2a}.cache-rate.medium{color:#c8a030}.cache-rate.low{color:#d04040}
.cache-bar-wrap{flex:1;height:4px;background:#e0e0e0;border-radius:2px}.cache-bar-fill{height:100%;border-radius:2px}
.cache-bar-fill.high{background:#2a8a2a}.cache-bar-fill.medium{background:#c8a030}.cache-bar-fill.low{background:#d04040}.cache-detail{color:#888}
.msg-action-btn{display:inline-flex;align-items:center;gap:4px;background:transparent;border:none;color:#999;cursor:pointer;font-size:11px;padding:2px 6px;border-radius:3px;margin-top:4px;opacity:0;transition:opacity .15s}
.msg-action-btn.retry-btn{font-size:14px;padding:6px 16px;margin-top:8px;border:1px solid #ccc;border-radius:6px}
.msg-action-btn.copy-msg-btn{font-size:12px;padding:6px 16px;margin-top:8px;border:1px solid #ccc;border-radius:6px}
.msg-action-btn.handoff-btn{opacity:1 !important}
.msg-wrapper:hover .msg-action-btn{opacity:1}.msg-action-btn:hover{background:#e8e8e8;color:#333}.msg-action-btn.retry-btn:hover{color:#0078d4;border-color:#0078d4;background:#e3f2fd}.msg-action-btn.edit-btn:hover{color:#d04040}.msg-action-btn.copy-msg-btn:hover{color:#2a8a2a;border-color:#2a8a2a;background:#e8f5e9}.msg-action-btn.copy-msg-btn.copied{color:#2a8a2a;opacity:1}
.inline-edit-area{margin:4px 0}.inline-edit-area textarea{box-sizing:border-box;width:100%;min-height:80px;background:#fff;color:#333;border:1px solid #0078d4;border-radius:6px;padding:8px 12px;font-size:13px;font-family:inherit;resize:vertical}
.edit-actions{display:flex;gap:8px;margin-top:6px}.inline-edit-btn-save{background:#0078d4;color:#fff;border:none;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px}
.inline-edit-btn-save:hover{background:#106ebe}.inline-edit-btn-cancel{background:#e8e8e8;color:#555;border:1px solid #ccc;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px}
@keyframes blink{0%,100%{opacity:1}50%{opacity:0}}@keyframes fadeIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:translateY(0)}}.streaming-cursor{display:inline-block;width:1px;height:14px;background:#0078d4;margin-left:2px;animation:blink 1s infinite;vertical-align:text-bottom}
.branch-nav{display:flex;align-items:center;gap:6px;margin-top:6px;font-size:11px;color:#888}
.branch-nav-btn{background:transparent;border:1px solid #ccc;color:#555;cursor:pointer;font-size:11px;padding:2px 8px;border-radius:3px}
.branch-nav-btn:hover:not(:disabled){background:#e8e8e8;color:#333}.branch-nav-btn:disabled{opacity:.3;cursor:default}.branch-nav-label{color:#555;min-width:40px;text-align:center}
.tool-call-section{border-left:2px solid #0078d4;padding-left:12px;margin:8px 0;font-size:12px;line-height:1.6}
.tool-call-section p{margin:2px 0}
.tool-call-section ul{padding-left:16px;margin:4px 0}
.tool-call-section li{margin:3px 0;color:#3a5070}
.tool-call-section code{background:#e3f2fd;color:#106ebe;font-size:11px}
.tool-call-section strong{color:#333}
.tool-call-result{color:#6a6;font-size:11px;margin-left:4px}
.agent-task-panel{position:sticky;bottom:0;z-index:10;margin:8px 0 0 0;border:1px solid #c0d0e0;border-radius:6px 6px 0 0;background:#f0f4f8;overflow:hidden;box-shadow:0 -2px 12px rgba(0,0,0,.1)}
.agent-task-panel.collapsed .agent-task-panel-body{display:none}
.agent-task-panel-header{display:flex;align-items:center;gap:6px;padding:6px 10px;background:#e4ecf4;cursor:pointer;user-select:none;border-bottom:1px solid #d0dce8}
.agent-task-panel-header:hover{background:#d8e4f0}
.task-icon{font-size:12px}.task-title{color:#3a5070;font-size:11px;font-weight:600;flex:1}.task-progress{color:#5a7a9a;font-size:10px}
.task-close{background:transparent;border:1px solid #b0c0d0;color:#7a8a9a;cursor:pointer;font-size:11px;width:20px;height:20px;border-radius:50%;display:flex;align-items:center;justify-content:center;transition:all .2s;flex-shrink:0}
.task-close:hover{background:#c0392b;color:#fff;border-color:#c0392b;transform:scale(1.1)}
.task-close.finished{background:#f8e0e0;color:#c04040;border-color:#d8a0a0}
.task-close.finished:hover{background:#c0392b;color:#fff;border-color:#c0392b}
.agent-task-panel-body{padding:6px 10px;max-height:24vh;overflow-y:auto}
.terminal-approval-card{margin:12px 0;border:1px solid #d8c8a0;border-radius:10px;background:#fffdf5;overflow:hidden;box-shadow:0 2px 12px rgba(0,0,0,.08)}
.terminal-approval-card-header{display:flex;align-items:center;gap:10px;padding:10px 14px;background:#faf5e8;border-bottom:1px solid #e8dcc0;font-size:13px;font-weight:600;color:#8a7030}
.terminal-approval-card-header .icon{font-size:18px}
.terminal-approval-card-header .title{color:#8a7030}
.terminal-approval-card-body{padding:12px 14px}
.terminal-approval-card-body .warning-text{color:#c04040;font-size:12px;margin-bottom:8px}
.terminal-approval-card-body .terminal-purpose{color:#8a7030;font-size:11px;margin-bottom:10px;padding:6px 10px;background:#faf5e8;border-left:3px solid #c8a84e;border-radius:4px;line-height:1.5}
.terminal-approval-card-body .cmd-block{background:#f6f8fa;border:1px solid #d0d7de;border-radius:6px;padding:10px 14px;font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:12px;color:#333;white-space:pre-wrap;word-break:break-all;max-height:200px;overflow-y:auto;margin-bottom:8px}
.terminal-approval-card-body .cmd-explanation{color:#888;font-size:11px;margin-top:6px}
.terminal-approval-card-footer{display:flex;gap:8px;padding:10px 14px;border-top:1px solid #e8dcc0;justify-content:flex-end}
.terminal-approval-btn-allow{background:#e8f5e9;color:#2a7a2a;border:1px solid #a0d0a0;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s}
.terminal-approval-btn-allow:hover{background:#2a7a2a;color:#fff}
.terminal-approval-btn-skip{background:#e8e8e8;color:#555;border:1px solid #ccc;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;transition:all .15s}
.terminal-approval-btn-skip:hover{background:#d0d0d0;color:#333}
.file-delete-card{margin:12px 0;border:1px solid #e0b0b0;border-radius:10px;background:#fff8f8;overflow:hidden;box-shadow:0 2px 12px rgba(0,0,0,.08)}
.file-delete-card-header{display:flex;align-items:center;gap:10px;padding:10px 14px;background:#fae8e8;border-bottom:1px solid #e8c8c8;font-size:13px;font-weight:600;color:#c04040}
.file-delete-card-header .icon{font-size:18px}
.file-delete-card-header .title{color:#c04040}
.file-delete-card-body{padding:12px 14px}
.file-delete-card-body .file-delete-purpose{color:#8a7030;font-size:11px;margin-bottom:10px;padding:6px 10px;background:#faf5e8;border-left:3px solid #c8a84e;border-radius:4px;line-height:1.5}
.file-delete-card-body .warning-text{color:#c04040;font-size:12px;margin-bottom:8px}
.file-delete-card-body .file-list{display:flex;flex-direction:column;gap:4px;margin-bottom:8px}
.file-delete-card-body .file-item{display:flex;align-items:center;gap:6px;padding:4px 8px;background:#f6f8fa;border:1px solid #e0d0d0;border-radius:4px}
.file-delete-card-body .file-icon{font-size:14px}
.file-delete-card-body .file-path{color:#333;font-size:12px;font-family:'Cascadia Code','Fira Code',Consolas,monospace}
.file-delete-card-footer{display:flex;gap:8px;padding:10px 14px;border-top:1px solid #e8c8c8;justify-content:flex-end}
.file-delete-btn-confirm{background:#fae0e0;color:#c04040;border:1px solid #e0a0a0;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s}
.file-delete-btn-confirm:hover{background:#c04040;color:#fff}
.file-delete-btn-cancel{background:#e8e8e8;color:#555;border:1px solid #ccc;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px;transition:all .15s}
.file-delete-btn-cancel:hover{background:#d0d0d0;color:#333}
::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-track{background:#f0f0f0}::-webkit-scrollbar-thumb{background:#ccc;border-radius:4px}::-webkit-scrollbar-thumb:hover{background:#999}";

        #endregion

        #region IDisposable

        public void Dispose()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                VSColorTheme.ThemeChanged -= OnVSThemeChanged;
            }
            catch { }
        }

        #endregion

        #endregion // VS Theme Detection
    }
}
