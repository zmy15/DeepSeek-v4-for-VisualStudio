using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class BuildAgentTests
{
    [Fact]
    public void AllowedTools_ContainsDirectSearchTools()
    {
        var agent = new BuildAgent(new DeepSeekApiService("test-api-key"));

        agent.Definition.AllowedTools.Should().Contain("file_search");
        agent.Definition.AllowedTools.Should().Contain("grep_search");
        agent.Definition.AllowedTools.Should().Contain("list_dir");
    }
}
