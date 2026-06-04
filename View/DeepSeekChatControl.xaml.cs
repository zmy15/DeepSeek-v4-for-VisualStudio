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
                    RefreshBalanceDisplay();
                });
            };

            // 初始化模型和推理强度下拉框
            ModelComboBox.ItemsSource = new[] { "deepseek-v4-pro", "deepseek-v4-flash" };
            ModelComboBox.SelectedIndex = 0;

            EffortComboBox.ItemsSource = new[] { "high", "max" };
            EffortComboBox.SelectedIndex = 0;

            // 初始化审批模式下拉框
            InitializeApprovalModeComboBox();

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

            // ── 从设置恢复审批模式 ──
            RefreshApprovalModeFromSettings();

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
        /// 根据 API 返回的币种格式化余额显示，同时显示本会话 token 消耗。
        /// 币种符号直接使用 API 返回值，不根据语言选择。
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
            string balanceText = FormatBalanceText(balance);

            // ── 会话消耗部分 ──
            string consumptionText = FormatSessionConsumption();

            // ── 组合显示 ──
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
        /// 格式化余额文本（提取为独立方法，供 UpdateBalanceDisplay 和 RefreshConsumptionDisplay 复用）。
        /// </summary>
        private static string FormatBalanceText(BalanceResponse balance)
        {
            if (!balance.IsAvailable || balance.BalanceInfos.Count == 0)
                return string.Empty;

            var info = balance.BalanceInfos[0];
            string symbol = GetCurrencySymbol(info.Currency);

            bool isChinese = LocalizationService.Instance.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            return isChinese
                ? $"💰 余额: {symbol}{info.TotalBalance}"
                : $"💰 Balance: {symbol}{info.TotalBalance}";
        }

        /// <summary>
        /// 根据 API 返回的币种代码获取对应符号。
        /// </summary>
        private static string GetCurrencySymbol(string currency)
        {
            return (currency ?? "").ToUpperInvariant() switch
            {
                "CNY" => "¥",
                "USD" => "$",
                "EUR" => "€",
                "GBP" => "£",
                "JPY" => "¥",
                "KRW" => "₩",
                _ => currency ?? "",
            };
        }

        /// <summary>
        /// 格式化当前会话的 token 消耗信息。
        /// 包含：API 实际 Token 消耗 + 上下文窗口利用率。
        /// </summary>
        private string FormatSessionConsumption()
        {
            if (_apiService == null) return string.Empty;

            long promptTokens = _apiService.TotalPromptTokens;
            long completionTokens = _apiService.TotalCompletionTokens;
            long totalTokens = promptTokens + completionTokens;

            bool isChinese = LocalizationService.Instance.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            string FormatTokens(long n) => n >= 1000 ? $"{n / 1000.0:F1}K" : n.ToString();

            // ── API 消耗部分 ──
            string apiPart;
            if (totalTokens == 0)
            {
                apiPart = "";
            }
            else
            {
                apiPart = isChinese
                    ? $"📊 本会话: 输入 {FormatTokens(promptTokens)} tokens, 输出 {FormatTokens(completionTokens)} tokens"
                    : $"📊 Session: {FormatTokens(promptTokens)} in, {FormatTokens(completionTokens)} out";
            }

            // ── 上下文窗口利用率（仅在有对话内容时显示）──
            string ctxPart = "";
            if (_contextManager != null && !_contextManager.IsEmpty)
            {
                int estimatedTokens = _contextManager.EstimatedTokens;
                int tokenBudget = _contextManager.TokenBudget;
                double usagePercent = _contextManager.UsagePercent;
                if (estimatedTokens > 0)
                {
                    string ctxLabel = isChinese ? "上下文" : "Context";
                    string warnIcon = usagePercent > 90 ? " ⚠️" : usagePercent > 70 ? " ℹ️" : "";
                    ctxPart = $"📐 {ctxLabel}: {FormatTokens(estimatedTokens)}/{FormatTokens(tokenBudget)} ({usagePercent:F0}%){warnIcon}";
                }
            }

            // ── 组合输出 ──
            if (!string.IsNullOrEmpty(apiPart) && !string.IsNullOrEmpty(ctxPart))
                return $"{apiPart}  |  {ctxPart}";
            if (!string.IsNullOrEmpty(apiPart))
                return apiPart;
            if (!string.IsNullOrEmpty(ctxPart))
                return ctxPart;
            return string.Empty;
        }

        /// <summary>
        /// 仅刷新消费显示（不重新请求余额 API），在流式生成完成后调用。
        /// 基于缓存的 _lastBalance 重建完整标签，避免字符串解析。
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

            // ── 基于缓存余额重建完整标签（避免脆弱的字符串解析）──
            if (_lastBalance != null)
            {
                UpdateBalanceDisplay(_lastBalance);
            }
            else
            {
                // 无余额缓存：仅更新消费部分
                string consumptionText = FormatSessionConsumption();
                if (!string.IsNullOrEmpty(consumptionText))
                {
                    balanceLabel.Text = consumptionText;
                    balanceBar.Visibility = System.Windows.Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// 使用缓存的余额数据刷新显示（不发起 API 请求），语言切换时调用。
        /// </summary>
        private void RefreshBalanceDisplay()
        {
            if (_lastBalance != null)
            {
                UpdateBalanceDisplay(_lastBalance);
            }
            else
            {
                RefreshConsumptionDisplay();
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

                // ── 刷新审批模式下拉框（语言切换时更新显示）──
                RefreshApprovalModeComboBox();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[i188] 更新 UI 标签失败: {ex.Message}");
            }
        }

        #region Approval Mode

        /// <summary>
        /// 审批模式选项数据类，用于绑定 ComboBox。
        /// </summary>
        private class ApprovalModeOption
        {
            public Models.ApprovalMode Mode { get; set; }
            public string DisplayText { get; set; } = string.Empty;
        }

        /// <summary>
        /// 初始化审批模式下拉框，从设置中加载当前模式。
        /// </summary>
        private void InitializeApprovalModeComboBox()
        {
            var L = LocalizationService.Instance;
            var options = new[]
            {
                new ApprovalModeOption { Mode = Models.ApprovalMode.BlockAll, DisplayText = L["approval.blockAll"] },
                new ApprovalModeOption { Mode = Models.ApprovalMode.AllowAll, DisplayText = L["approval.allowAll"] },
                new ApprovalModeOption { Mode = Models.ApprovalMode.SmartBlock, DisplayText = L["approval.smartBlock"] },
            };

            ApprovalModeComboBox.ItemsSource = options;
            ApprovalModeComboBox.DisplayMemberPath = "DisplayText";
            ApprovalModeComboBox.SelectedValuePath = "Mode";

            // 从设置恢复
            string savedMode = _options?.ApprovalMode ?? "SmartBlock";
            ApprovalModeComboBox.SelectedValue = savedMode switch
            {
                "BlockAll" => Models.ApprovalMode.BlockAll,
                "AllowAll" => Models.ApprovalMode.AllowAll,
                _ => Models.ApprovalMode.SmartBlock,
            };

            // 确保默认选中
            if (ApprovalModeComboBox.SelectedIndex < 0)
                ApprovalModeComboBox.SelectedValue = Models.ApprovalMode.SmartBlock;
        }

        /// <summary>
        /// 从设置恢复审批模式下拉框选中值。
        /// </summary>
        private void RefreshApprovalModeFromSettings()
        {
            if (ApprovalModeComboBox == null || _options == null) return;
            string savedMode = _options.ApprovalMode ?? "SmartBlock";
            ApprovalModeComboBox.SelectedValue = savedMode switch
            {
                "BlockAll" => Models.ApprovalMode.BlockAll,
                "AllowAll" => Models.ApprovalMode.AllowAll,
                _ => Models.ApprovalMode.SmartBlock,
            };
            if (ApprovalModeComboBox.SelectedIndex < 0)
                ApprovalModeComboBox.SelectedValue = Models.ApprovalMode.SmartBlock;
        }

        /// <summary>
        /// 刷新审批模式下拉框的显示文本（语言切换时调用）。
        /// </summary>
        private void RefreshApprovalModeComboBox()
        {
            if (ApprovalModeComboBox?.ItemsSource is ApprovalModeOption[] options)
            {
                var L = LocalizationService.Instance;
                foreach (var opt in options)
                {
                    opt.DisplayText = opt.Mode switch
                    {
                        Models.ApprovalMode.BlockAll => L["approval.blockAll"],
                        Models.ApprovalMode.AllowAll => L["approval.allowAll"],
                        Models.ApprovalMode.SmartBlock => L["approval.smartBlock"],
                        _ => opt.DisplayText,
                    };
                }
                // 强制刷新 ItemsSource 绑定
                var selectedValue = ApprovalModeComboBox.SelectedValue;
                ApprovalModeComboBox.ItemsSource = null;
                ApprovalModeComboBox.ItemsSource = options;
                ApprovalModeComboBox.SelectedValue = selectedValue;
            }
        }

        /// <summary>
        /// 获取当前审批模式。
        /// </summary>
        public Models.ApprovalMode GetCurrentApprovalMode()
        {
            if (ApprovalModeComboBox?.SelectedValue is Models.ApprovalMode mode)
                return mode;
            return Models.ApprovalMode.SmartBlock;
        }

        /// <summary>
        /// 检测命令是否危险（用于智能拦截模式）。
        /// 检查终端命令、文件删除等操作中是否包含危险模式。
        /// </summary>
        /// <param name="command">待执行的命令字符串</param>
        /// <param name="actionType">操作类型</param>
        /// <returns>true 表示命令危险，需要用户审批</returns>
        public static bool IsDangerousCommand(string command, string actionType)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            // 文件删除始终视为需要审批
            if (actionType == "file_delete")
                return true;

            // 转小写进行模式匹配
            string cmd = command.ToLowerInvariant().Trim();

            // ── 高危系统命令 ──
            // format / diskpart 等磁盘操作
            if (cmd.Contains("format ") && (cmd.Contains("c:") || cmd.Contains("d:") || cmd.Contains("/fs")))
                return true;
            if (cmd.Contains("diskpart"))
                return true;

            // ── 危险删除操作 ──
            // rm -rf / 或系统目录
            if ((cmd.Contains("rm ") || cmd.Contains("rmdir ")) && cmd.Contains("-rf"))
            {
                if (cmd.Contains(" /") || cmd.Contains(" ~") || cmd.Contains("/*")
                    || cmd.Contains("c:\\") || cmd.Contains("/windows") || cmd.Contains("/system"))
                    return true;
            }
            // del /f /s 系统路径
            if (cmd.Contains("del ") && (cmd.Contains("/s") || cmd.Contains("/f")) && cmd.Contains("c:\\"))
                return true;
            // Remove-Item 危险路径
            if (cmd.Contains("remove-item") && cmd.Contains("-recurse") && cmd.Contains("-force"))
            {
                if (cmd.Contains("c:\\windows") || cmd.Contains("c:\\program files")
                    || cmd.Contains("$env:systemroot") || cmd.Contains("/etc"))
                    return true;
            }

            // ── 危险的 git 操作 ──
            if (cmd.Contains("git push") && (cmd.Contains("--force") || cmd.Contains("-f"))
                && (cmd.Contains("master") || cmd.Contains("main")))
                return true;
            if (cmd.Contains("git reset") && cmd.Contains("--hard"))
                return true;
            if (cmd.Contains("git clean") && (cmd.Contains("-fd") || cmd.Contains("-df")))
                return true;

            // ── 关机/重启命令 ──
            if (cmd.Contains("shutdown") && (cmd.Contains("/s") || cmd.Contains("/r") || cmd.Contains("/t")))
                return true;
            if (cmd.Contains("restart-computer") || cmd.Contains("stop-computer"))
                return true;

            // ── 远程执行 / 下载并执行 ──
            if ((cmd.Contains("iex ") || cmd.Contains("invoke-expression") || cmd.Contains("invoke-webrequest")
                || cmd.Contains("iwr ")) && cmd.Contains("|"))
                return true;
            if (cmd.Contains("wget ") && cmd.Contains("| sh"))
                return true;
            if (cmd.Contains("curl ") && cmd.Contains("| bash"))
                return true;

            // ── 系统配置修改 ──
            if (cmd.Contains("set-executionpolicy") && !cmd.Contains("-scope process"))
                return true;
            if (cmd.Contains("reg delete") && (cmd.Contains("hklm") || cmd.Contains("hkey_local_machine")))
                return true;

            // ── 数据库危险操作 ──
            if ((cmd.Contains("drop table") || cmd.Contains("drop database") || cmd.Contains("truncate table"))
                && !cmd.Contains("if exists"))
                return true;
            if (cmd.Contains("delete from") && !cmd.Contains("where"))
                return true;

            return false;
        }

        #endregion

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

