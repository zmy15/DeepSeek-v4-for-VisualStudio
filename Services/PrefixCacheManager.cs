using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 前缀缓存稳定性管理器 — 基于 SHA-256 指纹监控前缀变化，保障 DeepSeek V4 自动前缀缓存命中率。
    /// 
    /// 核心职责：
    /// 1. 对不可变前缀（system prompt + tool catalog）计算 SHA-256 指纹
    /// 2. 对比当前指纹与 pinned 基准指纹，检测漂移（drift）
    /// 3. 区分漂移来源：是 system prompt 变了还是 tool 集变了
    /// 4. 跟踪缓存稳定性比率（stable requests / total checks）
    /// 
    /// 设计参考：CodeWhale prefix_cache.rs 三区域模型
    /// </summary>
    public class PrefixCacheManager : IPrefixCacheManager
    {
        /// <summary>Pinned 基准：system prompt 的 SHA-256 指纹</summary>
        private string? _pinnedSystemPromptFingerprint;

        /// <summary>Pinned 基准：tool catalog 的 SHA-256 指纹</summary>
        private string? _pinnedToolCatalogFingerprint;

        /// <summary>Pinned 基准：组合指纹（system + tool）</summary>
        private string? _pinnedCombinedFingerprint;

        /// <summary>总检查次数</summary>
        private int _totalChecks;

        /// <summary>稳定次数（无漂移）</summary>
        private int _stableChecks;

        /// <summary>是否已 pinned（已建立基准）</summary>
        public bool IsPinned => _pinnedCombinedFingerprint != null;

        /// <summary>缓存稳定性比率（0.0 ~ 1.0）</summary>
        public double StabilityRatio => _totalChecks > 0 ? (double)_stableChecks / _totalChecks : 1.0;

        /// <summary>总检查次数</summary>
        public int TotalChecks => _totalChecks;

        /// <summary>稳定次数</summary>
        public int StableChecks => _stableChecks;

        /// <summary>当前 pinned 的组合指纹（null = 未 pin）</summary>
        public string? PinnedCombinedFingerprint => _pinnedCombinedFingerprint;

        #region Fingerprint Computation

        /// <summary>
        /// 计算系统提示词文本的 SHA-256 指纹。
        /// </summary>
        public static string ComputeSystemPromptFingerprint(string? systemPrompt)
        {
            if (string.IsNullOrEmpty(systemPrompt))
                return ComputeSha256(string.Empty);
            return ComputeSha256(systemPrompt);
        }

        /// <summary>
        /// 计算工具目录的 SHA-256 指纹。
        /// 工具按名称排序后序列化为规范 JSON（仅 API 相关字段），确保注册顺序不影响指纹。
        /// </summary>
        public static string ComputeToolCatalogFingerprint(IEnumerable<ToolDefinition>? tools)
        {
            if (tools == null || !tools.Any())
                return ComputeSha256("[]");

            string canonicalJson = ToolSchemaNormalizer.SerializeForFingerprint(tools);
            return ComputeSha256(canonicalJson);
        }

        /// <summary>
        /// 计算组合 SHA-256 指纹（system_prompt_fp + tool_catalog_fp）。
        /// </summary>
        public static string ComputeCombinedFingerprint(string systemPromptFingerprint, string toolCatalogFingerprint)
        {
            return ComputeSha256(systemPromptFingerprint + "|" + toolCatalogFingerprint);
        }

        /// <summary>
        /// 一次性计算所有三个指纹。
        /// </summary>
        public static (string systemPromptFp, string toolCatalogFp, string combinedFp) ComputeAllFingerprints(
            string? systemPrompt,
            IEnumerable<ToolDefinition>? tools)
        {
            string spFp = ComputeSystemPromptFingerprint(systemPrompt);
            string toolFp = ComputeToolCatalogFingerprint(tools);
            string combinedFp = ComputeCombinedFingerprint(spFp, toolFp);
            return (spFp, toolFp, combinedFp);
        }

        private static string ComputeSha256(string input)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 截断指纹字符串用于日志显示（取前 8 位 + "..."）。
        /// </summary>
        private static string TruncateFingerprint(string? fp)
        {
            if (string.IsNullOrEmpty(fp)) return "null";
            int len = Math.Min(8, fp.Length);
            return fp.Substring(0, len) + "...";
        }

        #endregion

        #region Pin & Drift Detection

        /// <summary>
        /// 建立基准（pin）：将当前指纹设为会话的不可变基准。
        /// 在会话初始化时调用一次，后续所有检查都与此基准对比。
        /// </summary>
        /// <param name="systemPromptFingerprint">system prompt 指纹</param>
        /// <param name="toolCatalogFingerprint">tool catalog 指纹</param>
        /// <param name="combinedFingerprint">组合指纹</param>
        public void Pin(string systemPromptFingerprint, string toolCatalogFingerprint, string combinedFingerprint)
        {
            _pinnedSystemPromptFingerprint = systemPromptFingerprint;
            _pinnedToolCatalogFingerprint = toolCatalogFingerprint;
            _pinnedCombinedFingerprint = combinedFingerprint;
            _totalChecks = 0;
            _stableChecks = 0;

            Logger.Info($"[PrefixCache] Pinned 基准指纹: system={TruncateFingerprint(systemPromptFingerprint)}, " +
                        $"tool={TruncateFingerprint(toolCatalogFingerprint)}, " +
                        $"combined={TruncateFingerprint(combinedFingerprint)}");
        }

        /// <summary>
        /// 检查当前前缀是否与 pinned 基准一致，并记录漂移信息。
        /// 应在每次构建 API 请求前调用。
        /// </summary>
        /// <param name="currentSystemPromptFingerprint">当前 system prompt 指纹</param>
        /// <param name="currentToolCatalogFingerprint">当前 tool catalog 指纹</param>
        /// <param name="currentCombinedFingerprint">当前组合指纹</param>
        /// <returns>漂移检测结果</returns>
        public PrefixDriftInfo CheckAndUpdate(
            string currentSystemPromptFingerprint,
            string currentToolCatalogFingerprint,
            string currentCombinedFingerprint)
        {
            _totalChecks++;

            if (!IsPinned)
            {
                // 尚未 pin，自动 pin 到当前值
                Pin(currentSystemPromptFingerprint, currentToolCatalogFingerprint, currentCombinedFingerprint);
                _stableChecks++;
                return new PrefixDriftInfo
                {
                    HasDrift = false,
                    IsInitialPin = true,
                };
            }

            bool spChanged = _pinnedSystemPromptFingerprint != currentSystemPromptFingerprint;
            bool toolChanged = _pinnedToolCatalogFingerprint != currentToolCatalogFingerprint;

            if (!spChanged && !toolChanged)
            {
                _stableChecks++;
                return new PrefixDriftInfo
                {
                    HasDrift = false,
                    SystemPromptFingerprint = currentSystemPromptFingerprint,
                    ToolCatalogFingerprint = currentToolCatalogFingerprint,
                    CombinedFingerprint = currentCombinedFingerprint,
                };
            }

            // ── 检测到漂移 ──
            var driftInfo = new PrefixDriftInfo
            {
                HasDrift = true,
                SystemPromptChanged = spChanged,
                ToolCatalogChanged = toolChanged,
                PreviousSystemPromptFingerprint = _pinnedSystemPromptFingerprint,
                PreviousToolCatalogFingerprint = _pinnedToolCatalogFingerprint,
                PreviousCombinedFingerprint = _pinnedCombinedFingerprint,
                SystemPromptFingerprint = currentSystemPromptFingerprint,
                ToolCatalogFingerprint = currentToolCatalogFingerprint,
                CombinedFingerprint = currentCombinedFingerprint,
            };

            // 自动 re-pin 到新基准，避免同一变化被反复报告
            Pin(currentSystemPromptFingerprint, currentToolCatalogFingerprint, currentCombinedFingerprint);

            string cause = spChanged && toolChanged ? "system prompt 和 tool 集均变化"
                : spChanged ? "system prompt 变化"
                : "tool 集变化";

            Logger.Warn($"[PrefixCache] ⚠️ 前缀漂移检测: {cause} | " +
                        $"旧指纹={TruncateFingerprint(driftInfo.PreviousCombinedFingerprint)} → " +
                        $"新指纹={TruncateFingerprint(currentCombinedFingerprint)} | " +
                        $"已自动 re-pin。稳定性={StabilityRatio:P1} ({_stableChecks}/{_totalChecks})");

            return driftInfo;
        }

        /// <summary>
        /// 便捷方法：一次性计算指纹并执行检查。
        /// </summary>
        public PrefixDriftInfo CheckCurrentPrefix(string? systemPrompt, IEnumerable<ToolDefinition>? tools)
        {
            var (spFp, toolFp, combinedFp) = ComputeAllFingerprints(systemPrompt, tools);
            return CheckAndUpdate(spFp, toolFp, combinedFp);
        }

        /// <summary>
        /// 重置所有状态。
        /// </summary>
        public void Reset()
        {
            _pinnedSystemPromptFingerprint = null;
            _pinnedToolCatalogFingerprint = null;
            _pinnedCombinedFingerprint = null;
            _totalChecks = 0;
            _stableChecks = 0;
        }

        #endregion
    }

    /// <summary>
    /// 前缀漂移检测结果。
    /// </summary>
    public class PrefixDriftInfo
    {
        /// <summary>是否检测到前缀漂移</summary>
        public bool HasDrift { get; set; }

        /// <summary>是否为首次 pin（非漂移）</summary>
        public bool IsInitialPin { get; set; }

        /// <summary>漂移是否来自 system prompt 变化</summary>
        public bool SystemPromptChanged { get; set; }

        /// <summary>漂移是否来自 tool catalog 变化</summary>
        public bool ToolCatalogChanged { get; set; }

        /// <summary>之前的 system prompt 指纹</summary>
        public string? PreviousSystemPromptFingerprint { get; set; }

        /// <summary>之前的 tool catalog 指纹</summary>
        public string? PreviousToolCatalogFingerprint { get; set; }

        /// <summary>之前的组合指纹</summary>
        public string? PreviousCombinedFingerprint { get; set; }

        /// <summary>当前 system prompt 指纹</summary>
        public string? SystemPromptFingerprint { get; set; }

        /// <summary>当前 tool catalog 指纹</summary>
        public string? ToolCatalogFingerprint { get; set; }

        /// <summary>当前组合指纹</summary>
        public string? CombinedFingerprint { get; set; }

        /// <summary>漂移原因描述</summary>
        public string DriftCauseDescription
        {
            get
            {
                if (!HasDrift) return "无漂移";
                if (SystemPromptChanged && ToolCatalogChanged) return "System Prompt 和 Tool 集均变化";
                if (SystemPromptChanged) return "System Prompt 变化";
                if (ToolCatalogChanged) return "Tool 集变化";
                return "未知原因";
            }
        }
    }
}
