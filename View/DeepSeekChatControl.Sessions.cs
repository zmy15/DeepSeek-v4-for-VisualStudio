using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
            _activeSession.LastActiveAt = DateTime.Now;
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
                if (_apiService == null) return;
                if (_activeSession == null) return;
                if (_activeSession.Title != LocalizationService.Instance["session.new"]) return;

                // 截断过长的内容以控制 token 消耗
                string userSnippet = firstUserMessage.Length > 500
                    ? firstUserMessage.Substring(0, 500) + "…"
                    : firstUserMessage;
                string assistantSnippet = firstAssistantReply.Length > 500
                    ? firstAssistantReply.Substring(0, 500) + "…"
                    : firstAssistantReply;

                var prompt = $"请根据以下对话内容，生成一个简洁的会话标题（不超过20个字，不要引号，不要省略号）：\n\n用户：{userSnippet}\n\n助手：{assistantSnippet}\n\n标题：";

                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "user", Content = prompt }
                };

                Logger.Info("[AI标题] 正在调用 API 生成标题…");
                string title = await _apiService.CompleteAsync(messages);

                if (!string.IsNullOrWhiteSpace(title))
                {
                    // 清理标题：去除引号、换行、首尾空白
                    title = title.Trim().Trim('"', '\'', '「', '」', '《', '》', '"', '"');
                    // 截断到 30 个字符
                    if (title.Length > 30)
                        title = title.Substring(0, 30);

                    if (!string.IsNullOrWhiteSpace(title) && _activeSession.Title == LocalizationService.Instance["session.new"])
                    {
                        _activeSession.Title = title;
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        PopulateSessionComboBox();
                        SaveCurrentSession();
                        Logger.Info($"[AI标题] 会话标题已更新为: {title}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AI标题] 生成失败，回退到截取模式: {ex.Message}");
                // 回退：使用原始截取逻辑
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
            if (title.Length > 30)
                title = title.Substring(0, 30) + "…";

            _activeSession.Title = title;
            PopulateSessionComboBox();
            SaveCurrentSession();
            Logger.Info($"[AI标题] 回退标题: {title}");
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
                        _currentStreamingCts?.Cancel();
                        _isGenerating = false;
                    }
                }

                UpdateButtonsState();

                // 保存当前会话
                SaveCurrentSession();

                lock (_lock)
                {
                    // 切换到新会话
                    _activeSession = session;
                    _activeSession.LastActiveAt = DateTime.Now;

                    // ── 重置 AI 标题生成状态（切换到的会话可能已有标题） ──
                    _pendingAiTitle = false;
                    _firstUserMessageForTitle = null;

                    // 清空并加载消息
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

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
                UpdateBrowser();

                Logger.Info($"切换到会话: {_activeSession.Title}");
            }
            catch (Exception ex)
            {
                Logger.Error($"SwitchToSession 异常: {ex.Message}", ex);
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"会话切换失败: {ex.Message}";
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

            // 按最后活跃时间倒序排列
            var sortedSessions = _sessionsContainer.Sessions
                .OrderByDescending(s => s.LastActiveAt)
                .ToList();

            SessionComboBox.ItemsSource = null;
            SessionComboBox.ItemsSource = sortedSessions;

            if (_activeSession != null)
            {
                SessionComboBox.SelectedItem = sortedSessions.FirstOrDefault(s => s.Id == _activeSession.Id);
            }
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
                        _currentStreamingCts?.Cancel();
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
                        _currentStreamingCts?.Cancel();
                        _isGenerating = false;
                    }
                }

                UpdateButtonsState();

                string deletedTitle = _activeSession.Title;
                _sessionsContainer.Sessions.Remove(_activeSession);

                // 切换到第一个会话
                _activeSession = _sessionsContainer.Sessions.FirstOrDefault();
                _sessionsContainer.ActiveSessionId = _activeSession?.Id;

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
                StatusLabel.Text = $"删除会话失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 清空当前会话的消息（保留会话本身）。
        /// </summary>
        private void ClearCurrentSessionMessages()
        {
            try
            {
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
                StatusLabel.Text = $"清空消息失败: {ex.Message}";
            }
        }

        #endregion
    }
}
