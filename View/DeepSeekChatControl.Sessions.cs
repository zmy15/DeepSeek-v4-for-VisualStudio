using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 会话管理：创建、切换、删除、清空会话，以及会话持久化。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Private Methods - Session Management

        /// <summary>
        /// 创建新会话的内部方法（不保存）。
        /// </summary>
        private ChatSession CreateNewSessionInternal()
        {
            ResetActiveAgentToAsk();

            // ── 初始化新树 ──
            _tree = new ConversationTree();
            Logger.Info("[Tree] 新会话 → ConversationTree 已初始化");

            // ── 重置 AI 标题生成状态 ──
            _pendingAiTitle = false;
            _firstUserMessageForTitle = null;

            return new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = LocalizationService.Instance["session.new"],
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
            };
        }

        /// <summary>
        /// 保存当前活跃会话到容器并持久化。
        /// TreeDataJson 为唯一权威数据源（含所有 user/assistant 消息）。
        /// ApiHistory 保留（含 tool/system 消息，树结构不包含这些角色）。
        /// </summary>
        private void SaveCurrentSession()
        {
            if (_activeSession == null || _sessionsContainer == null) return;

            // ── 树状结构序列化 ──
            if (_tree != null)
            {
                try
                {
                    var treeData = _tree.Serialize();
                    string treeJson = System.Text.Json.JsonSerializer.Serialize(treeData,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                    _activeSession.TreeDataJson = treeJson;

                    // ── 诊断日志：记录树状态 ──
                    var activePath = _tree.GetActivePath();
                    int userMsgCount = activePath.Count(n => n.Message?.Role == "user");
                    int assistantMsgCount = activePath.Count(n => n.Message?.Role == "assistant");
                    Logger.Info($"[Tree] 保存: 总节点={_tree.TotalNodeCount}, 活跃路径={activePath.Count}节点 (用户={userMsgCount}, 助手={assistantMsgCount}), JSON长度={treeJson.Length}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Tree] 序列化失败: {ex.Message}", ex);
                }
            }
            else
            {
                Logger.Warn("[Tree] 保存时 _tree 为 null，仅保存 ApiHistory");
            }

            // ── ApiHistory 始终保存（含 tool/system 消息，树结构不包含）──
            _activeSession.ApiHistory = _contextManager.GetFullContext();

            // ── 持久化累计 Cache 统计（重启后恢复显示）──
            if (_apiService != null)
            {
                _activeSession.CumulativeCacheHitTokens = _apiService.TotalCacheHitTokens;
                _activeSession.CumulativeCacheMissTokens = _apiService.TotalCacheMissTokens;
                _activeSession.CumulativePromptTokens = _apiService.TotalPromptTokens;
                _activeSession.CumulativeCompletionTokens = _apiService.TotalCompletionTokens;
                _activeSession.CumulativeCostYuan = _apiService.TotalSessionCostYuan;
                _activeSession.CumulativeCostUsd = _apiService.TotalSessionCostUsd;
            }

            _sessionsContainer.ActiveSessionId = _activeSession.Id;
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);
        }

        /// <summary>
        /// 根据第一条用户消息，标记等待 AI 生成会话标题。
        /// 首轮对话完成后，由流式响应结束逻辑调用 GenerateAiTitleAsync 实际生成标题。
        /// </summary>
        private void AutoTitleSession()
        {
            if (_activeSession == null) return;
            if (_activeSession.Title != LocalizationService.Instance["session.new"]) return;

            var firstUserMsg = _messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg == null || string.IsNullOrWhiteSpace(firstUserMsg.Content))
                return;

            // 标记等待 AI 生成标题，存储第一条用户消息供后续摘要使用
            _pendingAiTitle = true;
            _firstUserMessageForTitle = firstUserMsg.Content.Trim();
            Logger.Info($"[AI标题] 已标记等待生成，首条用户消息长度: {_firstUserMessageForTitle.Length}");
        }

        /// <summary>
        /// 使用 AI 根据首轮对话内容生成会话标题。
        /// 在首轮助手回复完成后由流式响应结束逻辑调用。
        /// </summary>
        /// <param name="firstUserMessage">第一条用户消息内容</param>
        /// <param name="firstAssistantReply">第一条助手回复内容</param>
        private async Task GenerateAiTitleAsync(string firstUserMessage, string firstAssistantReply)
        {
            try
            {
                if (_activeAgent == null) return;
                if (_activeSession == null) return;
                if (_activeSession.Title != LocalizationService.Instance["session.new"]) return;

                string userSnippet = firstUserMessage;
                string assistantSnippet = firstAssistantReply;

                var prompt = string.Format(LocalizationService.Instance["session.generateTitlePrompt"], userSnippet, assistantSnippet);

                // ── v1.1.11: 前置 SharedImmutablePrefix 以命中 Prompt Cache ──
                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = AiPrompts.SharedImmutablePrefix },
                    new ChatApiMessage { Role = "user", Content = prompt }
                };

                Logger.Info("[AI标题] 正在调用 API 生成标题…");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // ── toolChoice:"none" 防止 AI 调用工具；thinkingEnabled:false 防止思考模型只输出推理而 content 为空 ──
                string rawTitle = await _activeAgent.CallAiWithMessagesAsync(messages, cts.Token, maxTokens: 128, temperature: 0.3, toolChoice: "none", model: "deepseek-v4-flash", thinkingEnabled: false);

                if (string.IsNullOrWhiteSpace(rawTitle))
                {
                    Logger.Warn($"[AI标题] API 返回空标题 (raw='{rawTitle ?? "<null>"}')，回退到截取模式");
                    FallbackAutoTitle(firstUserMessage);
                    return;
                }

                // 清理标题：去除引号、换行、首尾空白
                string title = rawTitle.Trim().Trim('"', '\'', '「', '」', '《', '》', '"', '"');

                if (string.IsNullOrWhiteSpace(title))
                {
                    Logger.Warn($"[AI标题] 清理后标题为空 (raw='{rawTitle}', trimmed='{title}')，回退到截取模式");
                    FallbackAutoTitle(firstUserMessage);
                    return;
                }

                if (_activeSession.Title != LocalizationService.Instance["session.new"])
                {
                    Logger.Info($"[AI标题] 标题已被更新为 '{_activeSession.Title}'，跳过覆盖");
                    return;
                }

                _activeSession.Title = title;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                PopulateSessionComboBox();
                SaveCurrentSession();
                Logger.Info($"[AI标题] 会话标题已更新为: {title} (raw='{rawTitle}')");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AI标题] 生成失败，回退到截取模式: {ex.Message}");
                FallbackAutoTitle(firstUserMessage);
            }
            finally
            {
                _pendingAiTitle = false;
                _firstUserMessageForTitle = null;
            }
        }

        /// <summary>
        /// 回退标题生成：截取第一条用户消息的前30个字符作为标题。
        /// 当 AI 标题生成失败时使用。
        /// </summary>
        private void FallbackAutoTitle(string firstUserMessage)
        {
            if (_activeSession == null) return;
            if (_activeSession.Title != LocalizationService.Instance["session.new"]) return;

            string title = firstUserMessage.Trim();
            int newlineIdx = title.IndexOf('\n');
            if (newlineIdx > 0)
                title = title.Substring(0, newlineIdx).Trim();
            // RAG-MARK: no-truncate — 不再截断回退标题

            _activeSession.Title = title;
            PopulateSessionComboBox();
            SaveCurrentSession();
            Logger.Info($"[AI标题] 回退标题: {title}");
        }

        /// <summary>
        /// 仅在用户真正提交消息时刷新会话活跃时间；切换/查看会话不应改变排序。
        /// 注意：本方法只更新内存值，持久化依赖后续 SaveCurrentSession() 序列化时
        /// 携带该值（LastActiveAt 已不再由 SaveCurrentSession 主动刷新）。
        /// </summary>
        private void TouchCurrentSessionLastActive()
        {
            if (_activeSession == null) return;
            _activeSession.LastActiveAt = DateTime.Now;
            PopulateSessionComboBox();
        }

        /// <summary>
        /// 切换到指定会话。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于会话切换（从事件处理程序调用），异常已在方法内处理
        private async void SwitchToSession(ChatSession session)
        {
            try
            {
                if (session == null || session == _activeSession) return;

                lock (_lock)
                {
                    if (_isGenerating)
                    {
                        CancelStreaming();
                        _isGenerating = false;
                    }
                }

                UpdateButtonsState();

                // 保存当前会话
                SaveCurrentSession();

                // ── 会话切换时清理计划面板相关状态 ──
                // DOM 将全量重建，所有计划面板需要重新创建
                _createdPlanIds.Clear();

                lock (_lock)
                {
                    // 切换到新会话
                    _activeSession = session;
                    if (_sessionsContainer != null)
                        _sessionsContainer.ActiveSessionId = _activeSession.Id;
                    ResetActiveAgentToAsk();

                    // ── 同步当前会话 ID 到内置工具服务（MemoryTool 需要）──
                    if (_builtInToolService != null)
                        _builtInToolService.CurrentSessionId = _activeSession.Id;

                    // ── 重置 AI 标题生成状态（切换到的会话可能已有标题） ──
                    _pendingAiTitle = false;
                    _firstUserMessageForTitle = null;

                    // 清空并加载消息
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                // ── 切换会话时重置并恢复累计 Token/费用统计 ──
                _apiService?.ResetAccumulatedStats();
                RestoreCacheStatsFromSession();

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
                            Logger.Info($"[Tree] 从 TreeData 恢复 (节点数: {treeData.Nodes.Count})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Tree] TreeData 反序列化失败: {ex.Message}");
                        _tree = null;
                    }
                }
                else
                {
                    _tree = null;
                }

                // ── 从 ApiHistory 恢复完整上下文（权威数据源，含 tool_calls/reasoning/system）──
                if (_activeSession.ApiHistory.Count > 0)
                {
                    try
                    {
                        _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                        Logger.Info($"[Context] SwitchToSession 从 ApiHistory 恢复上下文成功 ({_activeSession.ApiHistory.Count} 条消息)");

                        if (_contextManager.TurnCount == 0 && _tree != null)
                            RebuildContextFromTree();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Context] SwitchToSession 从 ApiHistory 恢复上下文失败，回退到树重建: {ex.Message}", ex);
                        RebuildContextFromTree();
                    }
                }
                else
                {
                    RebuildContextFromTree();
                }

                // ── 恢复系统级上下文（RestoreFullContext/Clear 会清空 system prompt / memory / skill）──
                await RestoreSystemContextAsync();

                // ── 若无对话消息（新会话/空会话），补上欢迎语 ──
                bool hasMessages;
                lock (_lock) { hasMessages = _messages.Count > 0; }
                if (!hasMessages)
                {
                    var welcomeMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = WelcomeMessage,
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    lock (_lock)
                    {
                        _messages.Add(welcomeMsg);
                        // 欢迎消息不加入树结构（仅 UI 展示）
                    }
                }

                // 更新下拉框选中项
                PopulateSessionComboBox();

                // 完整刷新浏览器
                RebuildMessagesHtml();
                _browserInitialized = false;
                _pageReady = false;  // 重置页面就绪标志，等待新页面加载完成
                UpdateBrowser();

                // ── 刷新余额/消耗显示，确保 Token 计数反映当前会话 ──
                RefreshConsumptionDisplay();

                // 页面刷新后重建持久化任务面板
                _ = RebuildPanelsWhenPageReadyAsync();

                // ── 持久化 ActiveSessionId：异常退出后重启仍恢复到刚切换的会话 ──
                if (_sessionsContainer != null)
                    ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

                Logger.Info($"切换到会话: {_activeSession.Title}");
            }
            catch (Exception ex)
            {
                Logger.Error($"SwitchToSession 异常: {ex.Message}", ex);
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = LocalizationService.Instance.Format("status.sessionSwitchFailed", ex.Message);
                }
                catch { }
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 填充会话下拉框，保持选中当前活跃会话。
        /// </summary>
        private void PopulateSessionComboBox()
        {
            if (_sessionsContainer == null) return;

            // 交互原则：当前会话钉在顶部，其余按最后活跃时间倒序，
            // 避免用户悬停选择时列表因活跃时间变化而重排跳动。
            var sortedSessions = _sessionsContainer.Sessions
                .OrderByDescending(s => _activeSession != null && s.Id == _activeSession.Id)
                .ThenByDescending(s => s.LastActiveAt)
                .ToList();

            // 程序化填充期间抑制 SelectionChanged，避免瞬时 null/重置触发误切换
            _suppressSessionSelection = true;
            try
            {
                SessionComboBox.ItemsSource = null;
                SessionComboBox.ItemsSource = sortedSessions;

                if (_activeSession != null)
                {
                    SessionComboBox.SelectedItem = sortedSessions.FirstOrDefault(s => s.Id == _activeSession.Id);
                }
            }
            finally
            {
                _suppressSessionSelection = false;
            }

            // ── P-B：同步 Copilot 式历史浮层与标题 ──
            RefreshHistoryUI();
        }

        /// <summary>历史浮层条目视图模型。</summary>
        internal sealed class HistoryItemViewModel
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string LastActiveText { get; set; } = string.Empty;
            public bool IsCurrent { get; set; }
            public string DeleteVisibility => IsCurrent ? "Collapsed" : "Visible";
        }

        private static string FormatRelativeTime(DateTime t)
        {
            var span = DateTime.Now - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return t.ToString("yyyy-MM-dd");
        }

        /// <summary>刷新历史浮层列表与当前会话标题。</summary>
        private void RefreshHistoryUI()
        {
            try
            {
                if (CurrentSessionTitle != null)
                    CurrentSessionTitle.Text = _activeSession?.Title ?? string.Empty;

                if (HistoryListBox == null || _sessionsContainer == null) return;

                var items = _sessionsContainer.Sessions
                    .OrderByDescending(s => _activeSession != null && s.Id == _activeSession.Id)
                    .ThenByDescending(s => s.LastActiveAt)
                    .Select(s => new HistoryItemViewModel
                    {
                        Id = s.Id,
                        Title = string.IsNullOrWhiteSpace(s.Title) ? "(未命名对话)" : s.Title,
                        LastActiveText = FormatRelativeTime(s.LastActiveAt),
                        IsCurrent = _activeSession != null && s.Id == _activeSession.Id,
                    })
                    .ToList();

                _suppressSessionSelection = true;
                try
                {
                    HistoryListBox.ItemsSource = items;
                }
                finally
                {
                    _suppressSessionSelection = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[History] 刷新失败: {ex.Message}");
            }
        }

        private void HistoryToggleButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshHistoryUI();
            HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
        }

        /// <summary>
        /// 历史浮层 DataTemplate 内删除按钮的 Loaded 钩子：
        /// 模板实例化时套用本地化 ToolTip（XAML 静态值仅为设计时占位）。
        /// </summary>
        private void HistoryDeleteButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
                btn.ToolTip = LocalizationService.Instance["input.deleteSessionItemTip"];
        }

        private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSessionSelection) return;
            if (HistoryListBox.SelectedItem is HistoryItemViewModel vm && _sessionsContainer != null)
            {
                var target = _sessionsContainer.Sessions.FirstOrDefault(s => s.Id == vm.Id);
                if (target != null && target != _activeSession)
                {
                    SwitchToSession(target);
                }
            }
            HistoryPopup.IsOpen = false;
        }

        private void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender is System.Windows.Controls.Button btn ? btn.Tag as string : null) is not string id)
                return;

            if (_activeSession != null && _activeSession.Id == id)
            {
                DeleteCurrentSession();   // 复用既有确认+清理流程
                RefreshHistoryUI();
                return;
            }

            var result = System.Windows.MessageBox.Show(
                LocalizationService.Instance["chat.confirmDeleteConversation"] ?? "删除此对话？",
                LocalizationService.Instance["chat.deleteConversation"] ?? "删除对话",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            if (_sessionsContainer != null)
            {
                var target = _sessionsContainer.Sessions.FirstOrDefault(s => s.Id == id);
                if (target != null) _sessionsContainer.Sessions.Remove(target);
                ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);
            }
            RefreshHistoryUI();
        }

        /// <summary>
        /// 创建新对话（"新对话" 按钮点击）。
        /// </summary>
        private void CreateNewChat()
        {
            try
            {
                lock (_lock)
                {
                    // 停止当前生成
                    if (_isGenerating)
                    {
                        CancelStreaming();
                        _isGenerating = false;
                    }
                }

                UpdateButtonsState();

                // 保存当前会话
                SaveCurrentSession();

                // 创建新会话
                _activeSession = CreateNewSessionInternal();
                if (_sessionsContainer == null)
                    _sessionsContainer = new SessionsContainer { SolutionPath = _solutionPath ?? "(unsaved)" };
                _sessionsContainer.Sessions.Add(_activeSession);
                _sessionsContainer.ActiveSessionId = _activeSession.Id;

                // ── 同步当前会话 ID 到内置工具服务 ──
                if (_builtInToolService != null)
                    _builtInToolService.CurrentSessionId = _activeSession.Id;

                lock (_lock)
                {
                    // 清空并添加欢迎语
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = WelcomeMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                lock (_lock)
                {
                    _messages.Add(welcomeMsg);
                    // 欢迎消息不加入树结构（仅 UI 展示）
                }

                // ── 重置累计 Token/费用统计（新会话从零开始）──
                _apiService?.ResetAccumulatedStats();

            // 更新下拉框
            PopulateSessionComboBox();

            // 持久化
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

            // 重置搜索额度状态（新会话可能额度已恢复）
            _webSearchService?.ResetQuotaState();

            // 清空附件列表
            ClearAttachedFiles();

            // 刷新浏览器
            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            // ── 刷新余额/消耗显示，确保新会话 token 计数归零 ──
            RefreshConsumptionDisplay();

            InputTextBox.Focus();
            Logger.Info(LocalizationService.Instance["session.createNew"]);
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateNewChat 异常: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["session.createNew"] + ": {0}", ex.Message);
            }
        }

        /// <summary>
        /// 删除当前会话。
        /// </summary>
        private void DeleteCurrentSession()
        {
            try
            {
                if (_sessionsContainer == null || _activeSession == null) return;
                if (_sessionsContainer.Sessions.Count <= 1)
                {
                    // 最后一个会话不能删除，清空即可
                    ClearCurrentSessionMessages();
                    return;
                }

                lock (_lock)
                {
                    if (_isGenerating)
                    {
                        CancelStreaming();
                        _isGenerating = false;
                    }
                }

                UpdateButtonsState();

                string deletedTitle = _activeSession.Title;
                _sessionsContainer.Sessions.Remove(_activeSession);

                // 切换到第一个会话
                _activeSession = _sessionsContainer.Sessions.FirstOrDefault();
                _sessionsContainer.ActiveSessionId = _activeSession?.Id;

                ResetActiveAgentToAsk();

                lock (_lock)
                {
                    // 加载新会话消息
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                if (_activeSession != null)
                {
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
                                Logger.Info($"[Tree] DeleteCurrentSession: 从 TreeData 恢复 (节点数: {treeData.Nodes.Count})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Tree] DeleteCurrentSession TreeData 反序列化失败: {ex.Message}");
                        }
                    }

                    // ── 从 ApiHistory 恢复完整上下文（权威数据源）──
                    if (_activeSession.ApiHistory.Count > 0)
                    {
                        try
                        {
                            _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                            Logger.Info($"[Context] DeleteCurrentSession 从 ApiHistory 恢复上下文成功 ({_activeSession.ApiHistory.Count} 条消息)");

                            if (_contextManager.TurnCount == 0 && _tree != null)
                                RebuildContextFromTree();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Context] DeleteCurrentSession 从 ApiHistory 恢复上下文失败，回退到树重建: {ex.Message}", ex);
                            RebuildContextFromTree();
                        }
                    }
                    else
                    {
                        RebuildContextFromTree();
                    }
                }

                // ── 若无对话消息（空会话），补上欢迎语 ──
                if (_messages.Count == 0)
                {
                    var welcomeMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = WelcomeMessage,
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(welcomeMsg);
                }

                // 更新下拉框并持久化
                PopulateSessionComboBox();
                ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

                // 刷新浏览器
                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                Logger.Info($"已删除会话: {deletedTitle}");
            }
            catch (Exception ex)
            {
                Logger.Error($"DeleteCurrentSession 异常: {ex.Message}", ex);
                    StatusLabel.Text = LocalizationService.Instance.Format("status.sessionDeleteFailed", ex.Message);
            }
        }

        /// <summary>
        /// 清空当前会话的消息（保留会话本身）。
        /// </summary>
        private void ClearCurrentSessionMessages()
        {
            try
            {
                // ── 重置累计 Token / 费用计数器 ──
                _apiService?.ResetAccumulatedStats();

                ResetActiveAgentToAsk();

                // ── 重置树 ──
                _tree = new ConversationTree();
                Logger.Info("[Tree] 清空会话 → ConversationTree 已重置");

                lock (_lock)
                {
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                if (_activeSession != null)
                {
                    _activeSession.ApiHistory.Clear();
                    _activeSession.TreeDataJson = null;
                    _activeSession.Title = LocalizationService.Instance["session.new"];
                }

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = WelcomeMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                lock (_lock)
                {
                    _messages.Add(welcomeMsg);
                }

                PopulateSessionComboBox();
                SaveCurrentSession();

                // 清空附件列表
                ClearAttachedFiles();

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();
                Logger.Info(LocalizationService.Instance["session.clearConfirm"]);
            }
            catch (Exception ex)
            {
                Logger.Error($"ClearCurrentSessionMessages 异常: {ex.Message}", ex);
                StatusLabel.Text = LocalizationService.Instance.Format("status.messageSaveFailed", ex.Message);
            }
        }

        #endregion
    }
}
