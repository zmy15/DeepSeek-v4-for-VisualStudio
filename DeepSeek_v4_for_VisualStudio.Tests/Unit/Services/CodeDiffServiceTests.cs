using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class CodeDiffServiceTests
{
    #region ComputeDiff

    [Fact]
    public void ComputeDiff_SameText_ReturnsAllUnchanged()
    {
        var text = "line1\nline2\nline3";
        var result = CodeDiffService.ComputeDiff(text, text);

        result.Should().HaveCount(3);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Unchanged));
        result[0].Content.Should().Be("line1");
        result[0].OldLineNumber.Should().Be(1);
        result[0].NewLineNumber.Should().Be(1);
    }

    [Fact]
    public void ComputeDiff_OneLineAdded_ReturnsAdded()
    {
        var oldText = "line1";
        var newText = "line1\nline2";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().HaveCount(2);
        result[0].Type.Should().Be(DiffLineType.Unchanged);
        result[1].Type.Should().Be(DiffLineType.Added);
        result[1].Content.Should().Be("line2");
        result[1].NewLineNumber.Should().Be(2);
    }

    [Fact]
    public void ComputeDiff_OneLineDeleted_ReturnsDeleted()
    {
        var oldText = "line1\nline2";
        var newText = "line1";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().HaveCount(2);
        result[0].Type.Should().Be(DiffLineType.Unchanged);
        result[1].Type.Should().Be(DiffLineType.Deleted);
        result[1].Content.Should().Be("line2");
        result[1].OldLineNumber.Should().Be(2);
    }

    [Fact]
    public void ComputeDiff_OneLineChanged_ReturnsDeletedAndAdded()
    {
        var oldText = "old line";
        var newText = "new line";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().Contain(d => d.Type == DiffLineType.Deleted && d.Content == "old line");
        result.Should().Contain(d => d.Type == DiffLineType.Added && d.Content == "new line");
    }

    [Fact]
    public void ComputeDiff_EmptyOld_ReturnsAllAdded()
    {
        var newText = "line1\nline2";

        var result = CodeDiffService.ComputeDiff("", newText);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Added));
    }

    [Fact]
    public void ComputeDiff_EmptyNew_ReturnsAllDeleted()
    {
        var oldText = "line1\nline2";

        var result = CodeDiffService.ComputeDiff(oldText, "");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Deleted));
    }

    [Fact]
    public void ComputeDiff_BothEmpty_ReturnsEmpty()
    {
        var result = CodeDiffService.ComputeDiff("", "");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDiff_WindowsLineEndings_NormalizedCorrectly()
    {
        var oldText = "a\r\nb\r\nc";
        var newText = "a\nb\nc";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().HaveCount(3);
        result.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Unchanged));
    }

    [Fact]
    public void ComputeDiff_MiddleInsertion_HandledCorrectly()
    {
        var oldText = "a\nb\nd";
        var newText = "a\nb\nc\nd";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().ContainSingle(d => d.Type == DiffLineType.Added && d.Content == "c");
        result.Count(d => d.Type == DiffLineType.Unchanged).Should().Be(3);
    }

    [Fact]
    public void ComputeDiff_MultipleChanges_AllCaptured()
    {
        var oldText = "keep1\nremove1\nkeep2\nremove2";
        var newText = "keep1\nadd1\nkeep2\nadd2";

        var result = CodeDiffService.ComputeDiff(oldText, newText);

        result.Should().Contain(d => d.Type == DiffLineType.Deleted && d.Content == "remove1");
        result.Should().Contain(d => d.Type == DiffLineType.Deleted && d.Content == "remove2");
        result.Should().Contain(d => d.Type == DiffLineType.Added && d.Content == "add1");
        result.Should().Contain(d => d.Type == DiffLineType.Added && d.Content == "add2");
        result.Should().Contain(d => d.Type == DiffLineType.Unchanged && d.Content == "keep1");
        result.Should().Contain(d => d.Type == DiffLineType.Unchanged && d.Content == "keep2");
    }

    [Fact]
    public void ComputeDiff_NullInput_HandlesGracefully()
    {
        var result = CodeDiffService.ComputeDiff(null!, "test");
        // Should not throw
    }

    #endregion
}
