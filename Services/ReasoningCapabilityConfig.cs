using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Reasoning 参数能力描述 — 参考 CC Switch 的 CodexChatReasoningConfig 设计。
    ///
    /// 不同 chat/completions 兼容端点对"深度思考"的表达方式不同：
    ///   - DeepSeek 官方: thinking:{type} + reasoning_effort
    ///   - Qwen/DashScope/SiliconFlow/ModelScope: enable_thinking (bool)
    ///   - GLM/Zhipu/Z.ai: thinking:{type}
    ///   - Kimi/Moonshot: thinking:{type}，无 effort
    ///   - MiniMax: reasoning_split (bool)
    ///   - OpenAI o-series / GPT-5+ / Grok: reasoning_effort (顶层)
    ///   - OpenRouter: reasoning:{effort} 对象
    ///
    /// 默认未知端点采用保守策略：不注入 thinking 开关字段，
    /// 但仍透传 reasoning_effort（OpenAI 风格事实标准）。
    /// </summary>
    public sealed class ReasoningCapabilityConfig
    {
        /// <summary>是否发送 thinking 开关字段（thinking:{type} / enable_thinking / reasoning_split）。</summary>
        public bool SupportsThinking { get; init; }

        /// <summary>是否发送 effort 字段（reasoning_effort / reasoning.effort）。</summary>
        public bool SupportsEffort { get; init; }

        /// <summary>当前端点是否提供任何可配置的 reasoning 能力。</summary>
        public bool HasReasoningOptions => SupportsThinking || SupportsEffort;

        /// <summary>thinking 开关参数名: "thinking" | "enable_thinking" | "reasoning_split" | "none"。</summary>
        public string ThinkingParam { get; init; } = "none";

        /// <summary>effort 参数名: "reasoning_effort" | "reasoning.effort" | "none"。</summary>
        public string EffortParam { get; init; } = "none";

        /// <summary>
        /// effort 档位映射模式: "deepseek" | "openrouter" | "passthrough"。
        /// deepseek: max/xhigh/ultra → max; openrouter: max/ultra → xhigh;
        /// passthrough: 原值透传（未知端点默认）。
        /// </summary>
        public string EffortValueMode { get; init; } = "passthrough";

        /// <summary>
        /// 该模型是否要求 max_completion_tokens 而非 max_tokens
        /// （OpenAI o-series / GPT-5+ 强制，其余端点收到 max_completion_tokens 可能报 400）。
        /// </summary>
        public bool UseMaxCompletionTokens { get; init; }

        /// <summary>
        /// 该模型是否拒绝采样参数（temperature 等）。
        /// OpenAI o-series / GPT-5 仅接受默认采样值，发送 temperature 会 400。
        /// </summary>
        public bool RejectsSamplingParams { get; init; }

        /// <summary>
        /// 流式请求是否需要显式 stream_options.include_usage 才返回 usage chunk。
        /// DeepSeek 官方默认返回；OpenAI/Kimi/vLLM 等必须显式声明。
        /// 缺失会导致流式 token/费用统计为 0。
        /// </summary>
        public bool NeedsStreamOptionsForUsage { get; init; }

        /// <summary>
        /// 模型无法关闭思考（例如 GLM Flash / Kingsoft）时，禁用请求也应发送
        /// 一个低强度 effort，而不是发送 thinking=disabled。
        /// </summary>
        public bool AlwaysThinking { get; init; }

        /// <summary>默认配置：不发 thinking 开关，透传 reasoning_effort。</summary>
        public static ReasoningCapabilityConfig Default { get; } = new()
        {
            SupportsThinking = false,
            SupportsEffort = true,
            ThinkingParam = "none",
            EffortParam = "reasoning_effort",
            EffortValueMode = "passthrough",
            NeedsStreamOptionsForUsage = true,
        };

        /// <summary>DeepSeek 官方端点（本扩展的原生形态）。</summary>
        public static ReasoningCapabilityConfig DeepSeek { get; } = new()
        {
            SupportsThinking = true,
            SupportsEffort = true,
            ThinkingParam = "thinking",
            EffortParam = "reasoning_effort",
            EffortValueMode = "deepseek",
            NeedsStreamOptionsForUsage = false,
        };

        /// <summary>
        /// 根据端点 Base URL 和模型名推断 reasoning 能力配置。
        /// 参考 CC Switch infer_codex_chat_reasoning_config 的分层匹配策略：
        /// 先按聚合平台标识（仅 baseUrl）判定，再按模型名/厂商判定。
        /// </summary>
        public static ReasoningCapabilityConfig Infer(string? baseUrl, string? model)
        {
            var url = (baseUrl ?? string.Empty).ToLowerInvariant();
            var mdl = (model ?? string.Empty).ToLowerInvariant();
            var haystack = $"{url} {mdl}";

            bool isOSeries = IsOpenAiOSeries(mdl);
            bool isGpt5Plus = mdl.StartsWith("gpt-5");
            bool strictSampling = isOSeries || isGpt5Plus;

            // ── 聚合平台：仅按 baseUrl 判定（同一模型在不同平台参数可能完全不同） ──

            // DeepSeek 官方
            if (url.Contains("api.deepseek.com"))
                return DeepSeek;

            // OpenRouter: 原生 reasoning:{effort} 对象，不认 thinking:{type}
            if (url.Contains("openrouter.ai"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = false,
                    SupportsEffort = true,
                    ThinkingParam = "none",
                    EffortParam = "reasoning.effort",
                    EffortValueMode = "openrouter",
                    NeedsStreamOptionsForUsage = true,
                };

            // SiliconFlow / ModelScope: 平台级 enable_thinking 布尔，无 effort
            if (url.Contains("siliconflow") || url.Contains("modelscope"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = true,
                    SupportsEffort = false,
                    ThinkingParam = "enable_thinking",
                    EffortParam = "none",
                    NeedsStreamOptionsForUsage = true,
                };

            // ── 模型/厂商规则 ──

            // Kimi / Moonshot: thinking:{type}，无 effort
            if (haystack.Contains("kimi") || haystack.Contains("moonshot"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = true,
                    SupportsEffort = false,
                    ThinkingParam = "thinking",
                    EffortParam = "none",
                    NeedsStreamOptionsForUsage = true,
                };

            // GLM Flash / Kingsoft 常见为始终思考模型：不接受 disabled，
            // 需要 reasoning_effort ∈ {low, high, max}。
            if (mdl.Contains("glm") && mdl.Contains("flash"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = false,
                    SupportsEffort = true,
                    ThinkingParam = "none",
                    EffortParam = "reasoning_effort",
                    EffortValueMode = "passthrough",
                    AlwaysThinking = true,
                    NeedsStreamOptionsForUsage = true,
                };

            // GLM / Zhipu / Z.ai: thinking:{type}，无 effort
            if (haystack.Contains("glm") || haystack.Contains("zhipu") || haystack.Contains("bigmodel"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = true,
                    SupportsEffort = false,
                    ThinkingParam = "thinking",
                    EffortParam = "none",
                    NeedsStreamOptionsForUsage = true,
                };

            // Qwen / DashScope / 百炼: enable_thinking 布尔，无 effort
            if (haystack.Contains("qwen") || haystack.Contains("dashscope") || haystack.Contains("aliyuncs"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = true,
                    SupportsEffort = false,
                    ThinkingParam = "enable_thinking",
                    EffortParam = "none",
                    NeedsStreamOptionsForUsage = true,
                };

            // MiniMax: reasoning_split 布尔，无 effort
            if (haystack.Contains("minimax"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = true,
                    SupportsEffort = false,
                    ThinkingParam = "reasoning_split",
                    EffortParam = "none",
                    NeedsStreamOptionsForUsage = true,
                };

            // DeepSeek 模型经第三方端点: 沿用 DeepSeek 原生参数形态，
            // 但流式 usage 需按目标端点声明（第三方不一定默认返回）
            if (mdl.Contains("deepseek"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = true,
                    SupportsEffort = true,
                    ThinkingParam = "thinking",
                    EffortParam = "reasoning_effort",
                    EffortValueMode = "deepseek",
                    NeedsStreamOptionsForUsage = true,
                };

            // OpenAI o-series / GPT-5+ / Grok: 顶层 reasoning_effort
            if (isOSeries || isGpt5Plus || mdl.Contains("grok"))
                return new ReasoningCapabilityConfig
                {
                    SupportsThinking = false,
                    SupportsEffort = true,
                    ThinkingParam = "none",
                    EffortParam = "reasoning_effort",
                    EffortValueMode = "passthrough",
                    UseMaxCompletionTokens = strictSampling,
                    RejectsSamplingParams = strictSampling,
                    NeedsStreamOptionsForUsage = true,
                };

            // 未知端点: 保守默认（不发 thinking 开关，透传 reasoning_effort）
            return Default;
        }

        /// <summary>OpenAI o-series 检测（o1/o3/o4-mini 等：o + 数字开头）。参考 CC Switch is_openai_o_series。</summary>
        private static bool IsOpenAiOSeries(string model)
            => model.Length > 1 && model[0] == 'o' && model[1] >= '0' && model[1] <= '9';

        /// <summary>
        /// 映射 effort 档位值到该平台合法枚举。
        /// 参考 CC Switch map_reasoning_effort：扩展档位（max/xhigh/ultra）
        /// 按平台合法枚举钳制而非丢弃。
        /// </summary>
        /// <param name="effort">请求的 effort 档位（如 "high" / "max"）</param>
        /// <returns>映射后的档位；null 表示该平台不支持 effort，不应发送</returns>
        public string? MapEffort(string? effort)
        {
            if (string.IsNullOrWhiteSpace(effort) || !SupportsEffort || EffortParam == "none")
                return null;

            var normalized = effort.Trim().ToLowerInvariant();
            if (normalized is "none" or "off" or "disabled")
                return null;

            return EffortValueMode switch
            {
                // DeepSeek 官方枚举: high | max
                "deepseek" => normalized is "max" or "xhigh" or "ultra" ? "max" : "high",

                // OpenRouter 枚举: xhigh | high | medium | low | minimal（无 max）
                "openrouter" => normalized switch
                {
                    "max" or "xhigh" or "ultra" => "xhigh",
                    "high" => "high",
                    "medium" => "medium",
                    "low" => "low",
                    "minimal" => "minimal",
                    _ => "high",
                },

                // 未知端点透传（OpenAI 风格: low | medium | high；扩展档原样转发）
                _ => normalized,
            };
        }

        /// <summary>
        /// 判断该配置是否与本实例等价（用于日志去噪）。
        /// </summary>
        public bool SameAs(ReasoningCapabilityConfig other)
            => other != null
                && SupportsThinking == other.SupportsThinking
                && SupportsEffort == other.SupportsEffort
                && string.Equals(ThinkingParam, other.ThinkingParam, StringComparison.Ordinal)
                && string.Equals(EffortParam, other.EffortParam, StringComparison.Ordinal)
                && string.Equals(EffortValueMode, other.EffortValueMode, StringComparison.Ordinal)
                && AlwaysThinking == other.AlwaysThinking;
    }
}
