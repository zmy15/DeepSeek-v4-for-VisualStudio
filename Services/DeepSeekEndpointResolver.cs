using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>解析后的端点配置（官方 DeepSeek 或自定义端点二选一）。
    /// <see cref="DeepSeekEndpointConfig.IsVision"/> 由 Resolve 权威判定：官方模式查官方模型目录，
    /// 自定义模式查用户手动标记的视觉名单（CustomVisionModels 设置）。</summary>
    public sealed record DeepSeekEndpointConfig(
        string ApiKey, string Model, string? BaseUrl, bool IsCustom,
        bool IsVision = false);

    /// <summary>
    /// 端点来源解析器 — DeepSeek 官方与自定义端点配置分离：
    /// 填写了自定义端点（ApiBaseUrl 非空）即启用自定义模式，使用独立的
    /// 自定义密钥（CustomApiKey）与自定义模型列表（CustomModelName）；
    /// 否则回退 DeepSeek 官方密钥与模型目录。
    /// 解析结果同时携带 IsVision（视觉能力权威判定）：官方模式查官方目录，
    /// 自定义模式查用户手动标记的 CustomVisionModels 名单。
    /// </summary>
    public static class DeepSeekEndpointResolver
    {
        public static DeepSeekEndpointConfig Resolve(DeepSeekOptionsPage? options)
            => options == null
                ? new DeepSeekEndpointConfig(string.Empty, DefaultModel, null, false)
                : Resolve(
                    options.ApiBaseUrl,
                    ApiKeyProtection.Unprotect(options.ApiKey),
                    ApiKeyProtection.Unprotect(options.CustomApiKey),
                    options.SelectedModel,
                    options.CustomModelName,
                    options.ActiveCustomModel,
                    options.ActiveModelSource,
                    options.GetCustomVisionModels());

        /// <summary>
        /// 原始参数重载（测试友好，不依赖 DialogPage 实例化）。
        /// </summary>
        public static DeepSeekEndpointConfig Resolve(
            string? apiBaseUrl,
            string officialApiKey,
            string customApiKey,
            string? selectedModel,
            string? customModels,
            string? activeCustomModel = "",
            string? activeModelSource = "auto",
            IReadOnlyList<string>? customVisionModels = null,
            IReadOnlyList<string>? officialModels = null)
        {
            var baseUrl = (apiBaseUrl ?? string.Empty).Trim();
            var sourcePreference = (activeModelSource ?? "auto").Trim().ToLowerInvariant();
            var isCustom = sourcePreference switch
            {
                "custom" => true,
                "official" => false,
                _ => baseUrl.Length > 0, // auto：跟随端点配置
            };

            // 自定义模式必须有端点；缺失时回退官方（配置不一致的兜底）
            if (isCustom && baseUrl.Length == 0)
            {
                isCustom = false;
                Logger.Warn("[Resolver] 模型来源为 custom 但未配置 API 端点，回退 DeepSeek 官方");
            }

            var model = isCustom
                ? CoalesceCustomModel(customModels, activeCustomModel)
                : CoalesceOfficialModel(
                    selectedModel,
                    officialModels ?? OfficialModelCatalogService.GetModels());

            // ── 视觉能力判断收敛点 ──
            // 官方模式：查官方模型目录（目录内名字含 vision 视为视觉模型）；
            // 自定义模式：查用户在 CustomVisionModels 中手动标记的名单。
            // 注：自定义模型的 FIM 回退无需在此考虑 —— FIM 为 DeepSeek 专有端点，
            // FimCompletionAsync 对非官方端点已提前返回空串。
            bool isVision = isCustom
                ? (customVisionModels ?? Array.Empty<string>())
                    .Any(v => string.Equals(v, model, StringComparison.OrdinalIgnoreCase))
                : DeepSeekModelCatalog.IsVisionModel(model);

            return new DeepSeekEndpointConfig(
                isCustom ? customApiKey : officialApiKey,
                model,
                isCustom ? baseUrl : null,
                isCustom,
                isVision);
        }

        private static string CoalesceCustomModel(string? customModels, string? activeCustomModel)
        {
            var models = DeepSeekOptionsPage.ParseCustomModels(customModels);
            if (models.Count == 0)
                return DefaultModel;

            var active = (activeCustomModel ?? string.Empty).Trim();
            if (active.Length > 0)
            {
                var match = models.FirstOrDefault(model =>
                    string.Equals(model, active, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }

            // 激活模型不存在（例如列表刚被编辑）时使用列表第一个条目。
            return models[0];
        }

        private static string CoalesceOfficialModel(
            string? selectedModel,
            IReadOnlyList<string> officialModels)
        {
            var model = (selectedModel ?? string.Empty).Trim();
            if (model.Length > 0 &&
                officialModels.Any(item => string.Equals(item, model, StringComparison.OrdinalIgnoreCase)))
                return model;

            return officialModels.FirstOrDefault() ?? DefaultModel;
        }

        private const string DefaultModel = "deepseek-v4-pro";
    }
}
