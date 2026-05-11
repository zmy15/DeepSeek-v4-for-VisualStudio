using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
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
        private McpManagerService? _mcpManager;
        private SkillService? _skillService;
        private SkillDiscoveryResult? _skillDiscoveryResult;
        private AgentDispatcher? _agentDispatcher;
        private CancellationTokenSource? _currentStreamingCts;
        private string? _solutionPath;

        // ── DTE 事件引用（必须保持存活以防 GC 回收） ──
        private EnvDTE.DTE? _dte;
        private EnvDTE.SolutionEvents? _solutionEvents;
        private bool _solutionEventsWired;

        private readonly List<ChatMessage> _messages = new();
        private readonly ConversationContextManager _contextManager = new();
        private RagService? _ragService;
        private ContextCompressorService? _compressorService;
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
        private bool _webViewInitialized;
        private int _lastRenderedMessagesLength;
        private readonly StringBuilder _messagesHtml = new();

        // ── 线程安全 ──
        private readonly object _lock = new();

        // ── 消息版本管理（重试/编辑功能） ──
        // Key: 用户消息索引，Value: 该用户消息对应的所有助手回复版本列表
        private readonly Dictionary<int, List<ChatMessage>> _assistantVersionHistory = new();
        // Key: 用户消息索引，Value: 当前显示的版本索引（0-based）
        private readonly Dictionary<int, int> _activeVersionIndex = new();

        // ── 文件变更历史追踪（重试/编辑前回退用） ──
        // Key: 用户消息索引，Value: 该轮对话中修改的文件及其原始/新内容
        private readonly Dictionary<int, List<Models.FileChangeSummary>> _fileChangeHistory = new();

        // ── 最近一次 Agent 执行的文件变更（临时存储，RunAgentWorkflowAsync 写入，RecordAgentFileChanges 消费后清空）──
        private List<Models.FileChangeSummary>? _pendingAgentFileChanges;

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
            InitializeMcp(); // MCP 后台初始化，不阻塞 UI
            InitializeSkills(); // Skill 后台发现，不阻塞 UI
            _ = ResolveSolutionPathAsync();
            _ = LoadAndShowAsync();

            // ── 后台异步校验 API Key 有效性 ──
            _ = ValidateAllApiKeysAsync();

            // ── 订阅设置变更事件，支持热切换配置 ──
            DeepSeekOptionsPage.SettingsChanged += OnOcrSettingsChanged;

            // ── 订阅 diff 预览状态事件，刷新全局控制栏 ──
            EditorDiffMarkerService.Instance.PendingDiffCountChanged += RefreshDiffGlobalBar;

            // ── 订阅解决方案事件，切换解决方案时自动重载对话 ──
            _ = WireSolutionEventsAsync();
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

            // ── 初始化/重建 Agent 调度器（ApiService 重建时必须同步重建）──
            if (_agentDispatcher != null)
            {
                _agentDispatcher.PermissionRequested -= OnAgentPermissionRequested;
                _agentDispatcher.Dispose();
            }
            _agentDispatcher = new AgentDispatcher(_apiService);
            _agentDispatcher.PermissionRequested += OnAgentPermissionRequested;
            Logger.Info("Agent 调度器初始化成功（多 Agent 模式：Ask / Plan / Explore / Edit）");

            Logger.Info("API 服务初始化成功");
        }

        /// <summary>
        /// 初始化 RAG 服务和上下文压缩服务。
        /// 在 API 服务就绪后调用。
        /// </summary>
        private void InitializeContextServices()
        {
            if (_options == null) return;

            // ── 初始化上下文压缩服务 ──
            if (_options.EnableAutoCompression)
            {
                var compressionConfig = new CompressionConfig
                {
                    CompressionThreshold = _options.CompressionThreshold / 100.0,
                    PreserveRecentTurns = _options.PreserveRecentTurns,
                    AutoCompressEnabled = _options.EnableAutoCompression,
                };

                // 当 API 服务可用时，使用 LLM 摘要；否则使用本地规则提取
                if (_apiService != null)
                {
                    _compressorService = new ContextCompressorService(
                        async (text, ct) =>
                        {
                            try
                            {
                                var messages = new List<ChatApiMessage>
                                {
                                    new ChatApiMessage { Role = "user", Content = text }
                                };
                                return await _apiService.CompleteAsync(messages, ct);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[ContextCompressor] LLM 摘要失败，回退到本地提取: {ex.Message}");
                                return string.Empty; // 返回空将触发回退
                            }
                        },
                        compressionConfig);
                }
                else
                {
                    _compressorService = new ContextCompressorService(null, compressionConfig);
                }

                _contextManager.SetCompressor(_compressorService);
                _contextManager.TokenBudget = _options.TokenBudget;

                Logger.Info($"[ContextServices] 上下文压缩已启用: " +
                    $"预算={_options.TokenBudget:N0}, 阈值={_options.CompressionThreshold}%, " +
                    $"保留轮次={_options.PreserveRecentTurns}");
            }
            else
            {
                _compressorService = null;
                _contextManager.SetCompressor(null);
                _contextManager.TokenBudget = _options.TokenBudget;
                _contextManager.AutoTrimTurns = _options.PreserveRecentTurns;

                Logger.Info($"[ContextServices] 上下文压缩已禁用，使用旧版截断: " +
                    $"预算={_options.TokenBudget:N0}, 保留轮次={_options.PreserveRecentTurns}");
            }

            // ── 初始化 RAG 服务 ──
            if (_options.EnableRag)
            {
                _ragService = new RagService { IsEnabled = true };
                Logger.Info("[ContextServices] RAG 服务已初始化（等待提供者注册）");
            }
            else
            {
                _ragService?.DeactivateProvider();
                _ragService = null;
            }
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
                    "PaddleOCR-Sharp" => OcrEngineType.PaddleOCR,
                    _ => OcrEngineType.WindowsBuiltIn,
                };
                Logger.Info($"[OCR] 引擎类型已设置: {OcrService.CurrentEngine}");

                // 设置插件根目录（用于 DLL 搜索路径）
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

        /// <summary>
        /// 后台异步初始化 MCP 服务器连接。
        /// 失败不影响核心聊天功能。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于 fire-and-forget 初始化
        private async void InitializeMcp()
        {
            try
            {
                // 从独立的配置文件加载 MCP 服务器配置
                var mcpConfigs = McpConfigStore.Load();
                var enabledConfigs = mcpConfigs.Where(c => c.Enabled).ToList();

                if (enabledConfigs.Count == 0)
                {
                    Logger.Info("[MCP] 没有启用的 MCP 服务器，跳过初始化。点击 🔌 按钮配置。");
                    return;
                }

                // 清理旧的 MCP 管理器
                _mcpManager?.Dispose();
                _mcpManager = new McpManagerService();
                OcrService.McpManager = _mcpManager; // 注入 OCR 服务

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _mcpManager.InitializeAsync(enabledConfigs, cts.Token);

                var toolCount = _mcpManager.AllTools.Count;
                if (toolCount > 0)
                {
                    Logger.Info($"[MCP] MCP 初始化完成，共 {toolCount} 个工具可用");
                    StatusLabel.Text = $"MCP 已连接: {toolCount} 个工具可用";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MCP] MCP 初始化失败: {ex.Message}", ex);
            }
            finally
            {
                UpdateMcpButtonAppearance();
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 后台发现并加载 Skill。
        /// 扫描项目目录和用户目录下的 SKILL.md 文件。
        /// 失败不影响核心聊天功能。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于 fire-and-forget 初始化
        private async void InitializeSkills()
        {
            try
            {
                _skillService = SkillService.Instance;
                _skillDiscoveryResult = await _skillService.DiscoverSkillsAsync(_solutionPath);

                if (_skillDiscoveryResult.TotalCount > 0)
                {
                    Logger.Info($"[Skill] Skill 发现完成: 共 {_skillDiscoveryResult.TotalCount} 个技能 " +
                        $"(项目: {_skillDiscoveryResult.ProjectSkillCount}, " +
                        $"用户: {_skillDiscoveryResult.UserSkillCount})");

                    var invocableCount = _skillDiscoveryResult.UserInvocableSkills.Count;
                    if (invocableCount > 0)
                    {
                        var names = string.Join(", ", _skillDiscoveryResult.UserInvocableSkills.ConvertAll(s => s.Name));
                        Logger.Info($"[Skill] 可调用技能: {names}");
                    }
                    // 内置示例技能已随扩展发布，在 BuiltInSkills 目录下
                }
                else
                {
                    Logger.Info("[Skill] 未发现任何 Skill 定义。可在 .github/skills/<name>/SKILL.md 或 ~/.copilot/skills/<name>/SKILL.md 中创建。");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skill] Skill 初始化失败: {ex.Message}", ex);
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
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

        /// <summary>
        /// 订阅 DTE 解决方案事件，当用户打开/关闭解决方案时自动重载对话。
        /// </summary>
        private async System.Threading.Tasks.Task WireSolutionEventsAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (_dte == null)
                {
                    Logger.Warn("[会话] 无法获取 DTE，解决方案事件监听跳过");
                    return;
                }

                _solutionEvents = _dte.Events.SolutionEvents;
                _solutionEvents.Opened += OnSolutionOpened;
                _solutionEvents.AfterClosing += OnSolutionClosed;
                _solutionEventsWired = true;

                Logger.Info("[会话] 解决方案事件监听已注册");
            }
            catch (Exception ex)
            {
                Logger.Error("[会话] 注册解决方案事件失败", ex);
            }
        }

        /// <summary>
        /// 用户打开解决方案时：保存当前对话，加载新解决方案的对话记录。
        /// </summary>
        private void OnSolutionOpened()
        {
            Logger.Info("[会话] 检测到解决方案已打开，准备切换对话存储");

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // 先保存当前对话
                    SaveCurrentSession();

                    // 解析新路径并重载
                    await ResolveSolutionPathAsync();

                    // ── 重置浏览器状态，切换解决方案时强制全量刷新 ──
                    _browserInitialized = false;

                    await LoadAndShowAsync();

                    Logger.Info($"[会话] 对话已切换到新解决方案: {_solutionPath ?? "(无)"}");
                }
                catch (Exception ex)
                {
                    Logger.Error("[会话] 切换解决方案时出错", ex);
                }
            });
        }

        /// <summary>
        /// 用户关闭解决方案时：保存当前对话并清空。
        /// </summary>
        private void OnSolutionClosed()
        {
            Logger.Info("[会话] 检测到解决方案已关闭，保存并清空对话");

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // 保存当前对话
                    SaveCurrentSession();

                    // 清空
                    _solutionPath = null;
                    _sessionsContainer = ChatPersistenceService.LoadSessions(null);
                    _activeSession = CreateNewSessionInternal();
                    _sessionsContainer.Sessions.Add(_activeSession);
                    _sessionsContainer.ActiveSessionId = _activeSession.Id;

                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;

                    Logger.Info("[会话] 对话已清空（解决方案已关闭）");
                }
                catch (Exception ex)
                {
                    Logger.Error("[会话] 关闭解决方案时出错", ex);
                }
            });
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
            _contextManager.Clear();

            if (_activeSession.Messages.Count > 0)
            {
                Logger.Info($"[Render] LoadConversation: 从会话 '{_activeSession.Title}' 加载了 {_activeSession.Messages.Count} 条消息");

                // ── 优先使用 ApiHistory 恢复完整上下文（含 tool 消息） ──
                if (_activeSession.ApiHistory.Count > 0)
                {
                    _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                }
                else
                {
                    // 回退：从 UI 消息列表重建（旧版会话兼容）
                    foreach (var msg in _activeSession.Messages)
                    {
                        msg.IsStreaming = false;
                        if (msg.Role is "user" or "assistant")
                        {
                            string apiContent = msg.Content ?? string.Empty;
                            if (msg.Role == "user" && msg.AttachedFiles.Count > 0)
                            {
                                string fileContext = FileParserService.FormatParseResultsForContext(msg.AttachedFiles);
                                if (!string.IsNullOrEmpty(fileContext))
                                    apiContent = fileContext + "\n" + apiContent;
                            }
                            if (msg.Role == "user")
                                _contextManager.AddUserMessage(apiContent);
                            else
                                _contextManager.AddAssistantMessage(apiContent, msg.ReasoningContent);
                        }
                    }
                }

                foreach (var msg in _activeSession.Messages)
                {
                    msg.IsStreaming = false;
                    _messages.Add(msg);
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

            // ── WebView2 只能初始化一次，切换解决方案时只刷新页面内容 ──
            if (_webViewInitialized)
            {
                Logger.Info("[Render] WebView2 已初始化，直接刷新页面内容");
                RebuildMessagesHtml();
                UpdateBrowser();
            }
            else
            {
                await InitializeWebViewAsync();
                _webViewInitialized = true;
            }
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
        /// 释放资源，取消事件订阅，保存对话。
        /// </summary>
        public void Dispose()
        {
            DeepSeekOptionsPage.SettingsChanged -= OnOcrSettingsChanged;

            // ── 取消解决方案事件订阅 ──
            if (_solutionEventsWired && _solutionEvents != null)
            {
                _solutionEvents.Opened -= OnSolutionOpened;
                _solutionEvents.AfterClosing -= OnSolutionClosed;
                _solutionEventsWired = false;
                Logger.Info("[会话] 解决方案事件监听已取消");
            }

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _apiService?.Dispose();
            _webSearchService?.Dispose();
            _mcpManager?.Dispose();

            if (_agentDispatcher != null)
            {
                _agentDispatcher.PermissionRequested -= OnAgentPermissionRequested;
                _agentDispatcher.Dispose();
                _agentDispatcher = null;
            }

            SaveCurrentSession();

            // ── 清理临时上下文文件 ──
            CleanupTempContextFiles();

            Logger.Info("DeepSeekChatControl 已释放");
        }

        /// <summary>
        /// 清理 %LocalAppData%\DeepSeekVS\temp\context\ 目录中的临时文件。
        /// </summary>
        private static void CleanupTempContextFiles()
        {
            try
            {
                string tempContextDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "context");

                if (System.IO.Directory.Exists(tempContextDir))
                {
                    System.IO.Directory.Delete(tempContextDir, recursive: true);
                    Logger.Info($"[Cleanup] 已清理临时上下文目录: {tempContextDir}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cleanup] 清理临时上下文目录失败: {ex.Message}");
            }
        }

        #endregion
    }
}

