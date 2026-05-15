using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class DeepSeekModelsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void DeepSeekChatRequest_Serialize_ProducesValidJson()
    {
        var request = new DeepSeekChatRequest
        {
            Model = "deepseek-v4-pro",
            Messages = new List<ChatApiMessage>
            {
                new() { Role = "user", Content = "Hello" }
            },
            Stream = true,
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);

        json.Should().Contain("\"model\"");
        json.Should().Contain("\"messages\"");
        json.Should().Contain("\"stream\"");
    }

    [Fact]
    public void DeepSeekChatRequest_WithThinking_SerializesCorrectly()
    {
        var request = new DeepSeekChatRequest
        {
            Model = "deepseek-v4-pro",
            Messages = new List<ChatApiMessage>(),
            Stream = true,
            Thinking = new ThinkingControl { Type = "enabled" },
            ReasoningEffort = "high",
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);

        json.Should().Contain("\"thinking\"");
        json.Should().Contain("\"reasoning_effort\"");
        json.Should().Contain("\"enabled\"");
    }

    [Fact]
    public void DeepSeekChatRequest_WithTools_SerializesCorrectly()
    {
        var request = new DeepSeekChatRequest
        {
            Model = "deepseek-v4-pro",
            Messages = new List<ChatApiMessage>(),
            Stream = true,
            Tools = new List<ToolDefinition>
            {
                new()
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "read_file",
                        Description = "Read a file",
                        Parameters = new { type = "object", properties = new { } }
                    }
                }
            },
            ToolChoice = "auto",
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);

        json.Should().Contain("\"tools\"");
        json.Should().Contain("\"tool_choice\"");
        json.Should().Contain("\"read_file\"");
    }

    [Fact]
    public void ChatApiMessage_WithToolCalls_SerializesCorrectly()
    {
        var message = new ChatApiMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls = new List<ToolCall>
            {
                new()
                {
                    Id = "call_123",
                    Type = "function",
                    Function = new ToolCallFunction
                    {
                        Name = "grep_search",
                        Arguments = "{\"pattern\":\"test\"}",
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(message, JsonOpts);

        json.Should().Contain("\"tool_calls\"");
        json.Should().Contain("\"call_123\"");
    }

    [Fact]
    public void ChatApiMessage_WithReasoningContent_SerializesWhenPresent()
    {
        var message = new ChatApiMessage
        {
            Role = "assistant",
            Content = "Here is the answer",
            ReasoningContent = "Let me think about this...",
        };

        var json = JsonSerializer.Serialize(message, JsonOpts);

        json.Should().Contain("\"reasoning_content\"");
        json.Should().Contain("Let me think about this");
    }

    [Fact]
    public void DeepSeekChatResponse_Deserialize_ReturnsValidObject()
    {
        var json = @"{
            ""id"": ""chatcmpl-123"",
            ""choices"": [
                {
                    ""index"": 0,
                    ""message"": {
                        ""role"": ""assistant"",
                        ""content"": ""Hello, World!""
                    },
                    ""finish_reason"": ""stop""
                }
            ],
            ""usage"": {
                ""prompt_tokens"": 10,
                ""completion_tokens"": 5,
                ""total_tokens"": 15
            }
        }";

        var response = JsonSerializer.Deserialize<DeepSeekChatResponse>(json, JsonOpts);

        response.Should().NotBeNull();
        response!.Id.Should().Be("chatcmpl-123");
        response.Choices.Should().HaveCount(1);
        response.Choices[0].Message!.Content.Should().Be("Hello, World!");
        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(10);
        response.Usage.CompletionTokens.Should().Be(5);
    }

    [Fact]
    public void DeepSeekUsage_Deserialize_WithCacheInfo()
    {
        var json = @"{
            ""prompt_tokens"": 100,
            ""completion_tokens"": 50,
            ""total_tokens"": 150,
            ""prompt_cache_hit_tokens"": 80,
            ""prompt_cache_miss_tokens"": 20
        }";

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage.Should().NotBeNull();
        usage!.PromptCacheHitTokens.Should().Be(80);
        usage.PromptCacheMissTokens.Should().Be(20);
        usage.PromptTokens.Should().Be(100);
    }

    [Fact]
    public void ThinkingControl_DefaultType_IsEnabled()
    {
        var control = new ThinkingControl();

        control.Type.Should().Be("enabled");
    }
}
