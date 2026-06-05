using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>Skill 发现上下文（独立存储）</summary>
        private string? _skillContext;

        /// <summary>RAG 检索上下文（独立存储，注入为 system 消息）</summary>
        private string? _ragContext;

        /// <summary>记忆上下文（独立存储，注入为 system 消息）</summary>
        private string? _memoryContext;

        /// <summary>当前 Token 估算计数器（字符级原始估算，未校准）</summary>
        private int _estimatedTokens;

        /// <summary>Token 估算校准系数（基于 API 实际 usage 的指数移动平均，1.0 = 无校准）</summary>
        private double _calibrationFactor = 1.0;

        /// <summary>校准样本权重（EMA α 值，0.15 约等于最近 ~7 次调用的加权）</summary>
        private const double CalibrationAlpha = 0.15;

        /// <summary>上下文压缩服务（可选注入）</summary>
        private ContextCompressorService? _compressor;

        /// <summary>压缩标记：是否正在压缩中</summary>
        private bool _isCompressing;

        /// <summary>
        /// 前缀缓存稳定性管理器（可选注入）。
        /// 用于监控 system prompt 和 tool catalog 的 SHA-256 指纹变化，
        /// 保障 DeepSeek V4 自动前缀缓存命中率。
        /// </summary>
        private PrefixCacheManager? _prefixCacheManager;

        /// <summary>Token 预算上限（默认 900K，DeepSeek V4 上下文窗口为 1M，留 100K 给输出）</summary>
        public int TokenBudget { get; set; } = 900_000;

        /// <summary>当超过 Token 预算时自动压缩的最旧轮次数（此值在压缩模式下仅作用为最小保留轮次）</summary>
        public int AutoTrimTurns { get; set; } = 3;

        /// <summary>获取当前对话轮次数（一个 user 消息 = 一轮）</summary>
        public int TurnCount => _entries.Count(e => e.Role == "user");

        /// <summary>获取消息总条数</summary>
        public int MessageCount => _entries.Count;

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

        /// <summary>获取前缀缓存管理器（null = 未注入）</summary>
        public PrefixCacheManager? PrefixCache => _prefixCacheManager;

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
        /// 设置 Skill 发现上下文。
        /// </summary>
        public void SetSkillContext(string? skillContext)
        {
            _skillContext = skillContext;
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
        }

        /// <summary>
        /// 注入前缀缓存管理器。
        /// 设置后，BuildApiMessages 时会自动检查前缀稳定性并记录漂移事件。
        /// </summary>
        public void SetPrefixCache(PrefixCacheManager? prefixCache)
        {
            _prefixCacheManager = prefixCache;
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
        {
            if (string.IsNullOrEmpty(content)) return;

            // ── 安全净化：防止工具注入标记进入上下文 ──
            content = StringExtensions.SanitizeUserInput(content);

            _entries.Add(new ContextEntry
            {
                Role = "user",
                Content = content,
                TurnIndex = TurnCount + 1, // 新轮次
            });
            _estimatedTokens += EstimateTokens(content);

            // 使用压缩替代直接删除
            if (_compressor != null && _compressor.Config.AutoCompressEnabled)
                AutoCompressIfNeeded();
            else
                AutoTrimIfNeeded();
        }

        /// <summary>
        /// 添加用户消息的异步版本 — 支持异步压缩（需要 LLM 调用时）。
        /// </summary>
        public async Task AddUserMessageAsync(string content, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(content)) return;

            // ── 安全净化：防止工具注入标记进入上下文 ──
            content = StringExtensions.SanitizeUserInput(content);

            _entries.Add(new ContextEntry
            {
                Role = "user",
                Content = content,
                TurnIndex = TurnCount + 1,
            });
            _estimatedTokens += EstimateTokens(content);

            if (_compressor != null && _compressor.Config.AutoCompressEnabled)
                await AutoCompressIfNeededAsync(cancellationToken);
            else
                AutoTrimIfNeeded();
        }

        /// <summary>
        /// 添加助手消息。
        /// </summary>
        /// <param name="content">回复内容</param>
        /// <param name="reasoningContent">思维链内容（思考模式下）</param>
        /// <param name="toolCalls">工具调用列表（可为 null）</param>
        public void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null)
        {
            _entries.Add(new ContextEntry
            {
                Role = "assistant",
                Content = content,
                ReasoningContent = reasoningContent,
                ToolCalls = toolCalls,
                HasToolCalls = toolCalls != null && toolCalls.Count > 0,
                TurnIndex = TurnCount, // 属于当前轮次
            });
            _estimatedTokens += EstimateTokens(content);
            if (!string.IsNullOrEmpty(reasoningContent))
                _estimatedTokens += EstimateTokens(reasoningContent);
        }

        /// <summary>
        /// 添加工具调用结果（tool 角色消息）。
        /// </summary>
        public void AddToolResult(string toolCallId, string toolName, string result)
        {
            _entries.Add(new ContextEntry
            {
                Role = "tool",
                Content = result,
                ToolCallId = toolCallId,
                Name = toolName,
                TurnIndex = TurnCount, // 工具调用属于当前轮次
            });
            _estimatedTokens += EstimateTokens(result);
        }

        /// <summary>
        /// 添加自定义角色消息（如 skill 指令注入的 system 消息）。
        /// 这些消息不计入 turn 管理。
        /// </summary>
        public void AddCustomMessage(string role, string content)
        {
            if (string.IsNullOrEmpty(content)) return;

            _entries.Add(new ContextEntry
            {
                Role = role,
                Content = content,
                TurnIndex = -1, // 不属于任何轮次
            });
            _estimatedTokens += EstimateTokens(content);
        }

        #endregion

        #region Core API — 构建 API 消息

        /// <summary>
        /// 构建发送给 DeepSeek API 的完整消息列表。
        /// 正确处理 reasoning_content 回传规则。
        /// 
        /// ── 前缀缓存优化（v1.1.9）──
        /// 消息结构遵循"固定前缀 + 对话历史 + 动态块"三层模型：
        ///   messages[0] = 冻结的不可变系统提示词（整个会话固定不变）
        ///   messages[1..N-1] = 对话历史（仅追加，不修改）→ 前缀缓存可覆盖到此
        ///   messages[N] = 动态上下文块（压缩摘要 + 搜索 + RAG + 记忆）
        ///   messages[N+1] = 用户消息（调用方追加）
        /// 
        /// 将动态块放在对话历史之后、用户消息之前，使前缀缓存可以从 messages[0]
        /// 一直延伸到对话历史的末尾。动态块的变化只影响它自身及之后的消息，
        /// 对话历史的前缀在每次请求间保持稳定。
        /// 
        /// DeepSeek V4 规则：
        /// - 如果 assistant 消息没有 tool_calls：reasoning_content 不应回传（会被 API 忽略）
        /// - 如果 assistant 消息有 tool_calls：reasoning_content 必须回传（否则 400 错误）
        /// - 所有 tool 角色消息必须保留 tool_call_id 和 name
        /// </summary>
        public List<ChatApiMessage> BuildApiMessages()
        {
            var messages = new List<ChatApiMessage>();

            // ── 1. 不可变前缀：冻结的系统提示词（messages[0]，整个会话不改变）──
            string? fixedPrompt = _fixedSystemPrompt;
            if (!string.IsNullOrWhiteSpace(fixedPrompt))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = fixedPrompt });
            }
            else
            {
                // 回退：尚未冻结时使用动态拼接（兼容旧调用路径）
                string? fallbackPrompt = BuildFinalSystemPrompt();
                if (!string.IsNullOrWhiteSpace(fallbackPrompt))
                {
                    messages.Add(new ChatApiMessage { Role = "system", Content = fallbackPrompt });
                }
            }

            // ── 2. 遍历对话历史，正确构建消息 ──
            //     历史位于系统提示词之后，使前缀缓存可连续命中所有不变的对话消息
            foreach (var entry in _entries)
            {
                // 跳过没有内容的条目（除非有 tool_calls）
                if (string.IsNullOrEmpty(entry.Content) && (entry.ToolCalls == null || entry.ToolCalls.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                };

                // ── reasoning_content 回传规则 ──
                if (entry.Role == "assistant")
                {
                    if (entry.HasToolCalls)
                    {
                        // 有工具调用 → 必须回传 reasoning_content（即使为空字符串也要包含字段，避免 API 报 400）
                        apiMsg.ReasoningContent = entry.ReasoningContent ?? string.Empty;
                    }
                    // 无工具调用 → 不回传 reasoning_content（API 会忽略），保持为 null
                }

                // ── 工具调用相关字段 ──
                if (entry.Role == "assistant" && entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                {
                    apiMsg.ToolCalls = entry.ToolCalls;
                }

                if (entry.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(entry.ToolCallId))
                        apiMsg.ToolCallId = entry.ToolCallId;
                    if (!string.IsNullOrEmpty(entry.Name))
                        apiMsg.Name = entry.Name;
                }

                messages.Add(apiMsg);
            }

            // ── 3. 动态上下文块：放在对话历史之后、用户消息之前 ──
            //     将压缩摘要、搜索、RAG、记忆合并为一条 system 消息，放在历史末尾。
            //     这样对话历史的前缀完全稳定，动态变化只影响这一条及其后的用户消息。
            string? dynamicBlock = BuildDynamicContextBlock();
            if (!string.IsNullOrWhiteSpace(dynamicBlock))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = dynamicBlock });
            }

            // ── 4. 前缀缓存漂移检测（若有 PrefixCacheManager 注入）──
            //     注意：此处仅记录日志，不阻止请求。实际指纹对比需要 tool 列表，
            //     由调用方（DeepSeekApiService）在发送前完成。
            if (_prefixCacheManager != null && _prefixCacheManager.IsPinned)
            {
                string? currentFixedPrompt = _fixedSystemPrompt ?? BuildFinalSystemPrompt();
                if (!string.IsNullOrWhiteSpace(currentFixedPrompt))
                {
                    string spFp = PrefixCacheManager.ComputeSystemPromptFingerprint(currentFixedPrompt);
                    if (spFp != _prefixCacheManager.PinnedCombinedFingerprint?.Split('|').FirstOrDefault())
                    {
                        Logger.Warn($"[PrefixCache] System prompt 指纹与 pinned 基准不匹配，缓存可能失效");
                    }
                }
            }

            return messages;
        }

        /// <summary>
        /// 构建动态上下文块：合并压缩摘要、搜索上下文、RAG 上下文、记忆上下文。
        /// 所有动态内容合并为一段文本，作为单条 system 消息注入。
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

            // 搜索上下文
            if (!string.IsNullOrWhiteSpace(_searchContext))
                parts.Add(_searchContext);

            // RAG 检索上下文
            if (!string.IsNullOrWhiteSpace(_ragContext))
                parts.Add(_ragContext);

            // 记忆上下文
            if (!string.IsNullOrWhiteSpace(_memoryContext))
                parts.Add(_memoryContext);

            if (parts.Count == 0)
                return null;

            return string.Join("\n\n", parts);
        }

        /// <summary>
        /// 构建仅包含最近 N 轮的 API 消息列表（用于 Agent 子调用）。
        /// 以 user 消息为轮次边界，保留完整的 tool 调用链。
        /// 前缀结构同 BuildApiMessages()：messages[0] = 冻结 prompt，历史在中间，动态块在末尾。
        /// </summary>
        /// <param name="maxTurns">保留的最大轮次数</param>
        public List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns)
        {
            if (TurnCount <= maxTurns)
                return BuildApiMessages();

            // 找到需要保留的起始 user 消息（倒数第 maxTurns 个）
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

            // 构建截断后的消息列表（前缀结构与 BuildApiMessages 一致）
            var messages = new List<ChatApiMessage>();

            // ── 1. 不可变前缀 ──
            string? fixedPrompt = _fixedSystemPrompt;
            if (!string.IsNullOrWhiteSpace(fixedPrompt))
                messages.Add(new ChatApiMessage { Role = "system", Content = fixedPrompt });
            else
            {
                string? fallbackPrompt = BuildFinalSystemPrompt();
                if (!string.IsNullOrWhiteSpace(fallbackPrompt))
                    messages.Add(new ChatApiMessage { Role = "system", Content = fallbackPrompt });
            }

            // ── 2. 从 startEntryIdx 开始构建对话历史 ──
            for (int i = startEntryIdx; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (string.IsNullOrEmpty(entry.Content) && (entry.ToolCalls == null || entry.ToolCalls.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                };

                if (entry.Role == "assistant" && entry.HasToolCalls)
                    apiMsg.ReasoningContent = entry.ReasoningContent ?? string.Empty;

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

            // ── 3. 动态上下文块（放在历史之后）──
            string? dynamicBlock = BuildDynamicContextBlock();
            if (!string.IsNullOrWhiteSpace(dynamicBlock))
                messages.Add(new ChatApiMessage { Role = "system", Content = dynamicBlock });

            return messages;
        }

        /// <summary>
        /// 克隆当前上下文（用于 Agent 并行调用时的隔离）。
        /// </summary>
        public ConversationContextManager Clone()
        {
            var clone = new ConversationContextManager
            {
                _systemPrompt = _systemPrompt,
                _fixedSystemPrompt = _fixedSystemPrompt,
                _searchContext = _searchContext,
                _skillContext = _skillContext,
                _ragContext = _ragContext,
                _memoryContext = _memoryContext,
                _compressor = _compressor,
                _prefixCacheManager = _prefixCacheManager,
                _estimatedTokens = _estimatedTokens,
                _calibrationFactor = _calibrationFactor,
                TokenBudget = TokenBudget,
                AutoTrimTurns = AutoTrimTurns,
            };
            clone._entries.AddRange(_entries.Select(e => e.Clone()));
            return clone;
        }

        #endregion

        #region Core API — 撤销与截断

        /// <summary>
        /// 移除指定索引之后的所有消息（用于重试/编辑场景）。
        /// </summary>
        /// <param name="entryIndex">条目在内部 _entries 列表中的索引</param>
        public void TrimAfter(int entryIndex)
        {
            if (entryIndex < 0 || entryIndex >= _entries.Count) return;

            int removeCount = _entries.Count - entryIndex;

            // 重新计算被移除部分的 token
            for (int i = entryIndex; i < _entries.Count; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(entryIndex, removeCount);
        }

        /// <summary>
        /// 移除最后一个 user 消息及其之后的所有条目，同时清除不属于任何轮次的 custom 消息
        /// （如 skill 指令）。用于重试/编辑回退场景。
        /// </summary>
        public void TrimAfterLastUserMessage()
        {
            // 从末尾向前找到最后一个 user 消息
            int lastUserIdx = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "user")
                {
                    lastUserIdx = i;
                    break;
                }
            }

            if (lastUserIdx < 0) return;

            // 同时移除所有 TurnIndex=-1 的 custom 消息（如 skill 指令），
            // 避免重试时残留旧的系统指令
            var toRemove = new List<int>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].TurnIndex == -1)
                    toRemove.Add(i);
            }
            // 从后往前移除，避免索引偏移
            for (int i = toRemove.Count - 1; i >= 0; i--)
            {
                int idx = toRemove[i];
                _estimatedTokens -= EstimateTokens(_entries[idx].Content);
                _entries.RemoveAt(idx);
                if (idx < lastUserIdx) lastUserIdx--; // 调整索引
            }

            TrimAfter(lastUserIdx);
        }

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
            foreach (char c in text)
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
        /// 当估算 Token 超过预算时，自动压缩最旧的轮次（新行为，有压缩服务时使用）。
        /// 同步版本：使用基于规则的本地压缩。使用校准后估算值作为阈值比较。
        /// </summary>
        public void AutoCompressIfNeeded()
        {
            if (_compressor == null || _isCompressing) return;

            double threshold = _compressor.Config.CompressionThreshold;
            int preserveTurns = _compressor.Config.PreserveRecentTurns;
            int minToCompress = _compressor.Config.MinTurnsToCompress;

            int targetBudget = (int)(TokenBudget * threshold);

            while (EstimatedTokens > targetBudget
                && TurnCount > preserveTurns + minToCompress)
            {
                _isCompressing = true;
                try
                {
                    CompressOldestTurnsSync(preserveTurns);
                }
                finally
                {
                    _isCompressing = false;
                }
            }
        }

        /// <summary>
        /// 当估算 Token 超过预算时，自动压缩最旧的轮次（异步版本，支持 LLM 摘要）。
        /// </summary>
        public async Task AutoCompressIfNeededAsync(CancellationToken cancellationToken = default)
        {
            if (_compressor == null || _isCompressing) return;

            double threshold = _compressor.Config.CompressionThreshold;
            int preserveTurns = _compressor.Config.PreserveRecentTurns;
            int minToCompress = _compressor.Config.MinTurnsToCompress;

            int targetBudget = (int)(TokenBudget * threshold);

            while (_estimatedTokens > targetBudget
                && TurnCount > preserveTurns + minToCompress)
            {
                _isCompressing = true;
                try
                {
                    await CompressOldestTurnsAsync(preserveTurns, cancellationToken);
                }
                finally
                {
                    _isCompressing = false;
                }
            }
        }

        /// <summary>
        /// 同步压缩最旧的轮次（基于规则的本地压缩）。
        /// </summary>
        private void CompressOldestTurnsSync(int preserveTurns)
        {
            if (_compressor == null) return;

            // 计算需要压缩的轮次范围
            int turnsToKeep = preserveTurns;
            int totalTurns = TurnCount;
            int compressUpTo = totalTurns - turnsToKeep;

            if (compressUpTo <= 0) return;

            // 收集要压缩的条目
            var entriesToCompress = _entries
                .Where(e => e.TurnIndex > 0 && e.TurnIndex <= compressUpTo)
                .ToList();

            if (entriesToCompress.Count == 0) return;

            // 移除要压缩的条目
            int removedTokens = 0;
            foreach (var entry in entriesToCompress)
            {
                removedTokens += EstimateTokens(entry.Content);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    removedTokens += EstimateTokens(entry.ReasoningContent);
                _entries.Remove(entry);
            }
            _estimatedTokens -= removedTokens;

            // 本地生成摘要（同步，无 LLM 调用）。
            // 使用 Task.Run 将压缩计算卸载到线程池，避免阻塞 UI 线程。
            var summary = System.Threading.Tasks.Task.Run(() =>
                _compressor.CompressTurnsAsync(
                    entriesToCompress, 1, compressUpTo)).GetAwaiter().GetResult();

            Logger.Info($"[ContextManager] 同步压缩第 1-{compressUpTo} 轮: " +
                $"{removedTokens} → {summary.CompressedTokens} tokens " +
                $"(压缩率 {summary.CompressionRatio:P0})");
        }

        /// <summary>
        /// 异步压缩最旧的轮次（支持 LLM 摘要）。
        /// </summary>
        private async Task CompressOldestTurnsAsync(int preserveTurns, CancellationToken cancellationToken)
        {
            if (_compressor == null) return;

            int totalTurns = TurnCount;
            int compressUpTo = totalTurns - preserveTurns;

            if (compressUpTo <= 0) return;

            var entriesToCompress = _entries
                .Where(e => e.TurnIndex > 0 && e.TurnIndex <= compressUpTo)
                .ToList();

            if (entriesToCompress.Count == 0) return;

            int removedTokens = 0;
            foreach (var entry in entriesToCompress)
            {
                removedTokens += EstimateTokens(entry.Content);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    removedTokens += EstimateTokens(entry.ReasoningContent);
                _entries.Remove(entry);
            }
            _estimatedTokens -= removedTokens;

            var summary = await _compressor.CompressTurnsAsync(
                entriesToCompress, 1, compressUpTo, cancellationToken);

            Logger.Info($"[ContextManager] 异步压缩第 1-{compressUpTo} 轮: " +
                $"{removedTokens} → {summary.CompressedTokens} tokens " +
                $"(压缩率 {summary.CompressionRatio:P0})");
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
                        AddUserMessage(msg.Content ?? string.Empty);
                        break;
                    case "assistant":
                        AddAssistantMessage(msg.Content, msg.ReasoningContent, msg.ToolCalls);
                        break;
                    case "tool":
                        if (!string.IsNullOrEmpty(msg.ToolCallId))
                            AddToolResult(msg.ToolCallId, msg.Name ?? "unknown", msg.Content ?? string.Empty);
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
                    AddUserMessage(msg.Content ?? string.Empty);
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
            _skillContext = null;
            _ragContext = null;
            _memoryContext = null;
            _compressor?.Clear();
            _prefixCacheManager?.Reset();
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
            if (string.IsNullOrWhiteSpace(_systemPrompt) && string.IsNullOrWhiteSpace(_skillContext))
                return null;

            if (string.IsNullOrWhiteSpace(_systemPrompt))
                return _skillContext;

            if (string.IsNullOrWhiteSpace(_skillContext))
                return _systemPrompt;

            return _systemPrompt + "\n\n" + _skillContext;
        }

        #endregion

        #region Inner Types

        /// <summary>
        /// 上下文条目 — 内部存储单元，比 ChatApiMessage 更丰富。
        /// 对 ContextCompressorService 可见以支持压缩操作。
        /// </summary>
        internal class ContextEntry
        {
            public string Role { get; set; } = "user";
            public string? Content { get; set; }
            public string? ReasoningContent { get; set; }
            public List<ToolCall>? ToolCalls { get; set; }
            public bool HasToolCalls { get; set; }
            public string? ToolCallId { get; set; }
            public string? Name { get; set; }
            /// <summary>所属轮次（1-based），-1 表示不属于任何轮次</summary>
            public int TurnIndex { get; set; } = -1;

            public ContextEntry Clone()
            {
                return new ContextEntry
                {
                    Role = Role,
                    Content = Content,
                    ReasoningContent = ReasoningContent,
                    ToolCalls = ToolCalls?.Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new ToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments,
                        }
                    }).ToList(),
                    HasToolCalls = HasToolCalls,
                    ToolCallId = ToolCallId,
                    Name = Name,
                    TurnIndex = TurnIndex,
                };
            }
        }

        #endregion
    }
}
