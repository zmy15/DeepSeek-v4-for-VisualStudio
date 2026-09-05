using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// P1-3 回归测试：DeepSeekApiService 消息清理（Rule5/6、ReasoningContent 注入）不得篡改调用方消息对象。
/// 修复前：无消息被移除/合并时 request.Messages 仍持有调用方原引用，Rule5 就地改 m.ToolCalls/ReasoningContent。
/// 修复后：CloneMessage 深克隆（含 ToolCalls 内元素），对克隆的修改绝不污染原消息。
/// </summary>
public class DeepSeekApiServiceCloneTests
{
    [Fact]
    public void CloneMessage_ProducesIndependentToolCalls()
    {
        var original = new ChatApiMessage
        {
            Role = "assistant",
            Content = "hi",
            ReasoningContent = "thinking...",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "call_1", Type = "function", Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" } },
            },
        };

        var clone = DeepSeekApiService.CloneMessage(original);

        clone.Should().NotBeSameAs(original);
        clone.ToolCalls.Should().NotBeNull();
        clone.ToolCalls.Should().NotBeSameAs(original.ToolCalls, "ToolCalls 列表必须深拷贝");
        clone.ToolCalls![0].Should().NotBeSameAs(original.ToolCalls![0], "ToolCall 元素必须深拷贝");
        clone.ToolCalls[0].Function.Should().NotBeSameAs(original.ToolCalls[0].Function, "ToolCallFunction 必须深拷贝");

        // 值等价
        clone.Role.Should().Be(original.Role);
        clone.Content.Should().Be(original.Content);
        clone.ReasoningContent.Should().Be(original.ReasoningContent);
        clone.ToolCalls[0].Id.Should().Be("call_1");
        clone.ToolCalls[0].Function.Name.Should().Be("read_file");
    }

    [Fact]
    public void MutatingClone_DoesNotAffectOriginal()
    {
        var original = new ChatApiMessage
        {
            Role = "assistant",
            Content = "hi",
            ReasoningContent = "thinking...",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "call_1", Type = "function", Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" } },
            },
        };

        var clone = DeepSeekApiService.CloneMessage(original);

        // 模拟 Rule5 的就地清理
        clone.ToolCalls = null;
        clone.ReasoningContent = null;

        // 调用方原消息必须不受影响
        original.ToolCalls.Should().NotBeNull();
        original.ToolCalls!.Should().ContainSingle();
        original.ReasoningContent.Should().Be("thinking...");
    }

    [Fact]
    public void CloneMessage_HandlesNullCollections()
    {
        var original = new ChatApiMessage
        {
            Role = "user",
            Content = "plain",
            ReasoningContent = null,
            ToolCalls = null,
            MultimodalContent = null,
        };

        var clone = DeepSeekApiService.CloneMessage(original);

        clone.ToolCalls.Should().BeNull();
        clone.MultimodalContent.Should().BeNull();
        clone.ReasoningContent.Should().BeNull();
        clone.Content.Should().Be("plain");
    }
}
