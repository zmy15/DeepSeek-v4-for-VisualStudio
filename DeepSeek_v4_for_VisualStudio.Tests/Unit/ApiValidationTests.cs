using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using FluentAssertions;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit
{
    public class ApiValidationTests
    {
        private class CaptureHandler : DelegatingHandler
        {
            public string? LastRequestBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Content != null)
                    LastRequestBody = await request.Content.ReadAsStringAsync();

                var responseJson = JsonSerializer.Serialize(new DeepSeekChatResponse
                {
                    Id = "test",
                    Choices = new List<DeepSeekChoice> { new DeepSeekChoice { Index = 0, Message = new DeepSeekMessage { Role = "assistant", Content = "ok" } } }
                });

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
                return resp;
            }
        }

        [Fact]
        public void ConversationContextManager_BuildApiMessages_Includes_ReasoningContent_When_ToolCalls()
        {
            var mgr = new ConversationContextManager();
            var toolCall = new ToolCall { Id = "1", Type = "function", Function = new ToolCallFunction { Name = "f", Arguments = "{}" } };
            // 添加 assistant 消息，toolCalls 非空，但 reasoningContent 为 null
            mgr.AddAssistantMessage(content: "partial", reasoningContent: null, toolCalls: new List<ToolCall> { toolCall });

            var msgs = mgr.BuildApiMessages();
            msgs.Should().NotBeNull();
            msgs.Should().ContainSingle(m => m.Role == "assistant");
            var assistant = msgs.Find(m => m.Role == "assistant");
            assistant.Should().NotBeNull();
            // 当存在 tool_calls 时，ReasoningContent 应当存在（即使为空字符串）
            assistant!.ReasoningContent.Should().Be("");
        }

        [Fact]
        public async Task DeepSeekApiService_CompleteAsync_Includes_ReasoningContent_When_ToolCalls()
        {
            var handler = new CaptureHandler();
            var http = new HttpClient(handler) { BaseAddress = new System.Uri("https://api.deepseek.com") };
            var svc = new DeepSeekApiService(http, model: "test-model");

            var assistantMsg = new ChatApiMessage
            {
                Role = "assistant",
                Content = "partial",
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "1", Function = new ToolCallFunction { Name = "f", Arguments = "{}" } } }
                // ReasoningContent intentionally null
            };

            var result = await svc.CompleteAsync(new[] { assistantMsg });

            handler.LastRequestBody.Should().NotBeNullOrEmpty();
            // 请求体应当包含 "reasoning_content": "" for the assistant message
            handler.LastRequestBody!.Should().Contain("\"reasoning_content\":\"\"");
        }
    }
}
