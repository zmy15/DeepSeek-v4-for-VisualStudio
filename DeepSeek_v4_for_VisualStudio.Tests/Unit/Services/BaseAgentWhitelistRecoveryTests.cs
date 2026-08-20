using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 白名单拒绝恢复行为测试：首次拒绝必须让模型看到错误并纠正；
/// 重复拒绝才终止工具循环。
/// </summary>
public class BaseAgentWhitelistRecoveryTests
{
    [Fact]
    public async Task FirstWhitelistRejection_AllowsModelToRecoverWithText()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            ContentSse("当前 Agent 无法执行终端命令。"),
        });
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "read_file" },
            CancellationToken.None);

        result.Should().Contain("当前 Agent 无法执行终端命令");
        result.Should().NotContain("白名单外工具调用重复发生");
        handler.RequestBodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task FirstWhitelistRejection_AllowsHandoffInSameRound()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            MultipleToolCallsSse(
                ("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
                ("request_handoff", "{\"targetAgent\":\"Edit\",\"reason\":\"需要终端\",\"taskDescription\":\"运行 Python 并返回输出\"}")),
        });
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "request_handoff" },
            CancellationToken.None);

        result.Should().Contain("任务已移交给 Edit Agent");
        result.Should().NotContain("白名单外工具调用重复发生");
        handler.RequestBodies.Should().HaveCount(1);
        agent.PendingHandoffRequest.Should().NotBeNull();
        agent.PendingHandoffRequest!.TargetAgent.Should().Be(AgentType.Edit);
    }

    [Fact]
    public async Task RepeatedWhitelistRejection_TerminatesToolLoop()
    {
        var responses = Enumerable.Repeat(
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            5).ToArray();
        var handler = new SequenceHttpMessageHandler(responses);
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "read_file" },
            CancellationToken.None);

        result.Should().Contain("连续 5 轮调用白名单外工具");
        handler.RequestBodies.Should().HaveCount(5);
    }

    [Fact]
    public async Task WhitelistCompliantRound_ResetsWhitelistRejectionCounter()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            ToolCallSse("read_file", "{\"filePath\":\"C:\\\\test\\\\file.txt\"}"),
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            ContentSse("再次说明无法执行终端命令。"),
        });
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "read_file" },
            CancellationToken.None);

        result.Should().Contain("再次说明无法执行终端命令");
        result.Should().NotContain("连续 5 轮调用白名单外工具");
        handler.RequestBodies.Should().HaveCount(4);
    }

    private static RecoveryTestAgent CreateAgent(SequenceHttpMessageHandler handler)
    {
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        return new RecoveryTestAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };
    }

    private static string ContentSse(string content) =>
        "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" +
        content.Replace("\"", "\\\"") + "\"}}]}\n\ndata: [DONE]\n";

    private static string ToolCallSse(string name, string arguments) =>
        "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[" +
        "{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"" + name +
        "\",\"arguments\":\"" + arguments.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}]}}]}\n\ndata: [DONE]\n";

    private static string MultipleToolCallsSse(params (string Name, string Arguments)[] calls)
    {
        var callJson = string.Join(",", calls.Select((call, index) =>
            "{\"index\":" + index + ",\"id\":\"call_" + (index + 1) +
            "\",\"type\":\"function\",\"function\":{\"name\":\"" + call.Name +
            "\",\"arguments\":\"" + call.Arguments.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}"));

        return "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[" +
            callJson + "]}}]}\n\ndata: [DONE]\n";
    }

    private sealed class RecoveryTestAgent : BaseAgent
    {
        public RecoveryTestAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask)
        {
        }

        public Task<string> RunLoopAsync(
            List<ChatApiMessage> messages,
            List<string> whitelist,
            CancellationToken ct)
            => CallAiWithToolLoopAsync(messages, null, ct, toolWhitelist: whitelist);

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                AllowedTools = new List<string>(AskAgent.AskTools),
                SystemPrompt = "test",
            };
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
            => Task.FromResult(new AgentResult { AgentType = AgentType.Ask, Content = userMessage });
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public List<string> RequestBodies { get; } = new();

        public SequenceHttpMessageHandler(IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();
            RequestBodies.Add(body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses.Count > 0 ? _responses.Dequeue() : ContentSse("no response"),
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}
