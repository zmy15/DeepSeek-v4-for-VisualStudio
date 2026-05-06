using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 消息发送与 API 交互：SendMessage、联网搜索、流式处理、搜索优化等。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Private Methods - API Interaction

        #pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件处理，符合 WPF 模式
        private async void SendMessage()
        {
            lock (_lock)
            {
                if (_isGenerating) return;
                _isGenerating = true;
            }

            try
            {

            var userText = InputTextBox.Text?.Trim();
            // 允许仅上传图片/文件而不输入文字，此时 userText 可为空
            bool hasAttachments = _attachedFilePaths.Count > 0;
            if (string.IsNullOrEmpty(userText) && !hasAttachments)
            {
                lock (_lock) { _isGenerating = false; }
                return;
            }

            // 校验 API 密钥
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
                var warningMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = ApiKeyMissingMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(warningMsg);
                AddMessagesHtml("assistant", ApiKeyMissingMessage);
                UpdateBrowser();
                StatusLabel.Text = "⚠️ 请先配置 API 密钥 (工具 → 选项 → DeepSeek Chat)";
                lock (_lock) { _isGenerating = false; }
                return;
            }

            // 热重载 API 服务
            InitializeApiService();
            if (_apiService == null)
            {
                lock (_lock) { _isGenerating = false; }
                return;
            }

            InputTextBox.Text = string.Empty;

            // ── 解析上传的文件 ──
            string fileContext = string.Empty;
            List<string> attachedFileNames = new();
            List<FileParseResult> parseResults = new();

            if (_attachedFilePaths.Count > 0)
            {
                StatusLabel.Text = "正在解析文件…";
                parseResults = await FileParserService.ParseFilesAsync(_attachedFilePaths);
                attachedFileNames = parseResults
                    .Where(r => r.Success)
                    .Select(r => r.FileName)
                    .ToList();

                fileContext = FileParserService.FormatParseResultsForContext(parseResults);
                if (!string.IsNullOrEmpty(fileContext))
                {
                    Logger.Info($"文件解析完成: {attachedFileNames.Count} 个文件");
                }
            }

            // ── 构建完整的用户消息内容 ──
            // UI 显示内容：用户文本 + 文件/OCR 提示
            string userDisplayContent = userText ?? string.Empty;
            if (string.IsNullOrEmpty(userDisplayContent) && attachedFileNames.Count > 0)
            {
                userDisplayContent = $"[已上传 {attachedFileNames.Count} 个文件]";
            }
            else if (!string.IsNullOrEmpty(userDisplayContent) && attachedFileNames.Count > 0)
            {
                // 有文字 + 有文件，保持文字不变（文件名已通过 AttachedFileNames 展示）
            }

            // AI 上下文内容：文件解析结果 + 用户文本
            string fullUserContent;
            if (!string.IsNullOrEmpty(fileContext) && !string.IsNullOrEmpty(userText))
            {
                fullUserContent = fileContext + "\n" + userText;
            }
            else if (!string.IsNullOrEmpty(fileContext))
            {
                fullUserContent = fileContext + "\n请分析以上文件内容。";
            }
            else
            {
                fullUserContent = userText ?? string.Empty;
            }

            // ── 添加用户消息 ──
            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = userDisplayContent,
                AttachedFileNames = attachedFileNames,
                AttachedFiles = parseResults,
                Timestamp = DateTime.Now,
            };
            lock (_lock)
            {
                _messages.Add(userMsg);
                _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = fullUserContent });
            }

            // ── 清空附件列表 ──
            ClearAttachedFiles();

            // 自动设置会话标题（使用第一条用户消息）
            AutoTitleSession();

            // ── 创建助手消息占位 ──
            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ReasoningContent = string.Empty,
                Timestamp = DateTime.Now,
                IsStreaming = true,
                IsRendered = false,
            };
            int assistantMsgIndex;
            lock (_lock)
            {
                _messages.Add(assistantMsg);
                assistantMsgIndex = _messages.Count - 1;
            }

            // ── 批量构建 HTML（用户消息 + 助手占位），仅调用一次 UpdateBrowser 避免竞态重复渲染 ──
            // 对于用户消息，只显示用户的原始文本 + 可折叠文件块
            AddMessagesHtml("user", userDisplayContent, null, parseResults);
            AddMessagesHtml("assistant", string.Empty);
            UpdateBrowser();

            _isGenerating = true;
            UpdateButtonsState();

            bool isWebSearchEnabled = _webSearchEngine != "Off";
            StatusLabel.Text = isWebSearchEnabled ? "正在联网搜索…" : "DeepSeek 思考中…";

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

            // ── 联网搜索（在 API 调用之前执行） ──
            string searchContext = string.Empty;
            List<WebSearchResult> capturedSearchResults = new List<WebSearchResult>();
            string? engineSwitchNote = null; // 引擎切换原因提示
            if (isWebSearchEnabled && _webSearchService != null)
            {
                // ── 热重载 API Key（支持不重启生效） ──
                ApplyWebSearchConfig();
                // ── 检查百度 API Key ──
                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = "⚠️ 请先配置百度 API Key (工具→选项→DeepSeek Chat→Web Search)";
                    assistantMsg.Content = "⚠️ **百度搜索未配置**\n\n请通过菜单 **工具 → 选项 → DeepSeek Chat → Web Search** 配置百度千帆 API Key。\n\n获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n\n也可以切换到 DuckDuckGo 搜索（免费，无需 Key）。\n\n";
                    assistantMsg.IsStreaming = false;
                    _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: true);
                    _isGenerating = false;
                    UpdateButtonsState();
                    StatusLabel.Text = "⚠️ 百度 API Key 未配置";
                    return;
                }

                // ── 时间词语替换（如"今天"→具体日期） ──
                string timeAwareQuery = ResolveTimeExpressions(userText!);

                // ── 如果有附件，先用 AI 从附件中提取关键信息用于搜索优化 ──
                string searchOptimizationInput = timeAwareQuery;
                if (!string.IsNullOrEmpty(fileContext) && _apiService != null)
                {
                    try
                    {
                        StatusLabel.Text = "AI 正在从附件中提取关键信息…";
                        string? extractedKeyInfo = await ExtractKeyInfoForSearchAsync(
                            fileContext, userText!, _currentStreamingCts.Token);
                        if (!string.IsNullOrWhiteSpace(extractedKeyInfo))
                        {
                            searchOptimizationInput = extractedKeyInfo + "\n用户问题：" + timeAwareQuery;
                            Logger.Info($"从附件提取关键信息成功 ({extractedKeyInfo.Length} 字符)，用于搜索优化");
                            StatusLabel.Text = "已提取附件关键信息，正在优化搜索词…";
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"从附件提取关键信息失败，使用原始查询: {ex.Message}");
                        // 提取失败不影响流程，使用原始查询继续
                    }
                }

                // ── AI 优化搜索查询 ──
                string optimizedQuery = timeAwareQuery;
                string? searchRecency = null;

                try
                {

                    if (_apiService != null)
                    {
                        try
                        {
                            StatusLabel.Text = "AI 正在优化搜索词…";
                            bool isBaidu = _webSearchEngine == "Baidu";
                            var optimization = await OptimizeSearchQueryAsync(searchOptimizationInput, _currentStreamingCts.Token, isBaidu);
                            if (optimization != null && !string.IsNullOrWhiteSpace(optimization.SearchQuery) && optimization.NeedSearch)
                            {
                                optimizedQuery = optimization.SearchQuery;
                                searchRecency = optimization.SearchRecency;
                                Logger.Info($"AI 优化搜索词: \"{userText}\" → \"{optimizedQuery}\", recency={searchRecency}");
                                StatusLabel.Text = $"搜索词已优化: \"{optimizedQuery}\"";
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"搜索词优化失败，使用原始查询: {ex.Message}");
                            // 优化失败不影响流程，使用原始查询
                        }
                    }

                    var searchResults = await _webSearchService.SearchAsync(optimizedQuery, _currentStreamingCts.Token, searchRecency);
                    capturedSearchResults = searchResults;
                    if (searchResults.Count > 0)
                    {
                        string providerLabel = _webSearchService.ActiveProvider == SearchProvider.Baidu
                            ? "百度搜索" : "DuckDuckGo";
                        StatusLabel.Text = $"已通过 {providerLabel} 获取 {searchResults.Count} 条搜索结果，正在抓取网页内容…";

                        // 在助手消息中显示搜索状态
                        assistantMsg.Content = $"🔍 已联网搜索到 {searchResults.Count} 条结果（{providerLabel}），正在抓取网页内容…\n\n";
                        _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                        // ── 抓取网页内容增强上下文（await 确保完成后才构建 AI 上下文） ──
                        await EnrichSearchContextAsync(searchResults, _currentStreamingCts.Token);
                        searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);

                        Logger.Info($"联网搜索完成，通过 {providerLabel} 获取 {searchResults.Count} 条结果");
                    }
                    else
                    {
                        // 检查是否是百度额度耗尽
                        if (_webSearchService.IsBaiduQuotaExhausted)
                        {
                            engineSwitchNote = "⚠️ 百度搜索免费额度已用尽，本次已自动切换至 DuckDuckGo。请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待次日重置。";
                            StatusLabel.Text = "⚠️ 百度搜索额度已耗尽，已自动切换至 DuckDuckGo";
                            assistantMsg.Content = "⚠️ 百度搜索免费额度已用尽，已自动切换至 DuckDuckGo 搜索…\n\n";
                            _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                            // 立即用 DuckDuckGo 重试（使用优化后的搜索词）
                            searchResults = await _webSearchService.SearchAsync(optimizedQuery, _currentStreamingCts.Token);
                            capturedSearchResults = searchResults;
                            if (searchResults.Count > 0)
                            {
                                StatusLabel.Text = $"已通过 DuckDuckGo 获取 {searchResults.Count} 条结果，正在抓取网页内容…";
                                assistantMsg.Content = $"🔍 已通过 DuckDuckGo 搜索到 {searchResults.Count} 条结果，正在抓取网页内容…\n\n";
                                _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                                await EnrichSearchContextAsync(searchResults, _currentStreamingCts.Token);
                                searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);
                            }
                        }
                        else
                        {
                            StatusLabel.Text = "未找到搜索结果，使用内置知识回复…";
                        }
                        Logger.Info("联网搜索未找到结果");
                    }
                }
                catch (ApiKeyInvalidException ex)
                {
                    // 百度 Key 无效 → 与 DeepSeek API Key 无效相同逻辑：直接报错并停止，不静默回退
                    Logger.Error($"[Render] 百度 API Key 无效", ex);
                    assistantMsg.Content = "⚠️ 百度搜索 API Key 无效，请检查配置：工具 → 选项 → DeepSeek Chat → Web Search。\n\n获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n\n也可以切换到 DuckDuckGo 搜索（免费，无需 Key）。";
                    assistantMsg.IsStreaming = false;
                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    lock (_lock) { _messages.Remove(assistantMsg); } // 不保存到对话记录
                    lock (_lock) { _isGenerating = false; }
                    UpdateButtonsState();
                    StatusLabel.Text = "⚠️ 百度 API Key 无效";
                    _currentStreamingCts?.Cancel();
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error($"联网搜索异常: {ex.Message}", ex);
                    StatusLabel.Text = "搜索失败，使用内置知识回复…";
                }
            }

            // ── 引擎切换提示：若用户选择百度但实际使用了 DuckDuckGo，记录原因 ──
            if (string.IsNullOrEmpty(engineSwitchNote) &&
                _webSearchEngine == "Baidu" &&
                _webSearchService != null &&
                _webSearchService.ActiveProvider == SearchProvider.DuckDuckGo)
            {
                engineSwitchNote = "⚠️ 百度搜索未返回结果，本次已自动切换至 DuckDuckGo。";
            }
            if (!string.IsNullOrEmpty(engineSwitchNote))
            {
                _pendingWarnings.Add(engineSwitchNote!);
            }

            try
            {
                var requestMessages = BuildRequestMessages(searchContext);
                var reasoningBuffer = new StringBuilder();
                var contentBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                var apiService = _apiService!;
                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = "DeepSeek 深度思考中…";

                        // 定期更新思考面板
                        if (reasoningBuffer.Length - lastReasoningLength >= 80)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            await UpdateStreamingMessageAsync(assistantMsgIndex,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                    else
                    {
                        if (reasoningBuffer.Length > 0 && lastReasoningLength < reasoningBuffer.Length)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                        }

                        contentBuffer.Append(chunk);
                        streamRenderTick += chunk.Length;
                        StatusLabel.Text = "DeepSeek 回复中...";

                        if (streamRenderTick >= StreamRenderInterval)
                        {
                            streamRenderTick = 0;
                            assistantMsg.Content = contentBuffer.ToString();
                            await UpdateStreamingMessageAsync(assistantMsgIndex,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                }

                // ── 流式完成：渲染最终 Markdown ──
                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Render] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    assistantMsgIndex,
                    contentBuffer.ToString(),
                    reasoningBuffer.ToString());

                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

                // ── 注入搜索结果链接卡片到 AI 消息上方 ──
                if (capturedSearchResults.Count > 0)
                {
                    string providerLabel = _webSearchService?.ActiveProvider == SearchProvider.Baidu
                        ? "百度搜索" : "DuckDuckGo";
                    string searchCardJs = ChatHtmlService.BuildSearchResultsInjectionJs(
                        assistantMsgIndex, capturedSearchResults, providerLabel);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(searchCardJs);
                }

                _conversationHistory.Add(new ChatApiMessage { Role = "assistant", Content = contentBuffer.ToString() });

                // 后台持久化
                var capturedMsg = assistantMsg;
                _ = Task.Run(() =>
                {
                    capturedMsg.HtmlContent = "rendered";
                    capturedMsg.IsRendered = true;
                    SaveCurrentSession();
                });
            }
            catch (ApiKeyInvalidException ex)
            {
                Logger.Error($"[Render] API Key 无效", ex);
                assistantMsg.Content = $"⚠️ {ex.Message}";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                lock (_lock) { _messages.Remove(assistantMsg); } // 不保存到对话记录
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[Render] 用户停止生成");
                assistantMsg.Content += "\n\n*[已停止]*";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
            {
                Logger.Error($"[Render] API 认证失败", ex);
                assistantMsg.Content = "⚠️ DeepSeek API Key 无效或已过期，请通过 工具 → 选项 → DeepSeek Chat 重新配置。\n获取密钥：https://platform.deepseek.com/api_keys";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                lock (_lock) { _messages.Remove(assistantMsg); } // 不保存到对话记录
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] API 出错", ex);
                assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            finally
            {
                assistantMsg.IsStreaming = false;
                lock (_lock)
                {
                    _isGenerating = false;
                }
                StatusLabel.Text = string.Empty;
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
            }
            }
            catch (Exception ex)
            {
                // 顶层兜底：捕获任何未预期的异常
                Logger.Error($"[Render] SendMessage 未处理异常: {ex.Message}", ex);
                lock (_lock)
                {
                    _isGenerating = false;
                }
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"发生错误: {ex.Message}";
                }
                catch { }
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 从已解析的附件内容中提取关键信息，用于优化联网搜索查询。
        /// 当用户上传文件并开启联网搜索时调用，在搜索优化之前执行。
        /// 使用 AI（非流式）从文件内容中提取核心主题、技术关键词、专有名词等，
        /// 返回简洁的摘要供搜索优化阶段使用。
        /// </summary>
        /// <param name="fileContent">已解析的文件内容（由 FileParserService 生成）。</param>
        /// <param name="userQuestion">用户的原始问题。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>提取的关键信息摘要；失败或无需提取时返回 null。</returns>
        private async Task<string?> ExtractKeyInfoForSearchAsync(string fileContent, string userQuestion, CancellationToken ct)
        {
            if (_apiService == null || string.IsNullOrWhiteSpace(fileContent))
                return null;

            // 截断过长的文件内容，避免 token 消耗过多（取前 8000 字符）
            string truncatedContent = fileContent.Length > 8000
                ? fileContent.Substring(0, 8000) + "\n...[内容已截断]"
                : fileContent;

            var extractionPrompt = AiPrompts.BuildFileExtractionPrompt(userQuestion, truncatedContent);

            try
            {
                var extractionMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = AiPrompts.FileExtractionSystem },
                    new ChatApiMessage { Role = "user", Content = extractionPrompt },
                };

                Logger.Info("开始从附件提取关键信息用于搜索优化");
                var rawResponse = await _apiService.CompleteAsync(extractionMessages, ct);
                Logger.Info($"附件关键信息提取原始响应: {rawResponse}");

                string result = rawResponse?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(result) ||
                    result.Equals("NO_INFO", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // 截断过长结果
                if (result.Length > 500)
                    result = result.Substring(0, 500);

                return result;
            }
            catch (OperationCanceledException)
            {
                Logger.Info("附件关键信息提取已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"附件关键信息提取异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 调用 AI 分析用户问题和上下文，生成优化的搜索关键词。
        /// 百度引擎：返回严格 JSON（含 search_recency 时效过滤）。
        /// DuckDuckGo：仅返回优化后的纯文本关键词。
        /// </summary>
        /// <param name="userQuery">用户原始问题</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="isBaiduSearch">是否使用百度搜索（true=JSON格式，false=纯文本关键词）</param>
        /// <returns>优化后的搜索查询对象，失败返回 null</returns>
        private async Task<SearchQueryOptimization?> OptimizeSearchQueryAsync(string userQuery, CancellationToken ct, bool isBaiduSearch = true)
        {
            if (_apiService == null)
                return null;

            // ── 构建优化提示词 ──
            string contextSummary = string.Empty;
            if (_conversationHistory.Count > 1)
            {
                // 取最近几条用户消息作为上下文
                var recent = _conversationHistory
                    .Where(m => m.Role == "user")
                    .Reverse()
                    .Take(3)
                    .Reverse()
                    .Select(m => m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content);
                contextSummary = string.Join(" | ", recent);
            }

            string contextLine = string.IsNullOrWhiteSpace(contextSummary)
                ? $"用户问题：{userQuery}"
                : $"对话上下文：{contextSummary}\n用户问题：{userQuery}";

            string optimizationPrompt = AiPrompts.BuildSearchOptimizationPrompt(contextLine, isBaiduSearch);
            string systemPrompt = AiPrompts.GetSearchOptimizationSystemPrompt(isBaiduSearch);

            try
            {
                var optimizationMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = systemPrompt },
                    new ChatApiMessage { Role = "user", Content = optimizationPrompt },
                };

                Logger.Info($"开始 AI 搜索词优化 ({(isBaiduSearch ? "百度" : "DuckDuckGo")})，原始查询: \"{userQuery}\"");
                var rawResponse = await _apiService.CompleteAsync(optimizationMessages, ct);
                Logger.Info($"AI 搜索词优化原始响应: {rawResponse}");

                if (isBaiduSearch)
                {
                    // ── 百度：校验 JSON ──
                    return ParseAndValidateSearchOptimization(rawResponse, userQuery);
                }
                else
                {
                    // ── DuckDuckGo：纯文本关键词 ──
                    string keyword = rawResponse?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(keyword) ||
                        keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SearchQueryOptimization
                        {
                            SearchQuery = userQuery,
                            NeedSearch = keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase) ? false : true,
                        };
                    }
                    // 清理可能的多余内容（AI 偶尔会返回带引号或前缀的文字）
                    keyword = keyword.Trim('"', '\'', '`');
                    if (keyword.Length > 72)
                        keyword = keyword.Substring(0, 72);
                    return new SearchQueryOptimization
                    {
                        SearchQuery = keyword,
                        NeedSearch = true,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("搜索词优化已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"搜索词优化异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 解析并校验 AI 返回的搜索优化 JSON。
        /// 若 JSON 不合法或关键字段缺失，回退到原始查询。
        /// </summary>
        private static SearchQueryOptimization ParseAndValidateSearchOptimization(string rawResponse, string fallbackQuery)
        {
            try
            {
                // 尝试提取 JSON 部分（AI 可能在 JSON 前后附加了文字）
                string jsonStr = rawResponse.Trim();

                // 去掉可能的 markdown 代码块标记
                if (jsonStr.StartsWith("```"))
                {
                    int endOfFirstLine = jsonStr.IndexOf('\n');
                    if (endOfFirstLine > 0)
                        jsonStr = jsonStr.Substring(endOfFirstLine + 1);
                    if (jsonStr.EndsWith("```"))
                        jsonStr = jsonStr.Substring(0, jsonStr.Length - 3);
                    jsonStr = jsonStr.Trim();
                }

                var result = System.Text.Json.JsonSerializer.Deserialize<SearchQueryOptimization>(jsonStr,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });

                // ── 校验 ──
                if (result == null)
                    throw new InvalidOperationException("JSON 解析结果为 null");

                if (string.IsNullOrWhiteSpace(result.SearchQuery))
                {
                    Logger.Info("AI 优化搜索词为空，使用原始查询");
                    return new SearchQueryOptimization
                    {
                        SearchQuery = fallbackQuery,
                        NeedSearch = result.NeedSearch,
                    };
                }

                // 校验 recency 值
                var validRecencies = new HashSet<string> { "week", "month", "semiyear", "year" };
                if (result.SearchRecency != null && !validRecencies.Contains(result.SearchRecency))
                {
                    Logger.Info($"无效的 search_recency 值: {result.SearchRecency}，已忽略");
                    result.SearchRecency = null;
                }

                Logger.Info($"搜索词优化成功: \"{fallbackQuery}\" → \"{result.SearchQuery}\"");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Info($"搜索优化 JSON 解析失败: {ex.Message}，使用原始查询 \"{fallbackQuery}\"");
                return new SearchQueryOptimization
                {
                    SearchQuery = fallbackQuery,
                    NeedSearch = true,
                };
            }
        }

        /// <summary>
        /// 将用户输入中的时间词语替换为具体日期。
        /// 例如："今天" → "2026-05-06"，"本周" → "2026-05-04 至 2026-05-10"。
        /// </summary>
        private static string ResolveTimeExpressions(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return query;

            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");
            string yesterday = now.AddDays(-1).ToString("yyyy-MM-dd");
            string tomorrow = now.AddDays(1).ToString("yyyy-MM-dd");
            string thisWeekStart = now.AddDays(-(int)now.DayOfWeek + 1).ToString("yyyy-MM-dd");
            string thisWeekEnd = now.AddDays(7 - (int)now.DayOfWeek).ToString("yyyy-MM-dd");
            string thisMonth = now.ToString("yyyy年M月");
            string lastMonth = now.AddMonths(-1).ToString("yyyy年M月");
            string thisYear = now.ToString("yyyy年");

            var result = query;

            // 精确匹配（长词优先，避免"今天"匹配到"今天天气"中的一部分）
            var replacements = new Dictionary<string, string>
            {
                ["今天"] = today,
                ["今日"] = today,
                ["昨天"] = yesterday,
                ["昨日"] = yesterday,
                ["明天"] = tomorrow,
                ["明日"] = tomorrow,
                ["本周"] = $"{thisWeekStart} 至 {thisWeekEnd}",
                ["这周"] = $"{thisWeekStart} 至 {thisWeekEnd}",
                ["这个月"] = thisMonth,
                ["本月"] = thisMonth,
                ["上个月"] = lastMonth,
                ["上月"] = lastMonth,
                ["今年"] = thisYear,
                ["当前日期"] = today,
                ["目前"] = $"最新(截至{today})",
                ["最近"] = $"最近(截至{today})",
                ["最新"] = $"最新(截至{today})",
                ["近期"] = $"近期(截至{today})",
                ["最近一周"] = $"最近一周({thisWeekStart} 至 {thisWeekEnd})",
                ["最近一个月"] = $"最近一个月({lastMonth} 至 {thisMonth})",
                ["最近几天"] = $"最近几天({yesterday} 至 {today})",
                ["前几天"] = $"前几天({yesterday} 至 {today})",
            };

            foreach (var kvp in replacements)
            {
                result = result.Replace(kvp.Key, kvp.Value);
            }

            if (result != query)
                Logger.Info($"时间词语解析: \"{query}\" → \"{result}\"");

            return result;
        }

        /// <summary>
        /// 异步抓取搜索结果中前几条 URL 的网页内容，用于增强搜索上下文。
        /// 这是"尽力而为"的后台操作，失败不影响主流程。
        /// </summary>
        private async Task EnrichSearchContextAsync(List<WebSearchResult> results, CancellationToken ct)
        {
            if (_webSearchService == null || results.Count == 0) return;

            try
            {
                // 只抓取前6条结果的网页内容
                int fetchCount = Math.Min(6, results.Count);
                for (int i = 0; i < fetchCount; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        string? pageContent = await _webSearchService.FetchWebPageContentAsync(results[i].Url, ct);
                        if (!string.IsNullOrWhiteSpace(pageContent))
                        {
                            // 将提取的网页内容追加到结果的 Snippet 中
                            string enriched = results[i].Snippet +
                                $"\n[网页内容摘要: {TruncateText(pageContent!, 300)}]";
                            results[i].Snippet = TruncateText(enriched, 800);
                            Logger.Info($"网页内容抓取成功: {results[i].Url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"网页内容抓取跳过 ({results[i].Url}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"网页内容增强失败: {ex.Message}", ex);
            }
        }

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 构建发送给 API 的消息列表。
        /// 当启用联网搜索时，将搜索结果作为系统消息注入到对话历史之前。
        /// </summary>
        /// <param name="searchContext">联网搜索的结果上下文，为空则不注入。</param>
        private List<ChatApiMessage> BuildRequestMessages(string searchContext = "")
        {
            var messages = new List<ChatApiMessage>();

            // ── 系统提示词 ──
            string systemPrompt = _options?.SystemPrompt ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
            }

            // ── 注入联网搜索结果作为系统消息 ──
            if (!string.IsNullOrWhiteSpace(searchContext))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = searchContext });
            }

            // ── 对话历史 ──
            messages.AddRange(_conversationHistory.Select(m => new ChatApiMessage
            {
                Role = m.Role,
                Content = m.Content,
            }));

            return messages;
        }

        private void StopGeneration()
        {
            try
            {
                lock (_lock)
                {
                    _currentStreamingCts?.Cancel();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"StopGeneration 异常: {ex.Message}", ex);
            }
        }

        #pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件处理，符合 WPF 模式
        private async void ClearConversation()
        {
            try
            {
                lock (_lock)
                {
                    if (_isGenerating)
                    {
                        _currentStreamingCts?.Cancel();
                        _isGenerating = false;
                    }
                }

                UpdateButtonsState();

                ClearCurrentSessionMessages();
                Logger.Info("清空对话完成");
            }
            catch (Exception ex)
            {
                Logger.Error($"ClearConversation 异常: {ex.Message}", ex);
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"清空失败: {ex.Message}";
                }
                catch { }
            }
        }
        #pragma warning restore VSTHRD100

        #endregion

        #region Private Methods - Helpers

        private void UpdateButtonsState()
        {
            SendButton.IsEnabled = !_isGenerating;
            StopButton.Visibility = _isGenerating ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Visibility = _isGenerating ? Visibility.Collapsed : Visibility.Visible;
            InputTextBox.IsReadOnly = _isGenerating;
        }

        #endregion
    }
}
