using DeepSeek_v4_for_VisualStudio.Models;
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
            string? toolChoice = null)
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
                MaxTokens = maxTokens
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
            {
                Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // ── 缓冲聚合：减少 yield return 迭代次数 ──
            var contentBatch = new StringBuilder(512);
            const int ContentFlushThreshold = 100;

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)  // 替代 EndOfStream
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
                catch (JsonException) { continue; }

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

            // 流结束，刷出残余
            if (contentBatch.Length > 0)
                yield return contentBatch.ToString();
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