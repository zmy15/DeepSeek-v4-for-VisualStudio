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
        private BuiltInToolService? _builtInToolService;
        private SkillService? _skillService;
        private SkillDiscoveryResult? _skillDiscoveryResult;
        private AgentDispatcher? _agentDispatcher;
        private CancellationTokenSource? _currentStreamingCts;
        private string? _solutionPath;

        // ── 解决方案事件已订阅标记（通过 Microsoft.VisualStudio.Shell.Events.SolutionEvents）──

        private readonly List<ChatMessage> _messages = new();
        private readonly ConversationContextManager _contextManager = new();

        // ── 树状对话结构 ──
        private ConversationTree? _tree;

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

        // ── 防止重复释放 ──
        private bool _disposed;

        // ── 增量渲染状态（对标 Turbo ucChat） ──
        private bool _browserInitialized;
        private bool _webViewInitialized;
        private int _lastRenderedMessagesLength;
        private readonly StringBuilder _messagesHtml = new();

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
                _agentDispatcher.Dispose();
            }

            // ── 创建内置工具服务 ──
            var buildService = new BuildService();
            _builtInToolService = new BuiltInToolService(_mcpManager, _webSearchService, buildService);

            _agentDispatcher = new AgentDispatcher(_apiService, _builtInToolService, _mcpManager);
            _agentDispatcher.ContextManager = _contextManager;
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

                // ── 将 MCP 管理器注入到 Agent 调度器 ──
                _agentDispatcher?.UpdateMcpManager(_mcpManager);

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

            // ── 校验 OCR 引擎状态（PaddleOCR 已移除，仅检查 Windows 内置 OCR）──
            {
                bool ocrReady = OcrService.IsEngineReady();
                string ocrStatus = OcrService.GetEngineStatus();
                Logger.Info($"OCR 引擎状态: {ocrStatus}");

                if (!ocrReady)
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = "⚠️ Windows 内置 OCR 不可用。请检查系统语言包设置。";
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
                        StatusLabel.Text = "⚠️ 部分上下文恢复失败，已回退";
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
            }
            catch (Exception ex)
            {
                Logger.Warn($"[i18n] 更新 UI 标签失败: {ex.Message}");
            }
        }

        #endregion


    }
}

