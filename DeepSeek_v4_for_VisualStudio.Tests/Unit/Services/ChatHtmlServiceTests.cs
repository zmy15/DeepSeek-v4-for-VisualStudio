using System.Reflection;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ChatHtmlServiceTests
{
    [Fact]
    public void RenderMarkdownToHtml_MarkdownContent_ReturnsHtml()
    {
        var result = ChatHtmlService.RenderMarkdownToHtml("# DeepSeek\n\nHello **world**");

        result.Should().Contain("<h1");
        result.Should().Contain("DeepSeek</h1>");
        result.Should().Contain("<strong>world</strong>");
    }

    [Fact]
    public void RenderMarkdownToHtml_ThinkBlock_RendersReasoningAndAnswer()
    {
        var result = ChatHtmlService.RenderMarkdownToHtml("<think>reason here</think>answer here");

        result.Should().Contain("reasoning-panel");
        result.Should().Contain("reason here");
        result.Should().Contain("answer here");
    }

    [Fact]
    public void EscapeJsString_SpecialCharacters_ReturnsJsonStringLiteral()
    {
        var method = typeof(ChatHtmlService).GetMethod(
            "EscapeJsString",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var result = (string?)method!.Invoke(null, new object[] { "a\"b\nc中文" });

        result.Should().Be("\"a\\\"b\\nc中文\"");
    }
}
