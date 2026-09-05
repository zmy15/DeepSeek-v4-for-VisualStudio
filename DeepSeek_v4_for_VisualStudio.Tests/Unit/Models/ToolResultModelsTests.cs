using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models
{
    /// <summary>
    /// ToolExecutionOutcome 结果分类单元测试（P2-B，序号 21）。
    /// </summary>
    public class ToolResultModelsTests
    {
        [Theory]
        [InlineData("ok output", ToolResultKind.Success)]
        [InlineData("", ToolResultKind.Success)]
        [InlineData(null, ToolResultKind.Success)]
        public void Classify_NonPrefixed_IsSuccess(string? raw, ToolResultKind kind)
        {
            ToolExecutionOutcome.Classify(raw).Should().Be(kind);
        }

        [Fact]
        public void Classify_ErrorPrefix_IsToolError()
        {
            ToolExecutionOutcome.Classify("Error: 工具执行失败: file not found")
                .Should().Be(ToolResultKind.ToolError);
        }

        [Fact]
        public void Classify_TimeoutPrefix_IsTimeout()
        {
            ToolExecutionOutcome.Classify("Timeout: 工具 build_solution 执行超时（120s），已终止")
                .Should().Be(ToolResultKind.Timeout);
        }

        [Fact]
        public void FromRaw_PreservesContractFields()
        {
            var o = ToolExecutionOutcome.FromRaw("read_file", "Error: missing", 123);

            o.ToolName.Should().Be("read_file");
            o.Output.Should().Be("Error: missing");       // 外部字符串契约不变
            o.DurationMs.Should().Be(123);
            o.Success.Should().BeFalse();
            o.Kind.Should().Be(ToolResultKind.ToolError);
        }

        [Fact]
        public void FromRaw_SuccessOutput_KeepsSuccess()
        {
            var o = ToolExecutionOutcome.FromRaw("grep_search", "3 matches found", 45);
            o.Success.Should().BeTrue();
            o.Kind.Should().Be(ToolResultKind.Success);
        }
    }
}
