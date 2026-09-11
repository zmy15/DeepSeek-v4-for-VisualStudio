using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Providers
{
    public class OpenAiCompatibleProvider : IChatCompletionProvider
    {
        protected readonly HttpClient _httpClient;
        /// <summary>通用 OpenAI 兼容端点的默认 Base URL。</summary>
        public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
        // 注意：相对路径不能以 / 开头 —— HttpClient 对以 / 开头的相对 URI 会
        // 从主机根开始拼接，丢失 BaseAddress 中的路径段（如自定义端点的 /v1）。
        protected const string ChatEndpoint = "chat/completions";
        private readonly string _defaultBaseUrl;
        protected readonly string ProviderName;

        /// <summary>Provider 标识，供路由、诊断和后续协议选择使用。</summary>
        public string ProviderId => ProviderName;

        /// <summary>当前使用的 API 端点 Base URL（实例级别，支持运行时热切换）。</summary>
        private volatile string _baseUrl;

        /// <summary>ServicePointManager 是否已配置（全局一次性初始化）</summary>
        private static volatile bool _servicePointConfigured;
        private static readonly object _spInitLock = new object();

        private string _model;

        /// <summary>
        /// 前缀缓存稳定性管理器（可选注入）。
        /// 设置后，每次 ChatStreamAsync 调用前会自动检查前缀指纹并记录漂移。
        /// </summary>
        public PrefixCacheManager? PrefixCache { get; set; }

        /// <summary>
        /// 最近一次 API 调用的 Usage 信息（含 Cache 命中统计）。
        /// 流式调用结束后更新，非流式调用后立即可用。
        /// </summary>
        public DeepSeekUsage? LastUsage { get; protected set; }

        /// <summary>
        /// 最近一次 Chat API 实际发送的消息列表（清洗/规则处理后的最终版本）。
        /// Handoff 时优先转发此快照，确保目标 Agent 使用与服务器缓存完全一致的 messages 长度和字段。
        /// </summary>
        public IReadOnlyList<ChatApiMessage>? LastSentMessages { get; private set; }

        // ── 线程安全的累计统计字段（使用 Interlocked 保证多 Agent 并行调用时正确累加）──
        private long _totalCacheHitTokens;
        private long _totalCacheMissTokens;
        private long _totalPromptTokens;
        private long _totalCompletionTokens;
        protected double TotalSessionCostYuanValue;
        protected double TotalSessionCostUsdValue;

        /// <summary>
        /// 累计 Chat API 统计（跨所有 API 调用汇总，含 Agent 内部调用）。
        /// 在每次 API 调用后自动累加。调用 <see cref="ResetAccumulatedStats"/> 重置。
        /// </summary>
        public long TotalCacheHitTokens => Interlocked.Read(ref _totalCacheHitTokens);
        public long TotalCacheMissTokens => Interlocked.Read(ref _totalCacheMissTokens);
        public long TotalPromptTokens => Interlocked.Read(ref _totalPromptTokens);
        public long TotalCompletionTokens => Interlocked.Read(ref _totalCompletionTokens);

        /// <summary>
        /// 累计费用（元，人民币，国内价目）。每次 API 调用按"调用时点的高峰/空闲时段"单价计价后累加。
        /// </summary>
        public double TotalSessionCostYuan => Volatile.Read(ref TotalSessionCostYuanValue);

        /// <summary>
        /// 累计费用（美元，国际价目）。与人民币双轨累计，显示时按账户币种取用。
        /// </summary>
        public double TotalSessionCostUsd => Volatile.Read(ref TotalSessionCostUsdValue);

        /// <summary>
        /// 累计 Cache 命中率（0.0 ~ 1.0）。
        /// </summary>
        public double TotalCacheHitRate
        {
            get
            {
                long hit = Interlocked.Read(ref _totalCacheHitTokens);
                long miss = Interlocked.Read(ref _totalCacheMissTokens);
                long total = hit + miss;
                return total > 0 ? (double)hit / total : 0;
            }
        }

        /// <summary>
        /// 重置累计 Chat 统计（新会话开始时调用）。
        /// 账户币种为账户级属性，不随会话重置。
        /// </summary>
        public void ResetAccumulatedStats()
        {
            Interlocked.Exchange(ref _totalCacheHitTokens, 0);
            Interlocked.Exchange(ref _totalCacheMissTokens, 0);
            Interlocked.Exchange(ref _totalPromptTokens, 0);
            Interlocked.Exchange(ref _totalCompletionTokens, 0);
            Interlocked.Exchange(ref TotalSessionCostYuanValue, 0.0);
            Interlocked.Exchange(ref TotalSessionCostUsdValue, 0.0);
        }

        /// <summary>
        /// 从持久化数据恢复累计 Chat 统计（重启后调用）。
        /// </summary>
        public void RestoreAccumulatedStats(long hitTokens, long missTokens, long promptTokens, long completionTokens, double costYuan, double costUsd)
        {
            Interlocked.Exchange(ref _totalCacheHitTokens, hitTokens);
            Interlocked.Exchange(ref _totalCacheMissTokens, missTokens);
            Interlocked.Exchange(ref _totalPromptTokens, promptTokens);
            Interlocked.Exchange(ref _totalCompletionTokens, completionTokens);
            Interlocked.Exchange(ref TotalSessionCostYuanValue, costYuan);
            Interlocked.Exchange(ref TotalSessionCostUsdValue, costUsd);
        }

        // ── 单轮统计快照（用于显示"本次问答"的 Cache 命中率，而非整个 Session 累计值）──
        private long _snapshotCacheHitTokens;
        private long _snapshotCacheMissTokens;
        private long _snapshotPromptTokens;
        private long _snapshotCompletionTokens;

        /// <summary>
        /// 对当前累计值拍摄快照，后续调用 <see cref="GetCacheDelta"/> 可获取自快照以来的增量。
        /// 应在每次用户消息/Agent 工作流开始时调用。
        /// </summary>
        public void TakeCacheSnapshot()
        {
            Interlocked.Exchange(ref _snapshotCacheHitTokens, Interlocked.Read(ref _totalCacheHitTokens));
            Interlocked.Exchange(ref _snapshotCacheMissTokens, Interlocked.Read(ref _totalCacheMissTokens));
            Interlocked.Exchange(ref _snapshotPromptTokens, Interlocked.Read(ref _totalPromptTokens));
            Interlocked.Exchange(ref _snapshotCompletionTokens, Interlocked.Read(ref _totalCompletionTokens));
        }

        /// <summary>
        /// 获取自上次快照以来的 Cache 统计增量（本次问答的 Token 消耗）。
        /// 返回 (hitTokens, missTokens, promptTokens, completionTokens)。
        /// </summary>
        public (long Hit, long Miss, long Prompt, long Completion) GetCacheDelta()
        {
            long currentHit = Interlocked.Read(ref _totalCacheHitTokens);
            long currentMiss = Interlocked.Read(ref _totalCacheMissTokens);
            long currentPrompt = Interlocked.Read(ref _totalPromptTokens);
            long currentCompletion = Interlocked.Read(ref _totalCompletionTokens);

            long deltaHit = currentHit - Interlocked.Read(ref _snapshotCacheHitTokens);
            long deltaMiss = currentMiss - Interlocked.Read(ref _snapshotCacheMissTokens);
            long deltaPrompt = currentPrompt - Interlocked.Read(ref _snapshotPromptTokens);
            long deltaCompletion = currentCompletion - Interlocked.Read(ref _snapshotCompletionTokens);

            return (deltaHit, deltaMiss, deltaPrompt, deltaCompletion);
        }

        /// <summary>
        /// 线程安全地累加一次 API 调用的 Usage 统计到累计值（Chat API）。
        /// 同时按"调用结束时点的高峰/空闲时段"单价，以国内（¥）和国际（$）
        /// 两套价目双轨累计费用；显示时按账户币种（余额 API 自动捕获）取用，
        /// 避免首次余额查询前或账户类型判定前后出现混币种累加。
        /// </summary>
        /// <param name="usage">本次调用的 usage</param>
        protected void AccumulateStats(DeepSeekUsage usage)
        {
            Interlocked.Add(ref _totalCacheHitTokens, usage.PromptCacheHitTokens);
            Interlocked.Add(ref _totalCacheMissTokens, usage.PromptCacheMissTokens);
            Interlocked.Add(ref _totalPromptTokens, usage.PromptTokens);
            Interlocked.Add(ref _totalCompletionTokens, usage.CompletionTokens);
            AccumulateProviderCost(usage);
        }

        /// <summary>由具体 Provider 根据自身计价规则累加费用；通用实现默认不收费。</summary>
        protected virtual void AccumulateProviderCost(DeepSeekUsage usage) { }

        /// <summary>
        /// 线程安全地累加费用（double 无 Interlocked.Add 重载，使用 CAS 循环）。
        /// </summary>
        protected void AddAccumulatedCost(double costYuan, double costUsd)
        {
            AddAccumulatedCost(ref TotalSessionCostYuanValue, costYuan);
            AddAccumulatedCost(ref TotalSessionCostUsdValue, costUsd);
        }

        /// <summary>CAS 循环累加单个 double 字段。</summary>
        private static void AddAccumulatedCost(ref double field, double cost)
        {
            if (cost <= 0) return;
            double initial, updated;
            do
            {
                initial = Volatile.Read(ref field);
                updated = initial + cost;
            } while (Interlocked.CompareExchange(ref field, updated, initial) != initial);
        }

        public OpenAiCompatibleProvider(string apiKey, string model = "gpt-4o-mini",
            int? requestTimeoutSeconds = null, string? baseUrl = null,
            bool isVision = false, bool isCustom = false)
            : this(apiKey, model, requestTimeoutSeconds, baseUrl, isVision, isCustom,
                DefaultOpenAiBaseUrl, "OpenAI-compatible")
        {
        }

        protected OpenAiCompatibleProvider(string apiKey, string model,
            int? requestTimeoutSeconds, string? baseUrl, bool isVision, bool isCustom,
            string defaultBaseUrl, string providerName)
        {
            _defaultBaseUrl = defaultBaseUrl;
            ProviderName = providerName;
            _model = model;
            CurrentIsVision = isVision;
            CurrentIsCustom = isCustom;
            _baseUrl = NormalizeBaseUrl(baseUrl, _defaultBaseUrl);

            // ── 确保全局 ServicePoint 配置仅初始化一次 ──
            ConfigureServicePointManagerOnce();

            // ── 创建优化的 HttpClientHandler ──
            //     目标：最大化 Agent 间 TCP 连接复用，提高服务端缓存亲和性概率。
            //     同一 HttpClient 实例的所有请求共享连接池；
            //     所有 Agent 通过 AgentFactory 共享同一个 DeepSeekApiService → 同一个 HttpClient。
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
            };

            // ── LLM 请求超时可配置（P2，序号 22）；默认保持原行为 5 分钟 ──
            int timeoutSeconds = requestTimeoutSeconds ?? 300;
            if (timeoutSeconds < 30) timeoutSeconds = 30;       // 下限：避免误配导致请求必失败
            if (timeoutSeconds > 3600) timeoutSeconds = 3600;   // 上限：1 小时

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            ApplyProviderHeaders(_httpClient, _baseUrl);

            Logger.Info($"[HTTP] {ProviderName} HttpClient 创建完成 (" +
                $"DefaultConnectionLimit={ServicePointManager.DefaultConnectionLimit}, " +
                $"MaxIdleTime={ServicePointManager.MaxServicePointIdleTime}ms)");
        }

        /// <summary>
        /// 测试用构造函数 — 接受外部 HttpClient（用于 Mock HTTP 处理程序）。
        /// </summary>
        public OpenAiCompatibleProvider(HttpClient httpClient, string model = "gpt-4o-mini",
            string? baseUrl = null, bool isVision = false, bool isCustom = false)
            : this(httpClient, model, baseUrl, isVision, isCustom,
                DefaultOpenAiBaseUrl, "OpenAI-compatible")
        {
        }

        protected OpenAiCompatibleProvider(HttpClient httpClient, string model,
            string? baseUrl, bool isVision, bool isCustom,
            string defaultBaseUrl, string providerName)
        {
            _defaultBaseUrl = defaultBaseUrl;
            ProviderName = providerName;
            _model = model;
            CurrentIsVision = isVision;
            CurrentIsCustom = isCustom;
            _baseUrl = NormalizeBaseUrl(baseUrl, _defaultBaseUrl);
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (_httpClient.BaseAddress == null)
                _httpClient.BaseAddress = new Uri(_baseUrl);
        }

        /// <summary>
        /// 规范化 Base URL：
        /// 1. 用户粘贴完整 chat 端点（/chat/completions 结尾）→ 剥离；
        /// 2. 确保以 / 结尾 —— HttpClient 的 BaseAddress 必须以 / 结尾，
        ///    相对请求路径（不带前导 /）才能正确追加，否则会丢失路径段（如 /v1）。
        /// </summary>
        private static string NormalizeBaseUrl(string? baseUrl, string defaultBaseUrl)
        {
            var trimmed = string.IsNullOrWhiteSpace(baseUrl)
                ? defaultBaseUrl
                : baseUrl.Trim();
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - "/chat/completions".Length);
            return trimmed.TrimEnd('/') + "/";
        }

        /// <summary>当前使用的 API 端点 Base URL。</summary>
        public string BaseUrl => _baseUrl;

        protected virtual void ApplyProviderHeaders(HttpClient httpClient, string baseUrl) { }

        /// <summary>
        /// 全局 ServicePointManager 一次性配置 — 优化 TCP 连接复用。
        /// 
        /// 在 .NET Framework 4.7.2 上，TCP 连接池由 ServicePointManager 全局管理。
        /// 默认 MaxIdleTime=100s, DefaultConnectionLimit=2 对高频 API 调用场景偏保守。
        /// 
        /// 优化策略：
        ///   - DefaultConnectionLimit=10: 提高并发连接数，避免 Agent 切换时等待新连接
        ///   - MaxServicePointIdleTime=300s: 延长空闲连接存活时间，提高 Agent 间复用概率
        ///   - ReusePort=true: 允许端口复用，减少 TIME_WAIT
        /// </summary>
        private static void ConfigureServicePointManagerOnce()
        {
            if (_servicePointConfigured) return;
            lock (_spInitLock)
            {
                if (_servicePointConfigured) return;
                try
                {
                    ServicePointManager.DefaultConnectionLimit = 10;
                    ServicePointManager.MaxServicePointIdleTime = 300_000; // 5 分钟
                    ServicePointManager.Expect100Continue = true;
                    ServicePointManager.ReusePort = true;
                    _servicePointConfigured = true;
                    Logger.Info($"[HTTP] ServicePointManager 全局配置完成: " +
                        $"DefaultConnectionLimit={ServicePointManager.DefaultConnectionLimit}, " +
                        $"MaxIdleTime={ServicePointManager.MaxServicePointIdleTime}ms");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[HTTP] ServicePointManager 配置失败: {ex.Message}");
                }
            }
        }

        public void UpdateModel(string model) => _model = model;

        /// <summary>
        /// 运行时一次性应用端点配置（Key/BaseUrl/Model/IsCustom/IsVision）。
        /// 替代 UpdateApiKey + UpdateBaseUrl + UpdateModel 三连调用，并同步能力标志。
        /// </summary>
        public void UpdateEndpoint(DeepSeekEndpointConfig config)
        {
            UpdateApiKey(config.ApiKey);
            UpdateBaseUrl(config.BaseUrl);
            UpdateModel(config.Model);
            CurrentIsCustom = config.IsCustom;
            CurrentIsVision = config.IsVision;
            Logger.Info($"[API] 端点配置已更新: model={config.Model}, isCustom={config.IsCustom}, isVision={config.IsVision}");
        }

        /// <summary>由具体 Provider 按协议写入思考相关请求参数。</summary>
        protected virtual void ApplyProviderRequestOptions(
            DeepSeekChatRequest request,
            bool? thinkingEnabled)
        {
        }

        /// <summary>由具体 Provider 按端点能力改写通用请求字段。</summary>
        protected virtual void ApplyProviderEndpointShaping(
            DeepSeekChatRequest request,
            bool isStreaming)
        {
            if (request.Tools == null || request.Tools.Count == 0)
            {
                request.ToolChoice = null;
                request.ParallelToolCalls = null;
            }
        }

        /// <summary>运行时更新 API Key（P1：选项页保存后即时生效，无需重启）。</summary>
        public void UpdateApiKey(string apiKey)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey ?? string.Empty);
        }

        /// <summary>运行时更新 API 端点 Base URL（选项页保存后即时生效，无需重启）。</summary>
        public void UpdateBaseUrl(string? baseUrl)
        {
            _baseUrl = NormalizeBaseUrl(baseUrl, _defaultBaseUrl);
            OnBaseUrlChanged(_baseUrl);
            Logger.Info($"[API] Base URL 已更新: {_baseUrl}");
        }

        protected virtual void OnBaseUrlChanged(string baseUrl) { }

        /// <summary>
        /// 构造绝对请求地址。HttpClient 在首个请求后不允许修改 BaseAddress，
        /// 因此运行时端点热切换必须每次根据当前 BaseUrl 显式解析请求 URI。
        /// </summary>
        protected Uri BuildRequestUri(string relativeOrAbsolutePath)
            => Uri.TryCreate(relativeOrAbsolutePath, UriKind.Absolute, out var absolutePath)
                ? absolutePath
                : new Uri(new Uri(_baseUrl, UriKind.Absolute), relativeOrAbsolutePath);

        /// <summary>当前使用的模型标识（用于视觉模型等能力分支判断）。</summary>
        public string CurrentModel => _model;

        /// <summary>当前模型是否具备多模态（视觉）能力，由端点解析器权威赋值。</summary>
        public bool CurrentIsVision { get; protected set; }

        /// <summary>当前是否为自定义端点（resolver 权威值，区别于 IsDeepSeekEndpoint 的 URL 推断）。</summary>
        public bool CurrentIsCustom { get; protected set; }

        /// <summary>
        /// 连接复用诊断日志 — 记录 ServicePoint 当前连接状态。
        /// 
        /// 用途：监控 Agent 间是否复用同一 TCP 连接。
        ///   - CurrentConnections 持续为 1 且无新建 → 连接被复用 
        ///   - CurrentConnections 频繁升降 → 连接在回收重建 
        ///   - 每次请求都是新 ServicePoint → 连接未曾复用 
        /// </summary>
        private void LogConnectionReuseDiagnostics()
        {
            try
            {
                var sp = ServicePointManager.FindServicePoint(new Uri(_baseUrl));
                // IdleSince 返回 DateTime（空闲开始的绝对时间），计算空闲时长
                double idleMs = (DateTime.Now - sp.IdleSince).TotalMilliseconds;
                Logger.Info($"[HTTP] 连接复用诊断: " +
                    $"CurrentConnections={sp.CurrentConnections}, " +
                    $"IdleSince={idleMs:F0}ms, " +
                    $"ConnectionLimit={sp.ConnectionLimit}, " +
                    $"SupportsPipelining={sp.SupportsPipelining}, " +
                    $"Provider={ProviderName}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[HTTP] 连接复用诊断失败: {ex.Message}");
            }
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            int? maxTokens = null,
            string? toolChoice = null,
            double? temperature = null,
            string? responseFormat = null,
            string? model = null,
            bool? thinkingEnabled = null)
        {
            // ── 工具 Schema 规范化：按名称排序，消除注册顺序对缓存的影响 ──
            //     参考 CodeWhale prefix_cache.rs:316-331
            List<ToolDefinition>? normalizedTools = ToolSchemaNormalizer.NormalizeForApi(tools);

            // toolChoice 优先级: 显式传入 > 有 tools 时 auto > null(不发送)
            string? effectiveToolChoice = toolChoice
                ?? (normalizedTools != null && normalizedTools.Count > 0 ? "auto" : null);

            var request = new DeepSeekChatRequest
            {
                Model = model ?? _model,
                Messages = new List<ChatApiMessage>(messages),
                Stream = true,
                Tools = normalizedTools,
                ToolChoice = effectiveToolChoice,
                MaxTokens = maxTokens,
                Temperature = temperature,
                ResponseFormat = responseFormat == "json_object"
                    ? new ResponseFormat { Type = "json_object" }
                    : null
            };
            ApplyProviderRequestOptions(request, thinkingEnabled);
            ApplyProviderEndpointShaping(request, isStreaming: true);
            Logger.Info($"[Reasoning] Provider={ProviderName}, 端点={_baseUrl}, 模型={request.Model}");

            // ── 消息清理：防止无效消息导致 HTTP 400 ──
            // DeepSeek API 对消息格式有严格要求：
            // 1. tool 消息必须有 tool_call_id
            // 2. assistant 消息有 tool_calls 时可以没有 content，但不能既无 content 又无 tool_calls
            // 3. 不能有连续的相同 role 消息（user-user, assistant-assistant）→ 合并而非丢弃
            //
            //  缓存关键（v1.1.10）：所有清理操作在 SHALLOW CLONE 上进行，
            //    不修改原始 ChatApiMessage 对象，确保下次请求的前缀不变，
            //    DeepSeek Prefix Cache 可持续命中。
            var cleanedMessages = new List<ChatApiMessage>();
            string? lastRole = null;
            int removedCount = 0;
            int mergedCount = 0;
            var mergedPositions = new List<string>(); // 记录合并位置用于诊断
            int msgIndex = 0;
            foreach (var msg in request.Messages)
            {
                // ── 规则 1：tool 消息必须有 tool_call_id ──
                if (msg.Role == "tool" && string.IsNullOrEmpty(msg.ToolCallId))
                {
                    Logger.Warn($"[API] 移除无效 tool 消息：缺少 tool_call_id (content={msg.Content?.Truncate(80)})");
                    removedCount++;
                    msgIndex++;
                    continue;
                }

                // ── 规则 2：assistant 消息既无 content 又无 tool_calls → 移除 ──
                if (msg.Role == "assistant"
                    && string.IsNullOrEmpty(msg.Content)
                    && (msg.ToolCalls == null || msg.ToolCalls.Count == 0))
                {
                    Logger.Warn($"[API] 移除无效 assistant 消息：无 content 且无 tool_calls");
                    removedCount++;
                    msgIndex++;
                    continue;
                }

                // ── 深克隆：后续所有修改仅影响克隆对象，不污染调用方原始消息 ──
                // 注意：ToolCalls 内的元素同样新建（Rule5 会写 m.ToolCalls / ReasoningContent，
                // 且泛型阶段式修改不应改到调用方 ConversationContextManager 的对象）。
                var clone = CloneMessage(msg);

                // ── 规则 3：assistant 消息有 tool_calls 但缺少 reasoning_content → 补全 ──
                if (clone.Role == "assistant" && clone.ToolCalls != null && clone.ToolCalls.Count > 0 && clone.ReasoningContent == null)
                {
                    clone.ReasoningContent = string.Empty;
                }

                // ── 规则 4：防止连续相同 role（DeepSeek API 要求 user/assistant 交替）──
                // tool 消息连续出现是合法的（多个工具调用结果），不检查
                // 对于连续 user 或 assistant 消息，合并内容而非丢弃
                if (lastRole != null && clone.Role == lastRole
                    && (clone.Role == "user" || clone.Role == "assistant"))
                {
                    if (cleanedMessages.Count > 0)
                    {
                        var lastMsg = cleanedMessages[cleanedMessages.Count - 1];
                        string existingContent = lastMsg.Content ?? string.Empty;
                        string newContent = clone.Content ?? string.Empty;

                        // ── 记录合并位置（含原索引、角色、前后内容长度）──
                        bool lastHasTc = lastMsg.ToolCalls != null && lastMsg.ToolCalls.Count > 0;
                        bool currHasTc = clone.ToolCalls != null && clone.ToolCalls.Count > 0;
                        mergedPositions.Add($"[{msgIndex}]{clone.Role}(lastTc={lastHasTc},curTc={currHasTc},exist={existingContent.Length},new={newContent.Length})");

                        if (clone.Role == "user"
                            && (clone.MultimodalContent is { Count: > 0 }
                                || lastMsg.MultimodalContent is { Count: > 0 }))
                        {
                            // 视觉消息不能简单按字符串拼接，否则会把图片块丢成纯文本。
                            lastMsg.MultimodalContent = MergeUserContentParts(lastMsg, clone);
                            lastMsg.Content = null;
                        }
                        else if (!string.IsNullOrWhiteSpace(newContent))
                        {
                            // ── 合并内容：用分隔线连接 ──
                            lastMsg.Content = string.IsNullOrWhiteSpace(existingContent)
                                ? newContent
                                : existingContent + "\n\n---\n\n" + newContent;
                        }
                        // else: 后者无内容，直接跳过（保留前者的内容）

                        // 如果后者有 reasoning_content，保留后者
                        if (!string.IsNullOrWhiteSpace(clone.ReasoningContent))
                            lastMsg.ReasoningContent = clone.ReasoningContent;
                        // 如果后者有 tool_calls，保留后者
                        if (clone.ToolCalls != null && clone.ToolCalls.Count > 0)
                            lastMsg.ToolCalls = clone.ToolCalls;

                        mergedCount++;
                        msgIndex++;
                        continue;
                    }
                }

                cleanedMessages.Add(clone);
                lastRole = clone.Role;
                msgIndex++;
            }

            if (removedCount > 0 || mergedCount > 0)
            {
                var parts = new List<string>();
                if (removedCount > 0) parts.Add($"移除了 {removedCount} 条无效消息");
                if (mergedCount > 0) parts.Add($"合并了 {mergedCount} 条连续消息 ({string.Join(", ", mergedPositions)})");
                Logger.Warn($"[API] 消息清理完成：{string.Join("，", parts)}，剩余 {cleanedMessages.Count} 条");
            }

            // ── P1-3 修复：无条件使用已克隆的 cleanedMessages。──
            // 即便没有移除/合并（最常见路径），也必须切换到克隆对象，
            // 保证后续 Rule5/Rule6 对 m.ToolCalls / m.ReasoningContent 的就地修改
            // 只作用在克隆上，绝不污染调用方（ConversationContextManager）的消息对象。
            request.Messages = cleanedMessages;

            // ── 规则 5：assistant-with-tool_calls 完整性检测 ──
            // 场景：ExploreAgent/PlanAgent 从 ContextManager 拿到父对话的 assistant(tool_calls)，
            // 但对应 tool 结果不在 _entries 中，导致 assistant(tool_calls) 后直接跟 system/user。
            // DeepSeek API 要求每个 tool_call_id 都有 tool 消息；并行调用只回来部分结果时，
            // 上游会返回 "insufficient tool messages"。此处剥离未配对的 tool_calls。
            var finalMessages = request.Messages;
            int rule5StrippedCount = 0;
            for (int i = 0; i < finalMessages.Count; i++)
            {
                var m = finalMessages[i];
                if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    // 收集该 assistant 的 tool_call IDs
                    var expectedIds = new HashSet<string>(
                        m.ToolCalls
                            .Where(tc => !string.IsNullOrEmpty(tc.Id))
                            .Select(tc => tc.Id!),
                        StringComparer.Ordinal);
                    var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
                    int stopAtIndex = -1;

                    // DeepSeek 要求每个 tool_call_id 都有对应 tool 消息；只有存在非 tool
                    // 消息或已收齐全部结果时才停止扫描。
                    for (int j = i + 1; j < finalMessages.Count; j++)
                    {
                        var next = finalMessages[j];
                        if (next.Role != "tool")
                        {
                            stopAtIndex = j;
                            break;
                        }

                        if (!string.IsNullOrEmpty(next.ToolCallId)
                            && expectedIds.Contains(next.ToolCallId))
                        {
                            resolvedIds.Add(next.ToolCallId);
                        }

                        if (resolvedIds.Count == expectedIds.Count)
                            break;
                    }

                    bool hasCompleteToolChain =
                        expectedIds.Count > 0 && resolvedIds.Count == expectedIds.Count;
                    if (!hasCompleteToolChain)
                    {
                        var tcNames = string.Join(", ", m.ToolCalls.Select(tc => tc.Function?.Name ?? "?"));
                        var missingTools = string.Join(", ", m.ToolCalls
                            .Where(tc => string.IsNullOrEmpty(tc.Id) || !resolvedIds.Contains(tc.Id))
                            .Select(tc => $"{tc.Function?.Name ?? "?"}({tc.Id ?? "?"})"));
                        string stopReason = stopAtIndex >= 0
                            ? $"遇到非tool消息[{stopAtIndex}](role={finalMessages[stopAtIndex].Role})"
                            : "到达消息列表末尾";
                        int originalToolCount = m.ToolCalls.Count;

                        Logger.Warn(
                            $"[API] Rule5 工具链不完整 assistant[{i}]: " +
                            $"expected={expectedIds.Count}, resolved={resolvedIds.Count}, " +
                            $"toolCount={originalToolCount} names=[{tcNames}] missing=[{missingTools}] " +
                            $"stopReason={stopReason} hasContent={!string.IsNullOrEmpty(m.Content)}");

                        if (resolvedIds.Count == 0)
                        {
                            // 没有任何 tool 结果：整个 tool_calls 链是孤立的，剥离全部。
                            m.ToolCalls = null;
                            m.ReasoningContent = null;
                            rule5StrippedCount += originalToolCount;
                        }
                        else
                        {
                            // 只保留已配对的 tool_calls，避免把缺失结果的 ID 发给上游。
                            m.ToolCalls = m.ToolCalls
                                .Where(tc => !string.IsNullOrEmpty(tc.Id) && resolvedIds.Contains(tc.Id))
                                .ToList();
                            rule5StrippedCount += originalToolCount - m.ToolCalls.Count;
                            Logger.Warn(
                                $"[API] Rule5 保留已配对 tool_calls {m.ToolCalls.Count}/{originalToolCount}");
                        }

                        if ((m.ToolCalls == null || m.ToolCalls.Count == 0)
                            && string.IsNullOrEmpty(m.Content))
                        {
                            Logger.Warn($"[API] Rule5 不完整 assistant[{i}] 无 content，标记移除");
                        }
                    }
                }
            }
            // 移除空的孤立 assistant（无 content 且 tool_calls 已被剥离）
            int beforeRemove = request.Messages.Count;
            request.Messages = finalMessages
                .Where(m => !(m.Role == "assistant" && string.IsNullOrEmpty(m.Content) && (m.ToolCalls == null || m.ToolCalls.Count == 0)))
                .ToList();
            int removedEmptyAssistants = beforeRemove - request.Messages.Count;
            if (rule5StrippedCount > 0 || removedEmptyAssistants > 0)
            {
                Logger.Info($"[API] Rule5 汇总: stripped={rule5StrippedCount} removedEmpty={removedEmptyAssistants} remaining={request.Messages.Count}");
            }

            // ── 规则 6：移除孤立的 tool 消息（tool_call_id 找不到对应 assistant）──
            // 场景：ExploreAgent 内部 tool 结果泄漏到主 ContextManager，但对应 assistant(tool_calls) 未写入，
            // 导致 tool 消息的 tool_call_id 没有前置 assistant 声明 → DeepSeek API 返回 400。
            var validToolCallIds = new HashSet<string>();
            foreach (var m in request.Messages)
            {
                if (m.Role == "assistant" && m.ToolCalls != null)
                {
                    foreach (var tc in m.ToolCalls)
                        if (!string.IsNullOrEmpty(tc.Id))
                            validToolCallIds.Add(tc.Id);
                }
            }

            // ──  诊断：遍历所有 tool 消息，记录哪些会被移除及原因 ──
            int totalToolMsgs = 0;
            var orphanDetails = new List<string>();
            for (int i = 0; i < request.Messages.Count; i++)
            {
                var m = request.Messages[i];
                if (m.Role != "tool") continue;
                totalToolMsgs++;
                if (string.IsNullOrEmpty(m.ToolCallId))
                {
                    orphanDetails.Add($"  [{i}] name={m.Name} — 缺少 tool_call_id");
                    continue;
                }
                if (!validToolCallIds.Contains(m.ToolCallId))
                {
                    orphanDetails.Add($"  [{i}] name={m.Name} tcid={m.ToolCallId.Truncate(40)} — tool_call_id 无匹配 assistant");
                }
            }
            if (orphanDetails.Count > 0)
            {
                Logger.Info($"[API] Rule6 诊断: totalToolMsgs={totalToolMsgs}, validToolCallIds={validToolCallIds.Count}, orphanCandidates={orphanDetails.Count}");
                foreach (var detail in orphanDetails)
                    Logger.Info(detail);
            }

            int orphanToolCount = request.Messages.RemoveAll(m =>
                m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId) && !validToolCallIds.Contains(m.ToolCallId));
            if (orphanToolCount > 0)
            {
                Logger.Warn($"[API] 移除 {orphanToolCount} 条孤立 tool 消息（tool_call_id 无匹配 assistant），避免 HTTP 400；剩余 {request.Messages.Count} 条");
            }

            // ── 保存本次实际发送的消息快照，供 Handoff 复用 ──
            LastSentMessages = request.Messages
                .Select(m => new ChatApiMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    MultimodalContent = m.MultimodalContent,
                    ReasoningContent = m.ReasoningContent,
                    ToolCalls = m.ToolCalls?
                        .Select(tc => new ToolCall
                        {
                            Id = tc.Id,
                            Type = tc.Type,
                            Function = new ToolCallFunction
                            {
                                Name = tc.Function?.Name,
                                Arguments = tc.Function?.Arguments,
                            }
                        }).ToList(),
                    ToolCallId = m.ToolCallId,
                    Name = m.Name,
                })
                .ToList();

            // ── 预序列化请求体，供重试时复用 ──
            var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var requestBodyBytes = Encoding.UTF8.GetBytes(requestJson);

            // ── 记录请求元数据 + 消息结构诊断 ──
            int diagSys = request.Messages.Count(m => m.Role == "system");
            int diagUser = request.Messages.Count(m => m.Role == "user");
            int diagAst = request.Messages.Count(m => m.Role == "assistant");
            int diagTool = request.Messages.Count(m => m.Role == "tool");
            int diagAstTc = request.Messages.Count(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0);
            Logger.Info($"[API] 发送请求: {requestBodyBytes.Length / 1024}KB, 消息数={request.Messages.Count}, 工具数={tools?.Count ?? 0}, maxTokens={maxTokens}");
            Logger.Info($"[API] 消息结构(清洗后): system={diagSys}, user={diagUser}, assistant={diagAst}, tool={diagTool} (含工具调用={diagAstTc})");

            // ── messages 前缀分段诊断（DeepSeek 缓存仅匹配 messages 字段）──
            int msg0Length = 0;
            try
            {
                if (request.Messages.Count > 0)
                {
                    // 单独序列化 messages[0] 估算其 token 占比
                    var msg0Json = JsonSerializer.Serialize(request.Messages[0], new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    });
                    msg0Length = Encoding.UTF8.GetByteCount(msg0Json);

                    // 序列化全部 messages 估算总长度
                    var allMsgJson = JsonSerializer.Serialize(request.Messages, new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    });
                    int allMsgLength = Encoding.UTF8.GetByteCount(allMsgJson);

                    Logger.Info($"[Cache] 前缀分段: messages[0]≈{msg0Length / 1024.0:F1}KB | " +
                        $"全部messages≈{allMsgLength / 1024.0:F1}KB | " +
                        $"总请求体={requestBodyBytes.Length / 1024.0:F1}KB | " +
                        $"工具数={tools?.Count ?? 0}(不参与缓存)");
                }
            }
            catch { }

            // ── 消息结构分解日志（诊断缓存命中率用）──
            try
            {
                int sysCount = request.Messages.Count(m => m.Role == "system");
                int userCount = request.Messages.Count(m => m.Role == "user");
                int asstCount = request.Messages.Count(m => m.Role == "assistant");
                int toolCount = request.Messages.Count(m => m.Role == "tool");
                int asstWithToolCalls = request.Messages.Count(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0);
                Logger.Info($"[Cache] 消息结构: system={sysCount}, user={userCount}, assistant={asstCount}, tool={toolCount} (含工具调用={asstWithToolCalls})");
            }
            catch { }

            // ── 前缀缓存稳定性检查（v1.1.9）──
            //     在发送前对比 system prompt + tool catalog 的 SHA-256 指纹，
            //     检测前缀漂移并记录日志，保障 V4 自动前缀缓存命中率可观测。
            //     
            //      v1.1.11：仅对使用标准 SharedImmutablePrefix 的调用执行检查。
            //     非标准调用（如代码变更总结、API Key 验证等）使用自定义短 prompt，
            //     不应污染 PrefixCache 的 pinned 基准，避免导致后续正常调用误判漂移。
            if (PrefixCache != null)
            {
                string? systemPrompt = request.Messages.Count > 0 && request.Messages[0].Role == "system"
                    ? request.Messages[0].Content
                    : null;

                bool isStandardPrefix = systemPrompt != null
                    && systemPrompt == AiPrompts.SharedImmutablePrefix;

                if (isStandardPrefix)
                {
                    var driftInfo = PrefixCache.CheckCurrentPrefix(systemPrompt, normalizedTools);

                    if (!driftInfo.IsInitialPin)
                    {
                        string driftTag = driftInfo.HasDrift ? " 漂移" : " 稳定";
                        Logger.Info($"[Cache] 前缀指纹状态: {driftTag} | 稳定性={PrefixCache.StabilityRatio:P1} ({PrefixCache.StableChecks}/{PrefixCache.TotalChecks})");
                    }
                }
                else
                {
                    Logger.Info($"[Cache] 前缀指纹检查跳过: 非标准前缀 (len={systemPrompt?.Length ?? 0}), 不参与 PrefixCache 基准");
                }
            }
            else
            {
                Logger.Warn("[Cache] PrefixCache 未注入，无法进行前缀稳定性监控");
            }

            // ── 消息前缀哈希日志（v1.1.11）──
            //     计算 messages 在关键前缀边界 [0]、[0..1]、[0..2]、[0..all] 处的 SHA-256 哈希。
            //     跨调用对比哈希值可精确定位缓存断裂发生的位置：
            //     - [0] 变化 → 稳定 system（共享前缀+fixedPrompt）不一致（不应发生）
            //     - [0..1] 变化 → 动态上下文（搜索/记忆/RAG）变化
            //     - [0..2] 变化 → 对话历史增长或压缩
            //     - [0..all] 变化 → 对话历史增长或压缩
            try
            {
                var prefixHashes = PrefixCacheManager.ComputeMessagePrefixHashes(request.Messages);
                if (prefixHashes.Count > 0)
                {
                    string hashLog = PrefixCacheManager.FormatPrefixHashes(prefixHashes, request.Messages.Count);
                    Logger.Info($"[Cache] 消息前缀哈希: {hashLog}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 消息前缀哈希计算失败: {ex.Message}");
            }

            // ── HTTP 层重试（指数退避：1s, 2s, 4s；最多 3 次额外重试）──
            HttpResponseMessage? response = null;
            int sendAttempt = 0;
            const int maxSendAttempts = 4;
            while (sendAttempt < maxSendAttempts)
            {
                string requestContext =
                    $"provider={ProviderName}, model={request.Model}, endpoint={BuildRequestUri(ChatEndpoint)}, " +
                    $"messages={request.Messages.Count}, tools={normalizedTools?.Count ?? 0}, " +
                    $"requestBytes={requestBodyBytes.Length}, attempt={sendAttempt + 1}/{maxSendAttempts}";

                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(ChatEndpoint))
                    {
                        Content = new ByteArrayContent(requestBodyBytes)
                    };
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    response = await _httpClient.SendAsync(
                        req,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        string preparedDetails = await BuildHttpErrorDetailsAsync(response, requestContext);
                        var statusException = new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null);
                        statusException.Data[HttpErrorDetailsDataKey] = preparedDetails;
                        throw statusException;
                    }

                    // ── 连接复用诊断（v1.1.11）：追踪 ServicePoint 连接状态 ──
                    //     帮助判断 Agent 间是否复用同一 TCP 连接，从而影响缓存亲和性。
                    LogConnectionReuseDiagnostics();

                    break; // success
                }
                catch (HttpRequestException ex) when (sendAttempt < maxSendAttempts - 1)
                {
                    int statusCode = (int)(response?.StatusCode ?? 0);

                    // 4xx 客户端错误（除 429 限流外）不应重试——请求本身有问题，重试不会改变结果
                    if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
                    {
                        string details =
                            ex.Data[HttpErrorDetailsDataKey] as string
                            ?? (response != null
                                ? await BuildHttpErrorDetailsAsync(response, requestContext)
                                : $"请求: {requestContext}\n异常: {ex.GetType().Name}: {ex.Message}");
                        Logger.Error($"[API] HTTP {statusCode} 是客户端错误，放弃重试。\n{details}");
                        response?.Dispose();
                        throw new HttpRequestException(
                            $"{ProviderName} API 返回 HTTP {statusCode}。\n{details}", ex);
                    }

                    sendAttempt++;
                    string retryDetails =
                        ex.Data[HttpErrorDetailsDataKey] as string
                        ?? (response != null
                            ? await BuildHttpErrorDetailsAsync(response, requestContext, maxBodyChars: 500)
                            : $"请求: {requestContext}\n异常: {ex.GetType().Name}: {ex.Message}");
                    response?.Dispose();
                    double backoff = Math.Pow(2, sendAttempt - 1);
                    Logger.Warn(
                        $"[API] HTTP {statusCode} 请求失败 (尝试 {sendAttempt + 1}/{maxSendAttempts})，{backoff}s 后重试…\n" +
                        retryDetails);
                    // ── 真正等待退避（与超时分支一致）；此前只打日志不等待，4 次请求零间隔连发 ──
                    await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && sendAttempt < maxSendAttempts - 1)
                {
                    // 超时（非用户取消）
                    sendAttempt++;
                    response?.Dispose();
                    double backoff = Math.Pow(2, sendAttempt - 1);
                    Logger.Warn($"[API] 请求超时 (尝试 {sendAttempt + 1}/{maxSendAttempts})，{backoff}s 后重试…\n请求: {requestContext}");
                    await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    int statusCode = (int)(response?.StatusCode ?? 0);
                    string details =
                        ex.Data[HttpErrorDetailsDataKey] as string
                        ?? (response != null
                            ? await BuildHttpErrorDetailsAsync(response, requestContext)
                            : $"请求: {requestContext}\n异常: {ex.GetType().Name}: {ex.Message}");
                    Logger.Error($"[API] HTTP {statusCode} 请求失败，已停止重试。\n{details}");
                    response?.Dispose();
                    throw new HttpRequestException(
                        $"{ProviderName} API 请求失败 (HTTP {statusCode})。\n{details}", ex);
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

            // ── 本地函数：输出缓存诊断 + 磁盘转储 ──
            void FlushCacheDiagnostics()
            {
                try
                {
                    if (LastUsage == null) return;
                    int hit = LastUsage.PromptCacheHitTokens;
                    int miss = LastUsage.PromptCacheMissTokens;
                    int cacheableTotal = hit + miss;
                    if (cacheableTotal <= 0)
                    {
                        Logger.Info($"[Cache]  API调用完成: 无可缓存数据 (prompt {LastUsage.PromptTokens:N0} tokens)");
                        return;
                    }

                    double rate = (double)hit / cacheableTotal;
                    string level = rate >= 0.90 ? "🟢" : rate >= 0.50 ? "🟡" : rate >= 0.20 ? "🟠" : "🔴";

                    const int bytesPerToken = 3;
                    int msg0TokenEstimate = msg0Length / bytesPerToken;
                    string missBoundary;
                    if (msg0Length > 0 && hit >= msg0TokenEstimate * 0.8)
                        missBoundary = $"messages[0] 命中 → miss 在对话历史/动态块之后";
                    else if (msg0Length > 0)
                        missBoundary = $"messages[0] 未完全命中！命中={hit} tokens, messages[0]≈{msg0TokenEstimate} tokens → SharedImmutablePrefix 可能已变化";
                    else
                        missBoundary = "（无分段数据）";

                    Logger.Info($"[Cache] {level} API调用完成: 命中率={rate * 100:F1}% (命中 {hit:N0} / 未命中 {miss:N0} / 可缓存 {cacheableTotal:N0} / prompt {LastUsage.PromptTokens:N0} tokens)\n" +
                        $"        ↳ 边界: {missBoundary}");
                    }
                catch (Exception ex)
                {
                    Logger.Warn($"[Cache] 缓存诊断输出异常: {ex.Message}");
                }
            }

            // ── SSE 流读取超时保护（v1.1.10）──
            // 问题：.NET Framework 4.7.2 的 ReadLineAsync 不接受 CancellationToken，
            // 当网络静默断开（TCP 无 RST/FIN）时 ReadLineAsync 会永久挂起。
            // 修复：创建 linked CTS，每收到一条数据重置超时计时器，
            // 超时后 Dispose 底层流使 ReadLineAsync 抛出 ObjectDisposedException，
            // 调用方将其转为可重试异常。
            // 用户取消 (ct) 仍正常传递 → 释放流 → 退出循环。
            const int sseReadTimeoutSeconds = 120; // 2 分钟无数据视为断连
            using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeoutCts.CancelAfter(TimeSpan.FromSeconds(sseReadTimeoutSeconds));

            // 注册：任一取消源触发时释放流，打断 ReadLineAsync
            using (readTimeoutCts.Token.Register(() =>
            {
                try { stream.Dispose(); } catch { }
            }))
            {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                // 用户主动取消立即抛出
                cancellationToken.ThrowIfCancellationRequested();

                // 每次成功读到数据，重置无数据超时计时器
                readTimeoutCts.CancelAfter(TimeSpan.FromSeconds(sseReadTimeoutSeconds));

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
                    // ── 在 [DONE] 处立即输出缓存诊断 + 磁盘转储 ──
                    FlushCacheDiagnostics();
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
                    // P3-7：空 choices 数组（如仅携带 usage 的尾包）会令索引器抛
                    // ArgumentOutOfRangeException 且不在下方 catch 白名单内，直接击穿流迭代器。
                    var delta = chunk?.Choices is { Count: > 0 } ? chunk.Choices[0]?.Delta : null;
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

            // ── 流正常结束（无 [DONE] 时）输出缓存诊断 ──
            FlushCacheDiagnostics();
            } // using(response) — 重试块闭合
        }

        /// <summary>
        /// 合并连续的用户消息，同时保留视觉内容块。视觉模型的 content 是数组，
        /// 不能像纯文本一样直接字符串拼接，否则 image_url 块会丢失。
        /// </summary>
        private static List<ChatContentPart> MergeUserContentParts(
            ChatApiMessage first,
            ChatApiMessage second)
        {
            var result = new List<ChatContentPart>();
            var seenImageUrls = new HashSet<string>(StringComparer.Ordinal);
            var seenText = new HashSet<string>(StringComparer.Ordinal);

            if (first.MultimodalContent is { Count: > 0 } firstParts)
                AddContentPartsDeduplicated(result, firstParts, seenImageUrls, seenText);
            else if (!string.IsNullOrWhiteSpace(first.Content))
                AddTextDeduplicated(result, first.Content, seenText);

            if (second.MultimodalContent is { Count: > 0 } secondParts)
                AddContentPartsDeduplicated(result, secondParts, seenImageUrls, seenText);
            else if (!string.IsNullOrWhiteSpace(second.Content))
                AddTextDeduplicated(result, second.Content, seenText);

            return result;
        }

        private static void AddContentPartsDeduplicated(
            List<ChatContentPart> target,
            IEnumerable<ChatContentPart> parts,
            HashSet<string> seenImageUrls,
            HashSet<string> seenText)
        {
            foreach (var part in parts)
            {
                if (part.Type == "image_url" && part.ImageUrl?.Url != null)
                {
                    if (seenImageUrls.Add(part.ImageUrl.Url))
                    {
                        target.Add(new ChatContentPart
                        {
                            Type = part.Type,
                            ImageUrl = new ChatImageUrl
                            {
                                Url = part.ImageUrl.Url,
                                Detail = part.ImageUrl.Detail,
                            },
                        });
                    }
                    continue;
                }

                if (part.Type == "text")
                {
                    AddTextDeduplicated(target, part.Text ?? string.Empty, seenText);
                    continue;
                }

                target.Add(CloneContentParts(new List<ChatContentPart> { part })[0]);
            }
        }

        private static void AddTextDeduplicated(
            List<ChatContentPart> target,
            string text,
            HashSet<string> seenText)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (seenText.Add(text))
                target.Add(TextContentPart(text));
        }

        private static List<ChatContentPart> CloneContentParts(List<ChatContentPart> parts)
        {
            return parts.Select(p => new ChatContentPart
            {
                Type = p.Type,
                Text = p.Text,
                ImageUrl = p.ImageUrl == null
                    ? null
                    : new ChatImageUrl { Url = p.ImageUrl.Url, Detail = p.ImageUrl.Detail },
                File = p.File == null
                    ? null
                    : new ChatFilePart
                    {
                        FileId = p.File.FileId,
                        FileData = p.File.FileData,
                        Filename = p.File.Filename,
                    },
            }).ToList();
        }

        private static ChatContentPart TextContentPart(string text)
        {
            return new ChatContentPart
            {
                Type = "text",
                Text = text,
            };
        }

        /// <summary>
        /// 深克隆一条消息（连同 ToolCalls / MultimodalContent 内元素一并新建）。
        /// 用于保证 API 消息清理（Rule5/6、ReasoningContent 注入）只改动克隆对象，
        /// 绝不污染调用方（ConversationContextManager）持有的消息实例。
        /// </summary>
        internal static ChatApiMessage CloneMessage(ChatApiMessage m)
        {
            return new ChatApiMessage
            {
                Role = m.Role,
                Content = m.Content,
                MultimodalContent = m.MultimodalContent == null ? null : new List<ChatContentPart>(m.MultimodalContent),
                ReasoningContent = m.ReasoningContent,
                ToolCalls = m.ToolCalls?.Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = tc.Function == null
                        ? new ToolCallFunction()
                        : new ToolCallFunction { Name = tc.Function.Name, Arguments = tc.Function.Arguments },
                }).ToList(),
                ToolCallId = m.ToolCallId,
                Name = m.Name,
            };
        }

        /// <summary>
        /// 非流式调用 API，用于搜索查询优化等需要快速完整响应的场景。
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="responseFormat">JSON Output 模式: "json_object" 启用，null 不启用</param>
        /// <returns>AI 返回的完整文本内容</returns>
        public async Task<string> CompleteAsync(
            IEnumerable<ChatApiMessage> messages,
            CancellationToken cancellationToken = default,
            string? responseFormat = null)
        {
            var request = new DeepSeekChatRequest
            {
                Model = _model,
                // P1-3 修复：非流式路径同样深克隆，避免对调用方消息对象就地修改（ReasoningContent 注入）
                Messages = messages.Select(m => CloneMessage(m)).ToList(),
                Stream = false,
                ResponseFormat = responseFormat == "json_object"
                    ? new ResponseFormat { Type = "json_object" }
                    : null
            };
            ApplyProviderRequestOptions(request, thinkingEnabled: false);
            ApplyProviderEndpointShaping(request, isStreaming: false);

            // Defensive check for non-streaming path as well
            foreach (var msg in request.Messages)
            {
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0 && msg.ReasoningContent == null)
                {
                    Logger.Warn("[API] (CompleteAsync) assistant message contains tool_calls but missing ReasoningContent — injecting empty string to avoid 400");
                    msg.ReasoningContent = string.Empty;
                }
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(ChatEndpoint))
            {
                Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            await ValidateResponseStatusAsync(
                response,
                $"model={request.Model}, endpoint={BuildRequestUri(ChatEndpoint)}, messages={request.Messages.Count}");
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DeepSeekChatResponse>(responseJson);

            // ── 捕获 Usage 信息（含 Cache 命中统计）──
            if (result?.Usage != null)
            {
                LastUsage = result.Usage;
                AccumulateStats(result.Usage);
            }

            // P3-7：空 choices 数组防索引越界（与流式路径同一守卫）
            return result?.Choices is { Count: > 0 } ? result.Choices[0]?.Message?.Content ?? string.Empty : string.Empty;
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
                };
                ApplyProviderRequestOptions(request, thinkingEnabled: false);
                ApplyProviderEndpointShaping(request, isStreaming: false);

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(ChatEndpoint))
                {
                    Content = JsonContent.Create(request, options: new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    })
                };

                using var response = await _httpClient.SendAsync(httpRequest);
                await ValidateResponseStatusAsync(
                    response,
                    $"model={request.Model}, endpoint={BuildRequestUri(ChatEndpoint)}");
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

        private const int MaxErrorBodyChars = 4096;
        private const int MaxResponseHeaderChars = 2048;
        private const string HttpErrorDetailsDataKey = "DeepSeek.HttpErrorDetails";

        /// <summary>
        /// 读取错误响应体；读取失败或为空时返回可诊断的占位文本。
        /// </summary>
        internal static async Task<string> ReadResponseBodySafelyAsync(
            HttpResponseMessage response,
            int maxChars = MaxErrorBodyChars)
        {
            if (response.Content == null) return string.Empty;

            try
            {
                string body = await response.Content.ReadAsStringAsync();
                return TruncateForDiagnostics(body, maxChars);
            }
            catch (Exception ex)
            {
                return $"<读取响应体失败: {ex.GetType().Name}: {ex.Message}>";
            }
        }

        /// <summary>
        /// 构造包含状态、请求上下文、响应头和响应体的错误详情。
        /// </summary>
        internal static string BuildHttpErrorDetails(
            HttpResponseMessage response,
            string? requestContext,
            string? responseBody)
        {
            var sb = new StringBuilder();
            sb.Append("状态: HTTP ").Append((int)response.StatusCode);
            if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
                sb.Append(' ').Append(response.ReasonPhrase);

            if (!string.IsNullOrWhiteSpace(requestContext))
                sb.Append("\n请求: ").Append(requestContext.Trim());

            if (response.RequestMessage?.RequestUri != null)
            {
                sb.Append("\n请求 URI: ")
                  .Append(response.RequestMessage.Method)
                  .Append(' ')
                  .Append(response.RequestMessage.RequestUri);
            }

            sb.Append("\n响应头: ").Append(FormatResponseHeaders(response));
            sb.Append("\n响应体: ")
              .Append(string.IsNullOrWhiteSpace(responseBody) ? "<empty>" : responseBody.Trim());
            return sb.ToString();
        }

        internal static async Task<string> BuildHttpErrorDetailsAsync(
            HttpResponseMessage response,
            string? requestContext = null,
            int maxBodyChars = MaxErrorBodyChars)
        {
            string body = await ReadResponseBodySafelyAsync(response, maxBodyChars);
            return BuildHttpErrorDetails(response, requestContext, body);
        }

        private static string FormatResponseHeaders(HttpResponseMessage response)
        {
            var parts = new List<string>();

            foreach (var header in response.Headers)
            {
                if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    continue;
                parts.Add($"{header.Key}={string.Join(",", header.Value)}");
            }

            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    parts.Add($"{header.Key}={string.Join(",", header.Value)}");
                }
            }

            string result = parts.Count == 0 ? "<none>" : string.Join("; ", parts);
            return TruncateForDiagnostics(result, MaxResponseHeaderChars);
        }

        private static string TruncateForDiagnostics(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value;
            return value.Substring(0, maxChars) + $"…(截断，原始长度 {value.Length})";
        }

        /// <summary>
        /// 检查 HTTP 响应状态，对认证错误抛出 ApiKeyInvalidException.
        /// </summary>
        protected async Task ValidateResponseStatusAsync(
            HttpResponseMessage response,
            string? requestContext = null)
        {
            if (response.IsSuccessStatusCode) return;

            int statusCode = (int)response.StatusCode;
            string body = await ReadResponseBodySafelyAsync(response);
            string details = BuildHttpErrorDetails(response, requestContext, body);
            Logger.Error($"[API] {ProviderName} 返回错误 HTTP {statusCode}。\n{details}");

            if (statusCode == 401 || statusCode == 403)
            {
                string detail = ExtractErrorMessage(body);
                throw new ApiKeyInvalidException(
                    $"{ProviderName} API Key 无效或已过期 (HTTP {statusCode})。\n" +
                    $"请通过 工具 → 选项 → DeepSeek Chat 重新配置 API Key。\n" +
                    (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}\n") +
                    details);
            }

            if (statusCode == 429)
            {
                throw new ApiKeyInvalidException(
                    $"{ProviderName} API 请求频率超限 (HTTP 429)，请稍后重试。\n{details}");
            }

            if (statusCode >= 500)
            {
                throw new ApiKeyInvalidException(
                    $"{ProviderName} 服务器错误 (HTTP {statusCode})，请稍后重试。\n{details}");
            }

            // 其他 4xx 客户端错误：抛出带完整诊断上下文的异常，便于定位请求格式问题。
            if (statusCode >= 400 && statusCode < 500)
            {
                string detail = ExtractErrorMessage(body);
                throw new InvalidOperationException(
                    $"{ProviderName} API 返回 HTTP {statusCode}:" +
                    (string.IsNullOrEmpty(detail) ? "" : $" {detail}") +
                    $"\n{details}");
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
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                        return error.GetString() ?? string.Empty;

                    if (error.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? string.Empty;

                    if (error.TryGetProperty("detail", out var errorDetail))
                        return errorDetail.GetString() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("message", out var rootMessage))
                    return rootMessage.GetString() ?? string.Empty;

                if (doc.RootElement.TryGetProperty("detail", out var rootDetail))
                    return rootDetail.GetString() ?? string.Empty;
            }
            catch { }
            return responseBody.Length > 200 ? responseBody.Substring(0, 200) : responseBody;
        }

        public virtual void Dispose() => _httpClient?.Dispose();
    }
}
