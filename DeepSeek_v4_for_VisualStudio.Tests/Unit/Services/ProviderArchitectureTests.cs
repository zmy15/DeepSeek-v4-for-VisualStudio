using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Providers;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ProviderArchitectureTests
{
    [Fact]
    public void DeepSeekApiService_DelegatesThroughProviderAbstraction()
    {
        using var service = new DeepSeekApiService("test-key");

        service.Should().BeAssignableTo<IChatCompletionProvider>();
        service.ProviderId.Should().Be("DeepSeek");
    }

    [Fact]
    public void ReservedProviderInterfaces_InheritChatCompletionContract()
    {
        typeof(IChatCompletionProvider).IsAssignableFrom(typeof(IAnthropicProvider))
            .Should().BeTrue();
        typeof(IChatCompletionProvider).IsAssignableFrom(typeof(IResponsesApiProvider))
            .Should().BeTrue();
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_UsesGenericChatPathWithoutDeepSeekCosting()
    {
        const string responseJson =
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""";
        var handler = new RecordingHandler(responseJson);
        using var provider = new OpenAiCompatibleProvider(
            new HttpClient(handler),
            model: "gpt-test",
            baseUrl: "https://relay.example.com/v1");

        var result = await provider.CompleteAsync(new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "hi" }
        });

        result.Should().Be("ok");
        provider.ProviderId.Should().Be("OpenAI-compatible");
        provider.TotalPromptTokens.Should().Be(2);
        provider.TotalSessionCostYuan.Should().Be(0);
        provider.TotalSessionCostUsd.Should().Be(0);
        handler.RequestUri.Should().Be("https://relay.example.com/v1/chat/completions");
        handler.HasDeepSeekClientHeader.Should().BeFalse();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public RecordingHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        public string? RequestUri { get; private set; }
        public bool HasDeepSeekClientHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            HasDeepSeekClientHeader = request.Headers.Contains("X-Client-Instance-Id");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
