using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Integration;

public class AgentFactoryTests
{
    [Fact]
    public void Constructor_WithValidApiService_CreatesSuccessfully()
    {
        var apiService = new DeepSeekApiService("test-key");

        var factory = new AgentFactory(apiService);

        factory.Should().NotBeNull();
    }

    [Fact]
    public void AskAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.AskAgent;

        agent.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Ask);
    }

    [Fact]
    public void ExploreAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.ExploreAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void PlanAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.PlanAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void EditAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.EditAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void GetAgent_ByType_ReturnsCorrectAgent()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var askAgent = factory.GetAgent(AgentType.Ask);
        var editAgent = factory.GetAgent(AgentType.Edit);

        askAgent.Should().BeOfType<AskAgent>();
        editAgent.Should().BeOfType<EditAgent>();
    }

    [Fact]
    public void UpdateMcpManager_WithNull_DoesNotThrow()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var act = () => factory.UpdateMcpManager(null!);

        act.Should().NotThrow();
    }

    [Fact]
    public void ActivePlan_CanBeSetAndRead()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            Steps = new List<AgentStep>
            {
                new() { Title = "Step 1", Description = "Do something", Status = AgentStepStatus.Pending }
            }
        };

        factory.ActivePlan = plan;

        factory.ActivePlan.Should().Be(plan);
        factory.ActivePlan!.Title.Should().Be("Test Plan");
    }

    [Fact]
    public async Task ExecuteAsync_AskAgent_ReturnsResult()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-99\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Mocked response\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var apiService = new DeepSeekApiService(httpClient);
        var factory = new AgentFactory(apiService);

        var context = new AgentContext
        {
            SolutionPath = @"F:\Test\Test.sln",
            CancellationToken = CancellationToken.None,
        };

        var askAgent = factory.AskAgent;
        var result = await askAgent.ExecuteAsync("Test question", context);

        result.Should().NotBeNull();
    }
}
