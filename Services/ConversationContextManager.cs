using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 统一上下文管理器 — 负责多轮对话历史的存储、拼接与 Token 预算管理。
    /// 
    /// 核心职责：
    /// 1. 单一数据源：所有 API 调用所需的对话历史由此类统一管理
    /// 2. reasoning_content 回传规则（严格遵守 DeepSeek V4 思考模式协议）：
    ///    - 无工具调用的 assistant 消息 → reasoning_content 不需要回传（API 会忽略）
    ///    - 有工具调用的 assistant 消息 → reasoning_content 必须完整回传（否则 400 错误）
    /// 3. Token 预算估算与自动压缩（不再直接截断/删除旧消息）
    /// 4. 轮次（Turn）边界追踪
    /// 5. RAG 上下文注入点
    /// 6. 上下文统计与使用率监控
    /// 
    /// DeepSeek V4 最大上下文窗口: 1M tokens（1,000,000）
    /// 默认预算: 900K tokens（留 100K 给模型输出）
    /// 
    /// 参考：
    /// - https://api-docs.deepseek.com/zh-cn/guides/multi_round_chat
    /// - https://api-docs.deepseek.com/zh-cn/guides/thinking_mode
    /// </summary>
    public class ConversationContextManager : IConversationContextManager
    {
        /// <summary>内部对话历史存储（单一数据源）</summary>
        private readonly List<ContextEntry> _entries = new();

        /// <summary>系统提示词（独立存储，不参与 turn 管理）</summary>
        private string? _systemPrompt;

        /// <summary>
        /// 已冻结的系统提示词（session 初始化时由 SetFixedSystemPrompt() 设置）。
        /// 一旦冻结，整个会话期间不再改变，保障 messages[0] 前缀稳定性，
        /// 使 DeepSeek V4 的自动前缀缓存可以持续命中。
        /// </summary>
        private string? _fixedSystemPrompt;

        /// <summary>搜索结果上下文（独立存储，注入为 system 消息）</summary>
        private string? _searchContext;

        /// <summary>IDE 实时态上下文（P1-A：活动文件/光标/选区/符号/诊断摘要，注入 volatile 块最前）</summary>
        private string? _ideContext;

        /// <summary>当前解决方案上下文（注入 volatile 块最前）</summary>
        private string? _solutionContext;

        /// <summary>Skill 发现上下文（独立存储）</summary>
        private string? _skillContext;

        /// <summary>始终注入的技能上下文（独立存储，每次对话均注入完整指令）</summary>
        private string? _alwaysInjectSkillsContext;

        /// <summary>RAG 检索上下文（独立存储，注入为 system 消息）</summary>
        private string? _ragContext;

        /// <summary>记忆上下文（独立存储，注入为 system 消息）</summary>
        private string? _memoryContext;

        /// <summary>当前工作区根目录（用于 Working Set 摘要相对路径生成）</summary>
        private string? _workspaceRoot;

        /// <summary>当前 Token 估算计数器（字符级原始估算，未校准）</summary>
        private int _estimatedTokens;

        /// <summary>上下文条目单调递增 ID，用于判断压缩边界内是否仍有新内容。</summary>
        private long _nextEntryId;

        /// <summary>已经压缩过的最大条目 ID。</summary>
        private long _lastCompressedEntryId;

        /// <summary>是否需要在本次完整对话结束后提示用户切换新对话。</summary>
        private bool _conversationResetNoticePending;

        /// <summary>从会话持久化恢复、等待 compressor 初始化后注入的摘要。</summary>
        private readonly List<CompressedTurnSummary> _pendingCompressedSummaries = new();

        /// <summary>Token 估算校准系数（基于 API 实际 usage 的指数移动平均，1.0 = 无校准）</summary>
        private double _calibrationFactor = 1.0;

        /// <summary>校准样本权重（EMA α 值，0.15 约等于最近 ~7 次调用的加权）</summary>
        private const double CalibrationAlpha = 0.15;

        /// <summary>上下文压缩服务（可选注入）</summary>
        private ContextCompressorService? _compressor;

        /// <summary>压缩标记：是否正在压缩中</summary>
        private bool _isCompressing;

        /// <summary>
        /// 压缩状态变化事件：参数为“是否正在压缩”和当前上下文使用率（0~100）。
        /// 供 UI 在耗时的 LLM 摘要压缩期间显示进度提示。
        /// </summary>
        public event Action<bool, double>? CompressionStateChanged;

        /// <summary>当前是否正在执行上下文压缩。</summary>
        public bool IsCompressing => _isCompressing;

        /// <summary>活跃文件追踪器（可选注入，用于 Working Set 摘要）</summary>
        private IActiveFileTracker? _activeFileTracker;

        /// <summary>当前使用的模型标识（用于裁剪器的大上下文模型检测）</summary>
        public string CurrentModel { get; set; } = "deepseek-v4";

        /// <summary>完整工具结果存储（按 tool_call_id 索引，保留原始结果备查）</summary>
        private readonly Dictionary<string, string> _fullToolResultStore = new();

        /// <summary>Token 预算上限（默认 900K，DeepSeek V4 上下文窗口为 1M，留 100K 给输出）</summary>
        public int TokenBudget { get; set; } = 900_000;

        /// <summary>当超过 Token 预算时自动压缩的最旧轮次数（此值在压缩模式下仅作用为最小保留轮次）</summary>
        public int AutoTrimTurns { get; set; } = 3;

        // ── 缓存友好窗口（v1.1.10 稳定前缀窗口优化）──
        //     将 messages 数组分为「固定前缀区」和「缓存窗口区」，
        //     超出窗口的旧轮次自动压缩为摘要，确保 messages 数组大小稳定，
        //     使 DeepSeek 服务端能快速检测公共前缀并落盘缓存。
        //     参考：DeepSeek API 文档 — 公共前缀检测需要 2-3 次请求才能触发落盘

        /// <summary>
        /// 缓存友好窗口：消息列表超过此估算 token 数时自动裁剪旧轮次并压缩为摘要。
        /// 默认 800K tokens，减少压缩频率、增大单次压缩范围，
        /// 降低前缀缓存因压缩导致的断裂次数。
        /// 设为 0 禁用缓存窗口裁剪。
        /// </summary>
        public int CacheWindowMaxTokens { get; set; } = 800_000;

        /// <summary>
        /// 缓存友好窗口：最多保留的最近轮次数。
        /// 默认 30 轮，配合 CacheWindowMaxTokens 使用（两者中限制更严格者生效）。
        /// </summary>
        public int CacheWindowMaxTurns { get; set; } = 30;

        /// <summary>
        /// 缓存友好窗口：最多保留的消息条目数（兜底保护）。
        /// 默认 500 条，防止单轮大量工具调用导致窗口失控。
        /// 设为 0 不限条目数。
        /// </summary>
        public int CacheWindowMaxEntries { get; set; } = 500;

        /// <summary>
        /// 压缩后保留轮次的比例（0.0~1.0）。
        /// 触发压缩时不只压缩到窗口边界，而是进一步压缩到窗口大小的此比例，
        /// 为后续对话增长留出余量，减少频繁压缩导致的缓存断裂。
        /// 默认 0.5：窗口 30 轮 → 压缩后保留 ~15 轮。
        /// </summary>
        public double CompressionAggressiveness { get; set; } = 0.5;

        // ──  缓存边界快照（v1.1.10）──
        //     Agent Handoff 时，在注入过渡消息前保存 _entries 的快照索引，
        //     目标 Agent 可调用 BuildApiMessagesUpToSnapshot() 仅包含边界前的历史，
        //     使前缀缓存跨 Agent 切换时仍能命中。
        //     null = 未设置快照（正常会话模式）。

        /// <summary>缓存边界快照：应在 Handoff 前由源 Agent 设置</summary>
        private int? _cacheSnapshotEntryIndex;

        /// <summary>
        ///  缓存边界快照时的 dynamicBlock 冻结副本（v1.1.10）。
        /// 快照活跃时，BuildApiMessages 使用此冻结版本替代实时 BuildDynamicContextBlock()，
        /// 防止压缩摘要/搜索/RAG/记忆等动态内容变化导致前缀缓存断裂。
        /// null = 无快照或快照时 dynamicBlock 为空。
        /// </summary>
        private string? _cachedDynamicBlock;

        /// <summary>
        /// 保存缓存边界快照：记录当前 _entries 的数量作为缓存前缀边界，
        /// 同时冻结当前的 dynamicBlock 文本，防止动态上下文变化破坏前缀缓存。
        /// Agent Handoff 前调用，确保目标 Agent 可排除过渡消息、复用缓存前缀。
        /// </summary>
        public void SnapshotForCache()
        {
            _cacheSnapshotEntryIndex = _entries.Count;
            _cachedDynamicBlock = BuildDynamicContextBlock();
            Logger.Info($"[ContextManager]  缓存边界快照已保存: entryIndex={_cacheSnapshotEntryIndex}, dynamicBlock={(_cachedDynamicBlock?.Length ?? 0)}chars");
        }

        /// <summary>
        /// 消费并清空缓存边界快照。
        /// 多步骤 Agent 在执行后续步骤前必须回到完整上下文；
        /// 否则 assistant/tool 历史会被 Handoff 边界意外排除。
        /// </summary>
        public void ClearCacheSnapshot()
        {
            if (!_cacheSnapshotEntryIndex.HasValue && _cachedDynamicBlock == null)
                return;

            Logger.Info($"[ContextManager]  缓存边界快照已清除: was at entry {_cacheSnapshotEntryIndex}");
            _cacheSnapshotEntryIndex = null;
            _cachedDynamicBlock = null;
        }

        /// <summary>获取当前对话轮次数（一个 user 消息 = 一轮）</summary>
        public int TurnCount => _entries.Count(e => e.Role == "user");

        /// <summary>获取消息总条数</summary>
        public int MessageCount => _entries.Count;

        /// <summary>获取当前上下文中 assistant 消息携带的工具调用总次数</summary>
        public int ToolCallCount => _entries.Sum(e => e.ToolCalls?.Count ?? 0);

        /// <summary>
        /// 从指定起始索引开始，将 _entries 转换为 ChatApiMessage 列表（不含 SP/FP/DB 前缀）。
        /// 用于子代理（Explore）执行后将内部工具循环消息回注到父代理消息列表。
        /// </summary>
        /// <param name="startIndex">起始索引（含）</param>
        /// <param name="endIndex">结束索引（不含），默认到末尾</param>
        public List<ChatApiMessage> GetEntryMessages(int startIndex, int endIndex = -1)
        {
            int end = endIndex < 0 ? _entries.Count : Math.Min(endIndex, _entries.Count);
            var result = new List<ChatApiMessage>();
            for (int i = startIndex; i < end; i++)
            {
                var entry = _entries[i];
                if (string.IsNullOrEmpty(entry.Content)
                    && (entry.ToolCalls == null || entry.ToolCalls.Count == 0)
                    && (entry.MultimodalContent == null || entry.MultimodalContent.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                    MultimodalContent = CloneContentParts(entry.MultimodalContent),
                };

                //  序列化保真度：原样复制 ReasoningContent（null 保持 null），
                //     不使用 ?? string.Empty 兜底。JsonIgnoreCondition.WhenWritingNull
                //     会省略 null 但序列化 "" → JSON 字节不同 → 前缀缓存断裂。
                if (entry.Role == "assistant" && entry.HasToolCalls)
                    apiMsg.ReasoningContent = entry.ReasoningContent;

                if (entry.Role == "assistant" && entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                    apiMsg.ToolCalls = entry.ToolCalls;

                if (entry.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(entry.ToolCallId))
                        apiMsg.ToolCallId = entry.ToolCallId;
                    if (!string.IsNullOrEmpty(entry.Name))
                        apiMsg.Name = entry.Name;
                }

                result.Add(apiMsg);
            }
            return result;
        }

        /// <summary>获取校准后的估算 Token 数（= 原始估算 × 校准系数）</summary>
        public int EstimatedTokens => (int)(_estimatedTokens * _calibrationFactor);

        /// <summary>获取原始字符级估算 Token 数（未校准），用于内部计算和校准对比</summary>
        public int RawEstimatedTokens => _estimatedTokens;

        /// <summary>上下文是否为空（无任何用户消息）</summary>
        public bool IsEmpty => !_entries.Any(e => e.Role == "user");

        /// <summary>获取上下文使用率（0.0 ~ 1.0，基于校准后估算）</summary>
        public double UsageRatio => TokenBudget > 0 ? (double)EstimatedTokens / TokenBudget : 0;

        /// <summary>获取上下文使用百分比</summary>
        public double UsagePercent => UsageRatio * 100;

        /// <summary>获取压缩服务实例</summary>
        public ContextCompressorService? Compressor => _compressor;

        /// <summary>获取 RAG 上下文</summary>
        public string? RagContext => _ragContext;

        #region Core API — 添加消息

        /// <summary>
        /// 设置系统提示词。
        /// 如果已通过 FreezeSystemPrompt() 冻结，则忽略此次调用并记录警告，
        /// 防止意外覆盖不可变前缀导致 DeepSeek V4 缓存失效。
        /// </summary>
        public void SetSystemPrompt(string? prompt)
        {
            if (_fixedSystemPrompt != null)
            {
                Logger.Warn($"[ContextManager] SetSystemPrompt() 被调用但 system prompt 已冻结 — 忽略。" +
                    $"当前冻结长度={_fixedSystemPrompt.Length} 字符, 新 prompt 长度={prompt?.Length ?? 0} 字符。" +
                    $"如需更换 system prompt，请先调用 Clear() 然后重新 FreezeSystemPrompt()。");
                return;
            }
            _systemPrompt = prompt;
        }

        /// <summary>
        /// 设置搜索上下文（注入为 system 消息）。
        /// </summary>
        public void SetSearchContext(string? searchContext)
        {
            _searchContext = searchContext;
        }

        /// <summary>
        /// 设置 IDE 实时态上下文（P1-A）。
        /// 由 View 在每次用户消息发送前捕获快照并格式化后传入；传 null 表示禁用或清空。
        /// Token 记账与 RAG 上下文一致。
        /// </summary>
        public void SetIdeContext(string? ideContext)
        {
            if (!string.IsNullOrEmpty(_ideContext))
                _estimatedTokens -= EstimateTokens(_ideContext);

            _ideContext = ideContext;

            if (!string.IsNullOrEmpty(_ideContext))
                _estimatedTokens += EstimateTokens(_ideContext);
        }

        /// <summary>
        /// 设置当前解决方案上下文。解决方案路径是会话元数据，
        /// 与 IDE Context 一起进入 volatile 块，不包装进当前 user 轮次。
        /// </summary>
        public void SetSolutionPath(string? solutionPath)
        {
            string? formatted = string.IsNullOrWhiteSpace(solutionPath)
                ? null
                : LocalizationService.Instance.Format("system.contextSolutionLabel", solutionPath);

            if (!string.IsNullOrEmpty(_solutionContext))
                _estimatedTokens -= EstimateTokens(_solutionContext);

            _solutionContext = formatted;

            if (!string.IsNullOrEmpty(_solutionContext))
                _estimatedTokens += EstimateTokens(_solutionContext);
        }

        // ── Context Debugger 只读探针（P2，序号 20 数据面）──

        /// <summary>本轮是否注入了联网搜索上下文</summary>
        [JsonIgnore]
        public bool HasSearchContext => !string.IsNullOrEmpty(_searchContext);

        /// <summary>本轮是否注入了 RAG 检索上下文</summary>
        [JsonIgnore]
        public bool HasRagContext => !string.IsNullOrEmpty(_ragContext);

        /// <summary>IDE 实时态上下文字符数（0 = 未注入/已禁用）</summary>
        [JsonIgnore]
        public int IdeContextChars => _ideContext?.Length ?? 0;

        /// <summary>Working Set 最近活跃文件 Top-N（P2 Context Debugger 数据面）。</summary>
        public System.Collections.Generic.IReadOnlyList<string> GetWorkingSetTopPaths(int limit = 8)
        {
            try
            {
                var root = !string.IsNullOrWhiteSpace(_workspaceRoot)
                    ? _workspaceRoot!
                    : Environment.CurrentDirectory;
                return _activeFileTracker?.GetTopPaths(root, limit)
                       ?? (System.Collections.Generic.IReadOnlyList<string>)Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 设置 Skill 发现上下文。
        /// </summary>
        public void SetSkillContext(string? skillContext)
        {
            _skillContext = skillContext;
        }

        /// <summary>
        /// 设置始终注入的技能上下文（每次对话均加载完整技能指令）。
        /// </summary>
        public void SetAlwaysInjectSkillsContext(string? alwaysInjectSkillsContext)
        {
            _alwaysInjectSkillsContext = alwaysInjectSkillsContext;
        }

        /// <summary>
        /// <summary>
        /// 设置记忆上下文（注入为 system 消息）。
        /// 会话初始化时由 ChatControl 调用，包含用户记忆和仓库记忆。
        /// </summary>
        public void SetMemoryContext(string? memoryContext)
        {
            _memoryContext = memoryContext;
        }

        /// <summary>
        /// 设置当前工作区根目录（用于 Working Set 摘要的相对路径生成）。
        /// </summary>
        public void SetWorkspaceRoot(string? workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        /// <summary>
        /// 设置 RAG 检索上下文（注入为 system 消息）。
        /// 由 RagService 在每次用户消息前调用。
        /// </summary>
        public void SetRagContext(string? ragContext)
        {
            // 移除旧的 RAG 上下文 token 计数
            if (!string.IsNullOrEmpty(_ragContext))
                _estimatedTokens -= EstimateTokens(_ragContext);

            _ragContext = ragContext;

            // 添加新的 RAG 上下文 token 计数
            if (!string.IsNullOrEmpty(_ragContext))
                _estimatedTokens += EstimateTokens(_ragContext);
        }

        /// <summary>
        /// 注入上下文压缩服务。
        /// 设置后，当 Token 超出预算时将自动调用压缩而非直接删除旧消息。
        /// </summary>
        public void SetCompressor(ContextCompressorService? compressor)
        {
            _compressor = compressor;
            if (_compressor != null && _pendingCompressedSummaries.Count > 0)
                _compressor.ReplaceSummaries(_pendingCompressedSummaries);
        }

        /// <summary>
        /// 获取当前压缩摘要的持久化快照。
        /// </summary>
        public List<CompressedTurnSummary> GetCompressedSummariesSnapshot()
        {
            var source = _compressor?.CompressedSummaries ?? _pendingCompressedSummaries;
            return source.Select(CloneCompressedSummary).ToList();
        }

        /// <summary>
        /// 从会话持久化恢复压缩摘要，并在 compressor 已初始化时立即注入。
        /// </summary>
        public void RestoreCompressedSummaries(IEnumerable<CompressedTurnSummary>? summaries)
        {
            _pendingCompressedSummaries.Clear();
            if (summaries != null)
                _pendingCompressedSummaries.AddRange(summaries.Select(CloneCompressedSummary));

            _compressor?.ReplaceSummaries(_pendingCompressedSummaries);
            _cachedDynamicBlock = null;
        }

        private static CompressedTurnSummary CloneCompressedSummary(CompressedTurnSummary summary)
        {
            return new CompressedTurnSummary
            {
                Summary = summary.Summary,
                FromTurn = summary.FromTurn,
                ToTurn = summary.ToTurn,
                OriginalTokens = summary.OriginalTokens,
                CompressedTokens = summary.CompressedTokens,
                CompressedAt = summary.CompressedAt,
            };
        }

        private ContextEntry CreateEntry(
            string Role,
            string? Content = null,
            List<ChatContentPart>? MultimodalContent = null,
            string? ReasoningContent = null,
            List<ToolCall>? ToolCalls = null,
            bool HasToolCalls = false,
            string? ToolCallId = null,
            string? Name = null,
            int TurnIndex = -1,
            bool IsVolatileSnapshot = false)
        {
            return new ContextEntry
            {
                EntryId = ++_nextEntryId,
                Role = Role,
                Content = Content,
                MultimodalContent = MultimodalContent,
                ReasoningContent = ReasoningContent,
                ToolCalls = ToolCalls,
                HasToolCalls = HasToolCalls,
                ToolCallId = ToolCallId,
                Name = Name,
                TurnIndex = TurnIndex,
                IsVolatileSnapshot = IsVolatileSnapshot,
            };
        }

        /// <summary>
        /// 判断待压缩边界内是否还有从未压缩过的对话条目。
        /// </summary>
        private bool HasNewCompressibleEntries(int compressionStart)
        {
            if (compressionStart <= 0)
                return false;

            for (int i = 0; i < compressionStart && i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.TurnIndex > 0 && entry.EntryId > _lastCompressedEntryId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 标记需要在当前 Agent 工作流结束后提示用户切换新对话。
        /// </summary>
        private void MarkConversationResetNeeded(string reason)
        {
            if (_conversationResetNoticePending)
                return;

            _conversationResetNoticePending = true;
            Logger.Warn($"[ContextManager] 没有新的可压缩对话内容，已停止压缩并要求切换新对话: {reason}");
        }

        /// <summary>
        /// 消费“需要切换新对话”通知。返回 true 表示本次完整对话结束后应提示用户。
        /// </summary>
        public bool ConsumeConversationResetNotice()
        {
            bool pending = _conversationResetNoticePending;
            _conversationResetNoticePending = false;
            return pending;
        }

        /// <summary>
        /// 清除待处理的“切换新对话”通知，不修改对话历史。
        /// </summary>
        public void ClearConversationResetNotice()
        {
            _conversationResetNoticePending = false;
        }

        /// <summary>
        /// 注入前缀缓存管理器。
        /// 设置后，BuildApiMessages 时会自动检查前缀稳定性并记录漂移事件。
        /// <summary>
        /// 注入活跃文件追踪器。
        /// 设置后，BuildVolatileContextBlock 将注入 Working Set 摘要。
        /// </summary>
        public void SetActiveFileTracker(IActiveFileTracker? tracker)
        {
            _activeFileTracker = tracker;
        }

        /// <summary>
        /// 获取完整未裁剪的工具结果（按 tool_call_id）。
        /// </summary>
        public string? GetFullToolResult(string toolCallId)
        {
            _fullToolResultStore.TryGetValue(toolCallId, out string? result);
            return result;
        }

        /// <summary>
        /// 冻结系统提示词为不可变前缀。
        /// 应在会话初始化时调用一次。冻结后整个会话期间不应再调用 SetSystemPrompt()。
        /// 
        /// 这是前缀缓存优化的核心：messages[0] 的内容在冻结后永远不会改变，
        /// 确保 DeepSeek V4 的自动前缀缓存在每次请求时都能命中。
        /// 
        /// 冻结内容 = systemPrompt + skillContext（若存在则以换行连接）
        /// </summary>
        public void FreezeSystemPrompt()
        {
            _fixedSystemPrompt = BuildFinalSystemPrompt();
            Logger.Info($"[ContextManager] System prompt 已冻结为不可变前缀 ({_fixedSystemPrompt?.Length ?? 0} 字符)");
        }

        /// <summary>
        /// 获取已冻结的系统提示词（null = 尚未冻结）。
        /// </summary>
        public string? GetFixedSystemPrompt() => _fixedSystemPrompt;

        /// <summary>
        /// 添加用户消息。
        /// 当 Token 超出预算时触发压缩而非直接删除。
        /// </summary>
        public void AddUserMessage(string content)
            => AddUserMessage(content, null);

        /// <summary>
        /// 添加用户消息。视觉模型轮次中可以携带 <paramref name="multimodalContent"/>，
        /// 即使文本为空也仍应作为有效 user 轮次保存，避免重启后 ContextManager 被判定为空。
        /// </summary>
        public void AddUserMessage(string content, List<ChatContentPart>? multimodalContent)
        {
            bool hasText = !string.IsNullOrEmpty(content);
            bool hasMultimodal = multimodalContent is { Count: > 0 };
            if (!hasText && !hasMultimodal) return;

            // ── 安全净化：防止工具注入标记进入上下文 ──
            if (hasText)
                content = StringExtensions.SanitizeUserInput(content);

            var normalizedMultimodalContent = CloneContentParts(multimodalContent);
            if (hasText
                && normalizedMultimodalContent is { Count: > 0 }
                && !normalizedMultimodalContent.Any(p =>
                    p.Type == "text" && string.Equals(p.Text, content, StringComparison.Ordinal)))
            {
                // 多模态消息序列化时 content 数组优先于纯文本字段。
                // 调用方通常只传图片块，因此必须把文本显式放回数组，避免文字丢失。
                normalizedMultimodalContent.Insert(0, new ChatContentPart
                {
                    Type = "text",
                    Text = content,
                });
            }

            // ──  缓存边界快照：新用户消息意味着新对话轮次，清除旧快照 ──
            if (_cacheSnapshotEntryIndex.HasValue)
            {
                Logger.Info($"[ContextManager]  新用户消息，清除缓存边界快照 (was at entry {_cacheSnapshotEntryIndex})");
                _cacheSnapshotEntryIndex = null;
            }

            _entries.Add(CreateEntry(
                Role: "user",
                Content: content,
                MultimodalContent: normalizedMultimodalContent,
                TurnIndex: TurnCount + 1)); // 新轮次

            _estimatedTokens += EstimateTokens(content);
            _estimatedTokens += EstimateMultimodalTokens(multimodalContent);

            // ──  v1.1.11：冻结动态上下文块，确保同轮次内后续API调用
            //     messages[2] 内容不变 → DeepSeek前缀缓存可持续命中。──
            _cachedDynamicBlock = BuildDynamicContextBlock();
        }

        /// <summary>
        /// 添加助手消息。
        /// </summary>
        /// <param name="content">回复内容</param>
        /// <param name="reasoningContent">思维链内容（思考模式下）</param>
        /// <param name="toolCalls">工具调用列表（可为 null）</param>
        public void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null)
        {
            // ──  前缀缓存优化：写时合并连续 assistant 消息 ──
            //     如果上一条也是 assistant，合并内容而非新增条目，
            //     避免 BuildApiMessages 产生连续 assistant 消息，进而触发 ChatStreamAsync
            //     的合并逻辑修改消息内容 → 破坏 DeepSeek Prefix Cache 前缀。
            //
            //     覆盖两种场景：
            //     A. 两条纯文本 assistant（均无 tool_calls）→ 合并文本
            //     B. 前条有 tool_calls + 本条纯文本 → 合并文本到前条（保留 tool_calls）
            //        这是 Agent 工具调用循环中的常见模式：AI 先发起工具调用，再输出文本响应
            if (_entries.Count > 0)
            {
                var last = _entries[_entries.Count - 1];
                if (last.Role == "assistant")
                {
                    bool lastHasTc = last.ToolCalls != null && last.ToolCalls.Count > 0;
                    bool currHasTc = toolCalls != null && toolCalls.Count > 0;

                    // 场景 A：两条都是纯文本 assistant → 合并
                    // 场景 B：前条有 tool_calls，本条是纯文本 → 合并文本到前条（保留 tool_calls 结构）
                    if (!currHasTc)
                    {
                        // 合并文本内容到前条 assistant
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            last.Content = string.IsNullOrWhiteSpace(last.Content)
                                ? content
                                : last.Content + "\n\n---\n\n" + content;
                        }
                        // 如果后者有 reasoning_content，保留后者
                        if (!string.IsNullOrWhiteSpace(reasoningContent))
                            last.ReasoningContent = reasoningContent;
                        _estimatedTokens += EstimateTokens(content);
                        if (!string.IsNullOrEmpty(reasoningContent))
                            _estimatedTokens += EstimateTokens(reasoningContent);

                        // ── 诊断：记录非标准合并场景 ──
                        if (lastHasTc)
                        {
                            Logger.Info($"[ContextManager] 写时合并 assistant: 前条有 tool_calls({last.ToolCalls!.Count}个), " +
                                $"本条纯文本({content?.Length ?? 0}chars) → 合并到前条");
                        }
                        return;
                    }
                    // 场景 C：前条纯文本 + 本条有 tool_calls → 不应合并（语义上 tool_calls 不应在文本之后）
                    // 保持独立条目，由 BuildApiMessages 自然处理
                }
            }

            _entries.Add(CreateEntry(
                Role: "assistant",
                Content: content,
                ReasoningContent: reasoningContent,
                ToolCalls: toolCalls,
                HasToolCalls: toolCalls != null && toolCalls.Count > 0,
                TurnIndex: TurnCount)); // 属于当前轮次
            _estimatedTokens += EstimateTokens(content);
            if (!string.IsNullOrEmpty(reasoningContent))
                _estimatedTokens += EstimateTokens(reasoningContent);
        }

        /// <summary>
        /// 添加工具调用结果（tool 角色消息）。
        /// 自动裁剪过长结果以保护上下文窗口。
        /// 完整结果保留在独立存储中备查。
        /// </summary>
        public void AddToolResult(string toolCallId, string toolName, string result)
        {
            // 保存原始完整结果
            if (!string.IsNullOrEmpty(toolCallId) && !string.IsNullOrEmpty(result))
                _fullToolResultStore[toolCallId] = result;

            _entries.Add(CreateEntry(
                Role: "tool",
                Content: result,
                ToolCallId: toolCallId,
                Name: toolName,
                TurnIndex: TurnCount)); // 工具调用属于当前轮次
            _estimatedTokens += EstimateTokens(result);
        }

        /// <summary>
        /// 添加自定义角色消息（如 skill 指令注入的 system 消息）。
        /// 这些消息不计入 turn 管理。
        /// </summary>
        public void AddCustomMessage(string role, string content)
        {
            if (string.IsNullOrEmpty(content)) return;

            _entries.Add(CreateEntry(
                Role: role,
                Content: content,
                TurnIndex: -1)); // 不属于任何轮次
            _estimatedTokens += EstimateTokens(content);
        }

        #endregion

        #region Core API — 构建 API 消息

        /// <summary>
        /// 构建发送给 DeepSeek API 的完整消息列表。
        /// 委托给 BuildApiMessagesCore(0)。
        /// </summary>
        public List<ChatApiMessage> BuildApiMessages()
        {
            return BuildApiMessagesCore(0);
        }

        /// <summary>
        /// 统一的消息列表构建核心。BuildApiMessages() 和 BuildApiMessagesRecentTurns()
        /// 共享相同的前缀构建、缓存窗口裁剪和条目转换逻辑，仅起始索引不同。
        /// </summary>
        /// <param name="startEntryIdx">_entries 中开始包含的起始索引</param>
        private List<ChatApiMessage> BuildApiMessagesCore(int startEntryIdx)
        {
            string? dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();

            // ── 压缩必须在构建正常请求前完成 ──
            //     压缩请求从对话开头截取到待压缩区间末尾，最后只追加压缩指令，
            //     保证待压缩内容既完整出现，又不会破坏这段缓存前缀。
            if (!_cacheSnapshotEntryIndex.HasValue)
            {
                int compressionStart = ResolveCompressionStartIndex(startEntryIdx, out string compressionReason);

                if (compressionStart > startEntryIdx)
                {
                    if (!HasNewCompressibleEntries(compressionStart))
                    {
                        MarkConversationResetNeeded(compressionReason);
                    }
                    else
                    {
                        string? compressionDynamicBlock = BuildCompressionPrefixDynamicBlock();
                        // 压缩请求只发送到“待压缩区间末尾”为止的完整对话前缀，
                        // 不包含之后要继续保留的历史，也不重复追加待压缩内容。
                        var compressionPrefix = BuildApiMessagesSnapshot(
                            startEntryIdx: 0,
                            compressionDynamicBlock,
                            entryLimitOverride: compressionStart);
                        Logger.Info($"[CacheWindow] 触发压缩: {compressionReason}, 压缩前保留 {TurnCount} 轮");
                        CompressEntriesBeforeWindow(compressionStart, compressionPrefix);

                        // 压缩摘要已生成，立即刷新动态块，确保本轮正常请求就能注入结果。
                        dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();
                        startEntryIdx = 0;
                    }
                }
                else if (startEntryIdx > 0)
                {
                    Logger.Info($"[CacheWindow] 触发压缩: 调用方限制(startEntry={startEntryIdx}), 当前{TurnCount}轮");
                }
            }

            return BuildApiMessagesSnapshot(startEntryIdx, dynamicBlock);
        }

        /// <summary>
        /// 解析当前应当压缩到哪个条目边界。
        /// 供正常请求构建和同一用户请求内的工具循环共用，确保触发条件一致。
        /// </summary>
        private int ResolveCompressionStartIndex(int startEntryIdx, out string compressionReason)
        {
            int compressionStart = startEntryIdx;
            compressionReason = "none";

            if (_compressor != null && _compressor.Config.AutoCompressEnabled)
            {
                int tokenWindowStart = FindCacheWindowStart(out string triggerReason);
                if (tokenWindowStart > compressionStart)
                {
                    int aggressiveTurns = Math.Max(1, (int)(CacheWindowMaxTurns * CompressionAggressiveness));
                    int aggressiveStart = FindTurnStartIndex(aggressiveTurns);
                    compressionStart = Math.Max(tokenWindowStart, aggressiveStart);
                    compressionReason = triggerReason;
                }

                var config = _compressor.Config;
                if (EstimatedTokens > TokenBudget * config.CompressionThreshold)
                {
                    bool severe = EstimatedTokens > TokenBudget * config.AggressiveThreshold;
                    double targetRatio = severe
                        ? config.AggressiveCompressionTargetRatio
                        : config.CompressionTargetRatio;
                    targetRatio = Math.Max(0.05, Math.Min(0.95, targetRatio));
                    int targetTokens = (int)(TokenBudget * targetRatio);
                    int budgetStart = FindTokenTargetStartIndex(targetTokens);
                    if (budgetStart > compressionStart)
                    {
                        compressionStart = budgetStart;
                        compressionReason = severe
                            ? $"usage>{config.AggressiveThreshold:P0}->{targetRatio:P0}"
                            : $"usage>{config.CompressionThreshold:P0}->{targetRatio:P0}";
                    }
                }
            }
            else
            {
                AutoTrimIfNeeded();
                int tokenWindowStart = FindCacheWindowStart(out string triggerReason);
                if (tokenWindowStart > compressionStart)
                {
                    compressionStart = tokenWindowStart;
                    compressionReason = triggerReason;
                }
            }

            return compressionStart;
        }

        /// <summary>
        /// 同一用户请求的 Agent 工具循环中，在工具结果写回后检查并执行压缩。
        /// 返回的消息数量用于同步裁剪本地 messages，避免下一轮仍发送旧历史。
        /// </summary>
        public bool TryCompressForToolLoop(
            out int staticPrefixMessageCount,
            out int removedMessageCount,
            out string? dynamicBlock)
        {
            staticPrefixMessageCount = 0;
            removedMessageCount = 0;
            dynamicBlock = null;

            if (_cacheSnapshotEntryIndex.HasValue)
                return false;

            int compressionStart = ResolveCompressionStartIndex(0, out string compressionReason);
            if (compressionStart <= 0)
                return false;

            if (!HasNewCompressibleEntries(compressionStart))
            {
                MarkConversationResetNeeded(compressionReason);
                return false;
            }

            string? currentDynamicBlock = BuildCompressionPrefixDynamicBlock();
            var staticPrefix = BuildApiMessagesSnapshot(
                startEntryIdx: 0,
                currentDynamicBlock,
                entryLimitOverride: 0);
            var compressionPrefix = BuildApiMessagesSnapshot(
                startEntryIdx: 0,
                currentDynamicBlock,
                entryLimitOverride: compressionStart);

            staticPrefixMessageCount = staticPrefix.Count;
            removedMessageCount = Math.Max(0, compressionPrefix.Count - staticPrefix.Count);
            if (removedMessageCount <= 0)
                return false;

            Logger.Info($"[ToolLoopCompression] 触发压缩: {compressionReason}, " +
                $"待压缩消息={removedMessageCount}, 当前={EstimatedTokens:N0}/{TokenBudget:N0}");

            CompressEntriesBeforeWindow(compressionStart, compressionPrefix);
            dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();
            return true;
        }

        /// <summary>
        /// 构建压缩请求使用的动态块。始终优先使用包含最新压缩摘要的实时内容，
        /// 防止缓存的旧动态块漏掉已生成的 [对话历史摘要]。
        /// </summary>
        private string? BuildCompressionPrefixDynamicBlock()
        {
            string? liveDynamicBlock = BuildDynamicContextBlock();
            if (!string.IsNullOrWhiteSpace(liveDynamicBlock))
                return liveDynamicBlock;

            return _cachedDynamicBlock;
        }

        /// <summary>
        /// 构建未压缩的 API 消息快照，不修改任何历史条目。
        /// 该快照既是正常对话前缀，也是压缩请求复用的前缀。
        /// </summary>
        private List<ChatApiMessage> BuildApiMessagesSnapshot(
            int startEntryIdx,
            string? dynamicBlock,
            int? entryLimitOverride = null)
        {
            var messages = new List<ChatApiMessage>();

            // ── [0] 稳定系统提示词（共享前缀 + 固定提示词）──
            string sharedPrefix = AiPrompts.SharedImmutablePrefix;
            string? fixedPrompt = _fixedSystemPrompt
                ?? (string.IsNullOrWhiteSpace(_systemPrompt) && string.IsNullOrWhiteSpace(_skillContext) ? null : BuildFinalSystemPrompt());
            string stableSystemPrompt = CombineSystemParts(fixedPrompt, sharedPrefix);
            if (!string.IsNullOrWhiteSpace(stableSystemPrompt))
                messages.Add(new ChatApiMessage { Role = "system", Content = stableSystemPrompt });

            // ── [1] 动态上下文块（仅在非空时注入）──
            if (!string.IsNullOrWhiteSpace(dynamicBlock))
                messages.Add(new ChatApiMessage { Role = "system", Content = dynamicBlock });

            // ── [2..] 对话历史 ──
            int entryLimit = entryLimitOverride ?? _cacheSnapshotEntryIndex ?? _entries.Count;
            for (int i = startEntryIdx; i < _entries.Count && i < entryLimit; i++)
            {
                var entry = _entries[i];
                if (string.IsNullOrEmpty(entry.Content)
                    && (entry.ToolCalls == null || entry.ToolCalls.Count == 0)
                    && (entry.MultimodalContent == null || entry.MultimodalContent.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                    MultimodalContent = CloneContentParts(entry.MultimodalContent),
                };

                if (entry.Role == "assistant")
                {
                    if (entry.HasToolCalls)
                        apiMsg.ReasoningContent = entry.ReasoningContent;
                }

                if (entry.Role == "assistant" && entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                    apiMsg.ToolCalls = entry.ToolCalls;

                if (entry.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(entry.ToolCallId))
                        apiMsg.ToolCallId = entry.ToolCallId;
                    if (!string.IsNullOrEmpty(entry.Name))
                        apiMsg.Name = entry.Name;
                }

                messages.Add(apiMsg);
            }

            return messages;
        }

        /// <summary>
        /// 构建动态上下文块：合并压缩摘要、记忆上下文和语言守卫。
        /// 所有同轮次内相对稳定的内容合并为一段文本，作为单条 system 消息注入。
        /// 这样将可变内容隔离在一个位置，保护 messages[0] 和对话历史前缀的缓存稳定性。
        /// </summary>
        private string? BuildDynamicContextBlock()
        {
            var parts = new List<string>();

            // 压缩摘要
            if (_compressor != null)
            {
                string compressedText = _compressor.GetCompressedContextText();
                if (!string.IsNullOrWhiteSpace(compressedText))
                    parts.Add(compressedText);
            }

            // 记忆上下文
            if (!string.IsNullOrWhiteSpace(_memoryContext))
                parts.Add(_memoryContext!);

            // 语言一致性守卫：检测用户最后一条消息的语言，确保回复语言与用户一致
            string? languageGuard = BuildLanguageGuard();
            if (languageGuard != null)
                parts.Add(languageGuard);

            if (parts.Count == 0)
                return null;

            return string.Join("\n\n", parts);
        }

        /// <summary>
        /// 检测最后一条用户消息的语言，如果用户使用英文而系统/记忆中有中文内容，
        /// 则生成一条语言一致性守卫指令，防止模型被中文上下文误导而用中文回复。
        /// </summary>
        private string? BuildLanguageGuard()
        {
            // 反向查找最后一条用户消息
            string? lastUserMessage = null;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "user" && !string.IsNullOrWhiteSpace(_entries[i].Content))
                {
                    lastUserMessage = _entries[i].Content;
                    break;
                }
            }

            if (lastUserMessage == null)
                return null;

            bool isEnglish = IsEnglishMessage(lastUserMessage);
            if (!isEnglish)
                return null;

            // 检查记忆上下文中是否包含中文（如果记忆是中文但用户用英文，需要守卫）
            bool memoryHasChinese = !string.IsNullOrWhiteSpace(_memoryContext)
                && ContainsCjkCharacters(_memoryContext);

            // 即使用户记忆中没有中文，也始终确保英文回复（因为用户用英文提问）
            return memoryHasChinese
                ? " LANGUAGE OVERRIDE: The user is writing in English. Even though some context or memory content above is in Chinese, you MUST respond in English. The memory content is metadata — the user's actual communication language is English."
                : null;
        }

        /// <summary>
        /// 检测文本是否主要为英文（非 CJK 语言）。
        /// 统计 CJK 字符占比，若低于 15% 则判定为英文消息。
        /// </summary>
        private static bool IsEnglishMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true; // 空消息默认为英文

            int cjkCount = 0;
            int totalLetters = 0;

            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    totalLetters++;
                    // CJK 统一表意文字范围
                    if ((c >= 0x4E00 && c <= 0x9FFF) ||   // CJK Unified Ideographs
                        (c >= 0x3400 && c <= 0x4DBF) ||   // CJK Unified Ideographs Extension A
                        (c >= 0x3000 && c <= 0x303F) ||   // CJK Symbols and Punctuation
                        (c >= 0xFF00 && c <= 0xFFEF) ||   // Halfwidth and Fullwidth Forms
                        (c >= 0xF900 && c <= 0xFAFF))     // CJK Compatibility Ideographs
                    {
                        cjkCount++;
                    }
                }
            }

            // 如果没有字母字符，默认为英文
            if (totalLetters == 0)
                return true;

            double cjkRatio = (double)cjkCount / totalLetters;
            return cjkRatio < 0.15;
        }

        /// <summary>
        /// 检测文本中是否包含 CJK 字符。
        /// </summary>
        private static bool ContainsCjkCharacters(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (char c in text)
            {
                if ((c >= 0x4E00 && c <= 0x9FFF) ||
                    (c >= 0x3400 && c <= 0x4DBF) ||
                    (c >= 0x3000 && c <= 0x303F) ||
                    (c >= 0xFF00 && c <= 0xFFEF) ||
                    (c >= 0xF900 && c <= 0xFAFF))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 构建易变上下文块：Active Working Set + 联网搜索结果 + RAG 检索上下文。
        /// 这些内容在同一会话内可能每轮都变，应从消息数组末尾注入以保护前缀缓存。
        /// </summary>
        public string? BuildVolatileContextBlock()
        {
            var parts = new List<string>();

            // ── 解决方案与 IDE 实时态快照（置于易变块最前，不进入 user 轮次）──
            if (!string.IsNullOrWhiteSpace(_solutionContext))
                parts.Add(_solutionContext!);

            if (!string.IsNullOrWhiteSpace(_ideContext))
                parts.Add(_ideContext!);

            // ── 活跃文件 Working Set ──
            if (_activeFileTracker != null)
            {
                string wsRoot = !string.IsNullOrWhiteSpace(_workspaceRoot)
                    ? _workspaceRoot!
                    : Environment.CurrentDirectory;
                string activeFileSummary = _activeFileTracker.GetActiveFileSummary(wsRoot);
                if (!string.IsNullOrWhiteSpace(activeFileSummary))
                    parts.Add(activeFileSummary);
            }

            // ── 联网搜索结果（与 RAG 一起放在 user 前，避免干扰 [0..1] 稳定前缀）──
            if (!string.IsNullOrWhiteSpace(_searchContext))
                parts.Add(_searchContext!);

            // ── RAG 检索上下文 ──
            if (!string.IsNullOrWhiteSpace(_ragContext))
                parts.Add(_ragContext!);

            if (parts.Count == 0)
                return null;

            return string.Join("\n\n", parts);
        }

        /// <summary>
        /// 将当前 volatile 块固化到标准历史中当前 user 之前。
        /// 下一轮请求会保留上一轮快照，避免 DeepSeek 前缀缓存在旧快照位置断裂。
        /// </summary>
        public bool PersistCurrentVolatileSnapshot()
        {
            string? snapshot = BuildVolatileContextBlock();
            if (string.IsNullOrWhiteSpace(snapshot))
                return false;

            int lastUserIndex = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "user")
                {
                    lastUserIndex = i;
                    break;
                }
            }

            if (lastUserIndex < 0)
                return false;

            int turnIndex = _entries[lastUserIndex].TurnIndex;
            if (lastUserIndex > 0)
            {
                var previous = _entries[lastUserIndex - 1];
                if (previous.IsVolatileSnapshot && previous.TurnIndex == turnIndex)
                    return false;
            }

            var snapshotEntry = CreateEntry(
                Role: "system",
                Content: snapshot,
                TurnIndex: turnIndex,
                IsVolatileSnapshot: true);

            _entries.Insert(lastUserIndex, snapshotEntry);
            _estimatedTokens += EstimateTokens(snapshot);
            return true;
        }

        /// <summary>
        /// 查找缓存窗口的起始条目索引。
        /// 从 _entries 末尾向前遍历，累计轮次、token 数（UTF-8 字节估算）、条目数，
        /// 直到达到任一限制。返回窗口内第一个条目的索引（包含），0 表示全部保留。
        /// </summary>
        private int FindCacheWindowStart(out string triggerReason)
        {
            int maxTurns = CacheWindowMaxTurns;
            int maxTokens = CacheWindowMaxTokens;
            int maxEntries = CacheWindowMaxEntries;

            triggerReason = "none";

            // 全部禁用 → 返回 0
            if (maxTurns <= 0 && maxTokens <= 0 && maxEntries <= 0)
                return 0;

            int turnCount = 0;
            int tokenSum = 0;
            int entryCount = 0;
            int lastUserTurn = -1;

            // 从后往前扫描
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                // ── Token 估算：UTF-8 字节数 / 2（比 chars/4 更接近 API 实际值）──
                int contentBytes = 0;
                if (!string.IsNullOrEmpty(entry.Content))
                    contentBytes = System.Text.Encoding.UTF8.GetByteCount(entry.Content);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    contentBytes += System.Text.Encoding.UTF8.GetByteCount(entry.ReasoningContent);
                int entryTokens = Math.Max(1, contentBytes / 2)
                    + EstimateMultimodalTokens(entry.MultimodalContent);

                // 检测到新的 user 消息 → 轮次+1
                if (entry.Role == "user" && entry.TurnIndex > 0)
                {
                    if (lastUserTurn != entry.TurnIndex)
                    {
                        turnCount++;
                        lastUserTurn = entry.TurnIndex;
                    }
                }

                tokenSum += entryTokens;
                entryCount++;

                // 检查是否超出窗口限制
                bool turnLimitExceeded = maxTurns > 0 && turnCount >= maxTurns;
                bool tokenLimitExceeded = maxTokens > 0 && tokenSum >= maxTokens;
                bool entryLimitExceeded = maxEntries > 0 && entryCount >= maxEntries;

                if (turnLimitExceeded || tokenLimitExceeded || entryLimitExceeded)
                {
                    var reasons = new List<string>();
                    if (turnLimitExceeded) reasons.Add($"轮次数>{maxTurns}(当前{TurnCount})");
                    if (tokenLimitExceeded) reasons.Add($"Token>{maxTokens / 1000}K(当前{EstimatedTokens / 1000}K)");
                    if (entryLimitExceeded) reasons.Add($"条目数>{maxEntries}(当前{_entries.Count})");
                    triggerReason = string.Join(", ", reasons);
                    return i;
                }
            }

            // 所有条目都在窗口内
            return 0;
        }

        /// <summary>
        /// 从末尾向前扫描，找到保留最近 maxTurns 轮所需的起始条目索引。
        /// 用于激进压缩：触发裁剪时不仅移除窗口外的，还多移除一部分窗口内的旧轮次。
        /// </summary>
        private int FindTurnStartIndex(int maxTurns)
        {
            if (maxTurns <= 0) return _entries.Count;

            int turnCount = 0;
            int lastUserTurn = -1;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Role == "user" && entry.TurnIndex > 0)
                {
                    if (lastUserTurn != entry.TurnIndex)
                    {
                        turnCount++;
                        lastUserTurn = entry.TurnIndex;
                        if (turnCount >= maxTurns)
                            return i;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// 按目标 Token 数选择需要保留的历史起点，并对齐到 user 轮次边界。
        /// 如果最近一轮本身已超过目标，则至少保留这一轮。
        /// </summary>
        private int FindTokenTargetStartIndex(int targetTokens)
        {
            if (_entries.Count == 0 || targetTokens <= 0)
                return 0;

            var turnStarts = new List<int>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user" && _entries[i].TurnIndex > 0)
                    turnStarts.Add(i);
            }

            if (turnStarts.Count == 0)
                return 0;

            int totalEntryTokens = _entries.Sum(EstimateEntryTokens);
            double calibrationFactor = _calibrationFactor > 0 ? _calibrationFactor : 1.0;
            int targetRawTokens = (int)(targetTokens / calibrationFactor);
            int overheadTokens = Math.Max(0, _estimatedTokens - totalEntryTokens);
            int allowedEntryTokens = Math.Max(0, targetRawTokens - overheadTokens);

            int candidateStart = turnStarts[turnStarts.Count - 1];
            int retainedTokens = EstimateRangeTokens(candidateStart, _entries.Count);

            for (int i = turnStarts.Count - 2; i >= 0; i--)
            {
                int previousTurnStart = turnStarts[i];
                int previousTurnTokens = EstimateRangeTokens(previousTurnStart, candidateStart);
                if (retainedTokens + previousTurnTokens > allowedEntryTokens)
                    break;

                retainedTokens += previousTurnTokens;
                candidateStart = previousTurnStart;
            }

            return candidateStart;
        }

        private static int EstimateEntryTokens(ContextEntry entry)
        {
            int tokens = EstimateTokens(entry.Content)
                + EstimateMultimodalTokens(entry.MultimodalContent);
            if (!string.IsNullOrEmpty(entry.ReasoningContent))
                tokens += EstimateTokens(entry.ReasoningContent);
            return tokens;
        }

        private int EstimateRangeTokens(int startIndex, int endExclusive)
        {
            int tokens = 0;
            for (int i = startIndex; i < endExclusive && i < _entries.Count; i++)
                tokens += EstimateEntryTokens(_entries[i]);
            return tokens;
        }

        /// <summary>
        /// 将指定索引之前的条目移出 _entries，可选压缩为摘要。
        /// 即使没有压缩服务，也会移除旧条目以控制上下文大小。
        /// </summary>
        private void CompressEntriesBeforeWindow(
            int windowStartIdx,
            IReadOnlyList<ChatApiMessage>? compressionPrefix)
        {
            if (windowStartIdx <= 0)
                return;

            var entriesToCompress = new List<ContextEntry>();
            int removedTokens = 0;

            for (int i = 0; i < windowStartIdx; i++)
            {
                var entry = _entries[i];
                entriesToCompress.Add(entry);
                removedTokens += EstimateTokens(entry.Content);
                removedTokens += EstimateMultimodalTokens(entry.MultimodalContent);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    removedTokens += EstimateTokens(entry.ReasoningContent);
            }

            if (entriesToCompress.Count == 0) return;

            long lastCompressedEntryId = entriesToCompress.Max(e => e.EntryId);
            _lastCompressedEntryId = Math.Max(_lastCompressedEntryId, lastCompressedEntryId);

            // 确定压缩的轮次范围
            int fromTurn = entriesToCompress.Where(e => e.TurnIndex > 0).Select(e => e.TurnIndex).DefaultIfEmpty(1).First();
            int toTurn = entriesToCompress.Where(e => e.TurnIndex > 0).Select(e => e.TurnIndex).DefaultIfEmpty(fromTurn).Last();

            // ── 始终从 _entries 中移除（无论有无 compressor）──
            _entries.RemoveRange(0, windowStartIdx);
            _estimatedTokens -= removedTokens;
            _cachedDynamicBlock = null;

            // ── 同步完成摘要，确保同一轮后续消息构建立即包含压缩结果 ──
            if (_compressor != null)
            {
                _isCompressing = true;
                RaiseCompressionStateChanged(true);
                try
                {
                    var summary = System.Threading.Tasks.Task.Run(() =>
                        _compressor.CompressTurnsAsync(
                            entriesToCompress,
                            fromTurn,
                            toTurn,
                            compressionPrefix,
                            CancellationToken.None)).GetAwaiter().GetResult();

                    _cachedDynamicBlock = BuildDynamicContextBlock();
                    Logger.Info($"[CacheWindow] 压缩第 {fromTurn}-{toTurn} 轮: " +
                        $"{removedTokens} → {summary.CompressedTokens} tokens (摘要)");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[CacheWindow] 压缩失败: {ex.Message}");
                    _cachedDynamicBlock = BuildDynamicContextBlock();
                }
                finally
                {
                    _isCompressing = false;
                    RaiseCompressionStateChanged(false);
                }
            }

            Logger.Info($"[CacheWindow] 裁剪 {entriesToCompress.Count} 条旧消息 " +
                $"(第 {fromTurn}-{toTurn} 轮, {removedTokens} tokens)，" +
                $"保留最近 {TurnCount} 轮在窗口内" +
                (_compressor == null ? " (无压缩器，直接丢弃)" : ""));
        }

        private void RaiseCompressionStateChanged(bool isCompressing)
        {
            try
            {
                CompressionStateChanged?.Invoke(isCompressing, UsagePercent);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ContextManager] 压缩状态通知失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建仅包含最近 N 轮的 API 消息列表（用于 Agent 子调用）。
        /// 委托给 BuildApiMessagesCore()，先按轮次计算起始索引。
        /// </summary>
        /// <param name="maxTurns">保留的最大轮次数</param>
        public List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns)
        {
            if (TurnCount <= maxTurns)
                return BuildApiMessages();

            int turnsToSkip = TurnCount - maxTurns;
            int userCount = 0;
            int startEntryIdx = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user")
                {
                    userCount++;
                    if (userCount > turnsToSkip)
                    {
                        startEntryIdx = i;
                        break;
                    }
                }
            }

            return BuildApiMessagesCore(startEntryIdx);
        }

        /// <summary>
        /// 移除指定索引之后的所有消息（用于重试/编辑场景）。
        /// </summary>
        public void TrimAfter(int entryIndex)
        {
            if (entryIndex < 0 || entryIndex >= _entries.Count) return;

            int removeCount = _entries.Count - entryIndex;

            // 重新计算被移除部分的 token
            for (int i = entryIndex; i < _entries.Count; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                _estimatedTokens -= EstimateMultimodalTokens(_entries[i].MultimodalContent);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(entryIndex, removeCount);
        }

        /// <summary>
        /// 移除最后一个 user 消息及其之后的所有条目，同时清除不属于任何轮次的 custom 消息
        /// （如 skill 指令）。用于重试/编辑回退场景。
        /// </summary>
        /// <summary>
        /// 移除最后一条助手消息（用于流式取消时不保存不完整回复）。
        /// </summary>
        public void RemoveLastAssistantMessage()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "assistant")
                {
                    _estimatedTokens -= EstimateTokens(_entries[i].Content);
                    if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                        _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
                    _entries.RemoveAt(i);
                    return;
                }
            }
        }

        #endregion

        #region Core API — Token 管理

        /// <summary>
        /// 估算文本的 Token 数。
        /// 改进版：1 英文字符 ≈ 0.3 token，1 中文字符 ≈ 0.6 token，
        /// 1 数字/符号 ≈ 0.3 token，CJK 标点 ≈ 0.6 token。
        /// </summary>
        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int chineseChars = 0;
            int otherChars = 0;
            foreach (char c in text!)
            {
                // CJK 统一表意文字 + CJK 扩展
                if ((c >= 0x4E00 && c <= 0x9FFF) ||
                    (c >= 0x3400 && c <= 0x4DBF) ||
                    (c >= 0x20000 && c <= 0x2A6DF))
                {
                    chineseChars++;
                }
                // CJK 标点符号
                else if ((c >= 0x3000 && c <= 0x303F) ||
                         (c >= 0xFF00 && c <= 0xFFEF))
                {
                    chineseChars++;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    otherChars++;
                }
            }
            // 1 中文 ≈ 0.6 token, 1 英文/数字 ≈ 0.3 token
            return (int)(chineseChars * 0.6 + otherChars * 0.3) + 1;
        }

        /// <summary>
        /// 估算消息列表的 Token 数。与持久化统计不同，这里包含
        /// tool_calls 的函数名和参数，适用于 Handoff 转发快照。
        /// </summary>
        public static int EstimateMessageTokens(IEnumerable<ChatApiMessage>? messages)
        {
            if (messages == null) return 0;

            int tokens = 0;
            foreach (var message in messages)
            {
                if (message == null) continue;

                tokens += EstimateTokens(message.Content);
                tokens += EstimateMultimodalTokens(message.MultimodalContent);
                tokens += EstimateTokens(message.ReasoningContent);
                if (message.ToolCalls == null) continue;

                foreach (var toolCall in message.ToolCalls)
                {
                    tokens += EstimateTokens(toolCall.Function?.Name);
                    tokens += EstimateTokens(toolCall.Function?.Arguments);
                }
            }

            return tokens;
        }

        private static int EstimateMultimodalTokens(List<ChatContentPart>? parts)
        {
            if (parts == null || parts.Count == 0)
                return 0;

            int tokens = 0;
            foreach (var part in parts)
            {
                if (part.Type == "text")
                {
                    tokens += EstimateTokens(part.Text);
                }
                else if (part.Type == "image_url")
                {
                    // 图片按尺寸/张数估算，绝不把 base64 data URI 当文本字符计数
                    tokens += EstimateImageTokens(part.ImageUrl?.Url);
                }
                else if (part.Type == "file")
                {
                    // file_id 引用极小；file_data 是 base64 数据，同样不能当文本计数
                    tokens += EstimateTokens(part.File?.FileId);
                    if (part.File?.FileData != null)
                        tokens += EstimateImageTokens(part.File.FileData);
                    tokens += EstimateTokens(part.File?.Filename);
                }
                else
                {
                    // 未知类型兜底
                    tokens += EstimateTokens(part.Text);
                }
            }
            return tokens;
        }

        /// <summary>
        /// 估算单张图片的 token 数，依据 DeepSeek Vision 文档的尺寸换算规则：
        /// 推理前会缩放，使有效面积落在约 [384×384, 800×800] 之间，上限约 384 token/张。
        /// https://api-docs.deepseek.com/guides/vision/#token-usage
        /// </summary>
        private static int EstimateImageTokens(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return 0;

            const int tokMin = 85;
            const int tokMax = 384;
            const double minArea = 384.0 * 384.0;
            const double maxArea = 800.0 * 800.0;
            const double pixelsPerToken = 1667.0;

            // 外链 http(s) URL：拿不到本地尺寸，按中等图片估算
            if (!imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return 256;

            int comma = imageUrl.IndexOf(',');
            if (comma < 0 || comma + 1 >= imageUrl.Length)
                return 256;

            try
            {
                byte[] bytes = Convert.FromBase64String(imageUrl.Substring(comma + 1));
                if (TryGetImageDimensions(bytes, out int width, out int height))
                {
                    double area = (double)width * height;
                    double effectiveArea = Math.Min(maxArea, Math.Max(minArea, area));
                    int tok = (int)Math.Round(effectiveArea / pixelsPerToken);
                    return Math.Max(tokMin, Math.Min(tokMax, tok));
                }
            }
            catch
            {
                // 解析失败按中等图片兜底，避免计数异常
            }

            return 256;
        }

        /// <summary>
        /// 从图像字节中解析宽高。支持 PNG / JPEG / GIF / WebP(VP8X/VP8L)；失败返回 false。
        /// </summary>
        private static bool TryGetImageDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (b == null || b.Length < 24) return false;

            // PNG：签名 + IHDR，宽高在 offset 16/20（大端）
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return width > 0 && height > 0;
            }

            // GIF：宽高在 offset 6/8（小端）
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b.Length >= 10)
            {
                width = b[6] | (b[7] << 8);
                height = b[8] | (b[9] << 8);
                return width > 0 && height > 0;
            }

            // JPEG：FF D8 FF，扫描 SOF 标记
            if (b[0] == 0xFF && b[1] == 0xD8)
                return TryGetJpegDimensions(b, out width, out height);

            // WebP：RIFF .... WEBP
            if (b.Length >= 30
                && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
                return TryGetWebPDimensions(b, out width, out height);

            return false;
        }

        private static bool TryGetJpegDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }            // 填充字节
                if (marker == 0xD9) break;                        // EOI
                if (marker == 0xD8 || marker == 0x01) { i += 2; continue; }
                if (marker >= 0xD0 && marker <= 0xD7) { i += 2; continue; } // RSTn

                int segLen = (b[i + 2] << 8) | b[i + 3];
                if (segLen < 2) break;

                // SOF0~SOF15，排除 DHT(0xC4)/JPG(0xC8)/DAC(0xCC)
                bool isSof = marker >= 0xC0 && marker <= 0xCF
                    && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    return width > 0 && height > 0;
                }

                i += 2 + segLen;
            }
            return false;
        }

        private static bool TryGetWebPDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;

            // VP8X（扩展）：canvas 尺寸在 offset 24/27（24 位小端，值为尺寸-1）
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
            {
                width = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                height = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                return width > 0 && height > 0;
            }

            // VP8L（无损）：chunk payload 首字节 0x2F，随后 4 字节小端含 14bit 宽-1 / 高-1
            if (b.Length >= 26 && b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
            {
                int v = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                width = (v & 0x3FFF) + 1;
                height = ((v >> 14) & 0x3FFF) + 1;
                return width > 0 && height > 0;
            }

            return false;
        }

        private static List<ChatContentPart>? CloneContentParts(List<ChatContentPart>? parts)
        {
            if (parts == null || parts.Count == 0)
                return null;

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

        /// <summary>
        /// 当估算 Token 超过预算时，自动修剪最旧的轮次（旧行为，无压缩服务时使用）。
        /// 使用校准后估算值作为阈值比较，确保在 API 真实 token 消耗接近预算时触发。
        /// </summary>
        public void AutoTrimIfNeeded()
        {
            while (EstimatedTokens > TokenBudget && TurnCount > AutoTrimTurns + 1)
            {
                TrimOldestTurn();
            }
        }

        /// <summary>
        /// 移除最旧的一轮对话（一个 user 消息 + 其后续的 assistant/tool 消息）。
        /// 仅在无压缩服务时作为回退使用。
        /// </summary>
        public void TrimOldestTurn()
        {
            // 找到第一个 user 消息
            int startIdx = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user" && _entries[i].TurnIndex > 0)
                {
                    startIdx = i;
                    break;
                }
            }

            if (startIdx < 0) return;

            int firstTurnIndex = _entries[startIdx].TurnIndex;

            // 移除从 startIdx 到下一个 user 消息（不含）之间的所有条目
            int endIdx = _entries.Count;
            for (int i = startIdx + 1; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user")
                {
                    endIdx = i;
                    break;
                }
            }

            // 扣除 token
            for (int i = startIdx; i < endIdx; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                _estimatedTokens -= EstimateMultimodalTokens(_entries[i].MultimodalContent);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(startIdx, endIdx - startIdx);
        }

        /// <summary>
        /// 获取上下文统计信息。
        /// </summary>
        public ContextStats GetStats()
        {
            var stats = new ContextStats
            {
                EstimatedTokens = EstimatedTokens,
                TokenBudget = TokenBudget,
                MessageCount = MessageCount,
                TurnCount = TurnCount,
                ToolCallCount = ToolCallCount,
                SystemPromptTokens = EstimateTokens(_systemPrompt),
                SearchContextTokens = EstimateTokens(_searchContext),
                CompressedTurns = _compressor?.CompressedSummaries.Count ?? 0,
                CompressedSummaryTokens = _compressor?.TotalCompressedTokens ?? 0,
            };

            // 统计工具调用结果 token
            stats.ToolResultTokens = _entries
                .Where(e => e.Role == "tool")
                .Sum(e => EstimateTokens(e.Content));

            return stats;
        }

        /// <summary>
        /// 使用 API 返回的实际 prompt_tokens 校准本地字符级估算。
        /// 使用指数移动平均 (EMA) 平滑校准系数，避免单次波动。
        /// 应在每次 Chat API 调用完成后调用。
        /// </summary>
        /// <param name="actualPromptTokens">API usage 中的实际 prompt_tokens</param>
        public void CalibrateFromApiUsage(long actualPromptTokens)
        {
            if (actualPromptTokens <= 0 || _estimatedTokens <= 0) return;

            // 计算本次调用的实际/估算比率
            double ratio = (double)actualPromptTokens / _estimatedTokens;

            // 合理性检查：比率在 0.2 ~ 10 之间才参与校准（过滤异常值）
            if (ratio < 0.2 || ratio > 10.0) return;

            // 指数移动平均：newFactor = oldFactor * (1 - α) + ratio * α
            _calibrationFactor = _calibrationFactor * (1 - CalibrationAlpha) + ratio * CalibrationAlpha;

            Logger.Info($"[ContextCalibration] API prompt_tokens={actualPromptTokens}, " +
                        $"rawEstimate={_estimatedTokens}, ratio={ratio:F3}, " +
                        $"newFactor={_calibrationFactor:F3}");
        }

        #endregion

        #region Core API — 查询与序列化

        /// <summary>
        /// 获取所有 user/assistant 角色的原始消息列表（用于持久化和 UI）。
        /// </summary>
        public List<ChatApiMessage> GetConversationHistory()
        {
            return _entries
                .Where(e => e.Role == "user" || e.Role == "assistant")
                .Select(e => new ChatApiMessage
                {
                    Role = e.Role,
                    Content = e.Content,
                    MultimodalContent = CloneContentParts(e.MultimodalContent),
                    ReasoningContent = e.ReasoningContent,
                })
                .ToList();
        }

        /// <summary>
        /// 获取包含所有角色（含 tool）的完整消息列表，用于完整持久化。
        /// 恢复时使用 RestoreFullContext()。
        /// </summary>
        public List<ChatApiMessage> GetFullContext()
        {
            return _entries
                .Select(e => new ChatApiMessage
                {
                    Role = e.Role,
                    Content = e.Content,
                    MultimodalContent = CloneContentParts(e.MultimodalContent),
                    ReasoningContent = e.ReasoningContent,
                    ToolCalls = e.ToolCalls?.Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new ToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments,
                        }
                    }).ToList(),
                    ToolCallId = e.ToolCallId,
                    Name = e.Name,
                })
                .ToList();
        }

        /// <summary>
        /// 从完整消息列表恢复上下文（含 tool 消息）。
        /// </summary>
        public void RestoreFullContext(List<ChatApiMessage> fullHistory)
        {
            Clear();
            foreach (var msg in fullHistory)
            {
                switch (msg.Role)
                {
                    case "user":
                        AddUserMessage(msg.Content ?? string.Empty, msg.MultimodalContent);
                        break;
                    case "assistant":
                        AddAssistantMessage(msg.Content, msg.ReasoningContent, msg.ToolCalls);
                        break;
                    case "tool":
                        if (!string.IsNullOrEmpty(msg.ToolCallId))
                            AddToolResult(msg.ToolCallId!, msg.Name ?? "unknown", msg.Content ?? string.Empty);
                        break;
                    case "system":
                        AddCustomMessage("system", msg.Content ?? string.Empty);
                        break;
                }
            }
        }

        /// <summary>
        /// 从持久化的 ChatApiMessage 列表恢复上下文。
        /// 用于会话切换时重建上下文。
        /// </summary>
        public void RestoreFromHistory(List<ChatApiMessage> history)
        {
            Clear();
            int turnIdx = 0;
            foreach (var msg in history)
            {
                if (msg.Role == "user")
                {
                    turnIdx++;
                    AddUserMessage(msg.Content ?? string.Empty, msg.MultimodalContent);
                }
                else if (msg.Role == "assistant")
                {
                    AddAssistantMessage(msg.Content, msg.ReasoningContent);
                }
            }
        }

        /// <summary>
        /// 清空所有上下文。
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            _estimatedTokens = 0;
            _systemPrompt = null;
            _fixedSystemPrompt = null;
            _searchContext = null;
            _ideContext = null;
            _solutionContext = null;
            _skillContext = null;
            _alwaysInjectSkillsContext = null;
            _ragContext = null;
            _memoryContext = null;
            _compressor?.Clear();
            _pendingCompressedSummaries.Clear();
            _cacheSnapshotEntryIndex = null;
            _cachedDynamicBlock = null;
            _nextEntryId = 0;
            _lastCompressedEntryId = 0;
            _conversationResetNoticePending = false;
        }

        /// <summary>
        /// 丢弃对话历史与压缩摘要，但保留 system prompt、记忆、RAG、IDE 等系统级上下文。
        /// 用于用户忽略“切换新对话”提示后继续发送消息的场景。
        /// </summary>
        public void ClearConversationHistory()
        {
            _entries.Clear();
            _estimatedTokens = 0;
            _fullToolResultStore.Clear();
            _compressor?.Clear();
            _pendingCompressedSummaries.Clear();
            _cacheSnapshotEntryIndex = null;
            _cachedDynamicBlock = null;
            _nextEntryId = 0;
            _lastCompressedEntryId = 0;
            _conversationResetNoticePending = false;
        }

        /// <summary>
        /// 获取对话历史的调试摘要。
        /// </summary>
        public string GetDebugSummary()
        {
            var sb = new StringBuilder();
            var stats = GetStats();
            sb.AppendLine($"=== 上下文管理器状态 ===");
            sb.AppendLine($"消息总数: {MessageCount}");
            sb.AppendLine($"轮次数: {TurnCount}");
            sb.AppendLine($"估算 Token: {_estimatedTokens:N0}/{TokenBudget:N0} ({UsagePercent:F1}%)");
            sb.AppendLine($"系统提示词: {(_systemPrompt != null ? $"{_systemPrompt.Length} 字符 ({stats.SystemPromptTokens} tokens)" : "无")}");
            sb.AppendLine($"搜索上下文: {(_searchContext != null ? $"{_searchContext.Length} 字符 ({stats.SearchContextTokens} tokens)" : "无")}");
            sb.AppendLine($"Skill 上下文: {(_skillContext != null ? $"{_skillContext.Length} 字符" : "无")}");
            sb.AppendLine($"始终注入 Skill: {(_alwaysInjectSkillsContext != null ? $"{_alwaysInjectSkillsContext.Length} 字符" : "无")}");
            sb.AppendLine($"RAG 上下文: {(_ragContext != null ? $"{_ragContext.Length} 字符" : "无")}");
            sb.AppendLine($"压缩摘要: {stats.CompressedTurns} 组 ({stats.CompressedSummaryTokens} tokens)");
            sb.AppendLine();
            foreach (var entry in _entries)
            {
                string preview = (entry.Content ?? "").Length > 80
                    ? (entry.Content ?? "").Substring(0, 80) + "..."
                    : (entry.Content ?? "");
                string reasoning = !string.IsNullOrEmpty(entry.ReasoningContent) ? " [含思维链]" : "";
                string tools = entry.HasToolCalls ? $" [工具调用:{entry.ToolCalls?.Count}]" : "";
                sb.AppendLine($"[T{entry.TurnIndex}] {entry.Role}: {preview}{reasoning}{tools}");
            }
            return sb.ToString();
        }

        #endregion

        #region Internal Helpers

        /// <summary>
        /// 组装最终的系统提示词（用户自定义 + Skill 上下文）。
        /// </summary>
        private string? BuildFinalSystemPrompt()
        {
            if (string.IsNullOrWhiteSpace(_systemPrompt) && string.IsNullOrWhiteSpace(_skillContext) && string.IsNullOrWhiteSpace(_alwaysInjectSkillsContext))
                return null;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_systemPrompt))
                parts.Add(_systemPrompt);
            if (!string.IsNullOrWhiteSpace(_skillContext))
                parts.Add(_skillContext);
            if (!string.IsNullOrWhiteSpace(_alwaysInjectSkillsContext))
                parts.Add(_alwaysInjectSkillsContext);

            return string.Join("\n\n", parts);
        }

        private static string? CombineSystemParts(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return first + "\n\n" + second;
        }

        #endregion

        #region Inner Types

        /// <summary>
        /// 上下文条目 — 内部存储单元，比 ChatApiMessage 更丰富。
        /// 对 ContextCompressorService 可见以支持压缩操作。
        /// </summary>
        internal class ContextEntry
        {
            /// <summary>单调递增条目 ID，用于判断压缩边界中的新内容</summary>
            public long EntryId { get; set; }
            public string Role { get; set; } = "user";
            public string? Content { get; set; }
            public List<ChatContentPart>? MultimodalContent { get; set; }
            public string? ReasoningContent { get; set; }
            public List<ToolCall>? ToolCalls { get; set; }
            public bool HasToolCalls { get; set; }
            public string? ToolCallId { get; set; }
            public string? Name { get; set; }
            /// <summary>所属轮次（1-based），-1 表示不属于任何轮次</summary>
            public int TurnIndex { get; set; } = -1;
            /// <summary>是否为固化到历史中的 volatile 上下文快照</summary>
            public bool IsVolatileSnapshot { get; set; }
        }

        #endregion
    }
}
