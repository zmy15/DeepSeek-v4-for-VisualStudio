using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class BaseAgentToolChainTests
{
    [Fact]
    public void CleanIncompleteToolChains_PartialResults_RemovesUnmatchedToolCall()
    {
        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "start" },
            new()
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_complete",
                        Type = "function",
                        Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" },
                    },
                    new()
                    {
                        Id = "call_missing",
                        Type = "function",
                        Function = new ToolCallFunction { Name = "list_dir", Arguments = "{}" },
                    },
                },
            },
            new() { Role = "tool", ToolCallId = "call_complete", Name = "read_file", Content = "ok" },
            new() { Role = "user", Content = "continue" },
        };

        var result = BaseAgent.CleanIncompleteToolChains(messages);

        var assistant = result.Single(m => m.Role == "assistant");
        assistant.ToolCalls.Should().ContainSingle();
        assistant.ToolCalls![0].Id.Should().Be("call_complete");
        result.Should().NotContain(m =>
            m.Role == "assistant"
            && m.ToolCalls != null
            && m.ToolCalls.Any(tc => tc.Id == "call_missing"));

        messages.Single(m => m.Role == "assistant").ToolCalls.Should().HaveCount(2);
    }
}
