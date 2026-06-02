using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    public class DeepSeekApiService : IDeepSeekApiService
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://api.deepseek.com";
        private const string ChatEndpoint = "/chat/completions";
        private const string FimBaseUrl = "https://api.deepseek.com/beta";
        private const string FimEndpoint = "/completions";

        private string _model;
        private bool _thinkingEnabled = true;
        private string _reasoningEffort = "high";

        /// <summary>
        /// 最近一次 API 调用的 Usage 信息（含 Cache 命中统计）。
        /// 流式调用结束后更新，非流式调用后立即可用。
        /// </summary>
        public DeepSeekUsage? LastUsage { get; private set; }

        /// <summary>
        /// 累计 Cache 统计（跨所有 API 调用汇总，含 Agent 内部调用）。
        /// 在每次 API 调用后自动累加。调用 <see cref="ResetAccumulatedStats"/> 重置。
        /// </summary>
        public long TotalCacheHitTokens { get; private set; }
        public long TotalCacheMissTokens { get; private set; }
        public long TotalPromptTokens { get; private set; }
        public long TotalCompletionTokens { get; private set; }

        /// <summary>
        /// 累计 Cache 命中率（0.0 ~ 1.0）。
        /// </summary>
        public double TotalCacheHitRate => TotalCacheHitTokens + TotalCacheMissTokens > 0
            ? (double)TotalCacheHitTokens / (TotalCacheHitTokens + TotalCacheMissTokens)
            : 0;

        /// <summary>
        /// 重置累计 Cache 统计（新会话开始时调用）。
        /// </summary>
        public void ResetAccumulatedStats()
        {
            TotalCacheHitTokens = 0;
            TotalCacheMissTokens = 0;
            TotalPromptTokens = 0;
            TotalCompletionTokens = 0;
        }

        /// <summary>
        /// 从持久化数据恢复累计 Cache 统计（重启后调用）。
        /// </summary>
        public void RestoreAccumulatedStats(long hitTokens, long missTokens, long promptTokens, long completionTokens)
        {
            TotalCacheHitTokens = hitTokens;
            TotalCacheMissTokens = missTokens;
            TotalPromptTokens = promptTokens;
            TotalCompletionTokens = completionTokens;
        }

        /// <summary>
        /// 累加一次 API 调用的 Usage 统计到累计值。
        /// </summary>
        private void AccumulateStats(DeepSeekUsage usage)
        {
            TotalCacheHitTokens += usage.PromptCacheHitTokens;
            TotalCacheMissTokens += usage.PromptCacheMissTokens;
            TotalPromptTokens += usage.PromptTokens;
            TotalCompletionTokens += usage.CompletionTokens;
        }

        public DeepSeekApiService(string apiKey, string model = "deepseek-v4-pro")
        {
            _model = model;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromMinutes(5)
            };
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        /// <summary>
        /// 测试用构造函数 — 接受外部 HttpClient（用于 Mock HTTP 处理程序）。
        /// </summary>
        public DeepSeekApiService(HttpClient httpClient, string model = "deepseek-v4-pro")
        {
            _model = model;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (_httpClient.BaseAddress == null)
                _httpClient.BaseAddress = new Uri(BaseUrl);
        }

        public void UpdateModel(string model) => _model = model;
        public void ConfigureThinking(bool enabled, string effort = "high")
        {
            _thinkingEnabled = enabled;
            _reasoningEffort = effort;
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            int? maxTokens = null,
            string? toolChoice = null,
            double? temperature = null)
        {
            // toolChoice 优先级: 显式传入 > 有 tools 时 auto > null(不发送)
            string? effectiveToolChoice = toolChoice
                ?? (tools != null && tools.Count > 0 ? "auto" : null);

            var request = new DeepSeekChatRequest
            {
                Model = _model,
                Messages = new List<ChatApiMessage>(messages),
                Stream = true,
                Thinking = new ThinkingControl { Type = _thinkingEnabled ? "enabled" : "disabled" },
                ReasoningEffort = _thinkingEnabled ? _reasoningEffort : null,
                Tools = tools,
                ToolChoice = effectiveToolChoice,
                MaxTokens = maxTokens,
                Temperature = temperature
            };

            // ── 消息清理：防止无效消息导致 HTTP 400 ──
            // DeepSeek API 对消息格式有严格要求：
            // 1. tool 消息必须有 tool_call_id
            // 2. assistant 消息有 tool_calls 时可以没有 content，但不能既无 content 又无 tool_calls
            // 3. 不能有连续的相同 role 消息（user-user, assistant-assistant）→ 合并而非丢弃
            var cleanedMessages = new List<ChatApiMessage>();
            string? lastRole = null;
            int removedCount = 0;
            int mergedCount = 0;
            foreach (var msg in request.Messages)
            {
                // ── 规则 1：tool 消息必须有 tool_call_id ──
                if (msg.Role == "tool" && string.IsNullOrEmpty(msg.ToolCallId))
                {
                    Logger.Warn($"[API] 移除无效 tool 消息：缺少 tool_call_id (content={msg.Content?.Truncate(80)})");
                    removedCount++;
                    continue;
                }

                // ── 规则 2：assistant 消息既无 content 又无 tool_calls → 移除 ──
                if (msg.Role == "assistant"
                    && string.IsNullOrEmpty(msg.Content)
                    && (msg.ToolCalls == null || msg.ToolCalls.Count == 0))
                {
                    Logger.Warn($"[API] 移除无效 assistant 消息：无 content 且无 tool_calls");
                    removedCount++;
                    continue;
                }

                // ── 规则 3：assistant 消息有 tool_calls 但缺少 reasoning_content → 补全 ──
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0 && msg.ReasoningContent == null)
                {
                    Logger.Warn("[API] assistant 消息包含 tool_calls 但缺少 ReasoningContent — 注入空字符串以避免 400");
                    msg.ReasoningContent = string.Empty;
                }

                // ── 规则 4：防止连续相同 role（DeepSeek API 要求 user/assistant 交替）──
                // tool 消息连续出现是合法的（多个工具调用结果），不检查
                // 对于连续 user 或 assistant 消息，合并内容而非丢弃
                if (lastRole != null && msg.Role == lastRole
                    && (msg.Role == "user" || msg.Role == "assistant"))
                {
                    if (cleanedMessages.Count > 0)
                    {
                        var lastMsg = cleanedMessages[cleanedMessages.Count - 1];
                        string existingContent = lastMsg.Content ?? string.Empty;
                        string newContent = msg.Content ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(newContent))
                        {
                            // ── 合并内容：用分隔线连接 ──
                            lastMsg.Content = string.IsNullOrWhiteSpace(existingContent)
                                ? newContent
                                : existingContent + "\n\n---\n\n" + newContent;
                            Logger.Warn($"[API] 合并连续的 {msg.Role} 消息（内容已拼接，防止数据丢失）");
                        }
                        else
                        {
                            Logger.Warn($"[API] 跳过连续的空 {msg.Role} 消息");
                        }

                        // 如果后者有 reasoning_content，保留后者
                        if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
                            lastMsg.ReasoningContent = msg.ReasoningContent;
                        // 如果后者有 tool_calls，保留后者
                        if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                            lastMsg.ToolCalls = msg.ToolCalls;

                        mergedCount++;
                        continue;
                    }
                }

                cleanedMessages.Add(msg);
                lastRole = msg.Role;
            }

            if (removedCount > 0 || mergedCount > 0)
            {
                var parts = new List<string>();
                if (removedCount > 0) parts.Add($"移除了 {removedCount} 条无效消息");
                if (mergedCount > 0) parts.Add($"合并了 {mergedCount} 条连续消息");
                Logger.Warn($"[API] 消息清理完成：{string.Join("，", parts)}，剩余 {cleanedMessages.Count} 条");
                request.Messages = cleanedMessages;
            }

            // ── 预序列化请求体，供重试时复用 ──
            var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var requestBodyBytes = Encoding.UTF8.GetBytes(requestJson);

            // ── 记录请求元数据 ──
            Logger.Info($"[API] 发送请求: {requestBodyBytes.Length / 1024}KB, 消息数={request.Messages.Count}, 工具数={tools?.Count ?? 0}, maxTokens={maxTokens}");

            // ── HTTP 层重试（指数退避：1s, 2s, 4s；最多 3 次额外重试）──
            HttpResponseMessage? response = null;
            int sendAttempt = 0;
            const int maxSendAttempts = 4;
            while (sendAttempt < maxSendAttempts)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
                    {
                        Content = new ByteArrayContent(requestBodyBytes)
                    };
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    response = await _httpClient.SendAsync(
                        req,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                    break; // success
                }
                catch (HttpRequestException ex) when (sendAttempt < maxSendAttempts - 1)
                {
                    int statusCode = (int)(response?.StatusCode ?? 0);

                    // 4xx 客户端错误（除 429 限流外）不应重试——请求本身有问题，重试不会改变结果
                    if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
                    {
                        // 记录更详细的错误响应（截断到 1KB），便于定位请求字段问题
                        string respSnippet = string.Empty;
                        try
                        {
                            if (response?.Content != null)
                            {
                                var bodyBytes = await response.Content.ReadAsByteArrayAsync();
                                respSnippet = Encoding.UTF8.GetString(bodyBytes);
                                if (respSnippet.Length > 1024)
                                    respSnippet = respSnippet.Substring(0, 1024) + "…(截断)";
                            }
                        }
                        catch { }
                        Logger.Error($"[API] HTTP {statusCode} 是客户端错误，放弃重试。响应: {respSnippet}");
                        throw;
                    }

                    sendAttempt++;
                    string? responseBody = null;
                    try
                    {
                        if (response?.Content != null)
                        {
                            var bodyBytes = await response.Content.ReadAsByteArrayAsync();
                            responseBody = Encoding.UTF8.GetString(bodyBytes);
                            if (responseBody.Length > 500)
                                responseBody = responseBody.Substring(0, 500) + "…(截断)";
                        }
                    }
                    catch { }
                    response?.Dispose();
                    double backoff = Math.Pow(2, sendAttempt - 1);
                    Logger.Warn($"[API] HTTP {statusCode} 请求失败 (尝试 {sendAttempt + 1}/{maxSendAttempts})，{backoff}s 后重试…"
                        + (responseBody != null ? $"\n[API] 响应: {responseBody}" : ""));
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && sendAttempt < maxSendAttempts - 1)
                {
                    // 超时（非用户取消）
                    sendAttempt++;
                    response?.Dispose();
                    double backoff = Math.Pow(2, sendAttempt - 1);
                    Logger.Warn($"[API] 请求超时 (尝试 {sendAttempt + 1}/{maxSendAttempts})，{backoff}s 后重试…");
                    await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
                }
            }

            if (response == null)
                throw new InvalidOperationException("HTTP request failed after all retries");

            using (response)
            {
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // ── 缓冲聚合：减少 yield return 迭代次数 ──
            var contentBatch = new StringBuilder(512);
            const int ContentFlushThreshold = 100;

            // ── 取消令牌注册：当 ct 触发时释放底层流，使 ReadLineAsync 立即抛出异常 ──
            // .NET Framework 4.7.2 的 ReadLineAsync 不接受 CancellationToken，
            // 通过释放流来实现同样的中断效果。释放 SslStream 可能抛出
            // ObjectDisposedException（而非 OperationCanceledException），
            // 调用方应在 foreach 外层捕获 ObjectDisposedException 并检查取消状态。
            string? line;
            using (cancellationToken.Register(() =>
            {
                try { stream.Dispose(); } catch { }
            }))
            {
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                    continue;

                var jsonData = line.Substring(6);
                if (jsonData == "[DONE]")
                {
                    // 结束前刷出残余缓冲
                    if (contentBatch.Length > 0)
                    {
                        yield return contentBatch.ToString();
                        contentBatch.Clear();
                    }
                    yield break;
                }

                // 解析 chunk...
                string? reasoning = null;
                string? content = null;
                string? toolCallJson = null;
                string? cacheInfo = null;
                try
                {
                    var chunk = JsonSerializer.Deserialize<DeepSeekStreamChunk>(jsonData);
                    var delta = chunk?.Choices?[0]?.Delta;
                    if (delta != null)
                    {
                        reasoning = delta.ReasoningContent;
                        content = delta.Content;
                        if (delta.ToolCalls != null && delta.ToolCalls.Count > 0)
                            toolCallJson = JsonSerializer.Serialize(delta.ToolCalls);
                    }
                    if (chunk?.Usage != null)
                    {
                        LastUsage = chunk.Usage;
                        AccumulateStats(chunk.Usage);
                        cacheInfo = $"{chunk.Usage.PromptCacheHitTokens}|{chunk.Usage.PromptCacheMissTokens}|{chunk.Usage.PromptTokens}|{chunk.Usage.CompletionTokens}";
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidOperationException)
                {
                    Logger.Warn($"[API] 流式数据解析失败，跳过该 chunk: {ex.Message} (data={jsonData.Truncate(200)})");
                    continue;
                }

                // ── 元数据（thinking/tool_call）到来前先刷出已聚合的内容 ──
                bool hasMeta = !string.IsNullOrEmpty(reasoning) || !string.IsNullOrEmpty(toolCallJson);
                if (hasMeta && contentBatch.Length > 0)
                {
                    yield return contentBatch.ToString();
                    contentBatch.Clear();
                }

                if (!string.IsNullOrEmpty(reasoning)) yield return $"[THINKING]{reasoning}";
                if (!string.IsNullOrEmpty(toolCallJson)) yield return $"[TOOL_CALL]{toolCallJson}";
                if (!string.IsNullOrEmpty(cacheInfo)) yield return $"[CACHE]{cacheInfo}";

                // ── 普通内容：聚合到缓冲区，达到阈值再 yield ──
                if (!string.IsNullOrEmpty(content))
                {
                    contentBatch.Append(content);
                    if (contentBatch.Length >= ContentFlushThreshold)
                    {
                        yield return contentBatch.ToString();
                        contentBatch.Clear();
                    }
                }
            }
            } // using(ctr) — 取消令牌注册已释放

            // 流结束，刷出残余
            if (contentBatch.Length > 0)
                yield return contentBatch.ToString();
            } // using(response) — 重试块闭合
        }

        /// <summary>
        /// 非流式调用 API，用于搜索查询优化等需要快速完整响应的场景。
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>AI 返回的完整文本内容</returns>
        public async Task<string> CompleteAsync(
            IEnumerable<ChatApiMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var request = new DeepSeekChatRequest
            {
                Model = _model,
                Messages = new List<ChatApiMessage>(messages),
                Stream = false,
                Thinking = new ThinkingControl { Type = "disabled" },
                ReasoningEffort = null,
            };

            // Defensive check for non-streaming path as well
            foreach (var msg in request.Messages)
            {
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0 && msg.ReasoningContent == null)
                {
                    Logger.Warn("[API] (CompleteAsync) assistant message contains tool_calls but missing ReasoningContent — injecting empty string to avoid 400");
                    msg.ReasoningContent = string.Empty;
                }
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
            {
                Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            await ValidateResponseStatusAsync(response);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DeepSeekChatResponse>(responseJson);

            // ── 捕获 Usage 信息（含 Cache 命中统计）──
            if (result?.Usage != null)
            {
                LastUsage = result.Usage;
                AccumulateStats(result.Usage);
            }

            return result?.Choices?[0]?.Message?.Content ?? string.Empty;
        }

        /// <summary>
        /// FIM（Fill-In-the-Middle）补全调用，用于代码自动补全。
        /// 端点: POST https://api.deepseek.com/beta/completions
        /// </summary>
        /// <param name="prompt">光标前的代码（prefix）</param>
        /// <param name="suffix">光标后的代码（suffix），可选</param>
        /// <param name="maxTokens">最大生成 token 数，默认 256</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>模型生成的补全文本</returns>
        public async Task<string> FimCompletionAsync(
            string prompt,
            string? suffix = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            var request = new DeepSeekFimRequest
            {
                Model = _model,
                Prompt = prompt,
                Suffix = suffix,
                MaxTokens = maxTokens ?? 256,
                Temperature = 0.0,   // 确定性输出，适合代码补全
                Stream = false,
            };

            // FIM 使用绝对 URI 直接指向 beta 端点，避免运行时修改 BaseAddress
            // （HttpClient.BaseAddress 在首次请求后不可修改，.NET 会抛 InvalidOperationException）
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, FimBaseUrl + FimEndpoint)
            {
                Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            await ValidateResponseStatusAsync(response);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DeepSeekFimResponse>(responseJson);

            // 捕获 Usage 信息
            if (result?.Usage != null)
            {
                LastUsage = result.Usage;
                AccumulateStats(result.Usage);
            }

            return result?.Choices?[0]?.Text ?? string.Empty;
        }

        /// <summary>
        /// 验证 API Key 是否有效。发送一个最小请求，检查响应。
        /// </summary>
        /// <returns>null 表示有效，否则返回错误描述</returns>
        public async Task<string?> ValidateApiKeyAsync()
        {
            try
            {
                var request = new DeepSeekChatRequest
                {
                    Model = _model,
                    Messages = new List<ChatApiMessage>
                    {
                        new ChatApiMessage { Role = "user", Content = "hi" }
                    },
                    Stream = false,
                    MaxTokens = 1,
                    Thinking = new ThinkingControl { Type = "disabled" },
                };

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
                {
                    Content = JsonContent.Create(request, options: new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    })
                };

                using var response = await _httpClient.SendAsync(httpRequest);
                await ValidateResponseStatusAsync(response);
                return null; // 有效
            }
            catch (ApiKeyInvalidException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                return $"API 连接失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 检查 HTTP 响应状态，对认证错误抛出 ApiKeyInvalidException.
        /// </summary>
        private static async Task ValidateResponseStatusAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;

            int statusCode = (int)response.StatusCode;
            string body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(); } catch { }

            if (statusCode == 401 || statusCode == 403)
            {
                string detail = ExtractErrorMessage(body);
                throw new ApiKeyInvalidException(
                    $"DeepSeek API Key 无效或已过期 (HTTP {statusCode})。\n" +
                    $"请通过 工具 → 选项 → DeepSeek Chat 重新配置 API Key。\n" +
                    $"获取 Key: https://platform.deepseek.com/api_keys\n" +
                    (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}"));
            }

            if (statusCode == 429)
            {
                throw new ApiKeyInvalidException(
                    "DeepSeek API 请求频率超限 (HTTP 429)，请稍后重试。");
            }

            if (statusCode >= 500)
            {
                throw new ApiKeyInvalidException(
                    $"DeepSeek 服务器错误 (HTTP {statusCode})，请稍后重试。\n详情: {body}");
            }

            // 其他 4xx 客户端错误：记录正文（1KB 截断）并抛出明确异常，便于定位请求格式问题
            if (statusCode >= 400 && statusCode < 500)
            {
                string snippet = body;
                if (!string.IsNullOrEmpty(snippet) && snippet.Length > 1024)
                    snippet = snippet.Substring(0, 1024) + "…(截断)";
                Logger.Error($"[API] 深度搜索返回客户端错误 HTTP {statusCode}: {snippet}");
                throw new InvalidOperationException($"DeepSeek API 返回 HTTP {statusCode}: {ExtractErrorMessage(body)}");
            }
        }

        /// <summary>
        /// 从 API 错误响应中提取可读的错误消息。
        /// </summary>
        private static string ExtractErrorMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? string.Empty;
            }
            catch { }
            return responseBody.Length > 200 ? responseBody.Substring(0, 200) : responseBody;
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}