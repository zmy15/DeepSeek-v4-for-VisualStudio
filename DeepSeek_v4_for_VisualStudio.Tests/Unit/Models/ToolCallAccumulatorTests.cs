using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class ToolCallAccumulatorTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void ParseChunk_SingleContentToken_ReturnsContent()
    {
        var chunk = new DeepSeekStreamChunk
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Delta = new DeepSeekDelta
                    {
                        Content = "Hello",
                    }
                }
            }
        };

        chunk.Choices[0].Delta!.Content.Should().Be("Hello");
    }

    [Fact]
    public void ParseChunk_ReasoningContent_IsCaptured()
    {
        var chunk = new DeepSeekStreamChunk
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Delta = new DeepSeekDelta
                    {
                        ReasoningContent = "I need to analyze this...",
                        Content = null,
                    }
                }
            }
        };

        var delta = chunk.Choices[0].Delta!;
        delta.ReasoningContent.Should().Be("I need to analyze this...");
        delta.Content.Should().BeNull();
    }

    [Fact]
    public void ParseChunk_ToolCallDelta_IsParsed()
    {
        var json = @"{
            ""id"": ""chatcmpl-xxx"",
            ""choices"": [{
                ""index"": 0,
                ""delta"": {
                    ""tool_calls"": [{
                        ""index"": 0,
                        ""id"": ""call_abc"",
                        ""type"": ""function"",
                        ""function"": {
                            ""name"": ""read_file"",
                            ""arguments"": ""{\""filePath\"":\""F:\\\\test.cs\""}""
                        }
                    }]
                }
            }]
        }";

        var chunk = JsonSerializer.Deserialize<DeepSeekStreamChunk>(json, JsonOpts);

        chunk.Should().NotBeNull();
        var delta = chunk!.Choices[0].Delta;
        delta!.ToolCalls.Should().NotBeNull();
        delta.ToolCalls!.Count.Should().Be(1);
        delta.ToolCalls[0].Function.Name.Should().Be("read_file");
    }

    [Fact]
    public void ParseChunk_UsageInfo_IsCaptured()
    {
        var json = @"{
            ""id"": ""chatcmpl-xxx"",
            ""choices"": [{
                ""index"": 0,
                ""delta"": {},
                ""finish_reason"": ""stop""
            }],
            ""usage"": {
                ""prompt_tokens"": 50,
                ""completion_tokens"": 30,
                ""total_tokens"": 80,
                ""prompt_cache_hit_tokens"": 40,
                ""prompt_cache_miss_tokens"": 10
            }
        }";

        var chunk = JsonSerializer.Deserialize<DeepSeekStreamChunk>(json, JsonOpts);

        chunk.Should().NotBeNull();
        chunk!.Usage.Should().NotBeNull();
        chunk.Usage!.PromptCacheHitTokens.Should().Be(40);
        chunk.Usage.PromptCacheMissTokens.Should().Be(10);
        chunk.Usage.PromptTokens.Should().Be(50);
        chunk.Usage.CompletionTokens.Should().Be(30);
    }

    [Fact]
    public void ParseChunk_MalformedJson_IsHandledGracefully()
    {
        var malformedJson = "this is not valid json";

        var act = () => JsonSerializer.Deserialize<DeepSeekStreamChunk>(malformedJson);

        act.Should().Throw<JsonException>();
    }
}
