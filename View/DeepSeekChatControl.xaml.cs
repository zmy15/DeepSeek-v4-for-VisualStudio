using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Events;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        private static string WelcomeMessage => AiPrompts.WelcomeMessage;

        private static string ApiKeyMissingMessage => AiPrompts.ApiKeyMissingMessage;

        /// <summary>
        /// 流式更新间隔（字符数），配合时间节流实现双重控制，减少 JS 跨进程调用。
        /// 使用 PostWebMessageAsString 非阻塞通道后，可适当增大间隔以减少消息频率。
        /// 2026-05-21 调优：字符阈值 200→100，时间阈值 120→80ms，提升流式输出响应速度。
        /// </summary>
        private const int StreamRenderInterval = 100;
        private const int StreamRenderMinIntervalMs = 80;
        private const int StatusUpdateMinIntervalMs = 200; // 状态栏最小刷新间隔
        private System.Diagnostics.Stopwatch? _streamRenderStopwatch;
        private System.Diagnostics.Stopwatch? _statusUpdateStopwatch;
        private string _lastToolCallNames = ""; // 工具调用名称缓存，避免重复更新

        #endregion

        #region Properties

        private DeepSeek_v4_for_VisualStudioPackage? _package;
        private DeepSeekOptionsPage? _options;
        private DeepSeekApiService? _apiService;
        private WebSearchService? _webSearchService;
        private McpManagerService? _mcpManager;
        private BuiltInToolService? _builtInToolService;
        private SkillService? _skillService;
        private SkillDiscoveryResult? _skillDiscoveryResult;
        private AgentDispatcher? _agentDispatcher;
        private CancellationTokenSource? _currentStreamingCts;

        /// <summary>
        /// 线程安全地创建新的流式 CTS（先取消并释放旧的）。
        /// 返回新 CTS 的快照引用，调用方应使用返回值而非直接访问 _currentStreamingCts。
        /// </summary>
        private CancellationTokenSource CreateNewStreamingCts()
        {
            lock (_lock)
            {
                _currentStreamingCts?.Cancel();
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = new CancellationTokenSource();
                return _currentStreamingCts;
            }
        }

        /// <summary>
        /// 线程安全地获取当前流式 CTS 的 Token。
        /// 如果 CTS 不存在则返回 CancellationToken.None。
        /// </summary>
        private CancellationToken GetStreamingToken()
        {
            lock (_lock)
            {
                return _currentStreamingCts?.Token ?? CancellationToken.None;
            }
        }

        /// <summary>
        /// 线程安全地取消当前流式操作。
        /// </summary>
        private void CancelStreaming()
        {
            lock (_lock)
            {
                _currentStreamingCts?.Cancel();
            }
        }

        /// <summary>
        /// 线程安全地释放当前流式 CTS。
        /// </summary>
        private void DisposeStreamingCts()
        {
            lock (_lock)
            {
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
            }
        }
        private string? _solutionPath;

        // ── 解决方案事件已订阅标记（通过 Microsoft.VisualStudio.Shell.Events.SolutionEvents）──

        private readonly List<ChatMessage> _messages = new();
        private readonly ConversationContextManager _contextManager = new();

        // ── 树状对话结构 ──
        private ConversationTree? _tree;

        private RagService? _ragService;
        private ContextCompressorService? _compressorService;
        private MemoryService? _memoryService;
        private bool _isGenerating;
        private string _webSearchEngine = "Off"; // "Off" | "Baidu" | "DuckDuckGo"
        private readonly List<string> _pendingWarnings = new(); // 待注入的警告消息

        // ── 文件上传 ──
        private readonly List<string> _attachedFilePaths = new(); // 已选文件路径列表

        // ── 多会话支持 ──
        private SessionsContainer? _sessionsContainer;
        private ChatSession? _activeSession;

        // ── 防止重复释放 ──
        private bool _disposed;

        // ── 余额查询定时器 ──
        private System.Windows.Threading.DispatcherTimer? _balanceTimer;
        private BalanceResponse? _lastBalance;

        // ── 增量渲染状态（对标 Turbo ucChat） ──
        private bool _browserInitialized;
        /// <summary>页面 DOM + JS 完全就绪后才为 true</summary>
        private bool _pageReady;
        private bool _webViewInitialized;
        /// <summary>抑制 CoreWebView2InitializationCompleted 中的 UpdateBrowser（由 LoadAndShowAsync 显式接管）</summary>
        private bool _suppressWebViewUpdate;
        private int _lastRenderedMessagesLength;
        private readonly StringBuilder _messagesHtml = new();

        /// <summary>当前正在流式输出的消息索引（-1 表示无）。用于停止时发送 streamEnd 以注入重试按钮。</summary>
        private int _currentStreamingMsgIndex = -1;

        // ── 线程安全 ──
        private readonly object _lock = new();

        // ── AI 自动标题生成 ──
        /// <summary>是否等待 AI 生成会话标题（首轮对话完成后触发）</summary>
        private bool _pendingAiTitle;
        /// <summary>第一条用户消息内容（用于 AI 摘要的输入）</summary>
        private string? _firstUserMessageForTitle;

        // ── 树状结构辅助 ──

        /// <summary>
        /// 确保 _tree 已初始化（懒初始化）。
        /// </summary>
        private ConversationTree EnsureTree()
        {
            if (_tree == null)
            {
                _tree = new ConversationTree();
                Logger.Info("[Tree] ConversationTree 已初始化");
            }
            return _tree;
        }

        /// <summary>
        /// 从活跃路径同步 _messages 列表（不改变渲染状态）。
        /// 用于普通消息追加后保持 _messages 与树同步。
        /// </summary>
        private void SyncMessagesFromTree()
        {
            if (_tree == null) return;
            lock (_lock)
            {
                _messages.Clear();
                var msgs = _tree.GetActiveMessages();
                _messages.AddRange(msgs);
            }
        }

        /// <summary>
        /// 从树重建消息列表并强制下次全量刷新浏览器。
        /// 用于分支切换或编辑/重试分叉（后续消息全部改变）。
        /// </summary>
        private void RebuildFromTree()
        {
            SyncMessagesFromTree();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;
            _browserInitialized = false;
        }

        /// <summary>
        /// 通过活跃路径中的消息索引查找对应的 ConvNode。
        /// msgIndex 是当前 _messages 列表中的索引。
        /// </summary>
        private ConvNode? GetConvNodeByMessageIndex(int msgIndex)
        {
            if (_tree == null) return null;
            lock (_lock)
            {
                if (msgIndex < 0 || msgIndex >= _messages.Count) return null;
                var msg = _messages[msgIndex];
                if (string.IsNullOrEmpty(msg.NodeId)) return null;
                return _tree.FindNode(msg.NodeId);
            }
        }

        // ── 文件变更历史追踪（重试/编辑前回退用） ──
        // Key: 用户消息索引，Value: 该轮对话中修改的文件及其原始/新内容
        private readonly Dictionary<int, List<Models.FileChangeSummary>> _fileChangeHistory = new();

        // ── 最近一次 Agent 执行的文件变更（临时存储，RunAgentWorkflowAsync 写入，RecordAgentFileChanges 消费后清空）──
        private List<Models.FileChangeSummary>? _pendingAgentFileChanges;

        /// <summary>最近一次 Agent 执行的 Handoff（用于 UI 渲染"开始实现"按钮）</summary>
        private Models.AgentHandoff? _pendingHandoff;

        // ── Agent 流式日志面板 ID（会话级，不依赖 PlanId）──
        private string _agentLogPanelId = "session";

        // ── 已创建的计划 ID 集合（防止重复创建计划消息）──
        private readonly HashSet<string> _createdPlanIds = new();

        // ── 待回放的 Agent 日志条目（面板因全量刷新被销毁时用于恢复）──
        private readonly List<AgentLogEntry> _pendingLogEntries = new();

        // ── Agent 实时思考气泡 ──
        private int _agentStreamingMsgIndex = -1;
        private readonly StringBuilder _agentThinkingContent = new();
        private int _lastReportedStepIndex;
        private string _lastReportedStepStatus = string.Empty;

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化控件。
        /// </summary>
        public DeepSeekChatControl()
        {
            InitializeComponent();

            // ── i18n：输入框占位文字跟随语言 ──
            UpdateInputPlaceholder();
            UpdateAllTooltips();
            UpdateUiLabels();
            LocalizationService.Instance.LanguageChanged += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateInputPlaceholder();
                    UpdateAllTooltips();
                    UpdateUiLabels();
                });
            };

            // 初始化模型和推理强度下拉框
            ModelComboBox.ItemsSource = new[] { "deepseek-v4-pro", "deepseek-v4-flash" };
            ModelComboBox.SelectedIndex = 0;

            EffortComboBox.ItemsSource = new[] { "high", "max" };
            EffortComboBox.SelectedIndex = 0;

            ThinkingCheckBox.IsChecked = true;

            // 联网搜索: 默认关闭
            var L = LocalizationService.Instance;
            WebSearchEngineComboBox.ItemsSource = new[] {
                "🔍 " + L["websearch.searchEngine.baidu"],
                "🦆 " + L["websearch.searchEngine.duckduckgo"]
            };
            WebSearchEngineComboBox.SelectedIndex = 0; // 默认百度

            _webSearchEngine = "Off";
            UpdateWebSearchToggleAppearance();

            // ── 状态信息直接显示在输入区顶部状态行 ──
            StatusLabel.Text = "正在初始化…";

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

            InitializeWebSearchService();
            InitializeApiService();
            InitializeOcrService();
            InitializeMcp(); // MCP 后台初始化，不阻塞 UI
            InitializeSkills(); // Skill 后台发现，不阻塞 UI

            // ── 链式初始化：先解析项目路径 → 再加载会话 ──
            // 之前两者各自 fire-and-forget，导致首条消息发送时 _solutionPath 可能仍为 null。
            // 现在确保 ResolveSolutionPathAsync 完成后再执行 LoadAndShowAsync，
            // 同时 BuildRequestMessagesAsync 内有惰性兜底以防万一。
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ResolveSolutionPathAsync();
                await LoadAndShowAsync();
            });

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
                _agentDispatcher.QuestionsRequested -= OnAgentQuestionsRequested;
                _agentDispatcher.Dispose();
            }

            // ── 创建内置工具服务 ──
            var buildService = new BuildService();
            _memoryService = new MemoryService();
            _builtInToolService = new BuiltInToolService(_mcpManager, _webSearchService, buildService, _memoryService);

            _agentDispatcher = new AgentDispatcher(_apiService, _builtInToolService, _mcpManager);
            _agentDispatcher.ContextManager = _contextManager;
            _agentDispatcher.PermissionRequested += OnAgentPermissionRequested;
            _agentDispatcher.QuestionsRequested += OnAgentQuestionsRequested;
            Logger.Info("Agent 调度器初始化成功（多 Agent 模式：Ask / Plan / Explore / Edit）");

            // 初始化 Agent 模式徽章（默认隐藏 Ask 模式）
            UpdateAgentModeBadge();

            // ── 初始化余额查询定时器（每分钟刷新一次）──
            StartBalanceTimer();

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
        /// 初始化联网搜索服务。从选项页读取默认搜索引擎。
        /// </summary>
        private void InitializeWebSearchService()
        {
            _webSearchService?.Dispose();
            _webSearchService = new WebSearchService();

            // 从选项页读取默认搜索引擎，同步到 ComboBox
            string optionsProvider = _options?.SearchProvider ?? "DuckDuckGo";
            string resolvedEngine = optionsProvider switch
            {
                "Baidu" => "Baidu",
                _ => "DuckDuckGo"
            };

            // 同步 ComboBox 选中项
            int idx = resolvedEngine switch
            {
                "Baidu" => 0,
                "DuckDuckGo" => 1,
                _ => 1
            };
            WebSearchEngineComboBox.SelectedIndex = idx;

            // 注意：_webSearchEngine 仍为 "Off"，用户需要点击 🌐 按钮开启
            // 但搜索引擎已预选为选项页中配置的值

            ApplyWebSearchConfig();
            Logger.Info($"联网搜索服务初始化成功 (默认引擎: {resolvedEngine})");
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

                // 设置 OCR 引擎类型（PaddleOCR-Sharp 已移除）
                OcrService.CurrentEngine = OcrEngineType.WindowsBuiltIn;
                Logger.Info($"[OCR] 引擎类型已设置: {OcrService.CurrentEngine}");

                // 检查引擎状态
                string status = OcrService.GetEngineStatus();
                bool ready = OcrService.IsEngineReady();
                Logger.Info($"[OCR] 引擎状态: {status}, 就绪={ready}");
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
        /// 热重载 OCR、Web 搜索、模型等配置，无需重启聊天窗口。
        /// </summary>
        private void OnOcrSettingsChanged()
        {
            Logger.Info("[Settings] 检测到设置变更，热重载配置...");
            try
            {
                // 刷新 _options 引用（DialogPage 属性已由 VS 自动更新）
                if (_package != null)
                    _options = _package.Options;

                // ── OCR 热重载 ──
                OcrService.ResetAllEngines();
                InitializeOcrService();
                Logger.Info($"[Settings] OCR 热切换完成 → {OcrService.CurrentEngine}");

                // ── Web 搜索热重载 ──
                string optionsProvider = _options?.SearchProvider ?? "DuckDuckGo";
                string resolvedEngine = optionsProvider switch
                {
                    "Baidu" => "Baidu",
                    _ => "DuckDuckGo"
                };

                int idx = resolvedEngine switch
                {
                    "Baidu" => 0,
                    "DuckDuckGo" => 1,
                    _ => 1
                };
                WebSearchEngineComboBox.SelectedIndex = idx;

                // 如果联网搜索当前是开启状态，同步引擎并应用配置
                if (_webSearchEngine != "Off")
                {
                    if (_webSearchEngine != resolvedEngine)
                    {
                        _webSearchEngine = resolvedEngine;
                        Logger.Info($"[Settings] 搜索引擎热切换为: {_webSearchEngine}");
                    }

                    ApplyWebSearchConfig();
                    UpdateWebSearchToggleAppearance();

                    if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                    {
                        StatusLabel.Text = LocalizationService.Instance["status.search.baiduKeyRequired"];
                    }
                    else
                    {
                        StatusLabel.Text = $"设置已更新 (搜索引擎: {_webSearchEngine})";
                    }
                }
                else
                {
                    StatusLabel.Text = $"设置已更新 (默认引擎: {resolvedEngine})";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Settings] 设置热切换失败: {ex.Message}", ex);
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

                // ── 将 MCP 管理器注入到 Agent 调度器 ──
                _agentDispatcher?.UpdateMcpManager(_mcpManager);

                var toolCount = _mcpManager.AllTools.Count;
                if (toolCount > 0)
                {
                    Logger.Info($"[MCP] MCP 初始化完成，共 {toolCount} 个工具可用");
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.mcpConnected"], toolCount);
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

            switch (_webSearchEngine)
            {
                case "Baidu":
                    if (_options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
                    {
                        _webSearchService.ConfigureBaiduSearch(_options.BaiduApiKey);
                        Logger.Info("联网搜索热重载: 百度千帆 (API Key 已配置)");
                    }
                    else
                    {
                        _webSearchService.ConfigureBaiduSearch(null!);
                        Logger.Info("联网搜索热重载: DuckDuckGo (百度 API Key 未配置)");
                    }
                    break;

                default:
                    _webSearchService.ConfigureBaiduSearch(null!);
                    Logger.Info($"联网搜索热重载: DuckDuckGo (用户选择 {_webSearchEngine})");
                    break;
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
                    StatusLabel.Text = LocalizationService.Instance["status.apiKeyInvalid"];
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
                    StatusLabel.Text = LocalizationService.Instance["status.baiduApiKeyInvalid"];
                }
                else
                {
                    Logger.Info("百度 API Key 校验通过");
                }
            }

            // ── 校验 OCR 引擎状态（PaddleOCR 已移除，仅检查 Windows 内置 OCR）──
            {
                bool ocrReady = OcrService.IsEngineReady();
                string ocrStatus = OcrService.GetEngineStatus();
                Logger.Info($"OCR 引擎状态: {ocrStatus}");

                if (!ocrReady)
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = LocalizationService.Instance["status.ocrUnavailable"];
                }
            }
        }

        /// <summary>
        /// 解析当前工作区路径。支持以下场景：
        /// 1. 传统 .sln 解决方案 → 使用 .sln 文件路径作为标识
        /// 2. 文件夹项目（CMake / Open Folder）→ 使用工作区根目录路径作为标识
        /// 3. 未打开任何项目 → null，使用 _unsaved.json 兜底存储
        /// 
        /// 解析顺序：
        ///   a) IVsSolution.GetSolutionInfo —— 适用于 .sln 项目，返回 .sln 文件路径
        ///   b) IVsWorkspaceService          —— 适用于所有 Open Folder 项目，返回工作区根目录
        ///   c) DTE                          —— 终极回退，兼容极少数边界情况
        /// </summary>
        private async Task ResolveSolutionPathAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // 第一步：.sln 项目 (仅返回 .sln 文件路径，文件夹项目此处返回 null)
                _solutionPath = GetSolutionPathFromIVsSolution();

                // 第二步：终极 DTE 回退（GetSolutionPathFromIVsSolution 已同时覆盖 .sln 和 Open Folder）
                if (string.IsNullOrEmpty(_solutionPath))
                {
                    var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                    _solutionPath = GetSolutionPathFromDTE(dte);
                }

                if (!string.IsNullOrEmpty(_solutionPath))
                    Logger.Info($"检测到项目路径: {_solutionPath}");
                else
                    Logger.Info("未检测到已打开的项目，使用默认存储 (_unsaved.json)");
            }
            catch (Exception ex)
            {
                Logger.Error("解析项目路径失败", ex);
                _solutionPath = null;
            }
        }

        /// <summary>
        /// 通过 IVsSolution.GetSolutionInfo 获取项目路径（首选方案）。
        /// 
        /// 这是 VS SDK 中获取解决方案/工作区路径的官方接口：
        ///   - GetSolutionInfo(out dir, out file, out opts)
        ///   - 对 .sln 项目，file 为非空 .sln 路径
        ///   - 对文件夹项目 (Open Folder/CMake)，file 为空，dir 为工作区根目录
        /// 
        /// 参考: https://learn.microsoft.com/zh-cn/dotnet/api/microsoft.visualstudio.shell.interop.ivssolution.getsolutioninfo
        /// </summary>
        /// <summary>
        /// 通过 IVsSolution.GetSolutionInfo 获取项目路径。
        /// - .sln 项目：返回 .sln 文件路径
        /// - Open Folder 项目（CMake 等无 .sln）：返回工作区根目录（solutionDir）
        /// 在 VS 2019+ 中，GetSolutionInfo 对 Open Folder 会在 solutionDir 中返回工作区根目录。
        /// </summary>
        private static string? GetSolutionPathFromIVsSolution()
        {
            try
            {
                var vsSolution = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                if (vsSolution == null)
                    return null;

                int hr = vsSolution.GetSolutionInfo(out string solutionDir, out string solutionFile, out string _);
                Logger.Info($"[Workspace] IVsSolution.GetSolutionInfo → HR=0x{hr:X8}, dir=[{solutionDir ?? "(null)"}], file=[{solutionFile ?? "(null)"}]");

                if (hr != VSConstants.S_OK)
                {
                    Logger.Warn($"[Workspace] IVsSolution.GetSolutionInfo 返回非 S_OK: 0x{hr:X8}");
                    return null;
                }

                // .sln 项目优先返回 .sln 文件路径
                if (!string.IsNullOrWhiteSpace(solutionFile))
                {
                    Logger.Info($"[Workspace] ✅ IVsSolution → .sln 项目: {solutionFile}");
                    return solutionFile;
                }

                // Open Folder 项目：solutionFile 为空，回退到 solutionDir
                if (!string.IsNullOrWhiteSpace(solutionDir))
                {
                    string dir = solutionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    Logger.Info($"[Workspace] ✅ IVsSolution → Open Folder 项目: {dir}");
                    return dir;
                }

                Logger.Info("[Workspace] IVsSolution 未发现项目路径");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Workspace] IVsSolution.GetSolutionInfo 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 通过 DTE 接口获取项目路径（回退方案，当 IVsSolution 不可用时）。
        /// 顺序：Solution.FullName → Solution.Properties("Path") → 首个项目父目录
        /// </summary>
        private static string? GetSolutionPathFromDTE(EnvDTE.DTE? dte)
        {
            if (dte?.Solution == null || !dte.Solution.IsOpen)
                return null;

            var solution = dte.Solution;

            // ── A) Solution.FullName（.sln 文件路径）──
            try
            {
                string? fullName = solution.FullName;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    Logger.Info($"[Workspace] DTE Solution.FullName: {fullName}");
                    return fullName;
                }
            }
            catch { }

            // ── B) Solution.Properties("Path") ──
            try
            {
                var pathProp = solution.Properties?.Item("Path");
                if (pathProp?.Value is string folderPath && !string.IsNullOrWhiteSpace(folderPath))
                {
                    Logger.Info($"[Workspace] DTE Solution.Properties(\"Path\"): {folderPath}");
                    return folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch { }

            // ── C) 首个项目的父目录 ──
            try
            {
                var projects = solution.Projects;
                if (projects != null && projects.Count > 0)
                {
                    foreach (EnvDTE.Project project in projects)
                    {
                        try
                        {
                            string? fullName = project?.FullName;
                            if (!string.IsNullOrWhiteSpace(fullName))
                            {
                                string? dir = Path.GetDirectoryName(fullName);
                                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                                {
                                    Logger.Info($"[Workspace] DTE Project.FullName 推断: {dir}");
                                    return dir;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 订阅 Microsoft.VisualStudio.Shell.Events.SolutionEvents（推荐 VSSDK 方式）。
        /// OnAfterOpenSolution / OnAfterCloseSolution 用于 .sln 项目；
        /// OnAfterOpenFolder / OnAfterCloseFolder 用于 Open Folder / CMake 项目。
        /// 这与 DTE SolutionEvents 不同——后者仅在 .sln 打开时触发。
        /// </summary>
        private async Task WireSolutionEventsAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // .sln 项目
                SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
                SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution;

                // Open Folder / CMake 项目
                SolutionEvents.OnAfterOpenFolder += OnAfterOpenFolder;
                SolutionEvents.OnAfterCloseFolder += OnAfterCloseFolder;

                Logger.Info("[会话] SolutionEvents 监听已注册（覆盖 .sln 和 Open Folder / CMake）");
            }
            catch (Exception ex)
            {
                Logger.Error("[会话] 注册解决方案事件失败", ex);
            }
        }

        // ── .sln 事件处理 ──
        private void OnAfterOpenSolution(object sender, OpenSolutionEventArgs e) => OnSolutionOpened();
        private void OnAfterCloseSolution(object sender, EventArgs e) => OnSolutionClosed();

        // ── Open Folder / CMake 事件处理 ──
        private void OnAfterOpenFolder(object sender, FolderEventArgs e)
        {
            Logger.Info($"[会话] OnAfterOpenFolder: {e.FolderPath}");
            OnSolutionOpened();
        }
        private void OnAfterCloseFolder(object sender, FolderEventArgs e)
        {
            Logger.Info($"[会话] OnAfterCloseFolder: {e.FolderPath}");
            OnSolutionClosed();
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
        /// 用户关闭解决方案时：保存当前对话，切换到无解决方案状态。
        /// 复用 LoadAndShowAsync 以避免每次关闭都创建新的空会话。
        /// </summary>
        private void OnSolutionClosed()
        {
            Logger.Info("[会话] 检测到解决方案已关闭，保存并清空对话");

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // 保存当前解决方案的对话
                    SaveCurrentSession();

                    // 切换到无解决方案状态
                    _solutionPath = null;

                    // ── 重置浏览器状态，切换时强制全量刷新 ──
                    _browserInitialized = false;

                    await LoadAndShowAsync();

                    Logger.Info("[会话] 对话已清空（解决方案已关闭）");
                }
                catch (Exception ex)
                {
                    Logger.Error("[会话] 关闭解决方案时出错", ex);
                }
            });
        }

        /// <summary>
        /// 触发代码索引：在后台线程执行，不阻塞 UI。
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

            bool hasData = _activeSession.ApiHistory.Count > 0
                        || !string.IsNullOrWhiteSpace(_activeSession.TreeDataJson);

            if (hasData)
            {
                Logger.Info($"[Render] LoadConversation: 从会话 '{_activeSession.Title}' 加载数据 "
                    + $"(apiHistory={_activeSession.ApiHistory.Count}, "
                    + $"hasTree={!string.IsNullOrWhiteSpace(_activeSession.TreeDataJson)})");

                // ── 从 TreeData 恢复树状结构（UI 展示用）──
                if (!string.IsNullOrWhiteSpace(_activeSession.TreeDataJson))
                {
                    try
                    {
                        var treeData = System.Text.Json.JsonSerializer.Deserialize<TreePersistenceData>(
                            _activeSession.TreeDataJson,
                            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                        if (treeData != null)
                        {
                            _tree = ConversationTree.Deserialize(treeData);
                            SyncMessagesFromTree();
                            Logger.Info($"[Tree] LoadConversation 从 TreeData 恢复 (节点数: {treeData.Nodes.Count})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Tree] LoadConversation TreeData 反序列化失败: {ex.Message}");
                    }
                }

                // ── 从 ApiHistory 恢复完整上下文（权威数据源，含 tool_calls/reasoning/system）──
                if (_activeSession.ApiHistory.Count > 0)
                {
                    try
                    {
                        _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                        Logger.Info($"[Context] 从 ApiHistory 恢复上下文成功 ({_activeSession.ApiHistory.Count} 条消息, "
                            + $"turnCount={_contextManager.TurnCount}, estimatedTokens={_contextManager.EstimatedTokens})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Context] 从 ApiHistory 恢复上下文失败，回退到树重建: {ex.Message}", ex);
                        // 回退：从树节点重建上下文（不含 tool_calls，但 user/assistant 可用）
                        RebuildContextFromTree();
                        StatusLabel.Text = LocalizationService.Instance["status.contextRestoreFailed"];
                    }
                }
                else
                {
                    // ApiHistory 为空时，从树节点重建上下文（简易对话无 tool_calls 场景）
                    RebuildContextFromTree();
                    Logger.Info("[Context] ApiHistory 为空，从树节点重建上下文");
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
                Logger.Info(hasApiKey ? "[Render] 添加欢迎语" : "[Render] 添加欢迎语 + API密钥缺失警告");
            }

            // 填充会话下拉框
            PopulateSessionComboBox();

            // ── 从会话恢复累计 Cache 统计 ──
            RestoreCacheStatsFromSession();

            // ── WebView2 只能初始化一次，切换解决方案时只刷新页面内容 ──
            if (_webViewInitialized)
            {
                Logger.Info("[Render] WebView2 已初始化，直接刷新页面内容");
                RebuildMessagesHtml();
                UpdateBrowser();
                // ── 重建持久化的任务面板 ──
                _ = RebuildPanelsWhenPageReadyAsync();
            }
            else
            {
                // ── 抑制 CoreWebView2InitializationCompleted 中的 UpdateBrowser ──
                // 事件在 EnsureCoreWebView2Async 期间同步触发，但其 RunAsync 延迟执行
                // 会覆盖此处的显式 UpdateBrowser，导致面板丢失。
                _suppressWebViewUpdate = true;
                bool initSuccess = await InitializeWebViewAsync();
                _webViewInitialized = initSuccess;

                if (initSuccess)
                {
                    // ── 显式构建 HTML 并全量加载到 WebView ──
                    // 使用 session 数据重建消息 HTML，重置初始化标志强制 NavigateToString
                    // 全量替换路径（而非增量追加），避免与事件处理器中的空白页面重复。
                    RebuildMessagesHtml();
                    _browserInitialized = false;
                    UpdateBrowser();
                    _suppressWebViewUpdate = false;
                    // ── 重建持久化的任务面板 ──
                    _ = RebuildPanelsWhenPageReadyAsync();
                }
            }
        }

        /// <summary>
        /// 初始化 WebView2 环境。返回 true 表示初始化成功。
        /// </summary>
        private async Task<bool> InitializeWebViewAsync()
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
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("[Render] WebView2 初始化失败", ex);
                StatusLabel.Text = $"WebView2 初始化失败: {ex.Message}";
                return false;
            }
        }

        #endregion

        #region Balance Query

        /// <summary>
        /// 启动余额查询定时器，每 60 秒自动刷新一次。
        /// </summary>
        private void StartBalanceTimer()
        {
            // 停止并释放旧定时器
            StopBalanceTimer();

            _balanceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _balanceTimer.Tick += async (s, e) => await RefreshBalanceAsync();
            _balanceTimer.Start();

            // 立即查询一次
            _ = RefreshBalanceAsync();
        }

        /// <summary>
        /// 停止余额查询定时器。
        /// </summary>
        private void StopBalanceTimer()
        {
            _balanceTimer?.Stop();
            _balanceTimer = null;
        }

        /// <summary>
        /// 异步查询余额并更新 UI。
        /// </summary>
        private async Task RefreshBalanceAsync()
        {
            if (_apiService == null) return;

            try
            {
                var balance = await _apiService.GetBalanceAsync();
                if (balance == null) return;

                _lastBalance = balance;
                UpdateBalanceDisplay(balance);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[余额] 刷新余额失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据当前语言环境格式化余额显示，同时显示本会话 token 消耗。
        /// 中文环境显示 RMB，英文环境显示 USD。
        /// </summary>
        private void UpdateBalanceDisplay(BalanceResponse balance)
        {
            // 确保在 UI 线程上执行
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateBalanceDisplay(balance));
                return;
            }

            var balanceBar = BalanceBar;
            var balanceLabel = BalanceLabel;
            if (balanceBar == null || balanceLabel == null) return;

            // ── 余额部分 ──
            string balanceText = string.Empty;
            if (balance.IsAvailable && balance.BalanceInfos.Count > 0)
            {
                bool isChinese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
                string targetCurrency = isChinese ? "CNY" : "USD";
                string currencySymbol = isChinese ? "¥" : "$";

                var targetInfo = balance.BalanceInfos.Find(b => b.Currency == targetCurrency)
                              ?? balance.BalanceInfos[0];

                balanceText = isChinese
                    ? $"💰 余额: {currencySymbol}{targetInfo.TotalBalance}"
                    : $"💰 Balance: {currencySymbol}{targetInfo.TotalBalance}";
            }

            // ── 会话消耗部分 ──
            string consumptionText = FormatSessionConsumption();

            if (!string.IsNullOrEmpty(balanceText) || !string.IsNullOrEmpty(consumptionText))
            {
                balanceLabel.Text = string.IsNullOrEmpty(balanceText)
                    ? consumptionText
                    : string.IsNullOrEmpty(consumptionText)
                        ? balanceText
                        : $"{balanceText}  |  {consumptionText}";
                balanceBar.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                balanceBar.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 格式化当前会话的 token 消耗信息。
        /// </summary>
        private string FormatSessionConsumption()
        {
            if (_apiService == null) return string.Empty;

            long promptTokens = _apiService.TotalPromptTokens;
            long completionTokens = _apiService.TotalCompletionTokens;
            long totalTokens = promptTokens + completionTokens;

            if (totalTokens == 0) return string.Empty;

            bool isChinese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

            string FormatTokens(long n) => n >= 1000 ? $"{n / 1000.0:F1}K" : n.ToString();

            return isChinese
                ? $"📊 本会话: 输入 {FormatTokens(promptTokens)} tokens, 输出 {FormatTokens(completionTokens)} tokens"
                : $"📊 Session: {FormatTokens(promptTokens)} in, {FormatTokens(completionTokens)} out";
        }

        /// <summary>
        /// 仅刷新消费显示（不重新请求余额 API），在流式生成完成后调用。
        /// </summary>
        private void RefreshConsumptionDisplay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => RefreshConsumptionDisplay());
                return;
            }

            var balanceLabel = BalanceLabel;
            var balanceBar = BalanceBar;
            if (balanceLabel == null || balanceBar == null) return;

            string consumptionText = FormatSessionConsumption();
            if (string.IsNullOrEmpty(consumptionText))
            {
                // 没有消费数据：如果余额也没显示，则隐藏整行
                if (balanceBar.Visibility == System.Windows.Visibility.Visible)
                {
                    // 保留余额部分，去掉消费部分
                    string currentText = balanceLabel.Text;
                    int pipeIdx = currentText.IndexOf("  |  ");
                    if (pipeIdx > 0)
                        balanceLabel.Text = currentText.Substring(0, pipeIdx);
                }
                return;
            }

            // 合并余额和消费
            string current = balanceLabel.Text;
            int idx = current.IndexOf("  |  ");
            string balancePart = idx > 0 ? current.Substring(0, idx) : current;

            if (string.IsNullOrEmpty(balancePart) || !balancePart.Contains("💰"))
            {
                // 只有消费，无余额
                balanceLabel.Text = consumptionText;
            }
            else
            {
                balanceLabel.Text = $"{balancePart}  |  {consumptionText}";
            }
            balanceBar.Visibility = System.Windows.Visibility.Visible;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源，取消事件订阅，保存对话。
        /// 幂等：重复调用不会产生副作用。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DeepSeekOptionsPage.SettingsChanged -= OnOcrSettingsChanged;

            // ── 取消 SolutionEvents 订阅 ──
            try
            {
                SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;
                SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;
                SolutionEvents.OnAfterOpenFolder -= OnAfterOpenFolder;
                SolutionEvents.OnAfterCloseFolder -= OnAfterCloseFolder;
                Logger.Info("[Dispose] [会话] SolutionEvents 监听已取消");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Dispose] [会话] 取消 SolutionEvents 失败: {ex.Message}");
            }

            CancelStreaming();
            DisposeStreamingCts();
            StopBalanceTimer();
            _apiService?.Dispose();
            _webSearchService?.Dispose();
            _mcpManager?.Dispose();

            if (_agentDispatcher != null)
            {
                _agentDispatcher.PermissionRequested -= OnAgentPermissionRequested;
                _agentDispatcher.QuestionsRequested -= OnAgentQuestionsRequested;
                _agentDispatcher.Dispose();
                _agentDispatcher = null;
            }

            SaveCurrentSession();

            // ── 清理临时上下文文件 ──
            CleanupTempContextFiles();

            Logger.Info("[Dispose] DeepSeekChatControl 已释放");
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

        /// <summary>
        /// 更新输入框占位文字（跟随 i18n 语言设置）。
        /// </summary>
        private void UpdateInputPlaceholder()
        {
            try
            {
                InputPlaceholder.Text = LocalizationService.Instance["input.placeholder"];
            }
            catch (Exception ex)
            {
                Logger.Warn($"[i18n] 更新输入框占位文字失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新所有按钮的 ToolTip 和文本（跟随 i18n 语言设置）。
        /// </summary>
        private void UpdateAllTooltips()
        {
            try
            {
                var L = LocalizationService.Instance;

                // 顶部按钮
                DeleteSessionButton.ToolTip = L["input.deleteConversationTip"];
                AddContextButton.ToolTip = L["input.addContextTip"];
                UploadButton.ToolTip = L["input.uploadFileTip"];
                NewChatButton.ToolTip = L["input.newChat"];
                NewChatButton.Content = L["input.newChat"];
                ClearButton.Content = L["input.clearChat"];
                McpConfigButton.ToolTip = L["input.mcpConfig"];

                // 添加上下文菜单项
                AddActiveDocMenuItem.Header = L["input.attachActiveDocument"];
                AddActiveDocMenuItem.ToolTip = L["input.attachActiveDocTip"];
                AddProjectFileMenuItem.Header = L["input.attachProjectFile"];
                AddProjectFileMenuItem.ToolTip = L["input.attachProjectFileTip"];
                AddAllFilesMenuItem.Header = L["input.attachAllProjectFiles"];
                AddAllFilesMenuItem.ToolTip = L["input.attachAllFilesTip"];
                AddSelectionMenuItem.Header = L["input.attachSelection"];
                AddSelectionMenuItem.ToolTip = L["input.attachSelectionTip"];
                AddDebugMenuItem.Header = L["input.attachDebug"];
                AddDebugMenuItem.ToolTip = L["input.attachDebugTip"];

                // 搜索引擎 tooltip
                if (WebSearchEngineComboBox != null)
                    WebSearchEngineComboBox.ToolTip = L["input.searchEngineTip"];
            }
            catch (Exception ex)
            {
                Logger.Warn($"[i18n] 更新 ToolTip 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新 UI 标签文本（跟随 i18n 语言设置）。
        /// 包括深度思考复选框等非 ToolTip 的 UI 元素。
        /// </summary>
        private void UpdateUiLabels()
        {
            try
            {
                var L = LocalizationService.Instance;

                // 深度思考复选框
                if (ThinkingCheckBox != null)
                    ThinkingCheckBox.Content = L["chat.thinkingCheckbox"];

                // @agent / /skill 弹出框标题
                if (AgentPopupTitle != null)
                    AgentPopupTitle.Text = L["popup.agentTitle"];
                if (SkillPopupTitle != null)
                    SkillPopupTitle.Text = L["popup.skillTitle"];

                // ── Diff 全局控制栏按钮 ──
                if (AcceptAllButton != null)
                    AcceptAllButton.Content = L["diff.global.acceptAll"];
                if (UndoAllButton != null)
                    UndoAllButton.Content = L["diff.global.undoAll"];

                // ── 状态栏默认文本 ──
                if (StatusLabel != null && string.IsNullOrEmpty(StatusLabel.Text))
                    StatusLabel.Text = L["status.ready"];

                // ── 刷新 diff 栏（如果当前可见，更新文本为当前语言）──
                RefreshDiffGlobalBar();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[i188] 更新 UI 标签失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新 Agent 模式徽章显示。
        /// </summary>
        public void UpdateAgentModeBadge()
        {
            try
            {
                if (AgentModeBadge == null || AgentModeText == null) return;

                var agentType = _agentDispatcher?.ActiveAgentType ?? Models.AgentType.Ask;

                // Ask 模式为默认，隐藏徽章
                if (agentType == Models.AgentType.Ask)
                {
                    AgentModeBadge.Visibility = System.Windows.Visibility.Collapsed;
                    return;
                }

                AgentModeBadge.Visibility = System.Windows.Visibility.Visible;

                switch (agentType)
                {
                    case Models.AgentType.Plan:
                        AgentModeBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x88, 0x00)); // 橙
                        AgentModeBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xAA, 0x22));
                        AgentModeText.Text = "PLAN";
                        break;
                    case Models.AgentType.Edit:
                        AgentModeBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32)); // 绿
                        AgentModeBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
                        AgentModeText.Text = "EDIT";
                        break;
                    case Models.AgentType.Build:
                        AgentModeBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6A, 0x1B, 0x9A)); // 紫
                        AgentModeBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x27, 0xB0));
                        AgentModeText.Text = "BUILD";
                        break;
                    case Models.AgentType.Explore:
                        AgentModeBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x6D, 0xA0)); // 蓝
                        AgentModeBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x8D, 0xC0));
                        AgentModeText.Text = "EXPLORE";
                        break;
                    default:
                        AgentModeBadge.Visibility = System.Windows.Visibility.Collapsed;
                        break;
                }

                AgentModeBadge.ToolTip = agentType switch
                {
                    Models.AgentType.Plan => "规划模式 — AI 分析代码库并制定实现计划",
                    Models.AgentType.Edit => "编辑模式 — AI 正在修改项目代码",
                    Models.AgentType.Build => "构建模式 — AI 正在诊断和修复编译错误",
                    Models.AgentType.Explore => "探索模式 — AI 正在检索项目代码",
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AgentMode] 更新徽章失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 带节流的状态栏更新。通过 ExecuteScriptAsync 直接更新 WebView2 内嵌 #status-bar，
        /// 绕过 WPF TextBlock 的 Measure→Arrange→Render 管线。
        /// </summary>
        private void UpdateStatusText(string text)
        {
            if (_statusUpdateStopwatch != null
                && _statusUpdateStopwatch.ElapsedMilliseconds < StatusUpdateMinIntervalMs)
                return;

            _statusUpdateStopwatch?.Restart();
            _ = UpdateStatusViaJsAsync(text);
        }

        /// <summary>
        /// 带缓存的工具调用状态更新。仅在工具名发生变化时更新。
        /// </summary>
        private void UpdateStatusToolCall(string toolNames)
        {
            if (toolNames == _lastToolCallNames) return;
            _lastToolCallNames = toolNames;

            if (_statusUpdateStopwatch != null
                && _statusUpdateStopwatch.ElapsedMilliseconds < StatusUpdateMinIntervalMs)
                return;

            _statusUpdateStopwatch?.Restart();
            _ = UpdateStatusViaJsAsync(
                string.Format(LocalizationService.Instance["status.callingTool"], toolNames));
        }

        /// <summary>
        /// 直接更新 WPF 状态行（StatusLabel），替代原先 WebView2 内嵌 #status-bar 的 JS 路径。
        /// 通过 VS JoinableTaskFactory 切换到 UI 线程更新。
        /// </summary>
        private async Task UpdateStatusViaJsAsync(string statusText)
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = statusText ?? "";
            }
            catch
            {
                // 调度失败时静默跳过
            }
        }

        #endregion


        // ── 流式更新批处理状态 ──
        private class StreamBatchState
        {
            public int MessageIndex;
            public StringBuilder Content = new(256);
            public StringBuilder Reasoning = new(64);
            public string? PendingStatus;
            public bool IsComplete;
            public long LastFlushTicks;
            /// <summary>上次刷新时的 Reason 长度，用于判断思考内容是否显著增长</summary>
            public int LastFlushedReasoningLength;
        }

        private readonly Dictionary<int, StreamBatchState> _streamBatchStates = new();
        private readonly object _streamBatchLock = new();

        private const long StreamBatchMinIntervalTicks = 60_0000; // 60ms (Stopwatch ticks, 配合 StreamRenderMinIntervalMs=80ms)

        /// <summary>
        /// 空闲超时定时器：每次 BatchStreamingUpdate 调用后重置 300ms，
        /// 到时后强制刷新所有待处理缓冲，确保短暂停顿的内容能被及时渲染。
        /// 只创建一次，重复 Stop/Start 避免反复 Dispose 的开销。
        /// </summary>
        private System.Timers.Timer? _flushIdleTimer;
        private bool _flushIdleTimerCreated;

        private void EnsureFlushIdleTimer()
        {
            if (_flushIdleTimerCreated) return;
            _flushIdleTimerCreated = true;
            _flushIdleTimer = new System.Timers.Timer(300) { AutoReset = false };
            _flushIdleTimer.Elapsed += (_, _) =>
            {
                try
                {
                    Dictionary<int, StreamBatchState> pending;
                    lock (_streamBatchLock)
                    {
                        if (_streamBatchStates.Count == 0) return;
                        pending = new Dictionary<int, StreamBatchState>(_streamBatchStates);
                    }
                    foreach (var kvp in pending)
                        BatchStreamingUpdate(kvp.Key);
                }
                catch { }
            };
        }

        /// <summary>
        /// 批处理流式更新：累积内容变化，仅在间隔达标或显著变化时推送。
        /// </summary>
        private void BatchStreamingUpdate(int messageIndex, string? content = null,
            string? reasoning = null, string? status = null, bool isComplete = false)
        {
            lock (_streamBatchLock)
            {
                if (!_streamBatchStates.TryGetValue(messageIndex, out var state))
                {
                    state = new StreamBatchState { MessageIndex = messageIndex };
                    _streamBatchStates[messageIndex] = state;
                }

                if (content != null)
                {
                    state.Content.Clear();
                    state.Content.Append(content);
                }
                if (reasoning != null)
                {
                    state.Reasoning.Clear();
                    state.Reasoning.Append(reasoning);
                }
                if (status != null)
                    state.PendingStatus = status;
                if (isComplete)
                    state.IsComplete = true;

                long now = Stopwatch.GetTimestamp();
                long elapsed = now - state.LastFlushTicks;

                // 仅当满足条件时实际推送：已完成 / 内容显著变化 / 思考显著变化 / 间隔达标且有任意内容
                bool contentChanged = state.Content.Length > StreamRenderInterval;
                bool reasoningChanged = state.Reasoning.Length > 0
                    && state.Reasoning.Length - state.LastFlushedReasoningLength >= 50;
                bool timeElapsed = elapsed >= StreamBatchMinIntervalTicks;

                if (state.IsComplete || contentChanged || reasoningChanged
                    || (timeElapsed && (state.Content.Length > 0 || state.Reasoning.Length > 0)))
                {
                    state.LastFlushTicks = now;
                    state.LastFlushedReasoningLength = state.Reasoning.Length;
                    PostStreamingUpdate(state.MessageIndex,
                        state.Content.ToString(),
                        state.Reasoning.ToString(),
                        state.IsComplete,
                        state.PendingStatus);
                    state.PendingStatus = null;
                    if (state.IsComplete)
                        _streamBatchStates.Remove(messageIndex);
                }

                // ── 有任意内容且未完成：重置空闲超时定时器（300ms 无新输入则强制刷新）──
                if (!state.IsComplete && (state.Content.Length > 0 || state.Reasoning.Length > 0))
                {
                    EnsureFlushIdleTimer();
                    _flushIdleTimer?.Stop();
                    _flushIdleTimer?.Start();
                }
            }
        }

        /// <summary>
        /// 强制刷新指定消息的批处理缓冲区。
        /// 在流式完成、最终渲染等关键时刻调用，确保累积内容不会丢失。
        /// </summary>
        private void FlushBatchStream(int messageIndex)
        {
            StreamBatchState? state;
            lock (_streamBatchLock)
            {
                if (!_streamBatchStates.TryGetValue(messageIndex, out state))
                    return;
                // 将 LastFlushTicks 置零，使下次检查一定超时
                state.LastFlushTicks = 0;
            }
            // 用当前内容重新调用批处理方法，将强制推送（因为 LastFlushTicks=0 确保 elapsed 超时）
            BatchStreamingUpdate(messageIndex);
        }

        /// <summary>
        /// 清除指定消息的批处理状态（用于中断/取消时清理）。
        /// </summary>
        private void ClearBatchStream(int messageIndex)
        {
            lock (_streamBatchLock)
                _streamBatchStates.Remove(messageIndex);
        }
    }
}

