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
        /// DeepSeek V4 官方定价，按"国内/国际（币种）× 模型（Flash/Pro）× 时段（高峰/空闲）"分档。
        /// 高峰时段为北京时间周一至周五 9:00-12:00、14:00-18:00；周六、周日全天为空闲时段。
        ///   国内（¥/百万 tokens）：
        ///     输入（缓存命中）:   空闲 Flash ¥0.02  / Pro ¥0.15  ；高峰 Flash ¥0.04  / Pro ¥0.30
        ///     输入（缓存未命中）: 空闲 Flash ¥1     / Pro ¥4.5   ；高峰 Flash ¥2     / Pro ¥9.0
        ///     输出:               空闲 Flash ¥4     / Pro ¥13.5  ；高峰 Flash ¥8     / Pro ¥27.0
        ///   国际（$/百万 tokens）：
        ///     输入（缓存命中）:   空闲 Flash $0.003 / Pro $0.022 ；高峰 Flash $0.006 / Pro $0.044
        ///     输入（缓存未命中）: 空闲 Flash $0.15  / Pro $0.66  ；高峰 Flash $0.30  / Pro $1.32
        ///     输出:               空闲 Flash $0.60  / Pro $1.98  ；高峰 Flash $1.20  / Pro $3.96
        /// </summary>
        /// <param name="model">模型标识；包含 "flash" 时按 Flash 价目，其余按 Pro 价目</param>
        /// <param name="isPeak">是否高峰时段</param>
        /// <param name="currency">币种："USD" 国际价目，其余（含默认）按国内 CNY 价目</param>
        public static (double CacheMiss, double CacheHit, double Output) GetPricing(
            string? model,
            bool isPeak,
            string currency = "CNY")
        {
            bool usd = (currency ?? "").Equals("USD", StringComparison.OrdinalIgnoreCase);
            bool isFlash = (model ?? string.Empty).Contains("flash", StringComparison.OrdinalIgnoreCase);

            if (isFlash)
            {
                return isPeak
                    ? (CacheMiss: usd ? 0.3 : 2.0, CacheHit: usd ? 0.006 : 0.04, Output: usd ? 1.2 : 8.0)
                    : (CacheMiss: usd ? 0.15 : 1.0, CacheHit: usd ? 0.003 : 0.02, Output: usd ? 0.6 : 4.0);
            }

            return isPeak
                ? (CacheMiss: usd ? 1.32 : 9.0, CacheHit: usd ? 0.044 : 0.30, Output: usd ? 3.96 : 27.0)
                : (CacheMiss: usd ? 0.66 : 4.5, CacheHit: usd ? 0.022 : 0.15, Output: usd ? 1.98 : 13.5);
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

        protected override void AccumulateProviderCost(DeepSeekUsage usage, string? effectiveModel)
        {
            if (CurrentIsCustom)
                return;

            string model = effectiveModel ?? CurrentModel ?? string.Empty;
            bool isPeak = IsBeijingPeakTime();
            var (missCny, hitCny, outputCny) = GetPricing(model, isPeak, "CNY");
            var (missUsd, hitUsd, outputUsd) = GetPricing(model, isPeak, "USD");
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
            await ValidateResponseStatusAsync(response);
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
                await ValidateResponseStatusAsync(response);
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
