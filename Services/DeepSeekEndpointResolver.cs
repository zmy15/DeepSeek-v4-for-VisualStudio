using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>解析后的端点配置（官方 DeepSeek 或自定义端点二选一）。</summary>
    public sealed record DeepSeekEndpointConfig(string ApiKey, string Model, string? BaseUrl, bool IsCustom);

    /// <summary>
    /// 端点来源解析器 — DeepSeek 官方与自定义端点配置分离：
    /// 填写了自定义端点（ApiBaseUrl 非空）即启用自定义模式，使用独立的
    /// 自定义密钥（CustomApiKey）与模型名（CustomModelName）；
    /// 否则回退 DeepSeek 官方密钥与模型目录。
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
                    options.ActiveModelSource);

        /// <summary>
        /// 原始参数重载（测试友好，不依赖 DialogPage 实例化）。
        /// </summary>
        public static DeepSeekEndpointConfig Resolve(
            string? apiBaseUrl,
            string officialApiKey,
            string customApiKey,
            string? selectedModel,
            string? customModelName,
            string? activeModelSource = "auto")
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
                ? CoalesceCustomModel(selectedModel, customModelName)
                : CoalesceOfficialModel(selectedModel);

            return new DeepSeekEndpointConfig(
                isCustom ? customApiKey : officialApiKey,
                model,
                isCustom ? baseUrl : null,
                isCustom);
        }

        private static string CoalesceCustomModel(string? selectedModel, string? customModelName)
        {
            var custom = (customModelName ?? string.Empty).Trim();
            if (custom.Length > 0)
                return custom;

            // 自定义端点未填模型名 → 回退官方模型目录选择（多数中转接受 DeepSeek 模型名）
            var fallback = (selectedModel ?? string.Empty).Trim();
            return fallback.Length > 0 ? fallback : DefaultModel;
        }

        private static string CoalesceOfficialModel(string? selectedModel)
        {
            var model = (selectedModel ?? string.Empty).Trim();
            return model.Length > 0 ? model : DefaultModel;
        }

        private const string DefaultModel = "deepseek-v4-pro";
    }
}
