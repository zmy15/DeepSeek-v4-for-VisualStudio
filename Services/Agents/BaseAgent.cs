using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using System;
using System.Collections.Concurrent;
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

        // ════════════════════════════════════════════════════════════════
        // MCP 工具自动分类：根据工具名前缀/关键词判断读写属性，
        // 使 MCP 导入的外部工具能自动匹配到合适的 Agent。
        // ════════════════════════════════════════════════════════════════

        /// <summary>已知内置工具名（MCP 同名工具会覆盖内置，不同名工具按分类注入）</summary>
        protected static readonly HashSet<string> KnownBuiltInToolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "list_dir", "read_file", "file_search", "grep_search", "symbol_search", "get_errors",
            "fetch_webpage", "build_solution", "replace_string_in_file", "multi_replace_string_in_file",
            "create_file", "delete_file", "apply_patch", "create_directory", "run_in_terminal",
            "get_terminal_output", "VisualStudio_askQuestions", "runSubagent", "request_handoff",
            "git", "memory", "search", "get_changed_files", "github_repo", "manage_todo_list",
            "create_and_run_task", "edit_notebook_file"
        };

        /// <summary>读类 MCP 工具名前缀/关键词（分配给 ExploreAgent）</summary>
        private static readonly string[] ReadMcpPatterns =
        {
            "get_", "list_", "read_", "search_", "find_", "query_", "fetch_", "describe_",
            "browse_", "view_", "show_", "check_", "lookup_", "scan_", "export_", "download_",
            "pull_", "status_", "log_", "diff_", "print_", "display_"
        };

        /// <summary>单词语义读类工具名（不含下划线的完整匹配）</summary>
        private static readonly HashSet<string> ReadSingleWordNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ocr", "recognize", "parse", "analyze", "validate", "resolve",
            "inspect", "identify", "detect", "ping", "whoami"
        };

        /// <summary>写类 MCP 工具名前缀/关键词（分配给 EditAgent / BuildAgent）</summary>
        private static readonly string[] WriteMcpPatterns =
        {
            "create_", "delete_", "update_", "write_", "run_", "execute_", "build_", "deploy_",
            "push_", "commit_", "apply_", "set_", "add_", "remove_", "edit_", "modify_",
            "install_", "publish_", "start_", "stop_", "restart_", "config_", "patch_",
            "merge_", "upload_", "save_", "insert_", "replace_"
        };

        /// <summary>
        /// 对 MCP 工具进行分类并返回应自动注入到指定 Agent 白名单的工具名。
        /// 仅返回不与内置工具重名的 MCP 工具。
        /// </summary>
        /// <param name="mcpManager">MCP 管理器（null 时返回空列表）</param>
        /// <param name="agentType">目标 Agent 类型</param>
        /// <returns>应自动注入的 MCP 工具名列表</returns>
        protected static List<string> GetAutoMcpToolNames(McpManagerService? mcpManager, AgentType agentType)
        {
            var result = new List<string>();
            if (mcpManager == null || mcpManager.AllTools.Count == 0)
                return result;

            bool wantRead = agentType == AgentType.Explore;
            bool wantWrite = agentType == AgentType.Edit || agentType == AgentType.Build;

            if (!wantRead && !wantWrite)
                return result; // Ask / Plan 不自动注入 MCP 工具

            foreach (var tool in mcpManager.AllTools)
            {
                // 跳过与内置工具同名的（已由 BuiltInToolService 统一管理，MCP 同名会覆盖内置）
                if (KnownBuiltInToolNames.Contains(tool.Name))
                    continue;

                bool isRead = IsReadMcpTool(tool);
                bool isWrite = IsWriteMcpTool(tool);

                if (wantRead && isRead)
                    result.Add(tool.Name);
                else if (wantWrite && isWrite)
                    result.Add(tool.Name);
                else if (wantRead && !isWrite)
                    // 只读 Agent：无法判断时默认归为只读（安全）
                    result.Add(tool.Name);
                // 写 Agent 仅在明确匹配写模式时才加入，无法判断时不加入（安全优先）
                // 如果 isRead && isWrite 同时为 true → 两个 Agent 都加上
            }

            return result;
        }

        /// <summary>
        /// 构建完整工具集（不过滤白名单），用于 DeepSeek Prefix Cache 稳定。
        /// 所有 API 调用统一发送此完整工具集，保持 tools JSON 不变。
        /// 工具调用由客户端按 Agent 白名单拦截。
        /// </summary>
        protected List<ToolDefinition> BuildFullToolSet()
        {
            var fullSet = new List<ToolDefinition>();
            if (BuiltInTools != null)
            {
                fullSet.AddRange(BuiltInTools.GetFullToolDefinitions());
            }
            else if (McpManager != null && McpManager.AllTools.Count > 0)
            {
                fullSet.AddRange(McpManager.GetToolDefinitions());
            }
            return fullSet;
        }

        /// <summary>
        /// 获取当前会话的完整工具集（缓存，避免重复构建）。
        /// 在 MCP 工具变更时调用 <see cref="InvalidateFullToolSetCache"/> 刷新。
        /// </summary>
        private List<ToolDefinition>? _cachedFullToolSet;

        /// <summary>
        /// 获取或构建缓存的完整工具集。
        /// </summary>
        protected List<ToolDefinition> GetFullToolSet()
        {
            if (_cachedFullToolSet == null)
            {
                _cachedFullToolSet = BuildFullToolSet();
            }
            return _cachedFullToolSet;
        }

        /// <summary>
        /// 使缓存的完整工具集失效（MCP 工具变更时调用）。
        /// </summary>
        public void InvalidateFullToolSetCache()
        {
            _cachedFullToolSet = null;
            Logger.Info($"[Agent:{Definition.Name}] 完整工具集缓存已失效（MCP 工具变更）");
        }

        private static bool IsReadMcpTool(McpTool tool)
        {
            // 前缀匹配
            foreach (var pattern in ReadMcpPatterns)
            {
                if (tool.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // 单词语义匹配（不含下划线的完整工具名）
            if (ReadSingleWordNames.Contains(tool.Name))
                return true;
            // 描述关键词辅助判断
            if (!string.IsNullOrEmpty(tool.Description))
            {
                var desc = tool.Description;
                if (desc.Contains("read", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("get", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("list", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("view", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("retrieve", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("recognize", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("ocr", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("scan", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("extract", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("analyze", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("detect", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("identify", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("inspect", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("validate", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("lookup", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsWriteMcpTool(McpTool tool)
        {
            foreach (var pattern in WriteMcpPatterns)
            {
                if (tool.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // 描述关键词辅助判断
            if (!string.IsNullOrEmpty(tool.Description))
            {
                var desc = tool.Description;
                if (desc.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("run", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("execute", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("modify", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("change", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("install", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("deploy", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("push", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

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
        /// 所有 Agent 共享的不可变前缀。始终放在 messages[0]，确保跨 Agent 切换时
        /// DeepSeek Prefix Cache 永远命中。Agent 专属行为指令不在此前缀中，
        /// 而是作为最后一条 system 消息注入（位于历史之后、用户消息之前）。
        /// 
        /// 内容 = CommonSystemPromptPrefixCore（角色定义 + 文件规则 + 终端规则 +
        /// Handoff 规则）+ 语言指令。
        /// 
        /// 委托给 AiPrompts.SharedImmutablePrefix，确保与 ConversationContextManager
        /// 的 BuildApiMessages 使用完全相同的 messages[0] 内容。
        /// </summary>
        protected static string GetSharedImmutablePrefix()
        {
            return AiPrompts.SharedImmutablePrefix;
        }

        /// <summary>
        /// 兼容旧代码：首次访问时返回当前语言的前缀。
        /// </summary>
        protected static string CommonSystemPromptPrefix => GetCommonSystemPromptPrefix();

        /// <summary>Agent 元数据定义</summary>
        public AgentDefinition Definition { get; protected set; }

        /// <summary>当前执行上下文</summary>
        public AgentContext? Context { get; set; }

        /// <summary>内置工具服务引用（由 AgentFactory 注入）</summary>
        public BuiltInToolService? BuiltInTools { get; set; }

        /// <summary>MCP 管理器引用（由 AgentFactory 注入，用于执行 MCP 工具）</summary>
        public McpManagerService? McpManager { get; set; }

        /// <summary>记忆服务引用（由 AgentFactory 注入，用于程序化读写持久化记忆）</summary>
        public IMemoryService? MemoryService { get; set; }

        /// <summary>ExploreAgent 引用（由 AgentFactory 注入，用于 runSubagent 委派探索任务）</summary>
        public ExploreAgent? ExploreAgent { get; set; }

        /// <summary>日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        /// <summary>权限请求事件</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

        /// <summary>向用户提问事件（VisualStudio_askQuestions 工具使用）</summary>
        public event Action<AgentQuestionRequest>? QuestionsRequested;

        /// <summary>
        /// QuestionsRequested 事件的当前订阅者数量（诊断用）。
        /// 供外部代码（如 DeepSeekChatControl）在绑定/解绑后验证订阅状态。
        /// </summary>
        public int QuestionsRequestedHandlerCount => QuestionsRequested?.GetInvocationList().Length ?? 0;

        /// <summary>文件变更实时通知事件（编辑阶段逐文件推送）</summary>
        public event Action<AgentFileChangeEventArgs>? FileChangeNotified;

        /// <summary>
        /// 当前待确认的权限请求字典（RequestId → AgentPermissionRequest）。
        /// 支持多个并发权限请求（工具调用并行执行时每个工具可能独立请求权限）。
        /// 请使用 TryGetPendingPermission(requestId) 或 TryRemovePendingPermission(requestId) 按 RequestId 精确查找。
        /// </summary>
        private readonly ConcurrentDictionary<string, AgentPermissionRequest> _pendingPermissions = new();

        /// <summary>
        /// 当前待回答的提问请求字典（RequestId → AgentQuestionRequest）。
        /// 支持多个并发提问请求。
        /// 请使用 TryGetPendingQuestion(requestId) 按 RequestId 精确查找。
        /// </summary>
        private readonly ConcurrentDictionary<string, AgentQuestionRequest> _pendingQuestions = new();

        /// <summary>AI 通过 request_handoff 工具发起的待处理移交请求</summary>
        public HandoffRequest? PendingHandoffRequest { get; protected set; }

        /// <summary>
        /// 并行子代理执行信号量 — 限制同时运行的 Explore 子代理数量（默认 3）。
        /// 避免过多子代理同时读取文件导致 token 浪费和缓存冲突。
        /// </summary>
        private static readonly SemaphoreSlim SubagentConcurrencyLimiter = new(3, 3);

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
            // 🔑 传入完整工具集以保持 Prefix Cache 稳定
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, TryGetFullToolSet(), ct, maxTokens, toolChoice: "none"))
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
        protected async Task<string> CallAiLongAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 4096, double? temperature = null, string? responseFormat = null)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt);

            var sb = new StringBuilder();
            // 🔑 传入完整工具集 + toolChoice:"none" 以保持 Prefix Cache 稳定
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, TryGetFullToolSet(), ct, maxTokens, temperature: temperature, responseFormat: responseFormat, toolChoice: "none"))
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
            double? temperature = null,
            string? responseFormat = null)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, extraSystemMessages);

            var sb = new StringBuilder();
            // 🔑 传入完整工具集以保持 Prefix Cache 稳定
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, TryGetFullToolSet(), ct, maxTokens, toolChoice, temperature, responseFormat))
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
        protected async Task<string> CallAiWithHistoryAsync(List<ChatApiMessage> history, CancellationToken ct, int maxTokens = 4096, string? responseFormat = null)
        {
            var sb = new StringBuilder();
            // 🔑 传入完整工具集 + toolChoice:"none" 以保持 Prefix Cache 稳定
            await foreach (var chunk in _apiService.ChatStreamAsync(history, TryGetFullToolSet(), ct, maxTokens, responseFormat: responseFormat, toolChoice: "none"))
            {
                if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 使用预构建消息列表调用 AI（支持 toolChoice 和 temperature 参数）。
        /// 
        /// 🔑 缓存关键：与 CallAiLongAsync 不同，此方法直接使用传入的 messages，
        /// 不通过 BuildContextAwareMessages 重建。这使得跨阶段的对话延续成为可能——
        /// 对齐阶段的 tool call 历史可以直接传递给设计阶段，DeepSeek Prefix Cache
        /// 可以匹配整个对齐对话前缀，而非仅匹配 system prompt。
        /// </summary>
        /// <param name="messages">预构建的完整消息列表（含 system + 历史 + user）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxTokens">最大输出 token 数</param>
        /// <param name="toolChoice">工具调用策略（"none" 禁用工具）</param>
        /// <param name="temperature">采样温度（0.0 = 确定性输出）</param>
        /// <param name="responseFormat">JSON Output 模式: "json_object" 启用，null 不启用</param>
        /// <summary>
        /// 使用预构建消息列表调用 AI（支持 toolChoice 和 temperature 参数）。
        /// 🔑 始终传入完整工具集 + toolChoice="none" 以保持 Prefix Cache 稳定。
        /// </summary>
        protected async Task<string> CallAiWithMessagesAsync(
            List<ChatApiMessage> messages,
            CancellationToken ct,
            int maxTokens = 4096,
            string? toolChoice = null,
            double? temperature = null,
            string? responseFormat = null)
        {
            // 🔑 传入完整工具集以保持 Prefix Cache 稳定
            var fullTools = TryGetFullToolSet();
            var sb = new StringBuilder();
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, fullTools, ct, maxTokens, toolChoice, temperature, responseFormat))
            {
                if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            LogCacheHitRate();
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 安全获取完整工具集（Agent 未初始化时返回 null 降级）。
        /// </summary>
        private List<ToolDefinition>? TryGetFullToolSet()
        {
            try
            {
                if (BuiltInTools != null || (McpManager != null && McpManager.AllTools.Count > 0))
                    return GetFullToolSet();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 使用 ConversationContextManager 构建的消息列表调用 AI。
        /// 正确处理 reasoning_content 回传规则。
        /// </summary>
        protected async Task<string> CallAiWithContextAsync(ConversationContextManager ctxManager, CancellationToken ct, int maxTokens = 4096, string? responseFormat = null)
        {
            var messages = ctxManager.BuildApiMessages();
            var sb = new StringBuilder();
            // 🔑 传入完整工具集 + toolChoice:"none" 以保持 Prefix Cache 稳定
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, TryGetFullToolSet(), ct, maxTokens, responseFormat: responseFormat, toolChoice: "none"))
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
        /// ── 消息结构（v1.1.11）──
        ///   messages[0] = SharedImmutablePrefix（跨 Agent 永远不变）
        ///   messages[1] = fixedPrompt（会话级上下文，session 内不变）
        ///   messages[2] = dynamicBlock（压缩摘要 + 搜索 + RAG + 记忆）
        ///   messages[3..N] = 对话历史（来自 ContextManager，起始位置固定）
        ///   messages[N+1] = Agent 专属行为指令（末尾，每次注入位置随历史长度变化）
        ///   messages[N+2] = 当前用户消息
        /// 
        /// 设计决策：Agent 提示词放在历史之后、用户消息之前。虽然位置随历史增长
        /// 而漂移（自身每次 miss），但历史 [3..] 起始位置固定，重叠部分始终命中。
        /// Handoff 时 ForwardedMessages 原封不动复用整个前缀，新 Agent 仅追加。
        /// </summary>
        /// <param name="systemPrompt">Agent 专属行为指令（不含共享前缀）</param>
        /// <param name="userPrompt">当前的用户消息/步骤描述</param>
        /// <param name="maxRecentTurns">注入对话历史的最近轮次数（0 = 不注入，默认不限制）</param>
        /// <returns>按缓存优化顺序排列的消息列表</returns>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt, string userPrompt, int maxRecentTurns = int.MaxValue)
        {
            // ── 🔑 Handoff 消息复用（v1.1.10）：若源 Agent 传递了工具循环消息列表，
            //    直接复用作为前缀，不再从 ContextManager 重建。
            //    这确保 Handoff 前后消息结构完全一致，DeepSeek Prefix Cache 可直接命中。
            List<ChatApiMessage>? forwarded = Context?.ForwardedMessages;
            if (forwarded != null && forwarded.Count > 0)
            {
                Context!.ForwardedMessages = null; // 消费后清空，防止下次误用
                var result = new List<ChatApiMessage>(forwarded);
                // ── 追加目标 Agent 专属指令和用户消息 ──
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    result.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
                result.Add(new ChatApiMessage { Role = "user", Content = userPrompt });
                return result;
            }

            var messages = new List<ChatApiMessage>();

            // ── 第1层：共享不可变前缀（永远放在 messages[0]，跨 Agent 完全相同）──
            messages.Add(new ChatApiMessage { Role = "system", Content = GetSharedImmutablePrefix() });

            // ── 第2-3层：fixedPrompt + dynamicBlock（来自 ContextManager，固定位置 [1][2]）──
            // BuildApiMessagesRecentTurns 返回结构：[0]=SP, [1]=FP, [2]=DB, [3..]=entries
            // 跳过 [0]=SP（已在上面添加），保留 [1]=FP、[2]=DB 和 [3..]=entries。
            var ctxManager = Context?.ContextManager;
            if (ctxManager != null && !ctxManager.IsEmpty && maxRecentTurns > 0)
            {
                var recentMessages = ctxManager.BuildApiMessagesRecentTurns(maxRecentTurns);
                if (recentMessages.Count > 0)
                {
                    // 跳过 SP（index 0），添加 FP、DB 和所有 entries
                    for (int i = 1; i < recentMessages.Count; i++)
                    {
                        messages.Add(recentMessages[i]);
                    }
                }
            }

            // ── 第4层：Agent 专属行为指令（历史之后、用户消息之前）──
            //     位置随历史长度变化，自身每次 miss，但历史 [3..] 起始位置固定，
            //     跨轮次重叠部分始终命中。Handoff 时 ForwardedMessages 走独立路径。
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });

            // ── 第5层：当前用户消息（变化最大，放在最后）──
            messages.Add(new ChatApiMessage { Role = "user", Content = userPrompt });

            return messages;
        }

        /// <summary>
        /// 构建上下文感知的消息列表（支持注入额外的系统级上下文）。
        /// 
        /// extraSystemMessages 插入在用户消息之前（Agent 提示词之后）。
        /// 前缀结构 [0..2] 与主重载一致，确保 DeepSeek Prefix Cache 跨路径共享。
        /// </summary>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt,
            string userPrompt,
            List<ChatApiMessage> extraSystemMessages,
            int maxRecentTurns = int.MaxValue)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, maxRecentTurns);

            // ── 在用户消息之前注入额外的 system 消息 ──
            // 用户消息始终是最后一条，insertPos = Count-1 即插入在用户消息之前
            int insertPos = messages.Count - 1; // 用户消息之前
            if (extraSystemMessages != null && extraSystemMessages.Count > 0)
            {
                messages.InsertRange(insertPos, extraSystemMessages);
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

            int round = BuiltInTools?.CurrentRound ?? 0;
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
                // 🔑 Prefix Cache 优化：始终发送完整工具集（所有内置 + MCP 工具），
                //    保持 tools JSON 跨 Agent/阶段不变，最大化 DeepSeek Prefix Cache 命中率。
                //    工具调用由客户端按白名单拦截（见下方拦截逻辑）。
                List<ToolDefinition>? toolDefs = null;
                List<string>? effectiveWhitelist = null;
                if (BuiltInTools != null || McpManager != null)
                {
                    // ── 始终发送完整工具集（不对 tools JSON 做白名单过滤）──
                    toolDefs = GetFullToolSet();

                    // ── 构建白名单（仅用于客户端拦截，不影响 tools JSON）──
                    effectiveWhitelist = toolWhitelist ?? Definition.AllowedTools;

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

                    // ── 自动注入未重叠的 MCP 工具（按 Agent 类型分类）──
                    if (McpManager != null && McpManager.AllTools.Count > 0 && effectiveWhitelist != null)
                    {
                        var autoMcpNames = GetAutoMcpToolNames(McpManager, Definition.Type);
                        if (autoMcpNames.Count > 0)
                        {
                            effectiveWhitelist = new List<string>(effectiveWhitelist);
                            effectiveWhitelist.AddRange(autoMcpNames);
                        }
                    }

                    Logger.Info($"[Agent:{Definition.Name}] 本轮携带 {toolDefs.Count} 个工具定义(完整集)" +
                        (toolWhitelist != null ? $", 白名单={effectiveWhitelist?.Count ?? 0}个" : ""));
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
                    catch (ObjectDisposedException) when (!ct.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                    {
                        // SSE 读取超时（v1.1.10）：120s 无数据 → 流被 Dispose → ReadLineAsync 抛 ObjectDisposedException
                        streamAttempt++;
                        savedPartialContent = contentBuilder.ToString();
                        savedPartialReasoning = reasoningBuilder.ToString();
                        double backoffSec = Math.Pow(2, streamAttempt);
                        Logger.Warn($"[Agent:{Definition.Name}] SSE 流读取超时 (尝试 {streamAttempt}/{maxStreamAttempts})，已收到 {savedPartialContent.Length} 字符，{backoffSec}s 后恢复…");
                        contentBuilder.Clear();
                        reasoningBuilder.Clear();
                        toolCallAccumulator.Clear();
                        await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
                    }
                    catch (IOException) when (ct.IsCancellationRequested)
                    {
                        // 用户取消导致的 IO 异常，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消令牌中断 (IO)");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append("\n\n> ⏏️ 操作已被取消。");
                        streamSuccess = true;
                    }
                    catch (IOException) when (!ct.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                    {
                        // 网络错误导致的 IO 异常（非用户取消），尝试重试
                        streamAttempt++;
                        savedPartialContent = contentBuilder.ToString();
                        savedPartialReasoning = reasoningBuilder.ToString();
                        double backoffSec = Math.Pow(2, streamAttempt);
                        Logger.Warn($"[Agent:{Definition.Name}] 流网络错误 (尝试 {streamAttempt}/{maxStreamAttempts})，已收到 {savedPartialContent.Length} 字符，{backoffSec}s 后恢复…");
                        contentBuilder.Clear();
                        reasoningBuilder.Clear();
                        toolCallAccumulator.Clear();
                        await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
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

                // ── 🔑 客户端白名单拦截：标记不在 effectiveWhitelist 中的工具调用 ──
                //    因为 API 请求始终发送完整工具集（Prefix Cache 优化），
                //    AI 可能调用不在当前 Agent/阶段白名单中的工具。
                //    标记后统一在执行阶段返回拒绝消息，让 AI 重试正确工具。
                HashSet<int>? blockedToolIndices = null;
                if (toolCalls.Count > 0 && effectiveWhitelist != null && effectiveWhitelist.Count > 0)
                {
                    var whitelistSet = new HashSet<string>(effectiveWhitelist, StringComparer.OrdinalIgnoreCase);
                    blockedToolIndices = new HashSet<int>();
                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        if (!whitelistSet.Contains(toolCalls[i].Function.Name))
                        {
                            blockedToolIndices.Add(i);
                        }
                    }
                    if (blockedToolIndices.Count > 0)
                    {
                        var blockedNames = string.Join(", ", blockedToolIndices.Select(i => toolCalls[i].Function.Name));
                        Logger.Warn($"[Agent:{Definition.Name}] 🚫 白名单拦截 {blockedToolIndices.Count} 个工具: {blockedNames}（白名单: {string.Join(", ", effectiveWhitelist)}）");
                    }
                }

                if (toolCalls.Count == 0) break;

                if (toolCalls.Count > 0)
                {
                    Logger.Info($"[Agent:{Definition.Name}] 检测到 {toolCalls.Count} 个工具调用: {string.Join(", ", toolCalls.Select(t => t.Function.Name))}");

                    // ── 去重：检测同一批次中完全相同的工具调用（同函数名+同参数），避免重复执行 ──
                    // 例如 AI 可能误调用 3 次相同的 runSubagent，去重后只执行 1 次，结果复用
                    var (dedupedIndices, dedupMapping) = DeduplicateToolCalls(toolCalls);
                    bool hasDedup = dedupedIndices.Count < toolCalls.Count;
                    if (hasDedup)
                    {
                        int skipped = toolCalls.Count - dedupedIndices.Count;
                        Logger.Info($"[Agent:{Definition.Name}] 🔄 去重: 跳过 {skipped} 个重复工具调用（{toolCalls.Count} → {dedupedIndices.Count} 个唯一调用）");
                    }

                    // ── 通知工具调用（含详细信息，每轮仅一次，去重后只通知唯一调用）──
                    foreach (var idx in dedupedIndices)
                    {
                        var tc = toolCalls[idx];
                        string summary = BuiltInToolService.GetToolCallDisplayText(tc.Function.Name, tc.Function.Arguments);
                        // MCP 工具标注：即使与内置工具同名，也标注来源
                        if (McpManager != null && McpManager.AllTools.Any(t => string.Equals(t.Name, tc.Function.Name, StringComparison.OrdinalIgnoreCase)))
                            summary = summary.Replace("🔧", "🔌 MCP");
                        onToolCall?.Invoke(summary);
                    }

                    // ── 收集本轮工具调用摘要到历史（去重后，用于循环终止时的上下文总结）──
                    foreach (var idx in dedupedIndices)
                    {
                        var tc = toolCalls[idx];
                        string summary = BuiltInToolService.GetToolCallDisplayText(tc.Function.Name, tc.Function.Arguments);
                        if (McpManager != null && McpManager.AllTools.Any(t => string.Equals(t.Name, tc.Function.Name, StringComparison.OrdinalIgnoreCase)))
                            summary = summary.Replace("🔧", "🔌 MCP");
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

                    // ── 同步 assistant 消息（含 tool_calls）到全局 ContextManager，确保重启后可恢复完整对话 ──
                    if (Context?.ContextManager != null)
                    {
                        Context.ContextManager.AddAssistantMessage(
                            contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                            reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                            toolCalls);
                    }

                    // ── 🔑 子Agent缓存优化：在执行工具前保存当前消息列表，
                    //     供runSubagent(ExploreAgent)复用父Agent的缓存前缀。──
                    if (Context != null && toolCalls.Any(tc => tc.Function.Name == "runSubagent"))
                    {
                        Context.ForwardedMessages = new List<ChatApiMessage>(messages);
                    }

                    // ── 并行执行工具调用（带超时保护，长时工具使用更长超时，已去重）──
                    //    被白名单拦截的工具跳过执行，直接返回拒绝消息。
                    var toolTasks = dedupedIndices.Select(idx =>
                    {
                        var tc = toolCalls[idx];
                        // ── 白名单拦截：不在白名单中的工具不执行，返回拒绝消息 ──
                        if (blockedToolIndices != null && blockedToolIndices.Contains(idx))
                        {
                            string allowedList = effectiveWhitelist != null
                                ? string.Join(", ", effectiveWhitelist)
                                : "无";
                            return Task.FromResult(
                                $"🚫 工具 '{tc.Function.Name}' 在当前 Agent/阶段不可用。\n" +
                                $"原因：该工具不在当前白名单中。\n" +
                                $"当前可用工具: {allowedList}\n" +
                                $"请使用可用工具重试，或考虑将任务移交给合适的 Agent。");
                        }
                        var timeout = GetToolTimeout(tc.Function.Name);
                        return ExecuteToolWithTimeoutAsync(tc, workspaceRoot, ct, timeout);
                    }).ToList();
                    var dedupedResults = await Task.WhenAll(toolTasks).ConfigureAwait(false);

                    // ── 将去重后的结果映射回原始 toolCalls 数组 ──
                    var toolResults = new string[toolCalls.Count];
                    for (int i = 0; i < dedupedIndices.Count; i++)
                    {
                        int originalIdx = dedupedIndices[i];
                        toolResults[originalIdx] = dedupedResults[i];

                        // ── 活跃文件追踪：记录工具访问的文件 ──
                        TryTrackActiveFileAccess(toolCalls[originalIdx], workspaceRoot);
                    }
                    // 将去重结果复制到所有重复调用
                    foreach (var mapping in dedupMapping)
                    {
                        int sourceIdx = mapping.Key;       // 实际执行的索引
                        foreach (int dupIdx in mapping.Value) // 重复调用的索引列表
                        {
                            toolResults[dupIdx] = dedupedResults[dedupedIndices.IndexOf(sourceIdx)];
                        }
                    }

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        var tc = toolCalls[i];
                        string toolResult = toolResults[i];

                        // ── 裁剪工具结果以保护上下文（与 ContextManager.AddToolResult 保持一致）──
                        string contextResult = CompactToolResultForAgent(tc.Function.Name, toolResult);

                        messages.Add(new ChatApiMessage
                        {
                            Role = "tool",
                            Content = contextResult,
                            ToolCallId = tc.Id,
                            Name = tc.Function.Name
                        });

                        // ── 同步 tool 结果到全局 ContextManager，确保重启后可恢复完整对话 ──
                        if (Context?.ContextManager != null)
                        {
                            Context.ContextManager.AddToolResult(tc.Id, tc.Function.Name, contextResult);
                        }

                        Logger.Info($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");
                    }

                    // ── 移交检测：如果 AI 调用了 request_handoff，立即终止循环 ──
                    if (PendingHandoffRequest != null)
                    {
                        Logger.Info($"[Agent:{Definition.Name}] 🔄 检测到移交请求 → {PendingHandoffRequest.TargetAgent}，终止工具循环");
                        // ── 🔑 保存工具循环消息列表，供 Handoff 目标 Agent 复用前缀 ──
                        //     浅克隆即可，不调用 CleanIncompleteToolChains：
                        //     - ChatStreamAsync Rule 5 在每次 API 调用时已统一处理孤儿 assistant
                        //     - CleanIncompleteToolChains 会修改消息内容（剥离 tool_calls），
                        //       导致 ForwardedMessages 与 DeepSeek 服务端缓存不一致 → 前缀断裂
                        //     - Rule 5 是确定性的：相同输入前缀 → 相同输出，不会产生缓存断裂
                        if (Context != null)
                        {
                            Context.ForwardedMessages = new List<ChatApiMessage>(messages);
                        }
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
        /// 记录本次工作流的 Cache 命中率汇总到日志（与 UI 显示一致）。
        /// 在所有轮次结束后调用，输出本次工作流（自上次 TakeCacheSnapshot 以来）的增量总计。
        /// </summary>
        private void LogTotalCacheHitRate(int finalRound)
        {
            try
            {
                var delta = _apiService?.GetCacheDelta() ?? (0, 0, 0, 0);
                long totalHit = delta.Hit;
                long totalMiss = delta.Miss;
                long totalCacheable = totalHit + totalMiss;
                if (totalCacheable == 0) return;

                double aggregateRate = (double)totalHit / totalCacheable;
                string level = aggregateRate >= 0.95 ? "🟢" : aggregateRate >= 0.70 ? "🟡" : aggregateRate >= 0.30 ? "🟠" : "🔴";

                Logger.Info($"[Cache] ═══════════════════════════════════════");
                Logger.Info($"[Cache] {level} 本次工作流汇总 ({finalRound} 轮)");
                Logger.Info($"[Cache]   Cache 命中率: {aggregateRate * 100:F1}%");
                Logger.Info($"[Cache]   命中: {totalHit:N0} tokens");
                Logger.Info($"[Cache]   未命中: {totalMiss:N0} tokens");
                Logger.Info($"[Cache]   Prompt: {delta.Prompt:N0} tokens");
                Logger.Info($"[Cache]   Completion: {delta.Completion:N0} tokens");
                Logger.Info($"[Cache] ═══════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录汇总命中率异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成本次工作流 Cache 命中率摘要文本（用于附加到 AI 响应末尾，与 UI 一致）。
        /// </summary>
        private string GetTotalCacheHitSummary(int finalRound)
        {
            try
            {
                var delta = _apiService?.GetCacheDelta() ?? (0, 0, 0, 0);
                long totalHit = delta.Hit;
                long totalMiss = delta.Miss;
                long totalCacheable = totalHit + totalMiss;
                if (totalCacheable == 0) return string.Empty;

                double rate = (double)totalHit / totalCacheable;
                string icon = rate >= 0.95 ? "🟢" : rate >= 0.70 ? "🟡" : rate >= 0.30 ? "🟠" : "🔴";

                return $"\n\n---\n\n{icon} **Cache 命中率: {rate * 100:F1}%**" +
                    $" · {totalHit:N0} 命中 / {totalMiss:N0} 未命中" +
                    $" · Prompt {delta.Prompt:N0} · Completion {delta.Completion:N0}" +
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
            // ── OCR 参数预处理：将文件路径自动转为 base64 ──
            argumentsJson = DeepSeekChatControl.SanitizeOcrToolArguments(toolName, argumentsJson);

            // ── 注入 ExploreHandler 到 BuiltInToolService（桥接 Agent.ExploreAgent）──
            // 🔑 修复：确保即使 BaseAgent.ExploreAgent 为 null 也能找到 ExploreAgent。
            // 场景：AskAgent 的 ExploreAgent 可能由 AgentFactory 通过不同类型引用设置。
            var effectiveExploreAgent = ExploreAgent
                ?? (this as AskAgent)?.ExploreAgent
                ?? (this as EditAgent)?.ExploreAgent
                ?? (this as PlanAgent)?.ExploreAgent
                ?? (this as BuildAgent)?.ExploreAgent;

            if (BuiltInTools != null && effectiveExploreAgent != null)
            {
                if (ExploreAgent == null)
                {
                    // 回退：将派生类的 ExploreAgent 同步到基类属性
                    ExploreAgent = effectiveExploreAgent;
                    Logger.Info($"[Agent:{Definition.Name}] 从派生类回退同步 ExploreAgent 到基类");
                }
                BuiltInTools.ExploreHandler = async (ctx) =>
                {
                    try
                    {
                        // ── 🔑 子Agent缓存优化：消费父Agent保存的ForwardedMessages，
                        //     使ExploreAgent复用父Agent的API缓存前缀。──
                        ctx.ForwardedMessages = Context?.ForwardedMessages;
                        if (Context != null)
                            Context.ForwardedMessages = null; // 消费后清空

                        // ── 同步上下文到 ExploreAgent ──
                        ExploreAgent.Context = this.Context;
                        if (ExploreAgent.BuiltInTools == null)
                            ExploreAgent.BuiltInTools = this.BuiltInTools;
                        if (ExploreAgent.McpManager == null)
                            ExploreAgent.McpManager = this.McpManager;

                        // ── 构建 ExploreAgent 上下文 ──
                        // 为 ExploreAgent 创建 FileReadCache 副本，避免共享同一实例导致
                        // "集合已修改；可能无法执行枚举操作" 异常。
                        var exploreFileCache = Context?.FileReadCache != null
                            ? new Dictionary<string, string>(Context.FileReadCache, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        var exploreCtx = new AgentContext
                        {
                            SolutionPath = ctx.WorkspaceRoot ?? Context?.SolutionPath,
                            CancellationToken = ct,
                            ContextManager = Context?.ContextManager,
                            FileReadCache = exploreFileCache,
                            DiscoveredFiles = Context?.DiscoveredFiles,
                            // 🔑 子Agent缓存优化：继承父Agent当前消息列表作为前缀，
                            //    使ExploreAgent的首轮API调用可复用父Agent的缓存前缀。
                            ForwardedMessages = ctx.ForwardedMessages,
                        };

                        AddLog("INFO", $"[{Definition.Name}] → ExploreAgent: {ctx.Description}");

                        // ── 并行子代理限流：获取信号量，限制同时运行的 Explore 子代理数量 ──
                        await SubagentConcurrencyLimiter.WaitAsync(ct);

                        // ── 获取信号量后再次检查取消（防止在排队等待期间用户点了停止）──
                        ct.ThrowIfCancellationRequested();

                        // ── 不再通过 LogEntryAdded 事件转发 Explore 日志（并行 runSubagent 会导致重复订阅）
                        //    改为在 Explore 完成后从 exploreResult.Logs 批量导入。 ──
                        AgentResult? exploreResult = null;
                        try
                        {
                            exploreResult = await ExploreAgent.ExecuteAsync(ctx.Prompt, exploreCtx);
                        }
                        finally
                        {
                            SubagentConcurrencyLimiter.Release();
                            // ── 导入 ExploreAgent 的日志到父 Agent 的 _logs ──
                            if (exploreResult != null && exploreResult.Logs.Count > 0)
                            {
                                foreach (var log in exploreResult.Logs)
                                {
                                    _logs.Add(new AgentLogEntry
                                    {
                                        Level = log.Level,
                                        Message = $"[Explore] {log.Message.Truncate(200)}"
                                    });
                                }
                                // ── 通过 AddLog 触发 LogEntryAdded 事件，使 UI 实时更新执行进度 ──
                                //     修复：之前仅用 _logs.Add() 导入日志，不触发事件，
                                //     导致 @agent 路由时 UI 思考面板不显示 ExploreAgent 执行流程。
                                AddLog("INFO", $"[Explore] 探索完成: {exploreResult.Logs.Count} 条日志, {exploreResult.Content?.Length ?? 0} 字符结果");
                                Logger.Info($"[{Definition.Name}][Explore] ExploreAgent 完成: {exploreResult.Logs.Count} 条日志, {exploreResult.Content?.Length ?? 0} 字符结果");
                            }
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
                            AddLog("INFO", $"[{Definition.Name}] ExploreAgent 完成: {exploreResult.Content!.Length} 字符");
                            return exploreResult.Content;
                        }

                        return exploreResult.Success
                            ? "(ExploreAgent 完成但无内容)"
                            : $"❌ ExploreAgent 失败: {exploreResult.ErrorMessage ?? "未知错误"}";
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Info($"[BaseAgent] runSubagent 被取消 (ExploreAgent: {ctx.Description.Truncate(80)})");
                        throw; // 让取消信号向上传播，停止整个 Agent 流程
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

                    // ── 显式路由拦截：@agent 时 AI 不应主动移交控制权 ──
                    // 例外：PlanAgent → EditAgent 是 Plan 的核心职责，允许
                    if (Context?.IsExplicitRoute == true
                        && !(Definition.Type == AgentType.Plan && request.TargetAgent == AgentType.Edit))
                    {
                        request.Rejected = true;
                        request.RejectReason = $"用户通过 @{Definition.Type.ToString().ToLowerInvariant()} 显式指定了你，请直接处理任务，不要移交控制权。";
                        AddLog("WARN", $"[{Definition.Name}] 🚫 显式路由拦截移交 → {request.TargetAgent}");
                        await Task.CompletedTask;
                        return;
                    }

                    PendingHandoffRequest = request;
                    AddLog("INFO", $"[{Definition.Name}] 🔄 移交请求: → {request.TargetAgent} (原因: {request.Reason})");
                    await Task.CompletedTask;
                };
            }

            // ── Git 工具审批（写操作需审批，只读操作自动放行）──
            if (toolName == "git" && BuiltInTools != null)
            {
                string operation = string.Empty;
                string purpose = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("operation", out var opProp))
                        operation = opProp.GetString()?.ToLowerInvariant() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                        purpose = purProp.GetString() ?? string.Empty;
                }
                catch { }

                // 设置 Agent 类型供 GitTool 运行时校验（ExploreAgent 只能执行只读操作）
                GitTool.CurrentAgentType = Definition.Type;

                // 解析额外参数以细化操作分类
                string branch = string.Empty;
                string stashMode = string.Empty;
                string resetPath = string.Empty;
                bool delete = false;
                try
                {
                    using var doc2 = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc2.RootElement.TryGetProperty("branch", out var bProp))
                        branch = bProp.GetString() ?? string.Empty;
                    if (doc2.RootElement.TryGetProperty("mode", out var mProp))
                        stashMode = mProp.GetString() ?? string.Empty;
                    if (doc2.RootElement.TryGetProperty("path", out var pProp))
                        resetPath = pProp.GetString() ?? string.Empty;
                    if (doc2.RootElement.TryGetProperty("delete", out var dProp) && dProp.ValueKind == System.Text.Json.JsonValueKind.True)
                        delete = true;
                }
                catch { }

                // 细化的只读判断：
                // - status/diff/log 始终只读
                // - branch 无参数（list）→ 只读
                // - stash mode=list → 只读
                // - reset + path（unstage）→ 只读
                bool isReadOnly = operation is "status" or "diff" or "log" or "show"
                    || (operation == "branch" && string.IsNullOrEmpty(branch) && !delete)
                    || (operation == "stash" && string.Equals(stashMode, "list", StringComparison.OrdinalIgnoreCase))
                    || (operation == "reset" && !string.IsNullOrEmpty(resetPath));

                if (!isReadOnly && !string.IsNullOrWhiteSpace(operation))
                {
                    string gitOpDesc = operation switch
                    {
                        "add" => "暂存文件",
                        "commit" => "提交更改",
                        "branch" => "分支操作",
                        "checkout" => "切换分支",
                        "pull" => "拉取远程更改",
                        "push" => "推送到远程",
                        "stash" => "暂存工作区",
                        "reset" => "回退更改",
                        _ => $"git {operation}"
                    };

                    string approvalCmd = $"git {operation}";
                    string approvalTitle = operation == "push"
                        ? $"⚠️ 确认 git push 操作"
                        : $"确认 git {operation}: {gitOpDesc}";

                    bool approved = await RequestPermissionAsync(
                        approvalTitle,
                        approvalCmd,
                        "git_operation",
                        purpose,
                        string.IsNullOrEmpty(purpose) ? $"AI 请求执行 git {operation} 操作" : purpose,
                        ct);

                    if (!approved)
                    {
                        AddLog("WARN", $"用户拒绝了 git {operation} 操作");
                        return $"⏭️ 用户跳过了 git 操作: git {operation}";
                    }
                    AddLog("INFO", $"用户批准了 git {operation} 操作");
                }
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
                    bool approved = await RequestTerminalApprovalAsync(command, explanation, purpose, ct);
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
                    bool approved = await RequestFileDeleteConfirmationAsync(paths, explanation, purpose, ct);
                    if (!approved)
                        return $"⏭️ 用户取消了文件删除: {System.IO.Path.GetFileName(filePath)}";
                }
            }

            // ── VisualStudio_askQuestions / askQuestions：向用户提问并等待回答 ──
            if (toolName == "VisualStudio_askQuestions" || toolName == "askQuestions")
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
                    return await RequestAskQuestionsAsync(questionsJson, ct);
                }
                return LocalizationService.Instance["service.baseAgent.missingQuestions"];
            }

            // ── 越权文件访问检查：AI 访问项目外路径需要用户审批 ──
            if (BuiltInTools != null && IsFileAccessingTool(toolName))
            {
                string? targetPath = ExtractAnyPathFromToolArgs(toolName, argumentsJson);
                if (!string.IsNullOrWhiteSpace(targetPath)
                    && !string.IsNullOrWhiteSpace(workspaceRoot)
                    && IsPathOutsideWorkspace(targetPath!, workspaceRoot!))
                {
                    string operation = GetToolOperationName(toolName);
                    string fileName = System.IO.Path.GetFileName(targetPath!.TrimEnd('/', '\\'));
                    string displayName = string.IsNullOrEmpty(fileName) ? targetPath : fileName;

                    bool approved = await RequestPermissionAsync(
                        $"确认{operation}项目外路径: {displayName}",
                        $"AI 正在尝试{operation}当前项目之外的路径：\n\n`{targetPath}`\n\n⚠️ 该路径不在当前工作区 `{workspaceRoot}` 内。",
                        "file_access_outside_workspace",
                        "",
                        $"AI 请求{operation}项目外部路径 `{targetPath}` 以完成任务",
                        ct);
                    if (!approved)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.permissionDenied", targetPath));
                        return $"⛔ 用户拒绝了项目外路径{operation}: {targetPath}\n\n"
                            + string.Format(AiPrompts.OutOfWorkspaceWarning, targetPath, workspaceRoot);
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
                        filePurpose,
                        ct);
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
                string? result = await BuiltInTools.ExecuteBuiltInToolAsync(toolName, argumentsJson, workspaceRoot, ct);
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
        /// 从对话历史中提取 runSubagent 工具调用结果，用于 Handoff 时传递给目标 Agent。
        /// 去重并按内容截断（~8KB），避免目标 Agent 因 maxRecentTurns 限制丢失探索上下文。
        /// </summary>
        private static string ExtractRunSubagentResults(List<ChatApiMessage> conversationHistory)
        {
            if (conversationHistory == null || conversationHistory.Count == 0)
                return string.Empty;

            var seenHashes = new HashSet<string>();
            var sb = new StringBuilder();
            const int maxBytes = 8192;

            foreach (var msg in conversationHistory)
            {
                if (msg.Role == "tool"
                    && string.Equals(msg.Name, "runSubagent", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    // 去重：相同内容的 tool 结果只保留一份
                    string hash = ComputeSimpleHash(msg.Content!);
                    if (!seenHashes.Add(hash))
                        continue;

                    if (sb.Length > 0)
                        sb.AppendLine("\n---\n");
                    sb.AppendLine(msg.Content!.Truncate(2048));

                    // 截断保护
                    if (sb.Length > maxBytes)
                    {
                        sb.AppendLine("\n> ⚠️ 探索结果已截断（总量超限），完整内容见对话历史。");
                        break;
                    }
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 简单哈希：用于 runSubagent 结果去重（不要求密码学强度）。
        /// </summary>
        private static string ComputeSimpleHash(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int hash = 17;
            foreach (char c in text)
                hash = hash * 31 + c;
            return hash.ToString("X8");
        }

        /// <summary>
        /// 将 HandoffRequest（AI 通过 request_handoff 工具发起的 JSON 移交）
        /// 转换为 AgentHandoff（供 UI 层使用的移交格式）。
        /// </summary>
        protected AgentHandoff ConvertHandoffRequestToHandoff(HandoffRequest request)
        {
            var L = LocalizationService.Instance;
            string label = request.TargetAgent switch
            {
                AgentType.Ask => L["agent.edit.handoffAskLabel"],
                AgentType.Edit => L["agent.ask.handoffEditLabel"],
                AgentType.Plan => L["agent.ask.handoffPlanLabel"],
                AgentType.Build => L["agent.ask.handoffBuildLabel"],
                AgentType.Explore => L["agent.ask.handoffExploreLabel"],
                _ => L["agent.handoff.defaultLabel"]
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
        /// 执行 Handoff：从当前 Agent 移交到目标 Agent。
        /// 构建 Handoff prompt（含计划上下文 + plan.md），调用目标 Agent 的 ExecuteAsync。
        /// 
        /// 从 AgentDispatcher.ExecuteHandoffAsync 搬过来，由 BaseAgent 统一提供。
        /// </summary>
        /// <param name="handoff">移交定义</param>
        /// <param name="context">执行上下文</param>
        /// <param name="activePlan">当前活跃计划（可选，注入到 prompt 中）</param>
        /// <param name="agentFactory">AgentFactory 引用，用于获取目标 Agent 实例</param>
        public async Task<AgentResult> ExecuteHandoffAsync(
            AgentHandoff handoff,
            AgentContext context,
            AgentTaskPlan? activePlan = null,
            AgentFactory? agentFactory = null)
        {
            Logger.Info($"[{Definition.Name}] Handoff: → {handoff.TargetAgent} ({handoff.Label})");

            BaseAgent targetAgent;
            if (agentFactory != null)
                targetAgent = agentFactory.GetAgent(handoff.TargetAgent);
            else
                throw new InvalidOperationException("AgentFactory 引用为空，无法获取目标 Agent");

            targetAgent.Context = context;

            // ── 🔑 缓存边界快照（v1.1.10）：在 Handoff 前保存 ContextManager 状态 ──
            //     目标 Agent 通过 BuildApiMessages 读取历史时，仅包含边界前的条目，
            //     排除 Handoff 过渡消息（步骤完成通知、最终构建结果等），
            //     使 DeepSeek Prefix Cache 在跨 Agent 切换时仍能命中。
            context.ContextManager?.SnapshotForCache();

            // ── 构建 Handoff prompt（包含完整计划上下文）──
            var sb = new StringBuilder();
            sb.AppendLine(handoff.Prompt);
            sb.AppendLine();

            // ── 🔄 Handoff 上下文提示：避免重复探索 ──
            sb.AppendLine(AiPrompts.HandoffContextPrompt);
            sb.AppendLine();

            if (activePlan != null)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format(LocalizationService.Instance["plan.format.title"], activePlan.Title));
                sb.AppendLine(string.Format(LocalizationService.Instance["plan.format.stepCount"], activePlan.Steps.Count));
                sb.AppendLine();

                foreach (var s in activePlan.Steps)
                {
                    sb.AppendLine(string.Format(LocalizationService.Instance["plan.format.stepItem"], s.Index, s.Title));
                    sb.AppendLine(s.Description);
                    sb.AppendLine();
                }
            }

            // ── 注入 plan.md 概述（仅开头部分，避免完整文档占用过多 token）──
            //     完整步骤详情已通过上方结构化列表提供，无需重复注入全部 plan.md
            string? planFilePath = context.PlanFilePath ?? activePlan?.PlanFilePath;
            if (!string.IsNullOrEmpty(planFilePath) && File.Exists(planFilePath))
            {
                try
                {
                    string planMd = await Task.Run(() => File.ReadAllText(planFilePath));
                    if (planMd.Length > 0)
                    {
                        // 按章节截断：找到 ## 标题边界，在 ~3000 字符附近最近的一个 ## 之前切断
                        const int maxPlanMdChars = 3000;
                        string planOverview = TruncatePlanMdBySection(planMd, maxPlanMdChars);
                        sb.AppendLine("## 📄 计划概述 (plan.md 开头部分)");
                        sb.AppendLine(planOverview);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[{Definition.Name}] 读取 plan.md 失败: {ex.Message}");
                }
            }

            // ── 注入源 Agent 的探索结果（从对话历史中提取 runSubagent tool 结果）──
            // Handoff 时 maxRecentTurns 限制可能导致目标 Agent 看不到源 Agent 的早期探索结果
            string explorationContext = ExtractRunSubagentResults(context.ConversationHistory);
            if (!string.IsNullOrWhiteSpace(explorationContext))
            {
                sb.AppendLine();
                sb.AppendLine("## 🔍 前一 Agent 的探索结果");
                sb.AppendLine(explorationContext);
            }

            // ── 注入跨步骤代码记忆（EditAgent 多步骤间持久化的关键代码片段）──
            if (!string.IsNullOrWhiteSpace(context.CodeMemory))
            {
                sb.AppendLine();
                sb.AppendLine("## 💾 代码记忆（跨步骤持久化）");
                sb.AppendLine(context.CodeMemory);
            }

            string handoffMessage = sb.ToString();
            // ── 保留 context 中已有的 ActivePlan，仅在非 null 时覆盖 ──
            context.ActivePlan = activePlan ?? context.ActivePlan;
            // ── 刷新 ConversationHistory ──
            if (context.ContextManager != null)
                context.ConversationHistory = context.ContextManager.GetConversationHistory();
            // ── Planning 模式仅对 Edit/Build 等执行类 Agent 生效 ──
            if (handoff.TargetAgent == AgentType.Edit || handoff.TargetAgent == AgentType.Build)
                context.IsPlanningMode = true;

            return await targetAgent.ExecuteAsync(handoffMessage, context);
        }

        /// <summary>
        /// 按 Markdown 章节边界截断 plan.md，在 ~maxChars 附近最近的 ## 标题前干净切断。
        /// 避免在段落中间截断导致内容不完整。
        /// </summary>
        private static string TruncatePlanMdBySection(string planMd, int maxChars)
        {
            if (planMd.Length <= maxChars)
                return planMd;

            // 收集所有 ## 标题的位置（行首 ## ，前无更高级别 #）
            var sectionStarts = new List<int>();
            var lines = planMd.Split('\n');
            int charPos = 0;
            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                // 匹配 ## 或 ### 标题（但不匹配 # 一级标题，保留它）
                if (trimmed.StartsWith("##") && !trimmed.StartsWith("###"))
                {
                    sectionStarts.Add(charPos);
                }
                charPos += line.Length + 1; // +1 for \n
            }

            // 从后往前找第一个在 maxChars 之前的 ## 边界
            int cutPos = maxChars;
            for (int i = sectionStarts.Count - 1; i >= 0; i--)
            {
                if (sectionStarts[i] < maxChars)
                {
                    cutPos = sectionStarts[i];
                    break;
                }
            }

            // 如果第一个 ## 都在 maxChars 之后，就用纯字符截断（只有开头概述，没有章节标题）
            string truncated = planMd.Substring(0, cutPos).TrimEnd();
            int keptSections = sectionStarts.Count(s => s < cutPos);
            string note = keptSections > 0
                ? $"\n\n... (已截断，保留了前 {keptSections} 个章节。后续步骤详情请参阅上方结构化步骤列表)"
                : "\n\n... (已截断。各步骤详情请参阅上方结构化步骤列表)";

            return truncated + note;
        }

        /// <summary>
        /// 清理消息列表中不完整的工具调用链。
        /// 与 ChatStreamAsync Rule 5 使用相同算法：对每个 assistant(tool_calls)，
        /// 向前扫描寻找匹配的 tool 结果；遇到非 tool 消息停止。
        /// 找不到匹配则剥离 tool_calls 和 reasoning_content，空 content 一并移除。
        /// 
        /// 在 Handoff (ForwardedMessages) 时调用，确保传递给目标 Agent 的消息列表
        /// 在后续每轮 API 调用中产生一致的清洗结果，避免缓存前缀断裂。
        /// </summary>
        private static List<ChatApiMessage> CleanIncompleteToolChains(List<ChatApiMessage> messages)
        {
            // 浅克隆避免修改原始消息（与 ChatStreamAsync 保持一致）
            var result = messages.Select(m => new ChatApiMessage
            {
                Role = m.Role,
                Content = m.Content,
                ReasoningContent = m.ReasoningContent,
                ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId,
                Name = m.Name,
            }).ToList();

            int strippedCount = 0;
            for (int i = 0; i < result.Count; i++)
            {
                var m = result[i];
                if (m.Role != "assistant" || m.ToolCalls == null || m.ToolCalls.Count == 0)
                    continue;

                var expectedIds = new HashSet<string>(m.ToolCalls.Select(tc => tc.Id ?? ""));
                bool hasMatch = false;
                for (int j = i + 1; j < result.Count; j++)
                {
                    var next = result[j];
                    if (next.Role == "tool" && !string.IsNullOrEmpty(next.ToolCallId)
                        && expectedIds.Contains(next.ToolCallId))
                    {
                        hasMatch = true;
                        break;
                    }
                    if (next.Role != "tool") break;
                }

                if (!hasMatch)
                {
                    var tcNames = string.Join(", ", m.ToolCalls.Select(tc => tc.Function?.Name ?? "?"));
                    Logger.Info($"[Agent] CleanIncompleteToolChains: 剥离 assistant[{i}] tool_calls=[{tcNames}] — 无匹配 tool 结果");
                    m.ToolCalls = null;
                    m.ReasoningContent = null;
                    strippedCount++;
                }
            }

            // 移除剥离后为空的 assistant
            int beforeRemove = result.Count;
            result.RemoveAll(m =>
                m.Role == "assistant"
                && string.IsNullOrEmpty(m.Content)
                && (m.ToolCalls == null || m.ToolCalls.Count == 0));
            int removedEmpty = beforeRemove - result.Count;

            if (strippedCount > 0 || removedEmpty > 0)
            {
                Logger.Info($"[Agent] CleanIncompleteToolChains: stripped={strippedCount} removedEmpty={removedEmpty} finalCount={result.Count}");
            }

            return result;
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
                field.PermissionRequested -= OnExplorePermissionRequested;
                field.QuestionsRequested -= OnExploreQuestionsRequested;
            }
            field = exploreAgent;
            if (field != null)
            {
                field.LogEntryAdded += OnExploreLog;
                field.FileChangeNotified += OnExploreFileChange;
                field.PermissionRequested += OnExplorePermissionRequested;
                field.QuestionsRequested += OnExploreQuestionsRequested;
            }
        }

        private void OnExploreLog(AgentLogEntry entry)
        {
            // 转发 ExploreAgent 日志到父 Agent，触发 LogEntryAdded 以便 UI 实时刷新进度
            // 由于 RegisterExploreAgent 只订阅一次（构造函数中），不会出现并行重复订阅问题
            // 注意：直接转发原始消息（不加前缀），确保 FormatLogForThinking 的 emoji 匹配正常工作
            RaiseLogEntryAdded(entry);
        }

        /// <summary>
        /// 确保 ExploreAgent 的日志、权限请求和提问请求转发到当前 Agent。
        /// 供 AgentFactory 在注入 ExploreAgent 引用后调用（AgentFactory 无法访问 protected RegisterExploreAgent）。
        /// </summary>
        public void WireExploreLogs()
        {
            if (ExploreAgent != null)
            {
                // 先移除防止重复订阅，再重新订阅
                ExploreAgent.LogEntryAdded -= OnExploreLog;
                ExploreAgent.LogEntryAdded += OnExploreLog;
                ExploreAgent.PermissionRequested -= OnExplorePermissionRequested;
                ExploreAgent.PermissionRequested += OnExplorePermissionRequested;
                ExploreAgent.QuestionsRequested -= OnExploreQuestionsRequested;
                ExploreAgent.QuestionsRequested += OnExploreQuestionsRequested;
            }
        }

        /// <summary>
        /// 转发 ExploreAgent 的权限请求到父 Agent（v1.1.10 修复）。
        /// 子代理执行 git/终端等操作需要审批时，弹窗需通过父 Agent 的事件链到达 UI。
        /// </summary>
        private void OnExplorePermissionRequested(AgentPermissionRequest request)
        {
            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"[Explore→{Definition.Name}] 转发权限请求: {request.Title}");
        }

        /// <summary>
        /// 转发 ExploreAgent 的提问请求到父 Agent（v1.1.10 修复）。
        /// </summary>
        private void OnExploreQuestionsRequested(AgentQuestionRequest request)
        {
            QuestionsRequested?.Invoke(request);
            AddLog("INFO", $"[Explore→{Definition.Name}] 转发提问请求: {request.Questions.Count} 个问题");
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
                or "grep_search"             // 同上
                or "git"                     // 写操作需要用户审批
                or "runSubagent";            // 子代理可能需要用户审批（如 Explore 用 git）
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
        /// 尝试从工具调用中提取文件路径并注册到 ActiveFileTracker。
        /// 解析工具参数 JSON 中的 filePath/path 字段，判断读写类型。
        /// </summary>
        private void TryTrackActiveFileAccess(ToolCall tc, string? workspaceRoot)
        {
            try
            {
                var tracker = BuiltInTools?.ActiveFileTracker;
                if (tracker == null) return;

                string toolName = tc.Function.Name;
                string filePath = ExtractFilePathFromToolArgs(toolName, tc.Function.Arguments) ?? string.Empty;
                if (string.IsNullOrEmpty(filePath)) return;

                // 转换为绝对路径
                string resolvedPath = ResolveToAbsolutePath(filePath, workspaceRoot);

                int currentTurn = Context?.ContextManager?.TurnCount ?? 0;

                if (IsWriteTool(toolName))
                    tracker.ObserveWrite(resolvedPath, toolName, currentTurn);
                else
                    tracker.ObserveRead(resolvedPath, toolName, currentTurn);
            }
            catch
            {
                // 追踪失败不阻塞工具执行
            }
        }

        /// <summary>
        /// 将路径解析为绝对路径。
        /// </summary>
        private static string ResolveToAbsolutePath(string filePath, string? workspaceRoot)
        {
            if (Path.IsPathRooted(filePath))
                return Path.GetFullPath(filePath);

            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string candidate = Path.Combine(workspaceRoot, filePath.Replace('/', '\\'));
                return Path.GetFullPath(candidate);
            }

            return filePath;
        }

        /// <summary>
        /// 裁剪工具结果（Agent 工具循环内部使用）。
        /// 与 ConversationContextManager.AddToolResult 使用同一裁剪器实例，
        /// 确保 messages 列表和 _entries 历史中的内容一致。
        /// </summary>
        private string CompactToolResultForAgent(string toolName, string rawResult)
        {
            try
            {
                var compactor = BuiltInTools?.ToolResultCompactor;
                if (compactor == null || string.IsNullOrEmpty(rawResult))
                    return rawResult ?? string.Empty;

                string model = Context?.ContextManager?.CurrentModel ?? "deepseek-v4";
                return compactor.CompactToolResultForContext(toolName, rawResult, model);
            }
            catch
            {
                return rawResult ?? string.Empty;
            }
        }

        /// <summary>
        /// 判断工具是否为写操作（编辑/创建/删除文件）。
        /// </summary>
        private static bool IsWriteTool(string toolName)
        {
            return toolName switch
            {
                "write_file" => true,
                "create_file" => true,
                "replace_in_file" => true,
                "multi_replace_in_file" => true,
                "delete_file" => true,
                "apply_patch" => true,
                "create_directory" => true,
                _ => false,
            };
        }

        /// <summary>
        /// 对同一批次中的工具调用进行去重。
        /// 相同函数名 + 相同参数（规范化 JSON）的调用只保留第一个，
        /// 后续重复调用映射到第一个的结果。
        /// </summary>
        /// <returns>
        /// dedupedIndices: 去重后需要实际执行的 toolCalls 索引列表。
        /// dedupMapping: 源索引 → 重复索引列表的映射（用于将结果复制到重复调用）。
        /// </returns>
        private static (List<int> dedupedIndices, Dictionary<int, List<int>> dedupMapping) DeduplicateToolCalls(
            List<ToolCall> toolCalls)
        {
            var dedupedIndices = new List<int>();
            var dedupMapping = new Dictionary<int, List<int>>();
            // key: "functionName|normalizedArgs" → 第一个出现的索引
            var seen = new Dictionary<string, int>();

            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                string normalizedArgs = NormalizeToolArguments(tc.Function.Arguments);
                string key = $"{tc.Function.Name}|{normalizedArgs}";

                if (seen.TryGetValue(key, out int firstIdx))
                {
                    // 重复调用：映射到第一个出现的索引
                    if (!dedupMapping.ContainsKey(firstIdx))
                        dedupMapping[firstIdx] = new List<int>();
                    dedupMapping[firstIdx].Add(i);
                }
                else
                {
                    seen[key] = i;
                    dedupedIndices.Add(i);
                }
            }

            return (dedupedIndices, dedupMapping);
        }

        /// <summary>
        /// 规范化工具参数 JSON：移除空格/换行等无意义差异，
        /// 确保相同语义的参数能匹配到同一个去重 key。
        /// </summary>
        private static string NormalizeToolArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                // 重新序列化，使用紧凑格式（无缩进、无多余空格）
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = false
                });
            }
            catch
            {
                // JSON 解析失败时，回退到原始字符串（去除首尾空白）
                return argumentsJson.Trim();
            }
        }

        /// <summary>
        /// 从工具参数 JSON 中提取目标文件路径（支持多种字段名和集合类型）。
        /// 覆盖所有文件相关工具的参数模式。
        /// </summary>
        private static string? ExtractFilePathFromToolArgs(string toolName, string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                // ── 单文件路径字段（优先级从高到低）──
                string[] singlePathKeys = { "filePath", "path", "file", "dirPath", "target" };
                foreach (var key in singlePathKeys)
                {
                    if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        string? val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }

                // ── 多文件路径数组（取第一个为代表）──
                string[] arrayPathKeys = { "paths", "files", "targets", "filePaths" };
                foreach (var key in arrayPathKeys)
                {
                    if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        var first = arr[0];
                        if (first.ValueKind == JsonValueKind.String)
                        {
                            string? val = first.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                }

                // ── 编辑类工具的 special 字段 ──
                // replace_in_file: oldFilePath → 返回 filePath（主文件）
                // apply_patch: filePath 已被覆盖
                // create_directory: dirPath 已被覆盖
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
        /// 尝试修复因 max_tokens 不足而被截断的 JSON。
        /// 策略：计数字符串外的括号不平衡数，在末尾补充缺失的引号/方括号/花括号。
        /// </summary>
        /// <param name="json">可能截断的 JSON 字符串</param>
        /// <returns>修复后的 JSON 字符串。如果无法安全修复则返回原字符串。</returns>
        protected static string TryRepairTruncatedJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            if (!json.TrimStart().StartsWith("{")) return json;

            // 快速检查：如果末尾已经是 }，可能不需要修复
            string trimmed = json.TrimEnd();
            if (trimmed.EndsWith("}"))
            {
                // 验证括号平衡
                if (IsJsonBracketBalanced(trimmed))
                    return json;
            }

            bool inString = false;
            bool escaped = false;
            int braceDepth = 0;
            int bracketDepth = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;

                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']') bracketDepth--;
            }

            var sb = new StringBuilder(json);

            // 如果截断在字符串内部（未闭合的引号）
            if (inString)
            {
                // 找到最后一个未转义的引号，在其后添加闭合引号
                sb.Append("\"");
            }

            // 闭合未配对的数组
            while (bracketDepth > 0)
            {
                sb.Append("]");
                bracketDepth--;
            }

            // 闭合未配对的对象
            while (braceDepth > 0)
            {
                sb.Append("}");
                braceDepth--;
            }

            string repaired = sb.ToString();

            // 验证修复结果（快速括号平衡检查）
            if (IsJsonBracketBalanced(repaired) && repaired.TrimEnd().EndsWith("}"))
                return repaired;

            // 修复失败 → 尝试暴力闭合（找到最后一个 " 后闭合）
            int lastQuote = json.LastIndexOf('"');
            if (lastQuote > 0)
            {
                // 从最后一个引号处截断，闭合所有结构
                string truncated = json.Substring(0, lastQuote + 1);
                // 重新计算括号深度
                braceDepth = 0;
                bracketDepth = 0;
                inString = false;
                escaped = false;
                for (int i = 0; i < truncated.Length; i++)
                {
                    char c2 = truncated[i];
                    if (escaped) { escaped = false; continue; }
                    if (c2 == '\\') { escaped = true; continue; }
                    if (c2 == '"') { inString = !inString; continue; }
                    if (inString) continue;
                    if (c2 == '{') braceDepth++;
                    else if (c2 == '}') braceDepth--;
                    else if (c2 == '[') bracketDepth++;
                    else if (c2 == ']') bracketDepth--;
                }
                var sb2 = new StringBuilder(truncated);
                if (inString) sb2.Append("\"");
                while (bracketDepth-- > 0) sb2.Append("]");
                while (braceDepth-- > 0) sb2.Append("}");
                return sb2.ToString();
            }

            return json; // 实在修不了，返回原文
        }

        /// <summary>
        /// 快速检查 JSON 的花括号和方括号是否平衡。
        /// </summary>
        private static bool IsJsonBracketBalanced(string json)
        {
            int braceDepth = 0, bracketDepth = 0;
            bool inString = false;
            bool escaped = false;
            foreach (char c in json)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']') bracketDepth--;
            }
            return braceDepth == 0 && bracketDepth == 0;
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
        public async Task<bool> RequestPermissionAsync(string title, string command, string actionType = "command", string detail = "", string purpose = "", CancellationToken ct = default)
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

            // 将请求注册到并发字典（支持多工具并行场景）
            _pendingPermissions[request.RequestId] = request;

            // ── 注册取消回调：停止按钮按下时自动拒绝权限，避免永久阻塞 ──
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => request.ResponseTcs?.TrySetResult(false));

            try
            {
                PermissionRequested?.Invoke(request);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingPermission"], title));

                bool approved = await request.ResponseTcs.Task;
                // 清理：如果该请求仍在字典中则移除（可能已被 RespondToPermission 移除）
                _pendingPermissions.TryRemove(request.RequestId, out _);
                AddLog("INFO", $"{LocalizationService.Instance["agent.log.permissionResult"]}: {(approved ? "✅ 允许" : "❌ 拒绝")} → {title}");
                return approved;
            }
            finally
            {
                ctr.Dispose();
            }
        }

        /// <summary>
        /// 请求用户批准终端命令执行（显示命令详情和说明）。
        /// </summary>
        /// <param name="command">待执行的命令字符串</param>
        /// <param name="explanation">命令用途说明（告诉用户这条命令在做什么，如"编译项目检查错误"）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要执行，如"验证代码修改后能否正常编译"）</param>
        /// <returns>true 表示用户允许执行，false 表示跳过</returns>
        public async Task<bool> RequestTerminalApprovalAsync(string command, string explanation, string purpose = "", CancellationToken ct = default)
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

            // 将请求注册到并发字典（支持多工具并行场景）
            _pendingPermissions[request.RequestId] = request;

            // ── 注册取消回调 ──
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => request.ResponseTcs?.TrySetResult(false));

            try
            {
                PermissionRequested?.Invoke(request);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingTerminalApproval"], command));

                bool approved = await request.ResponseTcs.Task;
                // 清理
                _pendingPermissions.TryRemove(request.RequestId, out _);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.terminalApprovalResult"],
                    approved ? "✅ 允许" : "⏭️ 跳过", command));
                return approved;
            }
            finally
            {
                ctr.Dispose();
            }
        }

        /// <summary>
        /// 按 RequestId 查找待处理的权限请求（不删除）。
        /// </summary>
        public AgentPermissionRequest? TryGetPendingPermission(string requestId)
        {
            _pendingPermissions.TryGetValue(requestId, out var request);
            return request;
        }

        /// <summary>
        /// 按 RequestId 查找并移除待处理的权限请求。
        /// </summary>
        public AgentPermissionRequest? TryRemovePendingPermission(string requestId)
        {
            _pendingPermissions.TryRemove(requestId, out var request);
            return request;
        }

        /// <summary>
        /// 响应权限请求。
        /// 支持并发权限请求：从 _pendingPermissions 字典中按 RequestId 精确查找并解析。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            // 从并发安全字典中查找（支持多工具并行场景）
            if (_pendingPermissions.TryRemove(requestId, out var request))
            {
                request.ResponseTcs?.TrySetResult(approved);
            }
        }

        /// <summary>
        /// 向用户提问并等待回答（VisualStudio_askQuestions 工具实现）。
        /// 使用 TaskCompletionSource 等待用户在 WebView 中提交答案。
        /// </summary>
        /// <param name="questionsJson">问题列表的 JSON 字符串</param>
        /// <returns>用户答案的 JSON 字符串，或超时/取消时的空字符串</returns>
        public async Task<string> RequestAskQuestionsAsync(string questionsJson, CancellationToken ct = default)
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

                // 将请求注册到并发字典（支持多工具并行场景）
                _pendingQuestions[request.RequestId] = request;

                // ── 注册取消回调：停止按钮按下时自动返回空答案，避免永久阻塞 ──
                CancellationTokenRegistration ctr = default;
                if (ct.CanBeCanceled)
                    ctr = ct.Register(() => request.ResponseTcs?.TrySetResult("[]"));

                try
                {
                    // ── 诊断：记录事件订阅状态 ──
                    int handlerCount = QuestionsRequested?.GetInvocationList().Length ?? 0;
                    Logger.Info($"[Agent:{Definition.Name}] QuestionsRequested 事件订阅数: {handlerCount}, RequestId={request.RequestId}");

                    if (handlerCount == 0)
                    {
                        // ── 无 UI 处理器订阅 → 无法向用户展示问题，立即返回空答案避免永久阻塞 ──
                        Logger.Warn($"[Agent:{Definition.Name}] ⚠️ QuestionsRequested 事件无订阅者！" +
                            $"无法向用户展示 {questions.Count} 个问题。请检查 Agent 事件绑定链。" +
                            $"当前活跃 Agent: {Definition.Type}, RequestId={request.RequestId}");
                        _pendingQuestions.TryRemove(request.RequestId, out _);
                        return "[]"; // 返回空答案，让 AI 知道用户未回答
                    }

                    QuestionsRequested!.Invoke(request);
                    AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingAnswers"],
                        questions.Count, questions[0].Header.Truncate(60)));

                    // 无限等待用户回答（不设超时），用户提交或跳过时通过 ResponseTcs 唤醒
                    string answers = await request.ResponseTcs.Task;

                    // 清理
                    _pendingQuestions.TryRemove(request.RequestId, out _);
                    AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.answersReceived"],
                        answers.Truncate(200)));
                    return answers;
                }
                finally
                {
                    ctr.Dispose();
                }
            }
            catch (Exception ex)
            {
                return $"❌ VisualStudio_askQuestions 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 按 RequestId 查找待处理的提问请求（不删除）。
        /// </summary>
        public AgentQuestionRequest? TryGetPendingQuestion(string requestId)
        {
            _pendingQuestions.TryGetValue(requestId, out var request);
            return request;
        }

        /// <summary>
        /// 响应用户提问回答。
        /// 支持并发提问请求：从 _pendingQuestions 字典中按 RequestId 精确查找并解析。
        /// </summary>
        public void RespondToQuestions(string requestId, string answersJson)
        {
            // 从并发安全字典中查找（支持多工具并行场景）
            if (_pendingQuestions.TryRemove(requestId, out var request))
            {
                request.ResponseTcs?.TrySetResult(answersJson);
            }
        }

        /// <summary>
        /// 请求用户确认文件删除操作。
        /// 会中断当前执行流，在 WebView 中渲染确认按钮，等待用户响应。
        /// </summary>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        /// <param name="reason">删除原因说明（告诉用户为什么删除这些文件）</param>
        /// <param name="purpose">操作目的（告诉用户删除后能达到什么效果）</param>
        /// <returns>true 表示用户确认删除，false 表示取消</returns>
        public async Task<bool> RequestFileDeleteConfirmationAsync(List<string> filePaths, string reason = "", string purpose = "", CancellationToken ct = default)
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

            // 将请求注册到并发字典（支持多工具并行场景）
            _pendingPermissions[request.RequestId] = request;

            // ── 注册取消回调 ──
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => request.ResponseTcs?.TrySetResult(false));

            try
            {
                PermissionRequested?.Invoke(request);
                AddLog("INFO", $"{LocalizationService.Instance["agent.log.waitingDeleteConfirm"]}: {title}");

                bool approved = await request.ResponseTcs.Task;
                // 清理
                _pendingPermissions.TryRemove(request.RequestId, out _);
                AddLog("INFO", LocalizationService.Instance.Format("agent.log.fileDeleteConfirm", approved ? "✅ 确认删除" : "❌ 取消", title));
                return approved;
            }
            finally
            {
                ctr.Dispose();
            }
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

        #region CodeMemory — 跨步骤代码记忆

        /// <summary>
        /// 语言感知的文件类型权重表。
        /// 头文件/接口类文件加权，在 LRU 淘汰中更难被移除。
        /// 未列出的扩展名默认权重 1.0。
        /// </summary>
        protected static readonly Dictionary<string, double> CodeMemoryTypeWeights = new(StringComparer.OrdinalIgnoreCase)
        {
            // C/C++ headers — 被多个源文件引用，高价值
            { ".h", 2.0 }, { ".hpp", 2.0 }, { ".hxx", 2.0 }, { ".h++", 2.0 },
            // Interface / type-definition files in other languages
            { ".cs", 1.5 },   // C# — 接口与实现在同一扩展名，适度加权
            { ".ts", 1.3 },   // TypeScript — 类型定义重要
            { ".d.ts", 1.5 }, // TypeScript 声明文件
            // Build / config files — 影响全局
            { ".csproj", 1.8 }, { ".props", 1.8 }, { ".targets", 1.8 },
            { ".sln", 1.5 }, { ".slnx", 1.5 },
            { ".json", 1.2 }, { ".yaml", 1.2 }, { ".yml", 1.2 },
            { ".cmake", 1.3 }, { ".mak", 1.3 },
            // Others at default 1.0
        };

        /// <summary>
        /// 从文件读取缓存刷新跨步骤代码记忆（供所有 Agent 调用）。
        /// 使用 LRU + 类型权重 + 步骤关键词加分的淘汰算法。
        /// 总量控制在 ~12KB 以内。
        /// </summary>
        /// <param name="context">执行上下文</param>
        /// <param name="modifiedPaths">已修改文件路径集合（这些文件被排除）</param>
        /// <param name="stepKeywords">步骤关键词（可选，匹配到的文件获加分）</param>
        protected void RefreshCodeMemory(
            AgentContext context,
            HashSet<string> modifiedPaths,
            HashSet<string>? stepKeywords = null)
        {
            if (BuiltInTools == null) return;

            var fileCache = BuiltInTools.GetFileReadCacheSnapshot();
            var roundCache = BuiltInTools.GetFileReadCacheRoundSnapshot();
            if (fileCache.Count == 0) return;

            int currentRound = BuiltInTools.CurrentRound;
            bool noRoundInfo = currentRound <= 0;
            if (noRoundInfo) currentRound = int.MaxValue;

            // ── 构建候选列表：排除已修改文件，计算加权 LRU 分数 ──
            var candidates = new List<(string Path, string Content, double Score)>();

            foreach (var kvp in fileCache)
            {
                if (modifiedPaths.Contains(NormalizePath(kvp.Key))) continue;

                string ext = Path.GetExtension(kvp.Key).ToLowerInvariant();
                double typeWeight = CodeMemoryTypeWeights.TryGetValue(ext, out double w) ? w : 1.0;

                int lastRound = roundCache.TryGetValue(kvp.Key, out int lr) ? lr : 0;
                int roundsAgo = (lastRound > 0 && currentRound > lastRound)
                    ? currentRound - lastRound
                    : 0;

                // 分数 = 距今轮数 / 类型权重（越小越优先）
                double score = roundsAgo / typeWeight;

                // ── P1-4: 首轮退化 tiebreaker ──
                // 当所有文件 roundsAgo=0 时，小文件优先（更高的信息密度）
                if (roundsAgo == 0)
                {
                    score = (kvp.Value.Length / 1000.0) / typeWeight;
                }

                // ── P2-9: 步骤关键词加分 ──
                if (stepKeywords != null && stepKeywords.Count > 0)
                {
                    string fileName = Path.GetFileName(kvp.Key).ToLowerInvariant();
                    if (stepKeywords.Any(kw => fileName.Contains(kw)))
                    {
                        score -= 0.5; // 负值提升排名
                    }
                }

                candidates.Add((kvp.Key, kvp.Value, score));
            }

            // ── 按分数升序排列（分数越低越优先）──
            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));

            // ── 按优先级填充 CodeMemory，12KB 封顶 ──
            var sb = new StringBuilder();
            const int maxTotalChars = 12000;
            const int maxHeaderChars = 3000;
            const int maxImplChars = 1500;
            int totalChars = 0;

            foreach (var (path, content, score) in candidates)
            {
                if (totalChars >= maxTotalChars) break;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool isHeader = ext == ".h" || ext == ".hpp" || ext == ".hxx" || ext == ".h++";
                int maxChars = isHeader ? maxHeaderChars : maxImplChars;

                string snippet = content;
                if (snippet.Length > maxChars)
                    snippet = snippet.Substring(0, maxChars) + "\n// ... (截断)";

                int estimatedChars = snippet.Length + Path.GetFileName(path).Length + 60;
                if (totalChars + estimatedChars > maxTotalChars && totalChars > 0)
                    break;

                sb.AppendLine($"### 📄 `{Path.GetFileName(path)}`");
                sb.AppendLine("```cpp");
                sb.AppendLine(snippet.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
                totalChars += estimatedChars;
            }

            // ── P2-7: 贪心填空 — 用小文件填充剩余空间 ──
            if (totalChars < maxTotalChars && totalChars > 0)
            {
                int remaining = maxTotalChars - totalChars;
                var includedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nameMatches = Regex.Matches(sb.ToString(), @"### 📄 `([^`]+)`");
                foreach (Match m in nameMatches)
                    includedNames.Add(m.Groups[1].Value);

                foreach (var (path, content, score) in candidates)
                {
                    if (includedNames.Contains(Path.GetFileName(path))) continue;

                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    bool isHeader = ext == ".h" || ext == ".hpp" || ext == ".hxx" || ext == ".h++";
                    int maxChars = isHeader ? maxHeaderChars : maxImplChars;
                    int available = Math.Min(maxChars, remaining - 60);
                    if (available <= 100) continue; // 太小无意义

                    string snippet = content.Length > available
                        ? content.Substring(0, available) + "\n// ..."
                        : content;
                    sb.AppendLine($"### 📄 `{Path.GetFileName(path)}`");
                    sb.AppendLine("```cpp");
                    sb.AppendLine(snippet.TrimEnd());
                    sb.AppendLine("```");
                    sb.AppendLine();
                    totalChars += snippet.Length + 60;
                    includedNames.Add(Path.GetFileName(path));
                    if (totalChars >= maxTotalChars) break;
                }
            }

            context.CodeMemory = sb.Length > 0 ? sb.ToString().TrimEnd() : null;

            // ── P2-8: 精确计数（用正则提取文件名，避免 Contains 误匹配）──
            if (!string.IsNullOrEmpty(context.CodeMemory))
            {
                var includedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nameMatches = Regex.Matches(context.CodeMemory!, @"### 📄 `([^`]+)`");
                foreach (Match m in nameMatches)
                    includedNames.Add(m.Groups[1].Value);

                int fileCount = candidates.Count(c => includedNames.Contains(Path.GetFileName(c.Path)));
                AddLog("INFO", string.Format(
                    LocalizationService.Instance["agent.log.codeMemoryUpdated"] ?? "代码记忆已更新 ({0} 字符, {1} 个文件)",
                    context.CodeMemory!.Length, fileCount));
            }
        }

        #endregion

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
