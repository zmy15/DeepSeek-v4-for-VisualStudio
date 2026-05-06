using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
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
            return new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "新对话",
                Messages = new List<ChatMessage>(),
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
            };
        }

        /// <summary>
        /// 保存当前活跃会话到容器并持久化。
        /// </summary>
        private void SaveCurrentSession()
        {
            if (_activeSession == null || _sessionsContainer == null) return;

            _activeSession.Messages = _messages.ToList();
            _activeSession.LastActiveAt = DateTime.Now;
            _sessionsContainer.ActiveSessionId = _activeSession.Id;
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);
        }

        /// <summary>
        /// 根据第一条用户消息自动设置会话标题。
        /// 截取前30个字符，去掉换行。
        /// </summary>
        private void AutoTitleSession()
        {
            if (_activeSession == null) return;
            if (_activeSession.Title != "新对话") return;

            var firstUserMsg = _messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg == null || string.IsNullOrWhiteSpace(firstUserMsg.Content))
                return;

            string title = firstUserMsg.Content.Trim();
            // 取第一行或前30个字符
            int newlineIdx = title.IndexOf('\n');
            if (newlineIdx > 0)
                title = title.Substring(0, newlineIdx).Trim();
            if (title.Length > 30)
                title = title.Substring(0, 30) + "…";

            _activeSession.Title = title;
            PopulateSessionComboBox();
            SaveCurrentSession();
            Logger.Info($"会话标题自动更新为: {title}");
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

                    // 清空并加载消息
                    _messages.Clear();
                    _conversationHistory.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                foreach (var msg in _activeSession.Messages)
                {
                    msg.IsStreaming = false;
                    lock (_lock)
                    {
                        _messages.Add(msg);
                    }
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
                        lock (_lock)
                        {
                            _conversationHistory.Add(new ChatApiMessage
                            {
                                Role = msg.Role,
                                Content = apiContent,
                            });
                        }
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
                    _conversationHistory.Clear();
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
                }
                _activeSession.Messages.Add(welcomeMsg);

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
            Logger.Info("创建新会话");
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateNewChat 异常: {ex.Message}", ex);
                StatusLabel.Text = $"创建新会话失败: {ex.Message}";
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
                    _conversationHistory.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                if (_activeSession != null)
                {
                    foreach (var msg in _activeSession.Messages)
                    {
                        msg.IsStreaming = false;
                        lock (_lock) { _messages.Add(msg); }
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
                            lock (_lock)
                            {
                                _conversationHistory.Add(new ChatApiMessage
                                {
                                    Role = msg.Role,
                                    Content = apiContent,
                                });
                            }
                        }
                    }
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
                lock (_lock)
                {
                    _messages.Clear();
                    _conversationHistory.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                }

                if (_activeSession != null)
                {
                    _activeSession.Messages.Clear();
                    _activeSession.Title = "新对话";
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
                _activeSession?.Messages.Add(welcomeMsg);

                PopulateSessionComboBox();
                SaveCurrentSession();

                // 清空附件列表
                ClearAttachedFiles();

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();
                Logger.Info("已清空当前会话消息");
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
