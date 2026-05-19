using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// 所有 Agent 的抽象基类。
    /// 提供：AI 调用、日志、权限请求、文件解析等共享能力。
    /// </summary>
    public abstract class BaseAgent : IDisposable
    {
        protected readonly DeepSeekApiService _apiService;
        protected readonly List<AgentLogEntry> _logs = new();

        /// <summary>
        /// 所有 Agent 共享的 System Prompt 前缀（不含语言指令，由 GetCommonSystemPromptPrefix 动态注入）。
        /// 放在 messages[0]，确保跨 Agent 切换时 DeepSeek Prefix Cache 仍能命中。
        /// </summary>
        protected const string CommonSystemPromptPrefixCore =
            "你是 DeepSeek v4 for Visual Studio——一个集成在 VS Code 中的 AI 编程助手。\n" +
            "你可以使用工具来读取文件、搜索代码库、获取网页内容、运行终端命令等。\n";

        /// <summary>
        /// 获取带语言指令的完整公共前缀（每次调用时根据当前语言动态拼接）。
        /// </summary>
        protected static string GetCommonSystemPromptPrefix()
        {
            return CommonSystemPromptPrefixCore + LocalizationService.Instance["system.agent.languageInstruction"] + "\n";
        }

        /// <summary>
        /// 兼容旧代码：首次访问时返回当前语言的前缀。
        /// </summary>
        protected static string CommonSystemPromptPrefix => GetCommonSystemPromptPrefix();

        /// <summary>Agent 元数据定义</summary>
        public AgentDefinition Definition { get; protected set; }

        /// <summary>当前执行上下文</summary>
        public AgentContext? Context { get; set; }

        /// <summary>内置工具服务引用（由 AgentDispatcher 注入）</summary>
        public BuiltInToolService? BuiltInTools { get; set; }

        /// <summary>MCP 管理器引用（由 AgentDispatcher 注入，用于执行 MCP 工具）</summary>
        public McpManagerService? McpManager { get; set; }

        /// <summary>日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        /// <summary>权限请求事件</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

        /// <summary>文件变更实时通知事件（编辑阶段逐文件推送）</summary>
        public event Action<AgentFileChangeEventArgs>? FileChangeNotified;

        /// <summary>当前待确认的权限请求</summary>
        public AgentPermissionRequest? PendingPermission { get; protected set; }

        protected BaseAgent(DeepSeekApiService apiService, AgentType agentType)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            Definition = CreateDefinition(agentType);
        }

        #region Abstract

        /// <summary>
        /// 子类实现：定义 Agent 的元数据（名称、描述、工具、系统提示词等）。
        /// </summary>
        protected abstract AgentDefinition CreateDefinition(AgentType agentType);

        /// <summary>
        /// 子类实现：Agent 核心执行逻辑。
        /// </summary>
        /// <param name="userMessage">用户消息</param>
        /// <param name="context">执行上下文</param>
        /// <returns>执行结果</returns>
        public abstract Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context);

        #endregion

        #region Shared AI Call Methods

        /// <summary>
        /// 调用 AI 进行简短回答（用于分类、路由判断等）。
        /// 公开给 AgentDispatcher 使用。
        /// </summary>
        public async Task<string> CallAiShortAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 512)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt);

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 调用 AI 进行长回答（用于代码生成、分析等）。
        /// </summary>
        protected async Task<string> CallAiLongAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 4096)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt);

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 调用 AI 进行长回答，支持注入额外的系统级上下文（如 discoveryContext）。
        /// extraSystemMessages 插入在历史与用户消息之间，保持 messages[0] 稳定可缓存。
        /// </summary>
        protected async Task<string> CallAiLongAsync(
            string systemPrompt,
            string userPrompt,
            List<ChatApiMessage> extraSystemMessages,
            CancellationToken ct,
            int maxTokens = 4096)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, extraSystemMessages);

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (!chunk.StartsWith("[THINKING]") && !chunk.StartsWith("[TOOL_CALL]"))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 带对话历史的 AI 调用。
        /// </summary>
        protected async Task<string> CallAiWithHistoryAsync(List<ChatApiMessage> history, CancellationToken ct, int maxTokens = 4096)
        {
            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(history, null, ct))
            {
                if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 使用 ConversationContextManager 构建的消息列表调用 AI。
        /// 正确处理 reasoning_content 回传规则。
        /// </summary>
        protected async Task<string> CallAiWithContextAsync(ConversationContextManager ctxManager, CancellationToken ct, int maxTokens = 4096)
        {
            var messages = ctxManager.BuildApiMessages();
            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct))
            {
                if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 构建上下文感知的消息列表，将 Agent 的 system prompt 与对话历史合并。
        /// 
        /// 消息顺序已针对 DeepSeek Prompt Cache 优化：
        /// 1. Agent 的 System Prompt（稳定，每次相同）→ 形成可缓存前缀
        /// 2. 对话历史摘要 / 压缩上下文（相对稳定）→ 延长可缓存前缀
        /// 3. 当前用户消息（变化最大）→ 缓存前缀到此为止
        /// 
        /// 缓存命中策略：
        /// - 同一 Agent 类型多次调用时，system prompt 前缀命中缓存
        /// - 同一会话内多轮调用时，system + 历史前缀命中缓存
        /// - 大幅降低重复上下文的 token 计费
        /// </summary>
        /// <param name="systemPrompt">Agent 的系统提示词</param>
        /// <param name="userPrompt">当前的用户消息/步骤描述</param>
        /// <param name="maxRecentTurns">注入对话历史的最近轮次数（0 = 不注入）</param>
        /// <returns>按缓存优化顺序排列的消息列表</returns>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt, string userPrompt, int maxRecentTurns = 5)
        {
            var messages = new List<ChatApiMessage>();

            // ── 第1层：Agent System Prompt（最稳定，始终在最前面确保缓存命中）──
            messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });

            // ── 第2层：对话历史（来自 ConversationContextManager，相对稳定）──
            // 使用 ContextManager 注入历史，而非 Context.ConversationHistory（后者不含 tool 消息和 reasoning 规则）
            var ctxManager = Context?.ContextManager;
            if (ctxManager != null && !ctxManager.IsEmpty && maxRecentTurns > 0)
            {
                // 注入压缩摘要（早期对话的精简版本）
                if (ctxManager.Compressor != null)
                {
                    string compressedText = ctxManager.Compressor.GetCompressedContextText();
                    if (!string.IsNullOrWhiteSpace(compressedText))
                        messages.Add(new ChatApiMessage { Role = "system", Content = compressedText });
                }

                // 注入最近 N 轮的原始消息（保留 tool 调用链完整性）
                var recentMessages = ctxManager.BuildApiMessagesRecentTurns(maxRecentTurns);
                if (recentMessages.Count > 0)
                {
                    // 跳过第一条 system 消息（Agent 已有自己的 system prompt），
                    // 但保留压缩摘要和搜索/RAG 上下文等 system 消息
                    bool firstSystemSkipped = false;
                    foreach (var msg in recentMessages)
                    {
                        if (msg.Role == "system")
                        {
                            if (!firstSystemSkipped)
                            {
                                firstSystemSkipped = true;
                                continue; // 跳过对话管理器的 system prompt
                            }
                        }
                        messages.Add(msg);
                    }
                }
            }

            // ── 第3层：当前用户消息（变化最大，放在最后）──
            messages.Add(new ChatApiMessage { Role = "user", Content = userPrompt });

            return messages;
        }

        /// <summary>
        /// 构建上下文感知的消息列表（支持注入额外的系统级上下文）。
        /// 
        /// 用于 Plan Agent 等需要将动态发现结果注入 prompt 的场景。
        /// extraSystemMessages 插入在历史消息之后、用户消息之前，
        /// 确保 messages[0]（Agent System Prompt）始终稳定可缓存。
        /// </summary>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt,
            string userPrompt,
            List<ChatApiMessage> extraSystemMessages,
            int maxRecentTurns = 5)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, maxRecentTurns);

            // ── 在用户消息之前注入额外的 system 消息 ──
            // 用户消息始终在最后，所以插入位置是 Count - 1
            if (extraSystemMessages.Count > 0)
            {
                messages.InsertRange(messages.Count - 1, extraSystemMessages);
            }

            return messages;
        }

        /// <summary>
        /// 带工具调用的 AI 对话循环（支持多轮工具调用）。
        /// 
        /// 这是 Agent 执行工具增强型任务的核心方法。
        /// 与主聊天流程 (DeepSeekChatControl.Messaging.cs) 中的工具调用循环一致。
        /// </summary>
        /// <param name="messages">消息列表（system + 历史 + user）</param>
        /// <param name="workspaceRoot">工作区根目录，用于内置工具（如 file_search, list_dir）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxTokens">最大 token 数</param>
        /// <param name="maxToolRounds">【已废弃】使用智能循环检测替代。保留参数兼容性，但不再使用。</param>
        /// <param name="toolWhitelist">自定义工具白名单（null = 使用 Definition.AllowedTools）</param>
        /// <param name="onThinking">思考内容回调（用于 UI 实时更新）</param>
        /// <param name="onContent">内容回调（用于 UI 实时更新）</param>
        /// <param name="onToolCall">工具调用回调（用于 UI 通知）</param>
        /// <returns>AI 最终生成的文本内容</returns>
        protected async Task<string> CallAiWithToolLoopAsync(
            List<ChatApiMessage> messages,
            string? workspaceRoot,
            CancellationToken ct,
            int maxTokens = 4096,
            int maxToolRounds = 30,
            List<string>? toolWhitelist = null,
            Action<string>? onThinking = null,
            Action<string>? onContent = null,
            Action<string>? onToolCall = null)
        {
            var reasoningBuilder = new StringBuilder();
            var contentBuilder = new StringBuilder();
            var toolCallAccumulator = new Dictionary<int, Models.ToolCallAccumulator>();

            // ── 循环检测状态 ──
            var callSignatureHistory = new List<string>();
            int consecutiveErrorRounds = 0;
            const int maxRepeatedSameCall = 3;
            const int maxConsecutiveErrors = 5;
            const int safetyLimit = 200;
            bool loopDetected = false;

            int round = 0;
            while (!loopDetected)
            {
                round++;
                if (round > safetyLimit)
                {
                    var L = LocalizationService.Instance;
                    Logger.Warn($"[Agent:{Definition.Name}] {string.Format(L["agent.log.safetyLimit"], safetyLimit)}");
                    contentBuilder.Append($"\n\n> ⚠️ {string.Format(L["agent.log.safetyLimit"], safetyLimit)}");
                    break;
                }

                toolCallAccumulator.Clear();
                reasoningBuilder.Clear();
                contentBuilder.Clear();

                // ── 获取工具定义 ──
                List<ToolDefinition>? toolDefs = null;
                if (BuiltInTools != null || McpManager != null)
                {
                    toolDefs = new List<ToolDefinition>();

                    // 使用自定义白名单或默认 Definition.AllowedTools
                    var effectiveWhitelist = toolWhitelist ?? Definition.AllowedTools;

                    // 内置工具
                    if (BuiltInTools != null)
                    {
                        var builtInDefs = BuiltInTools.GetFilteredToolDefinitions(effectiveWhitelist);
                        toolDefs.AddRange(builtInDefs);
                    }

                    // MCP 外部工具
                    if (McpManager != null && McpManager.AllTools.Count > 0)
                    {
                        var mcpDefs = McpManager.GetFilteredToolDefinitions(effectiveWhitelist);
                        toolDefs.AddRange(mcpDefs);
                    }

                    Logger.Info($"[Agent:{Definition.Name}] 本轮携带 {toolDefs.Count} 个工具定义" +
                        (toolWhitelist != null ? " (自定义白名单)" : ""));
                }

                // ── 流式调用 AI ──
                await foreach (var chunk in _apiService.ChatStreamAsync(messages, toolDefs, ct))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuilder.Append(thinking);
                        onThinking?.Invoke(thinking);
                    }
                    else if (chunk.StartsWith("[TOOL_CALL]"))
                    {
                        var tcJson = chunk.Substring(11);
                        try
                        {
                            var deltas = JsonSerializer.Deserialize<List<ToolCallDelta>>(tcJson);
                            if (deltas != null)
                            {
                                foreach (var delta in deltas)
                                {
                                    if (!toolCallAccumulator.ContainsKey(delta.Index))
                                        toolCallAccumulator[delta.Index] = new Models.ToolCallAccumulator();
                                    var acc = toolCallAccumulator[delta.Index];
                                    if (!string.IsNullOrEmpty(delta.Id)) acc.Id = delta.Id!;
                                    if (!string.IsNullOrEmpty(delta.Type)) acc.Type = delta.Type;
                                    if (delta.Function != null)
                                    {
                                        if (!string.IsNullOrEmpty(delta.Function.Name)) acc.FunctionName = delta.Function.Name;
                                        if (!string.IsNullOrEmpty(delta.Function.Arguments)) acc.ArgumentsBuilder.Append(delta.Function.Arguments);
                                    }
                                }
                            }
                        }
                        catch (JsonException) { }

                        var toolNames = toolCallAccumulator.Values
                            .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                            .Select(a => a.FunctionName!);
                        string toolSummary = string.Join(", ", toolNames);
                        onToolCall?.Invoke(toolSummary);
                    }
                    else if (chunk.StartsWith("[CACHE]"))
                    {
                        // ── Cache 统计信息 ── 过滤，不混入正文
                        // 累计统计由 ChatHtmlService.BuildCacheHitFooterHtml 渲染为外侧卡片
                    }
                    else
                    {
                        contentBuilder.Append(chunk);
                        onContent?.Invoke(chunk);
                    }
                }

                // ── 记录本轮 Cache 命中率 ──
                LogCacheHitRate(round);

                // ── 处理工具调用 ──
                if (toolCallAccumulator.Count > 0)
                {
                    var toolCalls = toolCallAccumulator.Values
                        .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                        .Select(a => new ToolCall
                        {
                            Id = a.Id,
                            Type = a.Type ?? "function",
                            Function = new ToolCallFunction
                            {
                                Name = a.FunctionName!,
                                Arguments = a.ArgumentsBuilder.ToString()
                            }
                        }).ToList();

                    if (toolCalls.Count == 0) break;

                    Logger.Info($"[Agent:{Definition.Name}] 检测到 {toolCalls.Count} 个工具调用: {string.Join(", ", toolCalls.Select(t => t.Function.Name))}");

                    // ── 添加 assistant 消息（含工具调用）──
                    messages.Add(new ChatApiMessage
                    {
                        Role = "assistant",
                        Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                        ReasoningContent = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                        ToolCalls = toolCalls
                    });

                    // ── 执行每个工具并添加 tool 结果消息 ──
                    foreach (var tc in toolCalls)
                    {
                        string toolResult;
                        try
                        {
                            toolResult = await ExecuteToolAsync(tc.Function.Name, tc.Function.Arguments, workspaceRoot, ct);
                        }
                        catch (Exception ex)
                        {
                            toolResult = $"❌ 工具执行异常: {ex.Message}";
                            Logger.Error($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 执行异常: {ex.Message}", ex);
                        }

                        messages.Add(new ChatApiMessage
                        {
                            Role = "tool",
                            Content = toolResult,
                            ToolCallId = tc.Id,
                            Name = tc.Function.Name
                        });

                        Logger.Info($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");
                    }

                    // ── 循环检测 ──
                    // 收集本轮签名
                    var roundSignatures = new List<string>();
                    foreach (var tc in toolCalls)
                    {
                        string sig = tc.Function.Name + "|" +
                            (tc.Function.Arguments.Length > 200
                                ? tc.Function.Arguments.Substring(0, 200)
                                : tc.Function.Arguments);
                        callSignatureHistory.Add(sig);
                        roundSignatures.Add(sig);
                    }

                    // 检测同一调用重复
                    foreach (var sig in roundSignatures)
                    {
                        int repeatCount = callSignatureHistory.Count(s => s == sig);
                        if (repeatCount >= maxRepeatedSameCall)
                        {
                            loopDetected = true;
                            string toolName = sig.Split('|')[0];
                            var L = LocalizationService.Instance;
                            Logger.Warn($"[Agent:{Definition.Name}] {string.Format(L["agent.log.loopDetected"], toolName, repeatCount)}");
                            contentBuilder.Append($"\n\n> ⚠️ {string.Format(L["agent.log.loopTerminated"], toolName, repeatCount)}");
                            break;
                        }
                    }

                    // 保留最近 30 条签名
                    while (callSignatureHistory.Count > 30)
                        callSignatureHistory.RemoveAt(0);

                    // 检测连续错误：检查本轮 tool 消息是否全部以 ❌ 开头
                    if (!loopDetected)
                    {
                        int toolMsgStart = messages.Count - toolCalls.Count;
                        bool allErrors = toolCalls.Count > 0;
                        for (int i = toolMsgStart; i < messages.Count && allErrors; i++)
                        {
                            if (messages[i].Role == "tool" && !(messages[i].Content ?? "").StartsWith("❌"))
                                allErrors = false;
                        }

                        if (allErrors)
                            consecutiveErrorRounds++;
                        else
                            consecutiveErrorRounds = 0;

                        if (consecutiveErrorRounds >= maxConsecutiveErrors)
                        {
                            loopDetected = true;
                            var L = LocalizationService.Instance;
                            Logger.Warn($"[Agent:{Definition.Name}] {string.Format(L["agent.log.consecutiveErrors"], consecutiveErrorRounds)}");
                            contentBuilder.Append($"\n\n> ⚠️ {string.Format(L["agent.log.consecutiveErrors"], consecutiveErrorRounds)}");
                        }
                    }

                    // ── 继续下一轮 ──
                    continue;
                }

                // ── 无工具调用，结束循环 ──
                break;
            }

            // ── 汇总累计 Cache 统计（日志）──
            LogTotalCacheHitRate(round);

            return contentBuilder.ToString().Trim();
        }

        /// <summary>
        /// 记录 DeepSeek Prompt Cache 命中率到日志（Agent 内部版）。
        /// 从 _apiService.LastUsage 读取 cache 统计信息并格式化输出。
        /// </summary>
        /// <param name="round">当前工具调用轮次（0 表示非工具循环模式）</param>
        private void LogCacheHitRate(int round = 0)
        {
            try
            {
                var usage = _apiService?.LastUsage;
                if (usage == null) return;

                int hit = usage.PromptCacheHitTokens;
                int miss = usage.PromptCacheMissTokens;
                int total = hit + miss;
                double rate = usage.CacheHitRate;

                string roundInfo = round > 0 ? $"[轮次#{round}] " : "";
                string level = rate >= 0.95 ? "🟢" : rate >= 0.70 ? "🟡" : rate >= 0.30 ? "🟠" : "🔴";

                Logger.Info($"[Cache] {level} {roundInfo}命中率: {usage.CacheHitRatePercent} " +
                    $"(命中 {hit:N0} / 未命中 {miss:N0} / 总计 {total:N0} tokens)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录命中率异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录跨所有工具调用轮次的累计 Cache 命中率到日志（Agent 内部版）。
        /// 在所有轮次结束后调用，输出汇总总计。
        /// </summary>
        private void LogTotalCacheHitRate(int finalRound)
        {
            try
            {
                long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                long totalCacheable = totalHit + totalMiss;
                if (totalCacheable == 0) return;

                double aggregateRate = (double)totalHit / totalCacheable;
                string level = aggregateRate >= 0.95 ? "🟢" : aggregateRate >= 0.70 ? "🟡" : aggregateRate >= 0.30 ? "🟠" : "🔴";

                Logger.Info($"[Cache] ═══════════════════════════════════════");
                Logger.Info($"[Cache] {level} 累计汇总 ({finalRound} 轮)");
                Logger.Info($"[Cache]   总 Cache 命中率: {aggregateRate * 100:F1}%");
                Logger.Info($"[Cache]   累计命中: {totalHit:N0} tokens");
                Logger.Info($"[Cache]   累计未命中: {totalMiss:N0} tokens");
                Logger.Info($"[Cache]   累计 Prompt: {(_apiService?.TotalPromptTokens ?? 0):N0} tokens");
                Logger.Info($"[Cache]   累计 Completion: {(_apiService?.TotalCompletionTokens ?? 0):N0} tokens");
                Logger.Info($"[Cache]   节省比例: {aggregateRate * 100:F1}% (DeepSeek Cache 对命中 token 仅按 $0.014/M 计费)");
                Logger.Info($"[Cache] ═══════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录汇总命中率异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成累计 Cache 命中率摘要文本（用于附加到 AI 响应末尾，用户可见）。
        /// </summary>
        private string GetTotalCacheHitSummary(int finalRound)
        {
            try
            {
                long totalHit = _apiService?.TotalCacheHitTokens ?? 0;
                long totalMiss = _apiService?.TotalCacheMissTokens ?? 0;
                long totalCacheable = totalHit + totalMiss;
                if (totalCacheable == 0) return string.Empty;

                double rate = (double)totalHit / totalCacheable;
                string icon = rate >= 0.95 ? "🟢" : rate >= 0.70 ? "🟡" : rate >= 0.30 ? "🟠" : "🔴";
                long totalPrompt = _apiService?.TotalPromptTokens ?? 0;
                long totalCompletion = _apiService?.TotalCompletionTokens ?? 0;

                return $"\n\n---\n\n{icon} **Cache 命中率: {rate * 100:F1}%**" +
                    $" · {totalHit:N0} 命中 / {totalMiss:N0} 未命中" +
                    $" · Prompt {totalPrompt:N0} · Completion {totalCompletion:N0}" +
                    (finalRound > 1 ? $" · {finalRound} 轮" : "");
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 执行单个工具调用（优先内置工具，其次 MCP 工具）。
        /// </summary>
        private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, string? workspaceRoot, CancellationToken ct)
        {
            // ── 1. 内置工具 ──
            if (BuiltInTools != null && BuiltInToolService.IsBuiltInTool(toolName))
            {
                string? result = await BuiltInTools.ExecuteBuiltInToolAsync(toolName, argumentsJson, workspaceRoot);
                if (result != null)
                    return result;
            }

            // ── 2. MCP 工具 ──
            if (McpManager != null)
            {
                try
                {
                    return await McpManager.CallToolAsync(toolName, argumentsJson, ct);
                }
                catch (Exception ex)
                {
                    return $"❌ MCP 工具调用失败 ({toolName}): {ex.Message}";
                }
            }

            return $"❌ 未知工具: {toolName}";
        }

        #endregion

        #region Shared Utility Methods

        /// <summary>
        /// 判断 chunk 是否为正文内容（过滤 [THINKING]/[TOOL_CALL]/[CACHE] 等控制前缀）。
        /// </summary>
        private static bool IsContentChunk(string chunk)
        {
            return !chunk.StartsWith("[THINKING]")
                && !chunk.StartsWith("[TOOL_CALL]")
                && !chunk.StartsWith("[CACHE]");
        }

        /// <summary>
        /// 从 AI 返回结果中提取 JSON（可能被 markdown 代码块包裹）。
        /// </summary>
        protected static string ExtractJsonFromMarkdown(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            int jsonStart = text.IndexOf('{');
            int jsonEnd = text.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                return text.Substring(jsonStart, jsonEnd - jsonStart + 1);

            return text.Trim();
        }

        /// <summary>
        /// 从 AI 返回结果中解析文件变更。
        /// 支持三种编辑格式：
        /// 1. ```file: 代码块（create_file / 全文件替换）
        /// 2. *** Begin Patch / *** End Patch（apply_patch）
        /// 3. ```insert_edit_into_file: 或 ```edit: 代码块
        /// </summary>
        protected static List<FileChangeSummary> ParseCodeChangesFromResult(string aiResult)
        {
            var changes = new List<FileChangeSummary>();
            if (string.IsNullOrWhiteSpace(aiResult)) return changes;

            // ── 格式1：```file: 代码块（原有格式）──
            var fileRegex = new System.Text.RegularExpressions.Regex(
                @"```file:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var fileMatches = fileRegex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in fileMatches)
            {
                string filePath = match.Groups["path"].Value.Trim();
                string newContent = match.Groups["content"].Value;
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                AddChangeFromContent(changes, filePath, newContent);
            }

            // ── 格式2：*** Begin Patch / *** End Patch ──
            var patchRegex = new System.Text.RegularExpressions.Regex(
                @"\*\*\*\s*Begin\s*Patch\s*\r?\n(.*?)\r?\n\s*\*\*\*\s*End\s*Patch",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var patchMatches = patchRegex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in patchMatches)
            {
                string body = match.Groups[1].Value;

                // 提取文件路径
                var fileHeaderMatch = System.Text.RegularExpressions.Regex.Match(body,
                    @"\*\*\*\s*(?:Update|Add|Delete)\s*File\s*:\s*(?<path>[^\r\n]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (fileHeaderMatch.Success)
                {
                    string filePath = fileHeaderMatch.Groups["path"].Value.Trim();
                    if (string.IsNullOrWhiteSpace(filePath)) continue;

                    // 提取 + 行作为新增内容
                    var addedLines = new System.Text.StringBuilder();
                    foreach (System.Text.RegularExpressions.Match lineMatch in
                        System.Text.RegularExpressions.Regex.Matches(body, @"^\+(.*)$", System.Text.RegularExpressions.RegexOptions.Multiline))
                    {
                        addedLines.AppendLine(lineMatch.Groups[1].Value);
                    }

                    // 统计 - 行数
                    int removedCount = System.Text.RegularExpressions.Regex.Matches(
                        body, @"^\-", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

                    string newContent = addedLines.ToString();
                    int newLines = CountLines(newContent);

                    changes.Add(new FileChangeSummary
                    {
                        FilePath = filePath,
                        NewContent = newContent,
                        LinesAdded = newLines,
                        LinesRemoved = removedCount,
                        BriefDescription = System.IO.Path.GetFileName(filePath) + " (patch)",
                    });
                }
            }

            // ── 格式3：```insert_edit_into_file: 或 ```edit: 代码块 ──
            var insertEditRegex = new System.Text.RegularExpressions.Regex(
                @"```(?:insert_edit_into_file|edit)\s*:\s*(?<path>[^\r\n]+)[\r\n]+(?<content>.*?)```",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var insertMatches = insertEditRegex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in insertMatches)
            {
                string filePath = match.Groups["path"].Value.Trim();
                string fullContent = match.Groups["content"].Value;
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                // 去除 ...existing code... 标记，估算总行数
                string cleanedContent = System.Text.RegularExpressions.Regex.Replace(
                    fullContent,
                    @"\/\/\s*\.\.\.existing\s*code\.\.\.|#\s*\.\.\.existing\s*code\.\.\.|<!--\s*\.\.\.existing\s*code\.\.\.\s*-->",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                int newLines = CountLines(cleanedContent);

                changes.Add(new FileChangeSummary
                {
                    FilePath = filePath,
                    NewContent = fullContent,
                    LinesAdded = newLines,
                    LinesRemoved = 0,
                    BriefDescription = System.IO.Path.GetFileName(filePath) + " (insert_edit)",
                });
            }

            return changes;
        }

        /// <summary>
        /// 辅助方法：从文件内容创建 FileChangeSummary 并添加到列表。
        /// </summary>
        private static void AddChangeFromContent(
            List<FileChangeSummary> changes, string filePath, string newContent)
        {
            int newLines = CountLines(newContent);
            int linesAdded = newLines;
            int linesRemoved = 0;

            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    string oldContent = System.IO.File.ReadAllText(filePath);
                    int oldLines = CountLines(oldContent);
                    linesAdded = Math.Max(0, newLines - oldLines);
                    linesRemoved = Math.Max(0, oldLines - newLines);
                }
                catch { }
            }

            changes.Add(new FileChangeSummary
            {
                FilePath = filePath,
                NewContent = newContent,
                LinesAdded = linesAdded,
                LinesRemoved = linesRemoved,
                BriefDescription = System.IO.Path.GetFileName(filePath)
                    + (System.IO.File.Exists(filePath) ? " (修改)" : " (新建)"),
            });
        }

        /// <summary>
        /// 从 AI 返回结果中解析待删除的文件（delete: 格式）。
        /// 支持格式:
        ///   delete: path/to/file.cs
        ///   delete_file: path/to/file.cs
        ///   ```delete: path/to/file.cs```
        /// </summary>
        protected static List<string> ParseFileDeletionsFromResult(string aiResult)
        {
            var deletions = new List<string>();
            if (string.IsNullOrWhiteSpace(aiResult)) return deletions;

            // 严格匹配：仅匹配行首的 delete: 或 delete_file: 格式
            // 要求路径包含文件扩展名（如 .cs/.cpp），排除代码块内的 delete 关键字误匹配
            var regex = new System.Text.RegularExpressions.Regex(
                @"(?<=^|\n)\s*(?:delete|delete_file)\s*:\s*(?<path>[^\r\n`]+?\.[a-zA-Z0-9]+)\b",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = regex.Matches(aiResult);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string filePath = match.Groups["path"].Value.Trim();
                // 过滤：路径不能以 ``` 结尾（排除代码块标记）
                if (string.IsNullOrWhiteSpace(filePath) || filePath.EndsWith("`"))
                    continue;

                // 过滤：路径不能在代码块内部
                int matchPos = match.Index;
                string textBefore = aiResult.Substring(0, Math.Min(matchPos, aiResult.Length));
                int openFences = CountSubstring(textBefore, "```");
                if (openFences % 2 != 0)
                    continue;

                deletions.Add(filePath);
            }

            return deletions;
        }

        /// <summary>
        /// 统计子串出现次数。
        /// </summary>
        private static int CountSubstring(string text, string substring)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += substring.Length;
            }
            return count;
        }

        /// <summary>
        /// 计算文本行数。
        /// </summary>
        protected static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            return count;
        }

        /// <summary>
        /// 解析 AI 返回的文件路径（支持相对路径和 Unix 风格路径）。
        /// </summary>
        protected static string ResolveFilePath(string filePath, string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;
            if (System.IO.Path.IsPathRooted(filePath) && System.IO.File.Exists(System.IO.Path.GetDirectoryName(filePath)))
                return filePath;

            if (System.IO.Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(solutionPath))
            {
                string relativePart = filePath.TrimStart('/').Replace('/', '\\');
                string candidate = System.IO.Path.Combine(solutionPath, relativePart);
                if (System.IO.File.Exists(candidate))
                {
                    Logger.Info($"[PathResolve] AI路径 {filePath} → {candidate}");
                    return candidate;
                }

                string fileName = System.IO.Path.GetFileName(filePath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        var matches = System.IO.Directory.GetFiles(solutionPath, fileName,
                            System.IO.SearchOption.AllDirectories);
                        if (matches.Length > 0)
                        {
                            Logger.Info($"[PathResolve] AI路径 {filePath} → {matches[0]} (方案目录搜索)");
                            return matches[0];
                        }
                    }
                    catch { }
                }

                string fallbackDir = System.IO.Path.GetDirectoryName(candidate) ?? solutionPath ?? string.Empty;
                Logger.Warn($"[PathResolve] AI路径无法匹配已有文件，将创建: {candidate}");
                return candidate;
            }
            else if (!string.IsNullOrEmpty(solutionPath))
            {
                string candidate = System.IO.Path.Combine(solutionPath, filePath.Replace('/', '\\'));
                Logger.Info($"[PathResolve] 相对路径 {filePath} → {candidate}");
                return candidate;
            }

            return filePath;
        }

        #endregion

        #region Permission

        /// <summary>
        /// 请求用户许可执行某个操作。
        /// </summary>
        public async Task<bool> RequestPermissionAsync(string title, string command, string actionType = "command")
        {
            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = actionType,
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingPermission"], title));

            bool approved = await request.ResponseTcs.Task;
            PendingPermission = null;
            AddLog("INFO", $"{LocalizationService.Instance["agent.log.permissionResult"]}: {(approved ? "✅ 允许" : "❌ 拒绝")} → {title}");
            return approved;
        }

        /// <summary>
        /// 响应权限请求。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            if (PendingPermission?.RequestId == requestId)
                PendingPermission.ResponseTcs?.TrySetResult(approved);
        }

        /// <summary>
        /// 请求用户确认文件删除操作。
        /// 会中断当前执行流，在 WebView 中渲染确认按钮，等待用户响应。
        /// </summary>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        /// <param name="reason">删除原因说明</param>
        /// <returns>true 表示用户确认删除，false 表示取消</returns>
        public async Task<bool> RequestFileDeleteConfirmationAsync(List<string> filePaths, string reason = "")
        {
            if (filePaths == null || filePaths.Count == 0)
                return false;

            var fileNames = filePaths.Select(p => System.IO.Path.GetFileName(p)).ToList();
            string title = filePaths.Count == 1
                ? $"删除文件: {fileNames[0]}"
                : $"删除 {filePaths.Count} 个文件";
            string command = !string.IsNullOrEmpty(reason) ? reason : string.Join("\n", filePaths);

            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = "file_delete",
                FilePaths = new List<string>(filePaths),
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"{LocalizationService.Instance["agent.log.waitingDeleteConfirm"]}: {title}");

            bool approved = await request.ResponseTcs.Task;
            PendingPermission = null;
            AddLog("INFO", $"文件删除确认结果: {(approved ? "✅ 确认删除" : "❌ 取消")} → {title}");
            return approved;
        }

        /// <summary>
        /// 通知文件变更（用于实时推送到 WebView）。
        /// </summary>
        /// <param name="planId">关联的计划 ID</param>
        /// <param name="changeType">变更类型: modify, create, delete</param>
        /// <param name="filePath">文件绝对路径</param>
        /// <param name="detail">变更详情</param>
        protected void NotifyFileChange(string planId, string changeType, string filePath, string detail)
        {
            try
            {
                FileChangeNotified?.Invoke(new AgentFileChangeEventArgs
                {
                    PlanId = planId,
                    ChangeType = changeType,
                    FilePath = filePath,
                    Detail = detail,
                });
            }
            catch { }
        }

        #endregion

        #region Event Helpers for Derived Classes

        /// <summary>
        /// 供派生类触发 LogEntryAdded 事件（不写日志文件，仅转发到订阅者）。
        /// </summary>
        protected void RaiseLogEntryAdded(AgentLogEntry entry)
        {
            try { LogEntryAdded?.Invoke(entry); } catch { }
        }

        #endregion

        #region Logging

        protected void AddLog(string level, string message)
        {
            var entry = new AgentLogEntry { Level = level, Message = message };
            _logs.Add(entry);
            try { LogEntryAdded?.Invoke(entry); } catch { }

            if (level == "ERROR") Logger.Error($"[{Definition.Name}] {message}");
            else if (level == "WARN") Logger.Warn($"[{Definition.Name}] {message}");
            else Logger.Info($"[{Definition.Name}] {message}");
        }

        public IReadOnlyList<AgentLogEntry> GetLogs() => _logs.AsReadOnly();

        #endregion

        #region IDisposable

        public virtual void Dispose()
        {
            _logs.Clear();
        }

        #endregion
    }
}
