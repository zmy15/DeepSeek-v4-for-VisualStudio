using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Providers
{
    /// <summary>
    /// DeepSeek Provider。
    /// 复用 OpenAI-compatible 的聊天传输层，并承载 DeepSeek 原生的 reasoning、
    /// FIM、余额和计价逻辑。
    /// </summary>
    public class DeepSeekProvider : OpenAiCompatibleProvider, IDeepSeekApiService
    {
        /// <summary>默认 DeepSeek 官方 API 地址（留空时的回退值）。</summary>
        public const string DefaultBaseUrl = "https://api.deepseek.com";

        private const string FimBaseUrl = "https://api.deepseek.com/beta";
        private const string FimEndpoint = "/completions";

        private static readonly string ClientInstanceId =
            Guid.NewGuid().ToString("N").Substring(0, 12);

        private bool _thinkingEnabled = true;
        private string _reasoningEffort = "high";

        private long _totalFimPromptTokens;
        private long _totalFimCompletionTokens;

        private volatile string _accountCurrency = "CNY";

        public DeepSeekProvider(string apiKey, string model = DeepSeekModelCatalog.Pro,
            int? requestTimeoutSeconds = null, string? baseUrl = null,
            bool isVision = false, bool isCustom = false)
            : base(apiKey, model, requestTimeoutSeconds, baseUrl, isVision, isCustom,
                DefaultBaseUrl, "DeepSeek")
        {
        }

        public DeepSeekProvider(HttpClient httpClient, string model = DeepSeekModelCatalog.Pro,
            string? baseUrl = null, bool isVision = false, bool isCustom = false)
            : base(httpClient, model, baseUrl, isVision, isCustom,
                DefaultBaseUrl, "DeepSeek")
        {
        }

        /// <summary>是否为 DeepSeek 官方端点。</summary>
        public bool IsDeepSeekEndpoint
            => string.Equals(BaseUrl.TrimEnd('/'), DefaultBaseUrl, StringComparison.OrdinalIgnoreCase);

        /// <summary>账户币种（"CNY" 国内 / "USD" 国际）。</summary>
        public string AccountCurrency => _accountCurrency;

        /// <summary>FIM 代码补全累计 Prompt Token 数。</summary>
        public long TotalFimPromptTokens => Interlocked.Read(ref _totalFimPromptTokens);

        /// <summary>FIM 代码补全累计 Completion Token 数。</summary>
        public long TotalFimCompletionTokens => Interlocked.Read(ref _totalFimCompletionTokens);

        /// <summary>配置思考模式和 reasoning effort。</summary>
        public void ConfigureThinking(bool enabled, string effort = "high")
        {
            _thinkingEnabled = enabled;
            _reasoningEffort = effort;
        }

        /// <summary>
        /// DeepSeek V4 官方统一定价（所有官方模型同价），按币种和高峰/空闲时段分档。
        /// </summary>
        public static (double CacheMiss, double CacheHit, double Output) GetPricing(
            bool isPeak,
            string currency = "CNY")
        {
            bool usd = (currency ?? "").Equals("USD", StringComparison.OrdinalIgnoreCase);
            return isPeak
                ? (CacheMiss: usd ? 0.3 : 2.0, CacheHit: usd ? 0.006 : 0.04, Output: usd ? 1.2 : 8.0)
                : (CacheMiss: usd ? 0.15 : 1.0, CacheHit: usd ? 0.003 : 0.02, Output: usd ? 0.6 : 4.0);
        }

        /// <summary>判断北京时间是否处于 DeepSeek 高峰计价时段。</summary>
        public static bool IsBeijingPeakTime() => IsBeijingPeakTime(DateTimeOffset.UtcNow);

        internal static bool IsBeijingPeakTime(DateTimeOffset utcNow)
        {
            var beijingNow = utcNow.ToOffset(TimeSpan.FromHours(8));
            if (beijingNow.DayOfWeek == DayOfWeek.Saturday || beijingNow.DayOfWeek == DayOfWeek.Sunday)
                return false;
            int hour = beijingNow.Hour;
            return (hour >= 9 && hour < 12) || (hour >= 14 && hour < 18);
        }

        protected override void ApplyProviderHeaders(HttpClient httpClient, string baseUrl)
        {
            if (string.Equals(baseUrl.TrimEnd('/'), DefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
                httpClient.DefaultRequestHeaders.Add("X-Client-Instance-Id", ClientInstanceId);
        }

        protected override void OnBaseUrlChanged(string baseUrl)
        {
            if (string.Equals(baseUrl.TrimEnd('/'), DefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                if (!_httpClient.DefaultRequestHeaders.Contains("X-Client-Instance-Id"))
                    _httpClient.DefaultRequestHeaders.Add("X-Client-Instance-Id", ClientInstanceId);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Remove("X-Client-Instance-Id");
            }
        }

        protected override void AccumulateProviderCost(DeepSeekUsage usage)
        {
            if (CurrentIsCustom)
                return;

            bool isPeak = IsBeijingPeakTime();
            var (missCny, hitCny, outputCny) = GetPricing(isPeak, "CNY");
            var (missUsd, hitUsd, outputUsd) = GetPricing(isPeak, "USD");
            AddAccumulatedCost(
                usage.PromptCacheMissTokens / 1_000_000.0 * missCny
                + usage.PromptCacheHitTokens / 1_000_000.0 * hitCny
                + usage.CompletionTokens / 1_000_000.0 * outputCny,
                usage.PromptCacheMissTokens / 1_000_000.0 * missUsd
                + usage.PromptCacheHitTokens / 1_000_000.0 * hitUsd
                + usage.CompletionTokens / 1_000_000.0 * outputUsd);
        }

        protected override void ApplyProviderRequestOptions(
            DeepSeekChatRequest request,
            bool? thinkingEnabled)
        {
            var capability = ReasoningCapabilityConfig.Infer(BaseUrl, request.Model);
            bool effectiveThinking = thinkingEnabled ?? _thinkingEnabled;
            ApplyReasoningOptions(request, capability, effectiveThinking, _reasoningEffort);
            Logger.Info($"[Reasoning] 端点={BaseUrl}, 模型={request.Model}, " +
                $"thinking={capability.ThinkingParam}, effort={capability.EffortParam}({capability.EffortValueMode})");
        }

        protected override void ApplyProviderEndpointShaping(
            DeepSeekChatRequest request,
            bool isStreaming)
        {
            base.ApplyProviderEndpointShaping(request, isStreaming);

            var capability = ReasoningCapabilityConfig.Infer(BaseUrl, request.Model);
            if (capability.UseMaxCompletionTokens && request.MaxTokens.HasValue)
            {
                request.MaxCompletionTokens = request.MaxTokens;
                request.MaxTokens = null;
            }

            if (capability.RejectsSamplingParams)
                request.Temperature = null;

            if (isStreaming && capability.NeedsStreamOptionsForUsage)
                request.StreamOptions = new StreamOptions { IncludeUsage = true };
        }

        private static void ApplyReasoningOptions(
            DeepSeekChatRequest request,
            ReasoningCapabilityConfig capability,
            bool thinkingEnabled,
            string? effort)
        {
            request.Thinking = null;
            request.ReasoningEffort = null;
            request.EnableThinking = null;
            request.ReasoningSplit = null;
            request.Reasoning = null;

            if (capability.SupportsThinking && capability.ThinkingParam != "none")
            {
                switch (capability.ThinkingParam)
                {
                    case "thinking":
                        request.Thinking = new ThinkingControl
                        {
                            Type = thinkingEnabled ? "enabled" : "disabled"
                        };
                        break;
                    case "enable_thinking":
                        request.EnableThinking = thinkingEnabled;
                        break;
                    case "reasoning_split":
                        request.ReasoningSplit = thinkingEnabled;
                        break;
                }
            }

            var effortForRequest = thinkingEnabled
                ? effort
                : capability.AlwaysThinking ? "low" : null;
            var mappedEffort = capability.MapEffort(effortForRequest);
            if (mappedEffort != null)
            {
                switch (capability.EffortParam)
                {
                    case "reasoning_effort":
                        request.ReasoningEffort = mappedEffort;
                        break;
                    case "reasoning.effort":
                        request.Reasoning = new ReasoningObject { Effort = mappedEffort };
                        break;
                }
            }
        }

        /// <summary>
        /// FIM（Fill-In-the-Middle）补全调用，用于代码自动补全场景。
        /// </summary>
        public async Task<string> FimCompletionAsync(
            string prompt,
            string? suffix = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsDeepSeekEndpoint)
                return string.Empty;

            var request = new DeepSeekFimRequest
            {
                Model = ResolveFimModel(),
                Prompt = prompt,
                Suffix = suffix,
                MaxTokens = maxTokens ?? 256,
                Temperature = 0.0,
                Stream = false,
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, FimBaseUrl + FimEndpoint)
            {
                Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            await ValidateResponseStatusAsync(
                response,
                $"model={request.Model}, endpoint={FimBaseUrl}{FimEndpoint}, " +
                $"promptChars={prompt.Length}, suffixChars={suffix?.Length ?? 0}");
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DeepSeekFimResponse>(responseJson);

            if (result?.Usage != null)
            {
                LastUsage = result.Usage;
                Interlocked.Add(ref _totalFimPromptTokens, result.Usage.PromptTokens);
                Interlocked.Add(ref _totalFimCompletionTokens, result.Usage.CompletionTokens);
            }

            return result?.Choices is { Count: > 0 }
                ? result.Choices[0]?.Text ?? string.Empty
                : string.Empty;
        }

        /// <summary>
        /// 查询 DeepSeek 账户余额。
        /// </summary>
        public async Task<BalanceResponse?> GetBalanceAsync()
        {
            try
            {
                if (!IsDeepSeekEndpoint)
                    return null;

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    BuildRequestUri("user/balance"));
                httpRequest.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(httpRequest);
                await ValidateResponseStatusAsync(response, "endpoint=/user/balance");
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<BalanceResponse>(responseJson);

                var currency = result?.BalanceInfos?.FirstOrDefault()?.Currency;
                if (!string.IsNullOrWhiteSpace(currency))
                    _accountCurrency = currency.ToUpperInvariant();

                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[余额] 查询余额失败: {ex.Message}");
                return null;
            }
        }

        private string ResolveFimModel()
            => CurrentIsVision ? DeepSeekModelCatalog.Flash : CurrentModel;
    }
}
