using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        /// 文本内容集中管理在 AiPrompts.CommonSystemPromptPrefixCore（通过 LocalizationService 支持多语言）。
        /// </summary>
        protected static string CommonSystemPromptPrefixCore => AiPrompts.CommonSystemPromptPrefixCore;

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

        /// <summary>ExploreAgent 引用（由 AgentDispatcher 注入，用于 runSubagent 委派探索任务）</summary>
        public ExploreAgent? ExploreAgent { get; set; }

        /// <summary>日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        /// <summary>权限请求事件</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

        /// <summary>向用户提问事件（VisualStudio_askQuestions 工具使用）</summary>
        public event Action<AgentQuestionRequest>? QuestionsRequested;

        /// <summary>文件变更实时通知事件（编辑阶段逐文件推送）</summary>
        public event Action<AgentFileChangeEventArgs>? FileChangeNotified;

        /// <summary>当前待确认的权限请求</summary>
        public AgentPermissionRequest? PendingPermission { get; protected set; }

        /// <summary>当前待回答的提问请求</summary>
        public AgentQuestionRequest? PendingQuestion { get; protected set; }

        /// <summary>AI 通过 request_handoff 工具发起的待处理移交请求</summary>
        public HandoffRequest? PendingHandoffRequest { get; protected set; }

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
            // toolChoice: "none" — 简短回答无需工具调用，防止 AI 生成工具调用 XML 污染输出
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct, maxTokens, toolChoice: "none"))
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
        protected async Task<string> CallAiLongAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 4096, double? temperature = null)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt);

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct, maxTokens, temperature: temperature))
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
        /// <param name="toolChoice">工具调用策略。Plan Agent Design 阶段应传 "none" 以禁用工具调用，避免 DSML 泄露。</param>
        /// <param name="temperature">采样温度。Plan Agent Design 阶段应传 0.0 以确保 JSON 输出确定性。</param>
        protected async Task<string> CallAiLongAsync(
            string systemPrompt,
            string userPrompt,
            List<ChatApiMessage> extraSystemMessages,
            CancellationToken ct,
            int maxTokens = 4096,
            string? toolChoice = null,
            double? temperature = null)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, extraSystemMessages);

            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct, maxTokens, toolChoice, temperature))
            {
                if (IsContentChunk(chunk))
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
            await foreach (var chunk in _apiService.ChatStreamAsync(history, null, ct, maxTokens))
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
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, ct, maxTokens))
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
        /// <param name="maxRecentTurns">注入对话历史的最近轮次数（0 = 不注入，默认不限制）</param>
        /// <returns>按缓存优化顺序排列的消息列表</returns>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt, string userPrompt, int maxRecentTurns = int.MaxValue)
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
            int maxRecentTurns = int.MaxValue)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, maxRecentTurns);

            // ── 在用户消息之前注入额外的 system 消息 ──
            // 用户消息始终在最后，所以插入位置是 Count - 1
            if (extraSystemMessages != null && extraSystemMessages.Count > 0)
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
            var toolCallHistory = new List<(int Round, string Summary)>();
            var lastResultBySignature = new Dictionary<string, string>();  // 跟踪每次调用的结果
            int consecutiveErrorRounds = 0;
            const int maxRepeatedSameCall = 5;    // 同一调用最多重复 5 次
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

                // ── 同步当前轮次到文件读取缓存，用于轮数过期策略 ──
                if (BuiltInTools != null)
                    BuiltInTools.CurrentRound = round;

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

                    if (Definition.Type != AgentType.Edit && Definition.Type != AgentType.Build && effectiveWhitelist != null)
                    {
                        var modifyingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "replace_string_in_file", "create_file", "create_directory", 
                            "edit_notebook_file", "delete_file", "apply_patch", 
                            "run_in_terminal", "write_file", "edit_file"
                        };
                        effectiveWhitelist = effectiveWhitelist.Where(t => !modifyingTools.Contains(t)).ToList();
                    }

                    // 内置工具（含通过 BuiltInToolService 统一管理的 MCP 工具）
                    if (BuiltInTools != null)
                    {
                        var builtInDefs = BuiltInTools.GetFilteredToolDefinitions(effectiveWhitelist);
                        toolDefs.AddRange(builtInDefs);
                    }
                    // MCP 工具已由 BuiltInTools.GetFilteredToolDefinitions 统一返回，无需再次添加
                    else if (McpManager != null && McpManager.AllTools.Count > 0)
                    {
                        // 仅当 BuiltInTools 不可用时才单独获取 MCP 工具
                        var mcpDefs = McpManager.GetFilteredToolDefinitions(effectiveWhitelist);
                        toolDefs.AddRange(mcpDefs);
                    }

                    Logger.Info($"[Agent:{Definition.Name}] 本轮携带 {toolDefs.Count} 个工具定义" +
                        (toolWhitelist != null ? " (自定义白名单)" : ""));
                }

                // ── 流式调用 AI（带断点续传重试）──
                bool streamSuccess = false;
                int streamAttempt = 0;
                const int maxStreamAttempts = 4; // 1 initial + 3 retries
                string savedPartialContent = "";
                string savedPartialReasoning = "";

                while (!streamSuccess && streamAttempt < maxStreamAttempts)
                {
                    try
                    {
                        // 如果是重试，将已接收的部分内容注入对话上下文
                        if (streamAttempt > 0)
                        {
                            // 在消息列表中追加部分 AI 回复 + 继续指令
                            var resumeMessages = new List<ChatApiMessage>(messages);
                            resumeMessages.Add(new ChatApiMessage
                            {
                                Role = "assistant",
                                Content = string.IsNullOrEmpty(savedPartialContent) ? null : savedPartialContent,
                                ReasoningContent = string.IsNullOrEmpty(savedPartialReasoning) ? null : savedPartialReasoning
                            });

                            string tailContent = savedPartialContent.Length > 300
                                ? "…(截断)…" + savedPartialContent.Substring(savedPartialContent.Length - 300)
                                : savedPartialContent;
                            resumeMessages.Add(new ChatApiMessage
                            {
                                Role = "user",
                                Content = $"[系统指令] 你之前的回复因网络中断被截断。以下是已发送的末尾内容：\n```\n{tailContent}\n```\n请从截断处**精确**继续，不要重复任何已发送的内容，不要道歉或解释中断。直接继续未完成的句子或代码块。"
                            });

                            // 将部分内容预置到缓冲区
                            contentBuilder.Append(savedPartialContent);
                            reasoningBuilder.Append(savedPartialReasoning);
                            Logger.Info($"[Agent:{Definition.Name}] 流断点续传：第 {streamAttempt + 1}/{maxStreamAttempts} 次，已注入 {savedPartialContent.Length} 字符部分内容");

                            // 使用 resume 消息而不是原始消息
                            await foreach (var chunk in _apiService.ChatStreamAsync(resumeMessages, toolDefs, ct))
                            {
                                ProcessStreamChunk(chunk, reasoningBuilder, contentBuilder, toolCallAccumulator, onThinking, onContent);
                            }
                        }
                        else
                        {
                            await foreach (var chunk in _apiService.ChatStreamAsync(messages, toolDefs, ct))
                            {
                                ProcessStreamChunk(chunk, reasoningBuilder, contentBuilder, toolCallAccumulator, onThinking, onContent);
                            }
                        }

                        streamSuccess = true;
                        if (streamAttempt > 0)
                        {
                            Logger.Info($"[Agent:{Definition.Name}] 流断点续传成功 (尝试 {streamAttempt + 1}/{maxStreamAttempts})");
                        }
                    }
                    catch (HttpRequestException ex) when (streamAttempt < maxStreamAttempts - 1)
                    {
                        string msg = ex.Message;
                        // 所有 4xx 客户端错误不重试（除 429 限流外）——请求本身有问题
                        if (msg.Contains("400") || msg.Contains("401") || msg.Contains("402") ||
                            msg.Contains("403") || msg.Contains("404") || msg.Contains("405") ||
                            msg.Contains("409") || msg.Contains("422"))
                            throw;

                        streamAttempt++;
                        savedPartialContent = contentBuilder.ToString();
                        savedPartialReasoning = reasoningBuilder.ToString();
                        double backoffSec = Math.Pow(2, streamAttempt);
                        Logger.Warn($"[Agent:{Definition.Name}] 流中断 (尝试 {streamAttempt}/{maxStreamAttempts})，已收到 {savedPartialContent.Length} 字符，{backoffSec}s 后恢复…");
                        contentBuilder.Clear();
                        reasoningBuilder.Clear();
                        toolCallAccumulator.Clear();
                        await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                    {
                        // 超时（非用户取消）
                        streamAttempt++;
                        savedPartialContent = contentBuilder.ToString();
                        savedPartialReasoning = reasoningBuilder.ToString();
                        double backoffSec = Math.Pow(2, streamAttempt);
                        Logger.Warn($"[Agent:{Definition.Name}] 流超时 (尝试 {streamAttempt}/{maxStreamAttempts})，{backoffSec}s 后恢复…");
                        contentBuilder.Clear();
                        reasoningBuilder.Clear();
                        toolCallAccumulator.Clear();
                        await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
                    }
                    catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                    {
                        // 用户取消导致的流释放，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消令牌中断");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append("\n\n> ⏏️ 操作已被取消。");
                        streamSuccess = true; // 不视为失败，正常退出
                    }
                    catch (IOException) when (ct.IsCancellationRequested)
                    {
                        // 用户取消导致的 IO 异常，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消令牌中断 (IO)");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append("\n\n> ⏏️ 操作已被取消。");
                        streamSuccess = true;
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户取消，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append("\n\n> ⏏️ 操作已被取消。");
                        streamSuccess = true;
                    }
                }

                if (!streamSuccess)
                {
                    // 所有重试均失败，返回部分内容让用户可通过重试按钮继续
                    Logger.Error($"[Agent:{Definition.Name}] 流式调用在 {maxStreamAttempts} 次尝试后全部失败");
                    if (savedPartialContent.Length > 0 || contentBuilder.Length > 0)
                    {
                        string partial = contentBuilder.Length > 0 ? contentBuilder.ToString() : savedPartialContent;
                        contentBuilder.Clear();
                        contentBuilder.Append(partial);
                        contentBuilder.Append($"\n\n> ⚠️ 网络连接在 {maxStreamAttempts} 次重试后仍未恢复。请点击重试按钮从中断处继续。");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"[Agent:{Definition.Name}] 流式调用在 {maxStreamAttempts} 次尝试后仍失败且无部分内容");
                    }
                }

                // ── 记录本轮 Cache 命中率 ──
                LogCacheHitRate(round);

                // ── 处理工具调用 ──
                var toolCalls = new List<ToolCall>();
                if (toolCallAccumulator.Count > 0)
                {
                    toolCalls = toolCallAccumulator.Values
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
                }

                if (toolCalls.Count == 0 && contentBuilder.Length > 0)
                {
                    var contentText = contentBuilder.ToString();
                    
                    // Parse leaked DSML / XML format
                    var xmlMatches = Regex.Matches(contentText, @"(?:<｜｜DSML｜｜|<)invoke\s+name=""([^""]+)""[^>]*>(.*?)(?:</｜｜DSML｜｜|</)invoke>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match m in xmlMatches)
                    {
                        string name = m.Groups[1].Value;
                        string inner = m.Groups[2].Value;
                        var paramMatches = Regex.Matches(inner, @"(?:<｜｜DSML｜｜|<)parameter\s+name=""([^""]+)""[^>]*>(.*?)(?:</｜｜DSML｜｜|</)parameter>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        var dict = new Dictionary<string, object>();
                        foreach (Match pm in paramMatches)
                        {
                            dict[pm.Groups[1].Value] = pm.Groups[2].Value;
                        }
                        toolCalls.Add(new ToolCall
                        {
                            Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Type = "function",
                            Function = new ToolCallFunction
                            {
                                Name = name,
                                Arguments = JsonSerializer.Serialize(dict)
                            }
                        });
                    }

                    // Parse leaked ReAct format
                    var reactMatches = Regex.Matches(contentText, @"Action:\s*(?<name>\w+)\s*Action Input:\s*(?<args>\{.*?\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match m in reactMatches)
                    {
                        toolCalls.Add(new ToolCall
                        {
                            Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Type = "function",
                            Function = new ToolCallFunction
                            {
                                Name = m.Groups["name"].Value,
                                Arguments = m.Groups["args"].Value
                            }
                        });
                    }

                    if (toolCalls.Count > 0)
                    {
                        Logger.Info($"[Agent:{Definition.Name}] (Fallback) 从内容中解析到 {toolCalls.Count} 个工具调用");
                    }
                }

                // ── 拦截非 Edit/非 Build Agent 的修改工具调用 ──
                if (Definition.Type != AgentType.Edit && Definition.Type != AgentType.Build && toolCalls.Count > 0)
                {
                    var modifyingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "replace_string_in_file", "create_file", "create_directory", 
                        "edit_notebook_file", "delete_file", "apply_patch", 
                        "run_in_terminal", "write_file", "edit_file"
                    };
                    int removed = toolCalls.RemoveAll(tc => modifyingTools.Contains(tc.Function.Name));
                    if (removed > 0)
                    {
                        Logger.Warn($"[Agent:{Definition.Name}] 拦截了 {removed} 个被禁止的修改文件工具调用");
                    }
                }

                if (toolCalls.Count == 0) break;

                if (toolCalls.Count > 0)
                {
                    Logger.Info($"[Agent:{Definition.Name}] 检测到 {toolCalls.Count} 个工具调用: {string.Join(", ", toolCalls.Select(t => t.Function.Name))}");

                    // ── 通知工具调用（含详细信息，每轮仅一次）──
                    // 每个工具调用单独一行，便于用户阅读执行过程
                    foreach (var tc in toolCalls)
                    {
                        string summary = BuiltInToolService.GetToolCallDisplayText(tc.Function.Name, tc.Function.Arguments);
                        onToolCall?.Invoke(summary);
                    }

                    // ── 收集本轮工具调用摘要到历史（用于循环终止时的上下文总结）──
                    foreach (var tc in toolCalls)
                    {
                        string summary = BuiltInToolService.GetToolCallDisplayText(tc.Function.Name, tc.Function.Arguments);
                        toolCallHistory.Add((round, summary));
                    }
                    // 保留最近 20 条记录防止内存增长
                    while (toolCallHistory.Count > 20)
                        toolCallHistory.RemoveAt(0);

                    // ── 添加 assistant 消息（含工具调用）──
                    messages.Add(new ChatApiMessage
                    {
                        Role = "assistant",
                        Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                        ReasoningContent = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                        ToolCalls = toolCalls
                    });

                    // ── 并行执行工具调用（带超时保护，长时工具使用更长超时）──
                    var toolTasks = toolCalls.Select(tc =>
                    {
                        var timeout = GetToolTimeout(tc.Function.Name);
                        return ExecuteToolWithTimeoutAsync(tc, workspaceRoot, ct, timeout);
                    }).ToList();
                    var toolResults = await Task.WhenAll(toolTasks).ConfigureAwait(false);

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        var tc = toolCalls[i];
                        string toolResult = toolResults[i];

                        messages.Add(new ChatApiMessage
                        {
                            Role = "tool",
                            Content = toolResult,
                            ToolCallId = tc.Id,
                            Name = tc.Function.Name
                        });

                        Logger.Info($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");
                    }

                    // ── 移交检测：如果 AI 调用了 request_handoff，立即终止循环 ──
                    if (PendingHandoffRequest != null)
                    {
                        Logger.Info($"[Agent:{Definition.Name}] 🔄 检测到移交请求 → {PendingHandoffRequest.TargetAgent}，终止工具循环");
                        if (contentBuilder.Length > 0)
                            contentBuilder.Append("\n\n> 🔄 任务已移交给 " + PendingHandoffRequest.TargetAgent + " Agent...");
                        break;
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

                    // 检测同一调用重复（带同结果判断：只有每次返回相同结果才终止）
                    foreach (var sig in roundSignatures)
                    {
                        int repeatCount = callSignatureHistory.Count(s => s == sig);
                        if (repeatCount >= maxRepeatedSameCall)
                        {
                            string toolName = sig.Split('|')[0];

                            // ── 同结果检测：找到本轮该签名对应的工具执行结果 ──
                            string? currentResult = null;
                            for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                            {
                                string tcSig = toolCalls[i].Function.Name + "|" +
                                    (toolCalls[i].Function.Arguments.Length > 200
                                        ? toolCalls[i].Function.Arguments.Substring(0, 200)
                                        : toolCalls[i].Function.Arguments);
                                if (tcSig == sig) { currentResult = toolResults[i]; break; }
                            }

                            // 检查与上次结果是否相同
                            bool sameResult = currentResult != null
                                && lastResultBySignature.TryGetValue(sig, out string? prevResult)
                                && AreResultsSubstantiallySame(prevResult, currentResult);

                            if (sameResult)
                            {
                                // 结果相同 → 真正的死循环，终止
                                loopDetected = true;
                                Logger.Warn($"[Agent:{Definition.Name}] 🔄 检测到循环调用: {toolName} 已重复 {repeatCount} 次且每次返回相同结果");

                                var terminatedBuilder = new StringBuilder();
                                terminatedBuilder.Append(contentBuilder);

                                if (toolCallHistory.Count > 0)
                                {
                                    terminatedBuilder.Append("\n\n---\n### 🔙 此前 AI 执行的操作\n");
                                    int startRound = toolCallHistory[0].Round;
                                    foreach (var (r, summary) in toolCallHistory)
                                    {
                                        string prefix = r == round ? "🔄" : $"第{r - startRound + 1}轮";
                                        terminatedBuilder.AppendLine($"- {prefix} {summary}");
                                    }
                                }

                                for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                                {
                                    string result = toolResults[i];
                                    if (!string.IsNullOrWhiteSpace(result))
                                        terminatedBuilder.Append($"\n\n### 📋 最后一次 `{toolCalls[i].Function.Name}` 结果\n\n{result.Truncate(3000)}");
                                }

                                terminatedBuilder.Append($"\n\n> ⚠️ 检测到 `{toolName}` 重复调用 {repeatCount} 次且每次返回相同结果，已自动终止循环。请根据以上工具结果修复问题后重新请求。");
                                contentBuilder.Clear();
                                contentBuilder.Append(terminatedBuilder.ToString());
                                break;
                            }
                            else
                            {
                                // 结果不同 → 用户正在修复问题，重置该签名的计数并记录本次结果
                                if (currentResult != null)
                                    lastResultBySignature[sig] = currentResult;
                                callSignatureHistory.RemoveAll(s => s == sig);
                                Logger.Info($"[Agent:{Definition.Name}] 🔄 {toolName} 重复 {repeatCount} 次但结果不同，可能是用户在修复问题，继续执行");
                            }
                        }
                        else
                        {
                            // 正常记录结果（用于后续比较）
                            for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                            {
                                string tcSig = toolCalls[i].Function.Name + "|" +
                                    (toolCalls[i].Function.Arguments.Length > 200
                                        ? toolCalls[i].Function.Arguments.Substring(0, 200)
                                        : toolCalls[i].Function.Arguments);
                                if (tcSig == sig && !string.IsNullOrWhiteSpace(toolResults[i]))
                                    lastResultBySignature[sig] = toolResults[i];
                            }
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
                            Logger.Warn($"[Agent:{Definition.Name}] 🔄 连续 {consecutiveErrorRounds} 轮工具调用全部返回错误，强制结束");

                            // ── 附加上次 AI 的上下文总结和最后一次工具调用结果摘要 ──
                            var terminatedBuilder = new StringBuilder();

                            // 1. AI 本轮文字回复
                            terminatedBuilder.Append(contentBuilder);

                            // 2. 历史工具调用总结
                            if (toolCallHistory.Count > 0)
                            {
                                terminatedBuilder.Append("\n\n---\n### 🔙 此前 AI 执行的操作\n");
                                int startRound = toolCallHistory[0].Round;
                                foreach (var (r, summary) in toolCallHistory)
                                {
                                    string prefix = r == round ? "🔄" : $"第{r - startRound + 1}轮";
                                    terminatedBuilder.AppendLine($"- {prefix} {summary}");
                                }
                            }

                            // 3. 最后一次工具调用结果
                            for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                            {
                                string result = toolResults[i];
                                if (!string.IsNullOrWhiteSpace(result))
                                {
                                    terminatedBuilder.Append($"\n\n### 📋 最后一次 `{toolCalls[i].Function.Name}` 结果\n\n{result.Truncate(3000)}");
                                }
                            }

                            terminatedBuilder.Append($"\n\n> ⚠️ 连续 {consecutiveErrorRounds} 轮工具调用均失败，已自动终止。");
                            contentBuilder.Clear();
                            contentBuilder.Append(terminatedBuilder.ToString());
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

            // ── 重置轮次，避免后续非循环调用受残留轮次影响 ──
            if (BuiltInTools != null)
                BuiltInTools.CurrentRound = 0;

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
        /// 带超时保护的工具执行包装。
        /// 每个工具调用单独计时，超时则返回错误信息而非阻塞整个循环。
        /// 对于需要用户交互的命令（run_in_terminal、delete_file、VisualStudio_askQuestions），不设超时。
        /// </summary>
        /// <param name="tc">工具调用定义</param>
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="timeout">超时时间（用户交互命令忽略此参数）</param>
        /// <returns>工具执行结果字符串</returns>
        private async Task<string> ExecuteToolWithTimeoutAsync(
            ToolCall tc, string? workspaceRoot, CancellationToken ct, TimeSpan timeout)
        {
            // 需要用户交互的命令不设超时，直接执行
            if (IsInteractiveTool(tc.Function.Name))
            {
                try
                {
                    return await ExecuteToolAsync(tc.Function.Name, tc.Function.Arguments, workspaceRoot, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 执行异常: {ex.Message}", ex);
                    return $"❌ 工具执行异常: {ex.Message}";
                }
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                var execTask = ExecuteToolAsync(tc.Function.Name, tc.Function.Arguments, workspaceRoot, timeoutCts.Token);
                var completed = await Task.WhenAny(execTask, Task.Delay(timeout, ct)).ConfigureAwait(false);

                if (completed == execTask)
                {
                    return await execTask.ConfigureAwait(false);
                }
                else
                {
                    // 超时 — 取消工具执行
                    timeoutCts.Cancel();
                    Logger.Warn($"[Agent:{Definition.Name}] ⏱️ 工具 {tc.Function.Name} 执行超时 ({timeout.TotalSeconds:F0}s)，已终止");
                    return $"⏱️ 工具 {tc.Function.Name} 执行超时（{timeout.TotalSeconds:F0}s），已跳过。";
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 仅由 timeoutCts 触发（非外部 ct）
                Logger.Warn($"[Agent:{Definition.Name}] ⏱️ 工具 {tc.Function.Name} 执行超时 ({timeout.TotalSeconds:F0}s)，已终止");
                return $"⏱️ 工具 {tc.Function.Name} 执行超时（{timeout.TotalSeconds:F0}s），已跳过。";
            }
            catch (Exception ex)
            {
                Logger.Error($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 执行异常: {ex.Message}", ex);
                return $"❌ 工具执行异常: {ex.Message}";
            }
        }

        /// <summary>
        /// 执行单个工具调用（优先内置工具，其次 MCP 工具）。
        /// 对于 run_in_terminal 命令，需要用户审批后才能执行。
        /// </summary>
        private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, string? workspaceRoot, CancellationToken ct)
        {
            // ── 注入 ExploreHandler 到 BuiltInToolService（桥接 Agent.ExploreAgent）──
            if (BuiltInTools != null && ExploreAgent != null)
            {
                BuiltInTools.ExploreHandler = async (ctx) =>
                {
                    try
                    {
                        // ── 同步上下文到 ExploreAgent ──
                        ExploreAgent.Context = this.Context;
                        if (ExploreAgent.BuiltInTools == null)
                            ExploreAgent.BuiltInTools = this.BuiltInTools;
                        if (ExploreAgent.McpManager == null)
                            ExploreAgent.McpManager = this.McpManager;

                        // ── 构建 ExploreAgent 上下文 ──
                        var exploreCtx = new AgentContext
                        {
                            SolutionPath = ctx.WorkspaceRoot ?? Context?.SolutionPath,
                            CancellationToken = ct,
                            ContextManager = Context?.ContextManager,
                            FileReadCache = Context?.FileReadCache ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            DiscoveredFiles = Context?.DiscoveredFiles,
                        };

                        AddLog("INFO", $"[{Definition.Name}] → ExploreAgent: {ctx.Description}");

                        // ── 转发 ExploreAgent 日志到父 Agent，让用户看到探索进度 ──
                        Action<AgentLogEntry> forwardLog = (entry) =>
                        {
                            AddLog(entry.Level, $"[Explore] {entry.Message.Truncate(200)}");
                        };
                        ExploreAgent.LogEntryAdded += forwardLog;
                        AgentResult exploreResult;
                        try
                        {
                            exploreResult = await ExploreAgent.ExecuteAsync(ctx.Prompt, exploreCtx);
                        }
                        finally
                        {
                            ExploreAgent.LogEntryAdded -= forwardLog;
                        }

                        // ── 回收 ExploreAgent 的缓存到当前 Context ──
                        if (Context != null && exploreResult.Success)
                        {
                            // 文件读缓存
                            if (exploreCtx.FileReadCache.Count > 0)
                            {
                                foreach (var kvp in exploreCtx.FileReadCache)
                                {
                                    Context.FileReadCache[kvp.Key] = kvp.Value;
                                }
                            }
                            // 发现的文件列表
                            if (exploreCtx.DiscoveredFiles != null && exploreCtx.DiscoveredFiles.Count > 0)
                            {
                                Context.DiscoveredFiles = exploreCtx.DiscoveredFiles;
                            }
                        }

                        if (exploreResult.Success && !string.IsNullOrEmpty(exploreResult.Content))
                        {
                            AddLog("INFO", $"[{Definition.Name}] ExploreAgent 完成: {exploreResult.Content.Length} 字符");
                            return exploreResult.Content;
                        }

                        return exploreResult.Success
                            ? "(ExploreAgent 完成但无内容)"
                            : $"❌ ExploreAgent 失败: {exploreResult.ErrorMessage ?? "未知错误"}";
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[BaseAgent] runSubagent 异常: {ex.Message}", ex);
                        return $"❌ runSubagent 执行异常: {ex.Message}";
                    }
                };
            }

            // ── 注入 HandoffHandler 到 BuiltInToolService（处理 request_handoff 工具）──
            if (BuiltInTools != null)
            {
                BuiltInTools.HandoffHandler = async (request) =>
                {
                    request.SourceAgent = Definition.Type;
                    PendingHandoffRequest = request;
                    AddLog("INFO", $"[{Definition.Name}] 🔄 移交请求: → {request.TargetAgent} (原因: {request.Reason})");
                    await Task.CompletedTask;
                };
            }

            // ── 终端命令需要用户审批 ──
            if (toolName == "run_in_terminal" && BuiltInTools != null)
            {
                string command = string.Empty;
                string explanation = string.Empty;
                string purpose = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("command", out var cmdProp))
                        command = cmdProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("explanation", out var explProp))
                        explanation = explProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                        purpose = purProp.GetString() ?? string.Empty;
                    // goal 是 DeepSeek 模型原生使用的参数名，作为 purpose 的 fallback
                    if (string.IsNullOrWhiteSpace(purpose) && doc.RootElement.TryGetProperty("goal", out var goalProp))
                        purpose = goalProp.GetString() ?? string.Empty;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(command))
                {
                    bool approved = await RequestTerminalApprovalAsync(command, explanation, purpose);
                    if (!approved)
                        return $"⏭️ 用户跳过了终端命令: {command}";
                }
            }

            // ── 文件删除需要用户审批 ──
            if (toolName == "delete_file" && BuiltInTools != null)
            {
                string filePath = string.Empty;
                string explanation = string.Empty;
                string purpose = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("filePath", out var fpProp))
                        filePath = fpProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("explanation", out var explProp))
                        explanation = explProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                        purpose = purProp.GetString() ?? string.Empty;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var paths = new List<string> { filePath };
                    bool approved = await RequestFileDeleteConfirmationAsync(paths, explanation, purpose);
                    if (!approved)
                        return $"⏭️ 用户取消了文件删除: {System.IO.Path.GetFileName(filePath)}";
                }
            }

            // ── VisualStudio_askQuestions：向用户提问并等待回答 ──
            if (toolName == "VisualStudio_askQuestions")
            {
                string questionsJson = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("questions", out var qProp))
                        questionsJson = qProp.GetRawText();
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(questionsJson))
                {
                    return await RequestAskQuestionsAsync(questionsJson);
                }
                return LocalizationService.Instance["service.baseAgent.missingQuestions"];
            }

            // ── 越权文件访问检查：AI 访问项目外路径需要用户审批 ──
            if (BuiltInTools != null && IsFileAccessingTool(toolName))
            {
                string? targetPath = ExtractAnyPathFromToolArgs(toolName, argumentsJson);
                if (!string.IsNullOrWhiteSpace(targetPath)
                    && !string.IsNullOrWhiteSpace(workspaceRoot)
                    && IsPathOutsideWorkspace(targetPath, workspaceRoot))
                {
                    string operation = GetToolOperationName(toolName);
                    string fileName = System.IO.Path.GetFileName(targetPath.TrimEnd('/', '\\'));
                    string displayName = string.IsNullOrEmpty(fileName) ? targetPath : fileName;

                    bool approved = await RequestPermissionAsync(
                        $"确认{operation}项目外路径: {displayName}",
                        $"AI 正在尝试{operation}当前项目之外的路径：\n\n`{targetPath}`\n\n⚠️ 该路径不在当前工作区 `{workspaceRoot}` 内。",
                        "file_access_outside_workspace",
                        "",
                        $"AI 请求{operation}项目外部路径 `{targetPath}` 以完成任务");
                    if (!approved)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.permissionDenied", targetPath));
                        return $"⛔ 用户拒绝了项目外路径{operation}: {targetPath}\n\n"
                            + $"⚠️ 重要提醒：用户已明确拒绝访问此项目外路径。\n"
                            + $"请绝对不要再尝试访问 `{targetPath}` 或其父目录下的任何文件。\n"
                            + $"请基于当前工作区 `{workspaceRoot}` 内的文件完成任务。";
                    }
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.permissionGranted", targetPath));
                }
            }

            // ── 修改项目文件（.vcxproj/.csproj/.slnx 等）需要用户确认 ──
            if (BuiltInTools != null && IsFileModifyingTool(toolName))
            {
                string? targetPath = ExtractFilePathFromToolArgs(toolName, argumentsJson);
                if (!string.IsNullOrWhiteSpace(targetPath) && IsProjectFile(targetPath))
                {
                    string fileName = System.IO.Path.GetFileName(targetPath);
                    string operation = toolName switch
                    {
                        "replace_string_in_file" => "修改",
                        "multi_replace_string_in_file" => "批量修改",
                        "create_file" => "创建",
                        "apply_patch" => "应用补丁到",
                        _ => "操作"
                    };
                    // 根据工具类型自动推断操作目的
                    string filePurpose = toolName switch
                    {
                        "create_file" => $"创建新的项目文件 `{fileName}` 以扩展项目功能",
                        "replace_string_in_file" => $"修改 `{fileName}` 中的项目配置以适配代码变更",
                        "multi_replace_string_in_file" => $"对 `{fileName}` 进行多处配置调整以适配代码变更",
                        "apply_patch" => $"向 `{fileName}` 应用代码补丁以完成修改",
                        _ => $"对项目文件 `{fileName}` 进行必要的配置变更"
                    };
                    bool approved = await RequestPermissionAsync(
                        $"确认{operation}项目文件: {fileName}",
                        $"即将{operation}项目配置文件 `{fileName}`\n\n路径: {targetPath}\n\n⚠️ 修改项目文件可能影响构建配置和项目结构。",
                        "file_write",
                        "",
                        filePurpose);
                    if (!approved)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.projectModDenied", fileName));
                        return $"⏭️ 用户取消了项目文件{operation}: {fileName}";
                    }
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.projectModGranted", fileName));
                }
            }

            // ── 1. MCP 工具优先（同名时覆盖内置）──
            if (McpManager != null)
            {
                var mcpTools = McpManager.AllTools;
                bool hasMcpTool = mcpTools.Any(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
                if (hasMcpTool)
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
            }

            // ── 2. 内置工具（回退）──
            if (BuiltInTools != null && BuiltInToolService.IsBuiltInTool(toolName))
            {
                string? result = await BuiltInTools.ExecuteBuiltInToolAsync(toolName, argumentsJson, workspaceRoot);
                if (result != null)
                    return result;
            }

            return $"❌ 未知工具: {toolName}";
        }

        #endregion

        #region Shared Utility Methods

        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名 / 文件名集合。
        /// 修改这些文件可能影响项目结构，需要用户明确许可。
        /// </summary>
        protected static readonly HashSet<string> ProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".vcxproj", ".csproj", ".fsproj", ".vbproj",
            ".sln", ".slnx",
            ".vcxproj.filters", ".vcxproj.user",
            ".csproj.user", ".vbproj.user",
            "CMakeLists.txt", "CMakeSettings.json",
            "packages.config", "Directory.Build.props", "Directory.Build.targets",
            ".props", ".targets",
            "Makefile", "makefile", "GNUmakefile",
            ".slnf",
        };

        /// <summary>
        /// 检查文件是否为项目配置文件（需要用户确认才能修改）。
        /// EditAgent / BuildAgent 共享使用。
        /// </summary>
        protected static bool IsProjectFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string fileName = System.IO.Path.GetFileName(filePath);
            string ext = System.IO.Path.GetExtension(filePath);
            return ProjectFileExtensions.Contains(fileName) || ProjectFileExtensions.Contains(ext);
        }

        /// <summary>
        /// 从 AgentContext 解析工作区根目录。
        /// BuildAgent / EditAgent / ExploreAgent 共享使用。
        /// </summary>
        protected static string GetWorkspaceRoot(AgentContext context)
        {
            string root = context.SolutionPath ?? string.Empty;
            if (!string.IsNullOrEmpty(root) && File.Exists(root))
                root = Path.GetDirectoryName(root) ?? root;
            return root;
        }

        /// <summary>
        /// 将 HandoffRequest（AI 通过 request_handoff 工具发起的 JSON 移交）
        /// 转换为 AgentHandoff（AgentDispatcher 使用的移交格式）。
        /// </summary>
        protected AgentHandoff ConvertHandoffRequestToHandoff(HandoffRequest request)
        {
            string label = request.TargetAgent switch
            {
                AgentType.Ask => "生成总结",
                AgentType.Edit => "执行修改",
                AgentType.Plan => "制定计划",
                AgentType.Build => "诊断修复",
                AgentType.Explore => "探索代码库",
                _ => "移交任务"
            };

            string prompt = $"[移交自 {request.SourceAgent}] {request.Reason}\n\n{request.TaskDescription}";

            return new AgentHandoff
            {
                Label = label,
                TargetAgent = request.TargetAgent,
                Prompt = prompt,
                AutoSend = request.AutoSend,
                ShowContinueOn = !request.AutoSend,
            };
        }

        /// <summary>
        /// 检测 AI 回复中是否表明编译仍存在错误。
        /// 使用多层策略：❌ 前缀 → 错误代码正则 → MSBuild 摘要 → 关键词匹配。
        /// BuildAgent / EditAgent 共享使用。
        /// </summary>
        protected static bool HasBuildFailure(string aiResponse)
        {
            if (string.IsNullOrWhiteSpace(aiResponse))
                return false;

            // ── 策略1：工具级失败标志 ❌ ──
            if (aiResponse.Contains("❌ 构建失败") || aiResponse.Contains("❌ 编译失败")
                || aiResponse.Contains("❌ build") || aiResponse.Contains("❌ Build"))
                return true;

            // ── 策略2：MSBuild / 编译器错误代码 ──
            if (System.Text.RegularExpressions.Regex.IsMatch(aiResponse,
                @"\berror\s+(CS|C|LNK|MSB|BC|FS|TS|RUST)\d+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            // ── 策略3：MSBuild 摘要失败模式（Singleline 支持跨行匹配）──
            if (aiResponse.Contains("Build FAILED")
                || System.Text.RegularExpressions.Regex.IsMatch(aiResponse,
                    @"\b0\s+succeeded.*\b[1-9]\d*\s+failed\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Singleline))
                return true;

            // ── 策略4：关键词匹配（排除误报）──
            string lower = aiResponse.ToLowerInvariant();
            bool hasFailure = lower.Contains("build failed")
                || lower.Contains("构建失败")
                || lower.Contains("编译失败")
                || lower.Contains("error cs")
                || lower.Contains("error lnk")
                || lower.Contains("error msb");

            bool hasSuccess = lower.Contains("build succeeded")
                || lower.Contains("0 个错误")
                || lower.Contains("0 errors")
                || lower.Contains("✅")
                || lower.Contains("编译通过")
                || lower.Contains("构建成功");

            return hasFailure && !hasSuccess;
        }

        /// <summary>
        /// 剥离 DeepSeek V4 泄露到 content 中的 DSML/工具调用 XML 标签。
        /// PlanAgent / BaseAgent 共享使用。
        /// </summary>
        protected static string StripDsmlContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // ── 移除完整的 DSML/工具调用 XML 块（含嵌套内容）──
            // 支持两种格式：
            //   1. 标准 XML: <tagname>...</tagname>
            //   2. DeepSeek V4 管道分隔: <|tagname|>...</|tagname|>
            string[] blockTags = {
                "DSML", "function_calls?", "tool_calls?", "invoke", "parameter",
                "VisualStudio_askQuestions", "runSubagent", "tool_result",
                "file_search", "grep_search", "list_dir", "read_file",
                "semantic_search", "fetch_webpage", "run_in_terminal",
                "create_file", "replace_string_in_file", "edit_notebook_file",
                "create_directory", "run_notebook_cell", "install_python_packages",
                "runSubagent", "runTests", "mcp_\\w+", "github_\\w+",
                // DeepSeek V4 DSML wrapper tags
                "response", "result", "output", "answer"
            };

            string result = text;
            foreach (var tag in blockTags)
            {
                // ── 格式1：标准 XML 自闭合标签 <tag ... /> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\s*" + tag + @"(\s+[^>]*)?\s*/\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // ── 格式1：标准 XML 配对标签 <tag>...</tag> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\s*" + tag + @"(\s+[^>]*)?\s*>.*?</\s*" + tag + @"\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // ── 格式2：管道分隔自闭合标签 <|tag| ... /> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\|\s*" + tag + @"\s*\|(\s+[^>]*)?\s*/\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // ── 格式2：管道分隔配对标签 <|tag|>...</|tag|> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\|\s*" + tag + @"\s*\|(\s+[^>]*)?\s*>.*?</\|\s*" + tag + @"\s*\|?\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            }

            // ── 移除残留的独立开标签/闭标签（标准 XML）──
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?\s*(?:DSML|function_calls?|tool_calls?|invoke|parameter|VisualStudio_askQuestions|runSubagent|tool_result|response|result|output|answer)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // ── 移除残留的独立开标签/闭标签（管道分隔格式）──
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?<|\|\s*(?:DSML|function_calls?|tool_calls?|invoke|parameter|VisualStudio_askQuestions|runSubagent|tool_result|response|result|output|answer)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // 更精确的管道分隔残留标签
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"<\|?\s*(?:DSML|function_calls?|tool_calls?|invoke|parameter|response|result|output|answer)[^>]*\|?>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // ── 移除  response / thinking / analysis / reasoning / plan / reflection 伪标签 ──
            // 支持标准XML和管道分隔两种格式
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?\s*(?:response|thinking|analysis|reasoning|plan|reflection)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"<\|?\s*(?:response|thinking|analysis|reasoning|plan|reflection)[^>]*\|?>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // ── 额外清理：移除 DSML 属性格式的标签残留 ──
            // 例如: name="tool_name" 或 type="function" 孤立属性
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"\b(?:name|type|arguments|function)\s*=\s*""[^""]*""",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return result.Trim();
        }

        /// <summary>
        /// 注册 ExploreAgent 子代理，自动管理日志和文件变更事件转发。
        /// EditAgent / PlanAgent 共享使用，消除重复的样板代码。
        /// </summary>
        protected void RegisterExploreAgent(ExploreAgent? exploreAgent, ref ExploreAgent? field)
        {
            if (field != null)
            {
                field.LogEntryAdded -= OnExploreLog;
                field.FileChangeNotified -= OnExploreFileChange;
            }
            field = exploreAgent;
            if (field != null)
            {
                field.LogEntryAdded += OnExploreLog;
                field.FileChangeNotified += OnExploreFileChange;
            }
        }

        private void OnExploreLog(AgentLogEntry entry)
        {
            AddLog(entry.Level, $"[Explore] {entry.Message}");
        }

        private void OnExploreFileChange(AgentFileChangeEventArgs args)
        {
            NotifyFileChange(args.PlanId, args.ChangeType, args.FilePath, args.Detail);
        }

        /// <summary>
        /// 判断工具是否为文件修改类工具（可能修改用户文件）。
        /// </summary>
        private static bool IsFileModifyingTool(string toolName)
        {
            return toolName is "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_file"
                or "apply_patch";
        }

        /// <summary>
        /// 判断工具是否为文件访问类工具（读/写/搜索/列表，涉及文件路径）。
        /// 用于越权检测——AI 访问项目外路径时需用户审批。
        /// </summary>
        private static bool IsFileAccessingTool(string toolName)
        {
            return toolName is "read_file"
                or "list_dir"
                or "create_file"
                or "delete_file"
                or "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_directory"
                or "file_search"
                or "grep_search"
                or "apply_patch";
        }

        /// <summary>
        /// 获取工具操作的中文名称（用于审批提示）。
        /// </summary>
        private static string GetToolOperationName(string toolName)
        {
            return toolName switch
            {
                "read_file" => "读取",
                "list_dir" => "列出目录",
                "create_file" => "创建文件",
                "delete_file" => "删除文件",
                "replace_string_in_file" => "修改",
                "multi_replace_string_in_file" => "批量修改",
                "create_directory" => "创建目录",
                "file_search" => "搜索文件",
                "grep_search" => "搜索内容",
                "apply_patch" => "应用补丁到",
                _ => "访问"
            };
        }

        /// <summary>
        /// 判断目标路径是否在工作区之外。
        /// 支持绝对路径和相对路径判断。
        /// </summary>
        private static bool IsPathOutsideWorkspace(string targetPath, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(workspaceRoot))
                return false;

            // ── 规范化路径 ──
            string normalizedTarget = targetPath.Replace('/', '\\').Trim();
            string normalizedRoot = workspaceRoot.Replace('/', '\\').Trim().TrimEnd('\\') + '\\';

            // ── 绝对路径：直接比较前缀 ──
            if (System.IO.Path.IsPathRooted(normalizedTarget))
            {
                string fullTarget = System.IO.Path.GetFullPath(normalizedTarget).TrimEnd('\\') + '\\';
                string fullRoot = System.IO.Path.GetFullPath(normalizedRoot).TrimEnd('\\') + '\\';
                return !fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }

            // ── 相对路径：以 ../ 或 ..\ 开头视为项目外 ──
            if (normalizedTarget.StartsWith(".."))
                return true;

            // ── 纯相对路径（如 "src/file.cs"）：视为项目内 ──
            return false;
        }

        /// <summary>
        /// 从各种工具参数中提取文件/目录路径。
        /// 比 ExtractFilePathFromToolArgs 更通用，覆盖 file_search/grep_search 的 includePattern 等。
        /// </summary>
        private static string? ExtractAnyPathFromToolArgs(string toolName, string argumentsJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                // ── 通用 filePath 参数 ──
                if (root.TryGetProperty("filePath", out var fpProp))
                    return fpProp.GetString();

                // ── list_dir / create_directory 的 path/dirPath ──
                if (root.TryGetProperty("path", out var pProp))
                    return pProp.GetString();
                if (root.TryGetProperty("dirPath", out var dpProp))
                    return dpProp.GetString();

                // ── file_search / grep_search 的 includePattern（可能是绝对路径）──
                if (root.TryGetProperty("includePattern", out var ipProp))
                {
                    string pattern = ipProp.GetString() ?? string.Empty;
                    // 如果 pattern 是绝对路径（如 "F:\\project\\src\\**"），提取目录部分
                    if (System.IO.Path.IsPathRooted(pattern))
                    {
                        // 去掉 glob 通配符部分，取目录
                        int globIdx = pattern.IndexOfAny(new[] { '*', '?' });
                        string dirPart = globIdx >= 0 ? pattern.Substring(0, globIdx) : pattern;
                        string? dir = System.IO.Path.GetDirectoryName(dirPart.TrimEnd('/', '\\'));
                        return dir ?? dirPart;
                    }
                    return null; // 相对 pattern，不检查
                }

                // ── file_search 的 query 参数也可能是绝对 glob 路径 ──
                // 例如 query="F:\\outside\\**\\*.cs"，Path.Combine 会因绝对路径直接跳转到项目外
                if (toolName == "file_search" && root.TryGetProperty("query", out var qProp))
                {
                    string query = qProp.GetString() ?? string.Empty;
                    if (System.IO.Path.IsPathRooted(query))
                    {
                        int globIdx = query.IndexOfAny(new[] { '*', '?' });
                        string dirPart = globIdx >= 0 ? query.Substring(0, globIdx) : query;
                        string? dir = System.IO.Path.GetDirectoryName(dirPart.TrimEnd('/', '\\'));
                        return dir ?? dirPart;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 判断工具是否需要用户交互（不设工具层超时）。
        /// 构建类工具也在此列，因为其执行时间不可预测，超时由 BuildService 内部自行管理。
        /// read_file / list_dir 等只读工具也包含在内，因为它们可能触发项目外路径权限弹窗，
        /// 用户响应时间不可预测，硬超时会导致权限拒绝结果丢失。
        /// </summary>
        private static bool IsInteractiveTool(string toolName)
        {
            return toolName is "run_in_terminal"
                or "delete_file"
                or "VisualStudio_askQuestions"
                or "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_file"
                or "apply_patch"
                or "build_solution"          // 构建时间不可预测，由 BuildService 内部控制超时
                or "create_and_run_task"     // 自定义任务同理
                or "read_file"               // 可能触发项目外路径权限弹窗
                or "list_dir"                // 同上
                or "create_directory"        // 同上
                or "file_search"             // 同上
                or "grep_search";            // 同上
        }

        /// <summary>
        /// 根据工具类型返回合适的超时时间（仅对非交互式、非构建类工具生效）。
        /// 交互式工具和构建类工具由 IsInteractiveTool 直接跳过超时。
        /// </summary>
        private static TimeSpan GetToolTimeout(string toolName)
        {
            return toolName switch
            {
                _ => TimeSpan.FromSeconds(60),  // 默认 60 秒
            };
        }

        /// <summary>
        /// 从工具参数 JSON 中提取目标文件路径。
        /// </summary>
        private static string? ExtractFilePathFromToolArgs(string toolName, string argumentsJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("filePath", out var fpProp))
                    return fpProp.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 处理流式响应的单个 chunk，分派到 reasoning/content/tool_call 缓冲区。
        /// 与 DeepSeekChatControl.Messaging.cs 中的逻辑一致。
        /// </summary>
        private static void ProcessStreamChunk(
            string chunk,
            StringBuilder reasoningBuilder,
            StringBuilder contentBuilder,
            Dictionary<int, Models.ToolCallAccumulator> toolCallAccumulator,
            Action<string>? onThinking,
            Action<string>? onContent)
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
                    var deltas = System.Text.Json.JsonSerializer.Deserialize<List<ToolCallDelta>>(tcJson);
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
                catch (System.Text.Json.JsonException) { }
            }
            else if (chunk.StartsWith("[CACHE]"))
            {
                // Cache 统计信息 — 过滤，不混入正文
            }
            else
            {
                contentBuilder.Append(chunk);
                onContent?.Invoke(chunk);
            }
        }

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
        /// 同时剥离 DeepSeek V4 可能在 content 中输出的 XML 风格标签。
        /// 当 tools=null 时，DeepSeek 会将工具调用意图以 DSML/function_call 等标签泄露到 content。
        /// </summary>
        protected static string ExtractJsonFromMarkdown(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            // ── 剥离 XML 风格的标签（DeepSeek V4 可能将推理/工具调用泄露到 content 字段）──
            // 支持标准 XML <tag> 和 DeepSeek 管道分隔 <|tag|> 两种格式
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"</?(?:\|?\s*)?(?:thinking|analysis|reasoning|plan|reflection|response|result|output|DSML|function_calls?|tool_calls?)(?:\s*\|?)?[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // 额外处理管道分隔格式的残留
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"<\|?\s*(?:invoke|parameter|read_file|file_search|grep_search|list_dir|run_in_terminal|create_file|replace_string_in_file|runSubagent)[^>]*\|?>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            int jsonStart = cleaned.IndexOf('{');
            int jsonEnd = cleaned.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                return cleaned.Substring(jsonStart, jsonEnd - jsonStart + 1);

            return cleaned.Trim();
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
                    // RAG-SOURCE: file-read 读取文件原始内容（计算变更行数）
                    string oldContent = System.IO.File.ReadAllText(filePath);
                    // 使用精确的逐行差异算法（CodeDiffService），而非简单行数减法
                    CountDiffLines(oldContent, newContent, out int added, out int removed);
                    linesAdded = added;
                    linesRemoved = removed;
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
        /// 使用 CodeDiffService 精确计算新增行数和删除行数。
        /// 替代之前仅用净行数差值的错误算法。
        /// </summary>
        protected static void CountDiffLines(string oldText, string newText, out int added, out int removed)
        {
            added = 0;
            removed = 0;
            if (string.IsNullOrEmpty(oldText) && string.IsNullOrEmpty(newText))
                return;

            try
            {
                var diff = CodeDiffService.ComputeDiff(oldText ?? string.Empty, newText ?? string.Empty);
                foreach (var line in diff)
                {
                    if (line.Type == DiffLineType.Added)
                        added++;
                    else if (line.Type == DiffLineType.Deleted)
                        removed++;
                }
            }
            catch
            {
                // 回退到简单行数比较
                int oldLines = CountLines(oldText ?? string.Empty);
                int newLines = CountLines(newText ?? string.Empty);
                added = Math.Max(0, newLines - oldLines);
                removed = Math.Max(0, oldLines - newLines);
            }
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
        /// <param name="title">简短标题（如"确认修改项目文件: foo.csproj"）</param>
        /// <param name="command">具体操作描述（告诉用户将要做什么）</param>
        /// <param name="actionType">操作类型</param>
        /// <param name="detail">可选额外详情（如文件写入时展示变更内容预览）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要执行此操作）</param>
        public async Task<bool> RequestPermissionAsync(string title, string command, string actionType = "command", string detail = "", string purpose = "")
        {
            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = actionType,
                Detail = detail,
                Purpose = purpose,
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
        /// 请求用户批准终端命令执行（显示命令详情和说明）。
        /// </summary>
        /// <param name="command">待执行的命令字符串</param>
        /// <param name="explanation">命令用途说明（告诉用户这条命令在做什么，如"编译项目检查错误"）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要执行，如"验证代码修改后能否正常编译"）</param>
        /// <returns>true 表示用户允许执行，false 表示跳过</returns>
        public async Task<bool> RequestTerminalApprovalAsync(string command, string explanation, string purpose = "")
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            var request = new AgentPermissionRequest
            {
                Title = "运行命令?",
                Command = command,
                ActionType = "terminal_command",
                Purpose = purpose,
                FilePaths = new List<string> { explanation ?? string.Empty },
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingTerminalApproval"], command));

            bool approved = await request.ResponseTcs.Task;
            PendingPermission = null;
            AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.terminalApprovalResult"],
                approved ? "✅ 允许" : "⏭️ 跳过", command));
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
        /// 向用户提问并等待回答（VisualStudio_askQuestions 工具实现）。
        /// 使用 TaskCompletionSource 等待用户在 WebView 中提交答案。
        /// </summary>
        /// <param name="questionsJson">问题列表的 JSON 字符串</param>
        /// <returns>用户答案的 JSON 字符串，或超时/取消时的空字符串</returns>
        public async Task<string> RequestAskQuestionsAsync(string questionsJson)
        {
            try
            {
                var questions = System.Text.Json.JsonSerializer.Deserialize<List<AgentQuestion>>(
                    questionsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (questions == null || questions.Count == 0)
                    return LocalizationService.Instance["service.baseAgent.emptyQuestions"];

                var request = new AgentQuestionRequest
                {
                    Questions = questions,
                    ResponseTcs = new TaskCompletionSource<string>(),
                };

                PendingQuestion = request;
                QuestionsRequested?.Invoke(request);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingAnswers"],
                    questions.Count, questions[0].Header.Truncate(60)));

                // 无限等待用户回答（不设超时），用户提交或跳过时通过 ResponseTcs 唤醒
                string answers = await request.ResponseTcs.Task;

                PendingQuestion = null;
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.answersReceived"],
                    answers.Truncate(200)));
                return answers;
            }
            catch (Exception ex)
            {
                return $"❌ VisualStudio_askQuestions 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 响应用户提问回答。
        /// </summary>
        public void RespondToQuestions(string requestId, string answersJson)
        {
            if (PendingQuestion?.RequestId == requestId)
                PendingQuestion.ResponseTcs?.TrySetResult(answersJson);
        }

        /// <summary>
        /// 请求用户确认文件删除操作。
        /// 会中断当前执行流，在 WebView 中渲染确认按钮，等待用户响应。
        /// </summary>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        /// <param name="reason">删除原因说明（告诉用户为什么删除这些文件）</param>
        /// <param name="purpose">操作目的（告诉用户删除后能达到什么效果）</param>
        /// <returns>true 表示用户确认删除，false 表示取消</returns>
        public async Task<bool> RequestFileDeleteConfirmationAsync(List<string> filePaths, string reason = "", string purpose = "")
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
                Purpose = purpose,
                FilePaths = new List<string>(filePaths),
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            PendingPermission = request;
            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"{LocalizationService.Instance["agent.log.waitingDeleteConfirm"]}: {title}");

            bool approved = await request.ResponseTcs.Task;
            PendingPermission = null;
            AddLog("INFO", LocalizationService.Instance.Format("agent.log.fileDeleteConfirm", approved ? "✅ 确认删除" : "❌ 取消", title));
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

        #region Shared Helpers

        /// <summary>
        /// 规范化文件路径（统一分隔符、去除尾部空格），用于 GroupBy 合并。
        /// </summary>
        protected static string NormalizePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            return filePath.Replace('/', '\\').Trim().TrimEnd('\\');
        }

        /// <summary>
        /// 判断两次工具调用结果是否实质相同（用于循环检测）。
        /// 对错误类工具（get_errors、build_solution）提取核心错误行比较，
        /// 避免因时间戳等噪声导致误判。
        /// </summary>
        private static bool AreResultsSubstantiallySame(string prevResult, string currentResult)
        {
            if (string.IsNullOrEmpty(prevResult) && string.IsNullOrEmpty(currentResult))
                return true;
            if (string.IsNullOrEmpty(prevResult) || string.IsNullOrEmpty(currentResult))
                return false;

            // ── 快速路径：完全一致 ──
            string a = prevResult.Trim();
            string b = currentResult.Trim();
            if (a == b) return true;

            // ── 提取错误行（以 "error " 或 "错误" 开头的行）进行比较 ──
            var prevErrors = ExtractErrorLines(a);
            var currErrors = ExtractErrorLines(b);

            if (prevErrors.Count == 0 && currErrors.Count == 0)
            {
                // 无错误行时，比较长度相似度（容忍 10% 差异）
                int lenDiff = Math.Abs(a.Length - b.Length);
                int maxLen = Math.Max(a.Length, b.Length);
                return maxLen > 0 && (double)lenDiff / maxLen < 0.1;
            }

            if (prevErrors.Count != currErrors.Count)
                return false;

            // 逐行比较错误内容
            for (int i = 0; i < prevErrors.Count; i++)
            {
                if (!string.Equals(prevErrors[i], currErrors[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 从工具结果中提取核心错误行（忽略时间戳、构建配置等可变前缀）。
        /// </summary>
        private static List<string> ExtractErrorLines(string result)
        {
            var errors = new List<string>();
            foreach (var line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                // 匹配典型的编译错误行格式: "error CS1234:" 或 "错误 CS1234:" 或 "error :" 等
                if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("0 Error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("0 错误", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("0 Error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("0 错误", StringComparison.OrdinalIgnoreCase))
                {
                    // 去掉行号前缀（如 "  (123,45): " 格式），保留核心错误描述
                    errors.Add(trimmed);
                }
            }
            return errors;
        }

        #endregion
    }
}
