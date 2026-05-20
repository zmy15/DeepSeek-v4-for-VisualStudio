using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Integration;

public class DeepSeekApiServiceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task ChatStreamAsync_WithValidResponse_YieldsContentTokens()
    {
        // Arrange: 模拟 SSE 流式响应
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}\n",
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" World\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new DeepSeekApiService(httpClient);

        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "Hi" }
        };

        // Act
        var tokens = new List<string>();
        await foreach (var token in service.ChatStreamAsync(messages))
        {
            tokens.Add(token);
        }

        // Assert
        // 内容片段已被批处理聚合（ContentFlushThreshold=200），"Hello"+" World" 合并为一个 token
        tokens.Should().Contain("Hello World");
    }

    [Fact]
    public async Task ChatStreamAsync_WithThinkingContent_YieldsThinkingPrefix()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-2\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Let me think...\"}}]}\n",
            "data: {\"id\":\"chatcmpl-2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Answer\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new DeepSeekApiService(httpClient);

        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "Question" }
        };

        var tokens = new List<string>();
        await foreach (var token in service.ChatStreamAsync(messages))
        {
            tokens.Add(token);
        }

        tokens.Should().Contain(t => t.StartsWith("[THINKING]"));
        tokens.Should().Contain("Answer");
    }

    [Fact]
    public async Task ChatStreamAsync_WithToolCall_YieldsToolCallJson()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-3\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}]}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new DeepSeekApiService(httpClient);

        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "Read file" }
        };

        var tokens = new List<string>();
        await foreach (var token in service.ChatStreamAsync(messages))
        {
            tokens.Add(token);
        }

        tokens.Should().Contain(t => t.StartsWith("[TOOL_CALL]"));
    }

    [Fact]
    public async Task ChatStreamAsync_WithUsageInfo_CapturesLastUsage()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-4\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new DeepSeekApiService(httpClient);

        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "Test" }
        };

        await foreach (var _ in service.ChatStreamAsync(messages)) { }

        service.LastUsage.Should().NotBeNull();
        service.LastUsage!.PromptTokens.Should().Be(10);
        service.LastUsage.CompletionTokens.Should().Be(5);
        service.LastUsage.TotalTokens.Should().Be(15);
    }

    [Fact]
    public async Task ChatStreamAsync_HttpError_ThrowsException()
    {
        var handler = new TestHttpMessageHandler(
            Array.Empty<string>(),
            HttpStatusCode.Unauthorized,
            "{\"error\":\"Invalid API Key\"}");

        var httpClient = new HttpClient(handler);
        var service = new DeepSeekApiService(httpClient);

        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "Hi" }
        };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in service.ChatStreamAsync(messages)) { }
        });
    }

    [Fact]
    public async Task ChatStreamAsync_EmptyResponse_CompletesWithoutTokens()
    {
        var sseLines = new[] { "data: [DONE]\n" };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new DeepSeekApiService(httpClient);

        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "Hi" }
        };

        var tokens = new List<string>();
        await foreach (var token in service.ChatStreamAsync(messages))
        {
            tokens.Add(token);
        }

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void ConfigureThinking_UpdatesSettings()
    {
        var service = new DeepSeekApiService("test-key");

        service.ConfigureThinking(false, "low");

        // 通过 chat stream 调用可以间接验证 — 此处仅验证方法不抛异常
        // 实际验证在 ChatStreamAsync 中通过检查请求体完成
    }

    [Fact]
    public void UpdateModel_ChangesModelName()
    {
        var service = new DeepSeekApiService("test-key");

        service.UpdateModel("deepseek-chat");

        // 模型名在内部存储，无公开 getter — 此处仅验证方法不抛异常
    }
}

/// <summary>
/// 测试用 HTTP 消息处理器 — 模拟 SSE 流式响应。
/// </summary>
internal class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly string[] _responseLines;
    private readonly HttpStatusCode _statusCode;
    private readonly string? _errorBody;

    public TestHttpMessageHandler(string[] responseLines, HttpStatusCode statusCode, string? errorBody = null)
    {
        _responseLines = responseLines;
        _statusCode = statusCode;
        _errorBody = errorBody;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode);

        if (_statusCode == HttpStatusCode.OK)
        {
            var sseBody = string.Join("", _responseLines);
            response.Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream");
        }
        else if (_errorBody != null)
        {
            response.Content = new StringContent(_errorBody, Encoding.UTF8, "application/json");
        }

        // 通过 tcs 支持异步语义
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        tcs.SetResult(response);
        return tcs.Task;
    }
}
