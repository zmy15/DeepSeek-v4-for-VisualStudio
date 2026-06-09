using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// 前缀缓存稳定性管理器（可选注入）。
        /// 设置后，每次 ChatStreamAsync 调用前会自动检查前缀指纹并记录漂移。
        /// </summary>
        public PrefixCacheManager? PrefixCache { get; set; }

        /// <summary>
        /// 最近一次 API 调用的 Usage 信息（含 Cache 命中统计）。
        /// 流式调用结束后更新，非流式调用后立即可用。
        /// </summary>
        public DeepSeekUsage? LastUsage { get; private set; }

        // ── 线程安全的累计统计字段（使用 Interlocked 保证多 Agent 并行调用时正确累加）──
        private long _totalCacheHitTokens;
        private long _totalCacheMissTokens;
        private long _totalPromptTokens;
        private long _totalCompletionTokens;

        /// <summary>
        /// 累计 Chat API 统计（跨所有 API 调用汇总，含 Agent 内部调用）。
        /// 在每次 API 调用后自动累加。调用 <see cref="ResetAccumulatedStats"/> 重置。
        /// 注意：FIM（代码补全）的 Token 独立统计在 <see cref="TotalFimPromptTokens"/> / <see cref="TotalFimCompletionTokens"/>。
        /// </summary>
        public long TotalCacheHitTokens => Interlocked.Read(ref _totalCacheHitTokens);
        public long TotalCacheMissTokens => Interlocked.Read(ref _totalCacheMissTokens);
        public long TotalPromptTokens => Interlocked.Read(ref _totalPromptTokens);
        public long TotalCompletionTokens => Interlocked.Read(ref _totalCompletionTokens);

        // ── FIM（代码补全）独立统计，不与聊天 Token 混合 ──
        private long _totalFimPromptTokens;
        private long _totalFimCompletionTokens;

        /// <summary>FIM 代码补全累计 Prompt Token 数（独立于聊天统计）</summary>
        public long TotalFimPromptTokens => Interlocked.Read(ref _totalFimPromptTokens);
        /// <summary>FIM 代码补全累计 Completion Token 数（独立于聊天统计）</summary>
        public long TotalFimCompletionTokens => Interlocked.Read(ref _totalFimCompletionTokens);

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
        /// </summary>
        public void ResetAccumulatedStats()
        {
            Interlocked.Exchange(ref _totalCacheHitTokens, 0);
            Interlocked.Exchange(ref _totalCacheMissTokens, 0);
            Interlocked.Exchange(ref _totalPromptTokens, 0);
            Interlocked.Exchange(ref _totalCompletionTokens, 0);
        }

        /// <summary>
        /// 从持久化数据恢复累计 Chat 统计（重启后调用）。
        /// </summary>
        public void RestoreAccumulatedStats(long hitTokens, long missTokens, long promptTokens, long completionTokens)
        {
            Interlocked.Exchange(ref _totalCacheHitTokens, hitTokens);
            Interlocked.Exchange(ref _totalCacheMissTokens, missTokens);
            Interlocked.Exchange(ref _totalPromptTokens, promptTokens);
            Interlocked.Exchange(ref _totalCompletionTokens, completionTokens);
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
        /// </summary>
        private void AccumulateStats(DeepSeekUsage usage)
        {
            Interlocked.Add(ref _totalCacheHitTokens, usage.PromptCacheHitTokens);
            Interlocked.Add(ref _totalCacheMissTokens, usage.PromptCacheMissTokens);
            Interlocked.Add(ref _totalPromptTokens, usage.PromptTokens);
            Interlocked.Add(ref _totalCompletionTokens, usage.CompletionTokens);
        }

        /// <summary>
        /// 线程安全地累加 FIM（代码补全）的 Usage 统计。
        /// FIM Token 独立于聊天 Token 统计，避免右下角计数器虚高。
        /// </summary>
        private void AccumulateFimStats(DeepSeekUsage usage)
        {
            Interlocked.Add(ref _totalFimPromptTokens, usage.PromptTokens);
            Interlocked.Add(ref _totalFimCompletionTokens, usage.CompletionTokens);
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

        /// <summary>API 请求序号（用于转储文件命名）</summary>
        private static int _requestSequence;

        /// <summary>
        /// 将完整 API 请求体 + 缓存命中统计写入磁盘，供离线分析。
        /// 文件路径: %TEMP%\DeepSeekCacheDumps\req_{序号}_{时间戳}.json
        /// </summary>
        private static void DumpRequestToDisk(string requestJson, int requestBytes,
            int hitTokens, int missTokens, int cacheableTokens, double hitRate,
            int messageCount, int toolCount, string? errorMessage = null)
        {
            try
            {
                int seq = Interlocked.Increment(ref _requestSequence);
                string dir = Path.Combine(Path.GetTempPath(), "DeepSeekCacheDumps");
                Directory.CreateDirectory(dir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string fileName = $"req_{seq:D4}_{timestamp}.json";
                string filePath = Path.Combine(dir, fileName);

                // 解析 messages 做摘要（避免文件过大）
                List<object> msgSummaries;
                try
                {
                    using var doc = JsonDocument.Parse(requestJson);
                    var msgs = doc.RootElement.GetProperty("messages");
                    msgSummaries = new List<object>();
                    foreach (var m in msgs.EnumerateArray())
                    {
                        string role = m.GetProperty("role").GetString() ?? "?";
                        string? content = null;
                        if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            content = c.GetString();
                        bool hasToolCalls = m.TryGetProperty("tool_calls", out _);

                        msgSummaries.Add(new
                        {
                            role,
                            content_length = content?.Length ?? 0,
                            content_preview = content?.Substring(0, Math.Min(content?.Length ?? 0, 200)),
                            has_tool_calls = hasToolCalls
                        });
                    }
                }
                catch { msgSummaries = new List<object>(); }

                // 解析 requestJson 为 JsonElement，使其在 dump 序列化时使用 relaxed encoder（可读中文）
                using var fullReqDoc = JsonDocument.Parse(requestJson);

                var dump = new
                {
                    sequence = seq,
                    timestamp = DateTime.Now.ToString("O"),
                    error = errorMessage,
                    request = new
                    {
                        size_bytes = requestBytes,
                        size_kb = requestBytes / 1024.0,
                        message_count = messageCount,
                        tool_count = toolCount,
                        messages_summary = msgSummaries,
                        full_request = fullReqDoc.RootElement
                    },
                    cache = errorMessage != null ? null : new
                    {
                        hit_tokens = hitTokens,
                        miss_tokens = missTokens,
                        cacheable_tokens = cacheableTokens,
                        hit_rate = hitRate,
                        hit_rate_pct = $"{hitRate * 100:F1}%"
                    }
                };

                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string dumpJson = JsonSerializer.Serialize(dump, opts);
                File.WriteAllText(filePath, dumpJson, Encoding.UTF8);

                Logger.Info($"[Dump] 请求已写入磁盘: {fileName} ({requestBytes / 1024}KB)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Dump] 写入磁盘失败: {ex.Message}");
            }
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            int? maxTokens = null,
            string? toolChoice = null,
            double? temperature = null,
            string? responseFormat = null)
        {
            // ── 工具 Schema 规范化：按名称排序，消除注册顺序对缓存的影响 ──
            //     参考 CodeWhale prefix_cache.rs:316-331
            List<ToolDefinition>? normalizedTools = ToolSchemaNormalizer.NormalizeForApi(tools);

            // toolChoice 优先级: 显式传入 > 有 tools 时 auto > null(不发送)
            string? effectiveToolChoice = toolChoice
                ?? (normalizedTools != null && normalizedTools.Count > 0 ? "auto" : null);

            var request = new DeepSeekChatRequest
            {
                Model = _model,
                Messages = new List<ChatApiMessage>(messages),
                Stream = true,
                Thinking = new ThinkingControl { Type = _thinkingEnabled ? "enabled" : "disabled" },
                ReasoningEffort = _thinkingEnabled ? _reasoningEffort : null,
                Tools = normalizedTools,
                ToolChoice = effectiveToolChoice,
                MaxTokens = maxTokens,
                Temperature = temperature,
                ResponseFormat = responseFormat == "json_object"
                    ? new ResponseFormat { Type = "json_object" }
                    : null
            };

            // ── 消息清理：防止无效消息导致 HTTP 400 ──
            // DeepSeek API 对消息格式有严格要求：
            // 1. tool 消息必须有 tool_call_id
            // 2. assistant 消息有 tool_calls 时可以没有 content，但不能既无 content 又无 tool_calls
            // 3. 不能有连续的相同 role 消息（user-user, assistant-assistant）→ 合并而非丢弃
            //
            // 🔑 缓存关键（v1.1.10）：所有清理操作在 SHALLOW CLONE 上进行，
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

                // ── 浅克隆：后续所有修改仅影响克隆对象，不污染调用方原始消息 ──
                var clone = new ChatApiMessage
                {
                    Role = msg.Role,
                    Content = msg.Content,
                    ReasoningContent = msg.ReasoningContent,
                    ToolCalls = msg.ToolCalls,
                    ToolCallId = msg.ToolCallId,
                    Name = msg.Name,
                };

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

                        if (!string.IsNullOrWhiteSpace(newContent))
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
                request.Messages = cleanedMessages;
            }

            // ── 规则 5：孤立 assistant-with-tool_calls 检测 ──
            // 场景：ExploreAgent/PlanAgent 从 ContextManager 拿到父对话的 assistant(tool_calls)，
            // 但对应 tool 结果不在 _entries 中，导致 assistant(tool_calls) 后直接跟 system/user。
            // DeepSeek API 要求 assistant(tool_calls) 后必须紧跟 tool 消息 → 剥离 orphan tool_calls。
            var finalMessages = request.Messages;
            int rule5StrippedCount = 0;
            for (int i = 0; i < finalMessages.Count; i++)
            {
                var m = finalMessages[i];
                if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    // 收集该 assistant 的 tool_call IDs
                    var expectedIds = new HashSet<string>(m.ToolCalls.Select(tc => tc.Id ?? ""));
                    // 检查后续消息中是否有匹配的 tool 结果（至少出现一个才算合法）
                    bool hasMatchingToolResult = false;
                    int stopAtIndex = -1;
                    for (int j = i + 1; j < finalMessages.Count; j++)
                    {
                        var next = finalMessages[j];
                        if (next.Role == "tool" && !string.IsNullOrEmpty(next.ToolCallId)
                            && expectedIds.Contains(next.ToolCallId))
                        {
                            hasMatchingToolResult = true;
                            break;
                        }
                        // 遇到非 tool 消息 → 停止搜索，当前 assistant 的 tool_calls 已孤立
                        if (next.Role != "tool")
                        {
                            stopAtIndex = j;
                            break;
                        }
                    }
                    if (!hasMatchingToolResult)
                    {
                        var tcNames = string.Join(", ", m.ToolCalls.Select(tc => tc.Function?.Name ?? "?"));
                        string stopReason = stopAtIndex >= 0
                            ? $"遇到非tool消息[{stopAtIndex}](role={finalMessages[stopAtIndex].Role})"
                            : "到达消息列表末尾";
                        Logger.Warn($"[API] Rule5 孤立 assistant[{i}]: toolCount={m.ToolCalls.Count} names=[{tcNames}] stopReason={stopReason} hasContent={!string.IsNullOrEmpty(m.Content)}");
                        m.ToolCalls = null;
                        m.ReasoningContent = null; // 无 tool_calls 时不应回传 reasoning_content
                        rule5StrippedCount++;
                        if (string.IsNullOrEmpty(m.Content))
                        {
                            Logger.Warn($"[API] Rule5 孤立的 assistant[{i}] 无 content，标记移除");
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

            // ── 🔍 诊断：遍历所有 tool 消息，记录哪些会被移除及原因 ──
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

            // ── 发送前 dump 请求体，确保 HTTP 400 等错误也能捕获 ──
            // DumpRequestToDisk(requestJson, requestBodyBytes.Length,
            //     0, 0, 0, 0,
            //     request.Messages.Count, tools?.Count ?? 0,
            //     "(pre-send)");

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
            //     🔑 v1.1.11：仅对使用标准 SharedImmutablePrefix 的调用执行检查。
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
                        string driftTag = driftInfo.HasDrift ? "⚠️ 漂移" : "✅ 稳定";
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
                        // ── 即使 HTTP 400 也 dump 请求体，便于诊断请求结构问题 ──
                        // DumpRequestToDisk(requestJson, requestBodyBytes.Length,
                        //     0, 0, 0, 0,
                        //     request.Messages.Count, tools?.Count ?? 0,
                        //     $"HTTP {statusCode}: {respSnippet}");
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
                        Logger.Info($"[Cache] ⚪ API调用完成: 无可缓存数据 (prompt {LastUsage.PromptTokens:N0} tokens)");
                        return;
                    }

                    double rate = (double)hit / cacheableTotal;
                    string level = rate >= 0.95 ? "🟢" : rate >= 0.70 ? "🟡" : rate >= 0.30 ? "🟠" : "🔴";

                    const int bytesPerToken = 3;
                    int msg0TokenEstimate = msg0Length / bytesPerToken;
                    string missBoundary;
                    if (msg0Length > 0 && hit >= msg0TokenEstimate * 0.8)
                        missBoundary = $"✅ messages[0] 命中 → miss 在对话历史/动态块之后";
                    else if (msg0Length > 0)
                        missBoundary = $"🔴 messages[0] 未完全命中！命中={hit} tokens, messages[0]≈{msg0TokenEstimate} tokens → SharedImmutablePrefix 可能已变化";
                    else
                        missBoundary = "（无分段数据）";

                    Logger.Info($"[Cache] {level} API调用完成: 命中率={rate * 100:F1}% (命中 {hit:N0} / 未命中 {miss:N0} / 可缓存 {cacheableTotal:N0} / prompt {LastUsage.PromptTokens:N0} tokens)\n" +
                        $"        ↳ 边界: {missBoundary}");

                    // DumpRequestToDisk(requestJson, requestBodyBytes.Length,
                    //     hit, miss, cacheableTotal, rate,
                    //     request.Messages.Count, tools?.Count ?? 0);
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

            // ── 流正常结束（无 [DONE] 时）输出缓存诊断 ──
            FlushCacheDiagnostics();
            } // using(response) — 重试块闭合
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
                Messages = new List<ChatApiMessage>(messages),
                Stream = false,
                Thinking = new ThinkingControl { Type = "disabled" },
                ReasoningEffort = null,
                ResponseFormat = responseFormat == "json_object"
                    ? new ResponseFormat { Type = "json_object" }
                    : null
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

            // 捕获 Usage 信息 — FIM（代码补全）独立统计，不混入聊天 Token
            if (result?.Usage != null)
            {
                LastUsage = result.Usage;
                AccumulateFimStats(result.Usage);
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

        /// <summary>
        /// 查询账户余额。
        /// 端点: GET https://api.deepseek.com/user/balance
        /// </summary>
        public async Task<BalanceResponse?> GetBalanceAsync()
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "/user/balance");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(httpRequest);
                await ValidateResponseStatusAsync(response);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<BalanceResponse>(responseJson);
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[余额] 查询余额失败: {ex.Message}");
                return null;
            }
        }
    }
}