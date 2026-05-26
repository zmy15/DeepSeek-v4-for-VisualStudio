using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using FluentAssertions;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class EditStringMatcherTests
{
    #region MatchWithFallback

    [Fact]
    public void MatchWithFallback_ExactMatch_ReturnsCorrectPosition()
    {
        var content = "line1\nline2\ntarget\nline4";
        var pos = EditStringMatcher.MatchWithFallback(content, "target", out var level);
        pos.Should().Be(12);
        level.Should().Be(MatchLevel.Exact);
    }

    [Fact]
    public void MatchWithFallback_NoMatch_ReturnsNegativeOne()
    {
        var pos = EditStringMatcher.MatchWithFallback("line1\nline2", "nonexistent", out _);
        pos.Should().Be(-1);
    }

    [Fact]
    public void MatchWithFallback_EmptySearch_ReturnsNegativeOne()
    {
        var pos = EditStringMatcher.MatchWithFallback("content", "", out var level);
        pos.Should().Be(-1);
        level.Should().Be(MatchLevel.Exact);
    }

    [Fact]
    public void MatchWithFallback_WhitespaceFlexible_FindsMatch()
    {
        var content = "line1\n  target   line  \nline3";
        var pos = EditStringMatcher.MatchWithFallback(content, "target line", out var level);
        pos.Should().BeGreaterOrEqualTo(0);
    }

    #endregion

    #region MatchContextInFileLines

    [Fact]
    public void MatchContextInFileLines_ExactMatch_FindsPosition()
    {
        var fileLines = new[] { "class A", "{", "  void M()", "  {", "  }", "}" };
        var ctxLines = new[] { "  void M()", "  {" };
        var match = EditStringMatcher.MatchContextInFileLines(fileLines, ctxLines, 0, out var level);
        match.Should().Be(2);
        level.Should().Be(MatchLevel.Exact);
    }

    [Fact]
    public void MatchContextInFileLines_NoMatch_ReturnsNegativeOne()
    {
        var match = EditStringMatcher.MatchContextInFileLines(
            new[] { "a", "b" }, new[] { "x" }, 0, out _);
        match.Should().Be(-1);
    }

    #endregion

    #region NormalizeUnicode

    [Fact]
    public void NormalizeUnicode_EmDash_ConvertsToHyphen()
    {
        var result = EditStringMatcher.NormalizeUnicode("foo\u2014bar");
        result.Should().Be("foo-bar");
    }

    #endregion

    #region GetLineColumn

    [Fact]
    public void GetLineColumn_FirstLine_ReturnsZero()
    {
        var (line, col) = EditStringMatcher.GetLineColumn("abc\ndef", 0);
        line.Should().Be(0);
        col.Should().Be(0);
    }

    [Fact]
    public void GetLineColumn_SecondLine_CorrectOffset()
    {
        var (line, col) = EditStringMatcher.GetLineColumn("abc\ndef", 5);
        line.Should().Be(1);
        col.Should().Be(1);
    }

    #endregion
}
