using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 模型列表抓取服务（参考 CC Switch model_fetch）。
    /// 通过 OpenAI 兼容的 GET /models 端点获取可用模型，
    /// 对 Base URL 生成候选端点按序尝试，404/405 自动回退下一候选。
    /// </summary>
    public static class ModelFetchService
    {
        /// <summary>已知的"Anthropic 协议兼容子路径"后缀；命中时追加剥离后根路径的候选。</summary>
        private static readonly string[] KnownCompatSuffixes =
        {
            "/api/claudecode", "/api/anthropic", "/apps/anthropic", "/api/coding",
            "/claudecode", "/anthropic", "/step_plan", "/coding", "/claude",
        };

        /// <summary>
        /// 抓取可用模型列表。按候选端点顺序尝试；成功返回按 id 排序去重的列表。
        /// </summary>
        /// <exception cref="InvalidOperationException">全部候选失败或上游报错时抛出，消息含最后一次 HTTP 状态。</exception>
        public static async Task<IReadOnlyList<string>> FetchModelsAsync(
            string? baseUrl,
            string? apiKey,
            CancellationToken cancellationToken = default,
            HttpMessageHandler? httpMessageHandler = null)
        {
            var candidates = BuildModelsUrlCandidates(baseUrl);
            if (candidates.Count == 0)
                throw new InvalidOperationException("Base URL is empty");

            using var client = new HttpClient(httpMessageHandler ?? new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            string? lastError = null;
            foreach (var url in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                HttpResponseMessage response;
                try
                {
                    response = await client.GetAsync(url, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           && !cancellationToken.IsCancellationRequested)
                {
                    lastError = ex.Message;
                    continue;
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var models = ParseModelIds(json);
                        if (models.Count > 0)
                            return models;
                        lastError = "empty model list";
                        continue;
                    }

                    // 404/405 → 尝试下一候选端点；其余状态直接报错（401/403 等重试无意义）
                    if (response.StatusCode is HttpStatusCode.NotFound
                        or HttpStatusCode.MethodNotAllowed)
                    {
                        lastError = $"HTTP {(int)response.StatusCode}";
                        continue;
                    }

                    var body = await response.Content.ReadAsStringAsync();
                    if (body.Length > 300)
                        body = body.Substring(0, 300) + "…";
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
                }
            }

            throw new InvalidOperationException(lastError ?? "All model endpoint candidates failed");
        }

        /// <summary>
        /// 按 CC Switch 规则生成 /models 候选端点（去重，按命中概率排序）：
        /// 1. 用户粘贴完整 chat 端点（/chat/completions 结尾）→ 先剥离
        /// 2. base 以版本段 /v{N} 结尾（如 /v1、智谱 /api/coding/paas/v4）→ {base}/models 优先；
        ///    非 /v1 再补 {base}/v1/models 兜底（避免 .../paas/v4/v1/models → 404）
        /// 3. 否则 → {base}/v1/models 优先，{base}/models 兜底
        /// 4. 命中 Anthropic 兼容后缀 → 追加剥根 {root}/v1/models、{root}/models
        /// </summary>
        public static IReadOnlyList<string> BuildModelsUrlCandidates(string? baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - "/chat/completions".Length).TrimEnd('/');
            if (trimmed.Length == 0)
                return Array.Empty<string>();

            var candidates = new List<string>();
            if (EndsWithVersionSegment(trimmed))
            {
                candidates.Add(trimmed + "/models");
                if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(trimmed + "/v1/models");
            }
            else
            {
                candidates.Add(trimmed + "/v1/models");
                candidates.Add(trimmed + "/models");
            }

            var stripped = StripCompatSuffix(trimmed);
            if (stripped != null)
            {
                var root = stripped.TrimEnd('/');
                candidates.Add(root + "/v1/models");
                candidates.Add(root + "/models");
            }

            var unique = new List<string>(candidates.Count);
            foreach (var url in candidates)
                if (!unique.Contains(url))
                    unique.Add(url);
            return unique;
        }

        /// <summary>解析 {"data":[{"id":..}]} 或裸数组两种响应形态，按 id 排序去重。</summary>
        public static IReadOnlyList<string> ParseModelIds(string json)
        {
            var ids = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                        AddId(ids, item);
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("data", out var data) &&
                         data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                        AddId(ids, item);
                }
            }
            catch (JsonException)
            {
                // 非 JSON 响应（如 404 HTML 页）→ 空列表，由调用方回退下一候选
            }

            return ids.Where(id => !string.IsNullOrWhiteSpace(id))
                      .Distinct(StringComparer.Ordinal)
                      .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }

        private static void AddId(List<string> ids, JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return;
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    ids.Add(value);
            }
        }

        private static bool EndsWithVersionSegment(string url)
        {
            var slash = url.LastIndexOf('/');
            if (slash < 0)
                return false;
            var segment = url.Substring(slash + 1);
            if (segment.Length < 2 || segment[0] != 'v')
                return false;
            for (var i = 1; i < segment.Length; i++)
                if (segment[i] < '0' || segment[i] > '9')
                    return false;
            return true;
        }

        private static string? StripCompatSuffix(string url)
        {
            foreach (var suffix in KnownCompatSuffixes)
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    url.Length > suffix.Length)
                    return url.Substring(0, url.Length - suffix.Length);
            }
            return null;
        }
    }
}
