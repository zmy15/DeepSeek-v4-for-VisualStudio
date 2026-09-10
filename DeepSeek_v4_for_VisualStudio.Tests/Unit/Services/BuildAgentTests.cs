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

    [Fact]
    public void SystemPrompt_UsesSingleBuildWorkflow()
    {
        var agent = new BuildAgent(new DeepSeekApiService("test-api-key"));

        agent.Definition.SystemPrompt.Should().Contain("build_solution");
        agent.Definition.SystemPrompt.Should().NotContain("build_solution 是异步的");
        agent.Definition.SystemPrompt.Should().NotContain("build_solution is async");
        agent.Definition.SystemPrompt.Should().NotContain("Turn 2");
    }
}
