using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

[Collection("OfficialModelCatalogService")]
public class OfficialModelCatalogServiceTests
{
    [Fact]
    public async Task RefreshAsync_EmptyApiKey_DoesNotRequestModels()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();

        var models = await OfficialModelCatalogService.RefreshAsync("", default, handler);

        models.Should().Equal(DeepSeekModelCatalog.All);
        handler.Requests.Should().BeEmpty();
        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public async Task RefreshAsync_RemoteModels_CachedUntilKeyOrLifetimeChanges()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();
        handler.Route["https://api.deepseek.com/v1/models"] = (HttpStatusCode.NotFound, "<html>404</html>");
        handler.Route["https://api.deepseek.com/models"] = (
            HttpStatusCode.OK,
            """{"data":[{"id":"model-b"},{"id":"model-a"}]}""");

        var first = await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);
        var second = await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);

        first.Should().Equal("model-a", "model-b");
        second.Should().Equal("model-a", "model-b");
        handler.Requests.Should().HaveCount(2);
        OfficialModelCatalogService.HasRemoteModels.Should().BeTrue();
        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public async Task RefreshAsync_RemoteFailure_FallsBackToBuiltInCatalog()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();
        handler.Route["https://api.deepseek.com/v1/models"] = (
            HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        var models = await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);

        models.Should().Equal(DeepSeekModelCatalog.All);
        OfficialModelCatalogService.HasRemoteModels.Should().BeFalse();
        OfficialModelCatalogService.ResetForTests();
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Route { get; } = new();
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
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
