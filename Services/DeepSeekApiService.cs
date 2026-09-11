using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Providers;
using System.Net.Http;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 现有调用方的兼容入口。
    /// 实际协议逻辑位于 <see cref="DeepSeekProvider"/> 与 <see cref="OpenAiCompatibleProvider"/>。
    /// </summary>
    public class DeepSeekApiService : DeepSeekProvider
    {
        public DeepSeekApiService(string apiKey, string model = DeepSeekModelCatalog.Pro,
            int? requestTimeoutSeconds = null, string? baseUrl = null,
            bool isVision = false, bool isCustom = false)
            : base(apiKey, model, requestTimeoutSeconds, baseUrl, isVision, isCustom)
        {
        }

        public DeepSeekApiService(HttpClient httpClient, string model = DeepSeekModelCatalog.Pro,
            string? baseUrl = null, bool isVision = false, bool isCustom = false)
            : base(httpClient, model, baseUrl, isVision, isCustom)
        {
        }
    }
}
