using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// DeepSeek Chat 主控件，对标共享项目 ucChat。
    /// 宿主 WebView2（Chromium），采用增量渲染模式：
    /// - 首次加载使用 NavigateToString 构建完整页面
    /// - 后续消息通过 ExecuteScriptAsync 调用 JS 增量追加
    /// - 流式输出时通过 BuildStreamingUpdateJs 实时更新 DOM，消除全页刷新闪烁
    /// </summary>
    public partial class DeepSeekChatControl : System.Windows.Controls.UserControl, IDisposable
    {
        #region Constants

        private const string WelcomeMessage = AiPrompts.WelcomeMessage;

        private const string ApiKeyMissingMessage = AiPrompts.ApiKeyMissingMessage;

        /// <summary>
        /// 流式更新间隔（字符数），每累积这么多字符触发一次 DOM 更新。
        /// </summary>
        private const int StreamRenderInterval = 15;

        #endregion

        #region Properties

        private DeepSeek_v4_for_VisualStudioPackage? _package;
        private DeepSeekOptionsPage? _options;
        private DeepSeekApiService? _apiService;
        private WebSearchService? _webSearchService;
        private CancellationTokenSource? _currentStreamingCts;
        private string? _solutionPath;

        private readonly List<ChatMessage> _messages = new();
        private readonly List<ChatApiMessage> _conversationHistory = new();
        private bool _isGenerating;
        private string _webSearchEngine = "Off"; // "Off" | "Baidu" | "DuckDuckGo"
        private readonly List<string> _pendingWarnings = new(); // 待注入的警告消息

        // ── 文件上传 ──
        private readonly List<string> _attachedFilePaths = new(); // 已选文件路径列表

        // ── 多会话支持 ──
        private SessionsContainer? _sessionsContainer;
        private ChatSession? _activeSession;

        // ── 增量渲染状态（对标 Turbo ucChat） ──
        private bool _browserInitialized;
        private int _lastRenderedMessagesLength;
        private readonly StringBuilder _messagesHtml = new();

        // ── 线程安全 ──
        private readonly object _lock = new();

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化控件。
        /// </summary>
        public DeepSeekChatControl()
        {
            InitializeComponent();

            // 初始化模型和推理强度下拉框
            ModelComboBox.ItemsSource = new[] { "deepseek-v4-pro", "deepseek-v4-flash" };
            ModelComboBox.SelectedIndex = 0;

            EffortComboBox.ItemsSource = new[] { "high", "max" };
            EffortComboBox.SelectedIndex = 0;

            ThinkingCheckBox.IsChecked = true;

            // 联网搜索: 默认关闭
            WebSearchEngineComboBox.ItemsSource = new[] { "🔍 百度搜索", "🦆 DuckDuckGo" };
            WebSearchEngineComboBox.SelectedIndex = 0; // 默认百度

            _webSearchEngine = "Off";
            UpdateWebSearchToggleAppearance();

            // 注册 WebView2 事件
            ChatWebView.CoreWebView2InitializationCompleted += ChatWebView_CoreWebView2InitializationCompleted;

            // ── 粘贴命令绑定：作为后备路径，支持剪贴板图片直接粘贴为附件 ──
            // 主路径在 PreviewKeyDown 中通过隧道事件拦截 Ctrl+V，确保优先于 TextBox 内部处理。
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
                (s, e) =>
                {
                    Logger.Info("CommandBinding.Paste Executed 触发");
                    ExecutePasteImage(e);
                },
                (s, e) =>
                {
                    bool hasImage = CanPasteImage();
                    e.CanExecute = hasImage;
                    e.Handled = hasImage; // 仅在有图像时拦截，文本粘贴交给 TextBox 默认行为
                    Logger.Info($"CommandBinding.Paste CanExecute: hasImage={hasImage}, CanExecute={e.CanExecute}, Handled={e.Handled}");
                }));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 工具窗口创建完成后调用，传入 Package 引用。
        /// 对标 TerminalWindowTurbo.OnCreate() → StartControl()。
        /// </summary>
        public void StartControl(DeepSeek_v4_for_VisualStudioPackage package)
        {
            _package = package;
            _options = package.Options;

            InitializeApiService();
            InitializeWebSearchService();
            InitializeOcrService();
            _ = ResolveSolutionPathAsync();
            _ = LoadAndShowAsync();

            // ── 后台异步校验 API Key 有效性 ──
            _ = ValidateAllApiKeysAsync();

            // ── 订阅设置变更事件，支持热切换配置 ──
            DeepSeekOptionsPage.SettingsChanged += OnOcrSettingsChanged;
        }

        #endregion

        #region Private Methods - Initialization

        private void InitializeApiService()
        {
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
                return;

            _apiService?.Dispose();
            _apiService = new DeepSeekApiService(_options.ApiKey, _options.SelectedModel);
            _apiService.ConfigureThinking(_options.IsThinkingEnabled, _options.ReasoningEffort);
            Logger.Info("API 服务初始化成功");
        }

        /// <summary>
        /// 初始化联网搜索服务。默认使用百度搜索，若未配置 API Key 则使用 DuckDuckGo。
        /// </summary>
        private void InitializeWebSearchService()
        {
            _webSearchService?.Dispose();
            _webSearchService = new WebSearchService();
            ApplyWebSearchConfig();
            Logger.Info("联网搜索服务初始化成功");
        }

        /// <summary>
        /// 初始化 OCR 服务，从选项页读取用户选择的引擎和模型路径。
        /// 此方法内部捕获所有异常，确保 OCR 初始化失败不会影响聊天核心功能。
        /// </summary>
        private void InitializeOcrService()
        {
            try
            {
                if (_options == null)
                {
                    Logger.Info("[OCR] 跳过初始化: _options 为 null");
                    return;
                }

                Logger.Info($"[OCR] 开始初始化，用户选择引擎: {_options.OcrEngine}");

                // 设置 OCR 引擎类型
                OcrService.CurrentEngine = _options.OcrEngine switch
                {
                    "Tesseract.NET" => OcrEngineType.Tesseract,
                    "PaddleOCR-Sharp" => OcrEngineType.PaddleOCR,
                    _ => OcrEngineType.WindowsBuiltIn,
                };
                Logger.Info($"[OCR] 引擎类型已设置: {OcrService.CurrentEngine}");

                // 设置模型路径（优先使用用户自定义路径）
                if (!string.IsNullOrWhiteSpace(_options.TesseractDataPath))
                {
                    OcrService.TesseractDataPath = _options.TesseractDataPath;
                    Logger.Info($"[OCR] 用户自定义 Tesseract 路径: {_options.TesseractDataPath}");
                }

                if (!string.IsNullOrWhiteSpace(_options.PaddleOcrModelPath))
                {
                    OcrService.PaddleOcrModelPath = _options.PaddleOcrModelPath;
                    Logger.Info($"[OCR] 用户自定义 PaddleOCR 路径: {_options.PaddleOcrModelPath}");
                }

                // 设置插件根目录（用于默认模型路径）
                if (_package != null)
                {
                    try
                    {
                        string? pluginRoot = System.IO.Path.GetDirectoryName(
                            typeof(DeepSeek_v4_for_VisualStudioPackage).Assembly.Location);
                        OcrService.PluginRootPath = pluginRoot;
                        Logger.Info($"[OCR] 插件根目录: {pluginRoot}");

                        // 将插件目录加入 Windows DLL 搜索路径（解决原生 DLL 加载问题）
                        OcrService.EnsureNativeDllSearchPath();
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"[OCR] 无法获取插件根目录: {ex.Message}");
                    }
                }

                // 检查引擎状态（此调用可能触发程序集加载，已做防护）
                string status = OcrService.GetEngineStatus();
                bool ready = OcrService.IsEngineReady();
                Logger.Info($"[OCR] 引擎状态: {status}, 就绪={ready}");

                if (!ready && _options.OcrEngine != "Windows Built-in")
                {
                    Logger.Info($"[OCR] ⚠️ 所选引擎未就绪: {_options.OcrEngine}，将自动回退到 Windows 内置 OCR");
                }
            }
            catch (Exception ex)
            {
                // ⚠️ 关键：OCR 初始化失败绝不能影响聊天核心功能
                Logger.Error($"[OCR] ❌ 初始化失败（已降级，不影响聊天）: {ex.GetType().Name} - {ex.Message}", ex);
                OcrService.CurrentEngine = OcrEngineType.WindowsBuiltIn;
            }
        }

        /// <summary>
        /// 设置变更事件回调（用户点击 Options 对话框的"确定"/"应用"时触发）。
        /// 热重载 OCR 配置，无需重启聊天窗口。
        /// </summary>
        private void OnOcrSettingsChanged()
        {
            Logger.Info("[OCR] 检测到设置变更，热重载 OCR 配置...");
            try
            {
                // 刷新 _options 引用（DialogPage 属性已由 VS 自动更新）
                if (_package != null)
                    _options = _package.Options;

                // 重置所有引擎缓存（下次 OCR 调用时重新检测环境）
                OcrService.ResetAllEngines();

                // 重新应用设置
                InitializeOcrService();

                StatusLabel.Text = $"OCR 引擎已切换至: {_options?.OcrEngine ?? "Windows Built-in"}";
                Logger.Info($"[OCR] 设置热切换完成 → {OcrService.CurrentEngine}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[OCR] 设置热切换失败: {ex.Message}", ex);
            }
        }
        /// 同时遵循用户在 ComboBox 中选择的搜索引擎偏好。
        /// 用于支持用户在 工具→选项 中修改 API Key 后无需重启即可生效。
        /// </summary>
        private void ApplyWebSearchConfig()
        {
            if (_webSearchService == null) return;

            // 根据用户选择的引擎决定使用哪个搜索提供商
            if (_webSearchEngine == "Baidu" && _options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
            {
                _webSearchService.ConfigureBaiduSearch(_options.BaiduApiKey);
                Logger.Info("联网搜索热重载: 百度千帆 (用户选择百度，API Key 已配置)");
            }
            else if (_webSearchEngine == "Baidu")
            {
                // 用户选择百度但未配置 Key，仍然尝试配置（会在搜索时提示用户）
                _webSearchService.ConfigureBaiduSearch(null!);
                Logger.Info("联网搜索热重载: DuckDuckGo (用户选择百度但 API Key 未配置)");
            }
            else
            {
                // DuckDuckGo 或 Off，统一使用 DuckDuckGo
                _webSearchService.ConfigureBaiduSearch(null!);
                Logger.Info($"联网搜索热重载: DuckDuckGo (用户选择 {_webSearchEngine})");
            }
        }

        /// <summary>
        /// 后台异步校验所有已配置的 API Key 是否有效。
        /// 启动时调用，校验结果通过 StatusLabel 提示用户。
        /// </summary>
        private async Task ValidateAllApiKeysAsync()
        {
            // ── 校验 DeepSeek API Key ──
            if (_apiService != null)
            {
                string? deepSeekError = await _apiService.ValidateApiKeyAsync();
                if (deepSeekError != null)
                {
                    Logger.Error($"DeepSeek API Key 校验失败: {deepSeekError}");
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = "⚠️ DeepSeek API Key 无效，请检查配置";
                }
                else
                {
                    Logger.Info("DeepSeek API Key 校验通过");
                }
            }

            // ── 校验百度 API Key ──
            if (_webSearchService != null && _webSearchEngine == "Baidu" &&
                _options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
            {
                string? baiduError = await _webSearchService.ValidateBaiduApiKeyAsync();
                if (baiduError != null)
                {
                    Logger.Error($"百度 API Key 校验失败: {baiduError}");
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = "⚠️ 百度 API Key 无效，请检查配置";
                }
                else
                {
                    Logger.Info("百度 API Key 校验通过");
                }
            }

            // ── 校验 OCR 引擎状态 ──
            if (_options != null && _options.OcrEngine != "Windows Built-in")
            {
                bool ocrReady = OcrService.IsEngineReady();
                string ocrStatus = OcrService.GetEngineStatus();
                Logger.Info($"OCR 引擎状态: {ocrStatus}");

                if (!ocrReady)
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"⚠️ OCR 引擎未就绪: {_options.OcrEngine}。请检查模型文件路径。";
                }
            }
        }

        private async Task ResolveSolutionPathAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                _solutionPath = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(_solutionPath))
                    Logger.Info($"检测到解决方案: {_solutionPath}");
                else
                    Logger.Info("未检测到已打开的解决方案，使用默认存储");
            }
            catch (Exception ex)
            {
                Logger.Error("解析解决方案路径失败", ex);
                _solutionPath = null;
            }
        }

        private async Task LoadAndShowAsync()
        {
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            // 加载所有会话
            _sessionsContainer = ChatPersistenceService.LoadSessions(_solutionPath);

            // 确定活跃会话
            if (!string.IsNullOrEmpty(_sessionsContainer.ActiveSessionId))
            {
                _activeSession = _sessionsContainer.Sessions
                    .FirstOrDefault(s => s.Id == _sessionsContainer.ActiveSessionId);
            }
            _activeSession ??= _sessionsContainer.Sessions.FirstOrDefault();

            // 如果没有会话，创建默认会话
            if (_activeSession == null)
            {
                _activeSession = CreateNewSessionInternal();
                _sessionsContainer.Sessions.Add(_activeSession);
                _sessionsContainer.ActiveSessionId = _activeSession.Id;
            }

            // 加载活跃会话的消息
            _messages.Clear();
            _conversationHistory.Clear();

            if (_activeSession.Messages.Count > 0)
            {
                Logger.Info($"[Render] LoadConversation: 从会话 '{_activeSession.Title}' 加载了 {_activeSession.Messages.Count} 条消息");
                foreach (var msg in _activeSession.Messages)
                {
                    msg.IsStreaming = false;
                    _messages.Add(msg);
                    if (msg.Role is "user" or "assistant")
                    {
                        // 对用户消息，重构完整内容（用户文本 + 文件内容）发送给 AI
                        string apiContent = msg.Content ?? string.Empty;
                        if (msg.Role == "user" && msg.AttachedFiles.Count > 0)
                        {
                            string fileContext = FileParserService.FormatParseResultsForContext(msg.AttachedFiles);
                            if (!string.IsNullOrEmpty(fileContext))
                            {
                                apiContent = fileContext + "\n" + apiContent;
                            }
                        }
                        _conversationHistory.Add(new ChatApiMessage
                        {
                            Role = msg.Role,
                            Content = apiContent,
                        });
                    }
                }
            }

            // 没有消息则显示欢迎语
            if (_messages.Count == 0)
            {
                bool hasApiKey = _options != null && !string.IsNullOrEmpty(_options.ApiKey);
                string welcomeContent = hasApiKey ? WelcomeMessage : ApiKeyMissingMessage;

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = welcomeContent,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(welcomeMsg);
                _activeSession.Messages.Add(welcomeMsg);
                Logger.Info(hasApiKey ? "[Render] 添加欢迎语" : "[Render] 添加欢迎语 + API密钥缺失警告");
            }

            // 填充会话下拉框
            PopulateSessionComboBox();

            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                Logger.Info("[Render] 开始初始化 WebView2 CoreWebView2 环境");
                var env = await CoreWebView2Environment.CreateAsync(
                    null,
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeepSeekVS", "WebView2"));
                await ChatWebView.EnsureCoreWebView2Async(env);
                Logger.Info("[Render] CoreWebView2 环境初始化成功");
            }
            catch (Exception ex)
            {
                Logger.Error("[Render] WebView2 初始化失败", ex);
                StatusLabel.Text = $"WebView2 初始化失败: {ex.Message}";
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源，保存对话。
        /// </summary>
        public void Dispose()
        {
            DeepSeekOptionsPage.SettingsChanged -= OnOcrSettingsChanged;

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _apiService?.Dispose();
            _webSearchService?.Dispose();

            SaveCurrentSession();

            Logger.Info("DeepSeekChatControl 已释放");
        }

        #endregion
    }
}

