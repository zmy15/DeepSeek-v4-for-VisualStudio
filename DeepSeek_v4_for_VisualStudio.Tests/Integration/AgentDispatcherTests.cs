using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Integration;

public class AgentDispatcherTests
{
    [Fact]
    public void Constructor_WithValidApiService_CreatesSuccessfully()
    {
        var apiService = new DeepSeekApiService("test-key");

        var dispatcher = new AgentDispatcher(apiService);

        dispatcher.Should().NotBeNull();
        dispatcher.ActiveAgentType.Should().Be(AgentType.Ask);
    }

    [Fact]
    public void AskAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);

        var agent = dispatcher.AskAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void ExploreAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);

        var agent = dispatcher.ExploreAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void PlanAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);

        var agent = dispatcher.PlanAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void EditAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);

        var agent = dispatcher.EditAgent;

        agent.Should().NotBeNull();
        // EditAgent 应自动获得 EditPatchService
        agent.EditPatchService.Should().NotBeNull();
    }

    [Fact]
    public void ActiveAgentAllowedTools_WhenNoActiveAgent_ReturnsNull()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);

        // 未执行任何 Agent 时，GetActiveAgent 返回 null
        dispatcher.ActiveAgentAllowedTools.Should().BeNull();
    }

    [Fact]
    public void SetMcpManager_WithNull_DoesNotThrow()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);

        var act = () => dispatcher.SetMcpManager(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ContextManager_CanBeSetAndRead()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);
        var contextManager = new ConversationContextManager();

        dispatcher.ContextManager = contextManager;

        dispatcher.ContextManager.Should().Be(contextManager);
    }

    [Fact]
    public void ActivePlan_CanBeSetAndRead()
    {
        var apiService = new DeepSeekApiService("test-key");
        var dispatcher = new AgentDispatcher(apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            Steps = new List<AgentStep>
            {
                new() { Title = "Step 1", Description = "Do something", Status = AgentStepStatus.Pending }
            }
        };

        dispatcher.ActivePlan = plan;

        dispatcher.ActivePlan.Should().Be(plan);
        dispatcher.ActivePlan!.Title.Should().Be("Test Plan");
    }

    [Fact]
    public async Task ExecuteAsync_WithMockHttp_CallsApi()
    {
        // Arrange: 模拟一个简单响应的 API
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-99\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Mocked response\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var apiService = new DeepSeekApiService(httpClient);
        var dispatcher = new AgentDispatcher(apiService);

        var context = new AgentContext
        {
            SolutionPath = @"F:\Test\Test.sln",
            CancellationToken = CancellationToken.None,
        };

        // Act
        var result = await dispatcher.ExecuteAsync("Test question", context);

        // Assert: AgentDispatcher 应返回结果（即使 mock 响应不完整）
        result.Should().NotBeNull();
    }
}
