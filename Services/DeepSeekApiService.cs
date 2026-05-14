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
    public class DeepSeekApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://api.deepseek.com";
        private const string ChatEndpoint = "/chat/completions";

        private string _model;
        private bool _thinkingEnabled = true;
        private string _reasoningEffort = "high";

        /// <summary>
        /// 最近一次 API 调用的 Usage 信息（含 Cache 命中统计）。
        /// 流式调用结束后更新，非流式调用后立即可用。
        /// </summary>
        public DeepSeekUsage? LastUsage { get; private set; }

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

        public void UpdateModel(string model) => _model = model;
        public void ConfigureThinking(bool enabled, string effort = "high")
        {
            _thinkingEnabled = enabled;
            _reasoningEffort = effort;
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = new DeepSeekChatRequest
            {
                Model = _model,
                Messages = new List<ChatApiMessage>(messages),
                Stream = true,
                Thinking = new ThinkingControl { Type = _thinkingEnabled ? "enabled" : "disabled" },
                ReasoningEffort = _thinkingEnabled ? _reasoningEffort : null,
                Tools = tools,
                ToolChoice = tools != null && tools.Count > 0 ? "auto" : null
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

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                    continue;

                var jsonData = line.Substring(6);
                if (jsonData == "[DONE]")
                    yield break;

                // 解析结果存入局部变量
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

                        // ── 工具调用增量 ──
                        if (delta.ToolCalls != null && delta.ToolCalls.Count > 0)
                        {
                            toolCallJson = JsonSerializer.Serialize(delta.ToolCalls);
                        }
                    }

                    // ── 捕获 Usage 信息（含 Cache 命中统计，通常出现在最后一个 chunk）──
                    if (chunk?.Usage != null)
                    {
                        LastUsage = chunk.Usage;
                        cacheInfo = $"{chunk.Usage.PromptCacheHitTokens}|{chunk.Usage.PromptCacheMissTokens}|{chunk.Usage.PromptTokens}|{chunk.Usage.CompletionTokens}";
                    }
                }
                catch (JsonException)
                {
                    // 忽略无法解析的行（如 keep-alive 注释）
                    continue;
                }

                // yield return 移至 try 块外部，解决编译错误
                if (!string.IsNullOrEmpty(reasoning))
                    yield return $"[THINKING]{reasoning}";

                if (!string.IsNullOrEmpty(toolCallJson))
                    yield return $"[TOOL_CALL]{toolCallJson}";

                if (!string.IsNullOrEmpty(cacheInfo))
                    yield return $"[CACHE]{cacheInfo}";

                if (!string.IsNullOrEmpty(content))
                    yield return content!;
            }
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
            }

            return result?.Choices?[0]?.Message?.Content ?? string.Empty;
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
        /// 检查 HTTP 响应状态，对认证错误抛出 ApiKeyInvalidException。
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