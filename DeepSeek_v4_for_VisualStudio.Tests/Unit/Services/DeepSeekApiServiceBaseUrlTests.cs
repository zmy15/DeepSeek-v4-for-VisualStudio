using System.Net;
using System.Net.Http;
using System.Text;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// Base URL 规范化与请求路径拼接回归测试。
/// HttpClient 对以 / 开头的相对 URI 会从主机根开始拼接，
/// 导致自定义端点 BaseAddress 中的路径段（如 /v1）丢失。
/// </summary>
public class DeepSeekApiServiceBaseUrlTests
{
    private const string ChatOkResponse =
        """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private const string BalanceOkResponse =
        """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"10.00"}]}""";

    [Fact]
    public async Task CompleteAsync_BaseUrlWithV1_KeepsVersionSegment()
    {
        var handler = new RecordingHandler();
        handler.Route["https://new-api.xiaoduoai.com/v1/chat/completions"] = ChatOkResponse;

        var service = new DeepSeekApiService(
            new HttpClient(handler), "deepseek-v4-pro",
            baseUrl: "https://new-api.xiaoduoai.com/v1/");

        var result = await service.CompleteAsync(new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "hi" }
        });

        result.Should().Be("ok");
        handler.Requests.Should().Contain("https://new-api.xiaoduoai.com/v1/chat/completions");
        handler.Requests.Should().NotContain("https://new-api.xiaoduoai.com/chat/completions");
    }

    [Fact]
    public async Task CompleteAsync_PastedFullChatEndpoint_StripsSuffix()
    {
        var handler = new RecordingHandler();
        handler.Route["https://x.example.com/v1/chat/completions"] = ChatOkResponse;

        var service = new DeepSeekApiService(
            new HttpClient(handler), "deepseek-v4-pro",
            baseUrl: "https://x.example.com/v1/chat/completions");

        var result = await service.CompleteAsync(new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "hi" }
        });

        result.Should().Be("ok");
        handler.Requests.Should().Contain("https://x.example.com/v1/chat/completions");
    }

    [Fact]
    public async Task CompleteAsync_OfficialBaseUrl_NoDuplicateSlash()
    {
        var handler = new RecordingHandler();
        handler.Route["https://api.deepseek.com/chat/completions"] = ChatOkResponse;

        var service = new DeepSeekApiService(
            new HttpClient(handler), "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com");

        var result = await service.CompleteAsync(new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "hi" }
        });

        result.Should().Be("ok");
        handler.Requests.Should().Contain("https://api.deepseek.com/chat/completions");
        handler.Requests.Should().NotContain("https://api.deepseek.com//chat/completions");
    }

    [Fact]
    public void IsDeepSeekEndpoint_TrailingSlash_StillTrue()
    {
        var service = new DeepSeekApiService(
            new HttpClient(), "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com/");

        service.IsDeepSeekEndpoint.Should().BeTrue();
    }

    [Fact]
    public async Task GetBalanceAsync_Official_RequestsUserBalanceUnderRoot()
    {
        var handler = new RecordingHandler();
        handler.Route["https://api.deepseek.com/user/balance"] = BalanceOkResponse;

        var service = new DeepSeekApiService(
            new HttpClient(handler), "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com");

        var balance = await service.GetBalanceAsync();

        balance.Should().NotBeNull();
        handler.Requests.Should().Contain("https://api.deepseek.com/user/balance");
        handler.Requests.Should().NotContain("https://api.deepseek.com//user/balance");
    }

    [Fact]
    public async Task UpdateBaseUrl_TogglesDeepSeekClientHeader()
    {
        var handler = new RecordingHandler();
        handler.Route["https://relay.example.com/v1/chat/completions"] = ChatOkResponse;
        handler.Route["https://api.deepseek.com/chat/completions"] = ChatOkResponse;

        var service = new DeepSeekApiService(
            new HttpClient(handler), "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com");

        service.UpdateBaseUrl("https://relay.example.com/v1");
        await service.CompleteAsync(new List<ChatApiMessage> { new() { Role = "user", Content = "hi" } });

        service.UpdateBaseUrl("https://api.deepseek.com");
        await service.CompleteAsync(new List<ChatApiMessage> { new() { Role = "user", Content = "hi" } });

        handler.Requests.Should().Contain("https://relay.example.com/v1/chat/completions");
        handler.Requests.Should().Contain("https://api.deepseek.com/chat/completions");
        handler.ClientInstanceIds.Should().ContainInOrder(false, true);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Route { get; } = new();
        public List<string> Requests { get; } = new();
        public List<bool> ClientInstanceIds { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            ClientInstanceIds.Add(request.Headers.Contains("X-Client-Instance-Id"));
            if (Route.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
