using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// DeepSeek API 服务接口：通用对话能力由 <see cref="IChatCompletionProvider"/> 提供，
    /// 此接口仅承载 DeepSeek 官方端点特有的余额、FIM、计价与 reasoning 配置。
    /// </summary>
    public interface IDeepSeekApiService : IChatCompletionProvider
    {
        /// <summary>累计费用（元，国内价目）。按每次 API 调用时点的高峰/空闲时段单价计价累加（所有官方模型同价）</summary>
        double TotalSessionCostYuan { get; }

        /// <summary>累计费用（美元，国际价目）。与人民币双轨累计，显示时按账户币种取用</summary>
        double TotalSessionCostUsd { get; }

        /// <summary>账户币种（"CNY" 国内 / "USD" 国际），由余额 API 自动捕获，首次查询前默认 "CNY"</summary>
        string AccountCurrency { get; }

        /// <summary>FIM 代码补全累计 Prompt Token 数（独立于聊天统计）</summary>
        long TotalFimPromptTokens { get; }
        /// <summary>FIM 代码补全累计 Completion Token 数（独立于聊天统计）</summary>
        long TotalFimCompletionTokens { get; }

        /// <summary>配置思考模式</summary>
        void ConfigureThinking(bool enabled, string effort = "high");

        /// <summary>是否为 DeepSeek 官方端点（决定余额/FIM/thinking 等 DeepSeek 特有功能的可用性）</summary>
        bool IsDeepSeekEndpoint { get; }

        /// <summary>
        /// FIM（Fill-In-the-Middle）补全调用，用于代码自动补全场景。
        /// 端点: POST https://api.deepseek.com/beta/completions
        /// </summary>
        /// <param name="prompt">光标前的代码（prefix）</param>
        /// <param name="suffix">光标后的代码（suffix）</param>
        /// <param name="maxTokens">最大生成 token 数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>模型生成的补全文本</returns>
        Task<string> FimCompletionAsync(
            string prompt,
            string? suffix = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 查询账户余额。
        /// 端点: GET https://api.deepseek.com/user/balance
        /// </summary>
        /// <returns>余额响应，失败时返回 null</returns>
        Task<BalanceResponse?> GetBalanceAsync();
    }
}
