using System.Net;
using System.Net.Http;
using System.Text;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// /models 候选端点规则与抓取回退（参考 CC Switch build_models_url_candidates）。
/// </summary>
public class ModelFetchServiceTests
{
    [Fact]
    public void BuildCandidates_NoVersionSegment_TriesV1FirstThenRoot()
    {
        var candidates = ModelFetchService.BuildModelsUrlCandidates("https://api.deepseek.com");
        candidates.Should().Equal(
            "https://api.deepseek.com/v1/models",
            "https://api.deepseek.com/models");
    }

    [Fact]
    public void BuildCandidates_EndsWithV1_SingleCandidate()
    {
        var candidates = ModelFetchService.BuildModelsUrlCandidates("https://new-api.xiaoduoai.com/v1/");
        candidates.Should().Equal("https://new-api.xiaoduoai.com/v1/models");
    }

    [Fact]
    public void BuildCandidates_EndsWithOtherVersion_PutsBaseModelsFirst()
    {
        var candidates = ModelFetchService.BuildModelsUrlCandidates(
            "https://open.bigmodel.cn/api/coding/paas/v4");
        candidates.Should().Equal(
            "https://open.bigmodel.cn/api/coding/paas/v4/models",
            "https://open.bigmodel.cn/api/coding/paas/v4/v1/models");
    }

    [Fact]
    public void BuildCandidates_FullChatEndpoint_StripsBeforeBuilding()
    {
        var candidates = ModelFetchService.BuildModelsUrlCandidates(
            "https://x.example.com/v1/chat/completions");
        candidates.Should().Equal("https://x.example.com/v1/models");
    }

    [Fact]
    public void BuildCandidates_CompatSuffix_AddsStrippedRootCandidates()
    {
        var candidates = ModelFetchService.BuildModelsUrlCandidates("https://x.example.com/api/claudecode");
        candidates.Should().Contain("https://x.example.com/v1/models");
        candidates.Should().Contain("https://x.example.com/models");
    }

    [Fact]
    public void BuildCandidates_Empty_ReturnsEmpty()
    {
        ModelFetchService.BuildModelsUrlCandidates("").Should().BeEmpty();
        ModelFetchService.BuildModelsUrlCandidates(null).Should().BeEmpty();
    }

    [Fact]
    public void ParseModelIds_ObjectWithData_SortsAndDedupes()
    {
        var json = """{"data":[{"id":"b-model"},{"id":"a-model"},{"id":"b-model"}]}""";
        ModelFetchService.ParseModelIds(json).Should().Equal("a-model", "b-model");
    }

    [Fact]
    public void ParseModelIds_BareArray_Parses()
    {
        var json = """[{"id":"m1"},{"id":"m2"}]""";
        ModelFetchService.ParseModelIds(json).Should().Equal("m1", "m2");
    }

    [Fact]
    public void ParseModelIds_NonJson_ReturnsEmpty()
    {
        ModelFetchService.ParseModelIds("<html>Not Found</html>").Should().BeEmpty();
    }

    [Fact]
    public async Task FetchModelsAsync_FirstCandidate404_FallsBackToNext()
    {
        var handler = new UrlRouterHandler();
        handler.Route["https://relay.example.com/v1/models"] = (HttpStatusCode.NotFound, "<html>404</html>");
        handler.Route["https://relay.example.com/models"] = (
            HttpStatusCode.OK,
            """{"data":[{"id":"m-b"},{"id":"m-a"}]}""");

        var models = await ModelFetchService.FetchModelsAsync("https://relay.example.com", "sk-test", default, handler);

        models.Should().Equal("m-a", "m-b");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchModelsAsync_SendsWithBearerAuth()
    {
        var handler = new UrlRouterHandler();
        handler.Route["https://api.example.com/v1/models"] = (
            HttpStatusCode.OK,
            """{"data":[{"id":"m1"}]}""");

        await ModelFetchService.FetchModelsAsync("https://api.example.com", "sk-secret", default, handler);

        handler.LastAuthorization.Should().Be("Bearer sk-secret");
    }

    private sealed class UrlRouterHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Route { get; } = new();
        public List<string> Requests { get; } = new();
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (Route.TryGetValue(url, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
