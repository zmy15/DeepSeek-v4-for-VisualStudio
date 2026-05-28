using DeepSeek_v4_for_VisualStudio.CodeCompletion;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.CodeCompletion;

/// <summary>
/// InlinePredictionManager 单元测试 — 通过反射测试私有静态工具方法
/// （FormatPrediction, NormalizeLineBreaks, RemoveBlankLines）。
/// 注意：实例方法（构造函数 / NotifySuggestionAccepted / ShowAutocompleteAsync）
/// 依赖 DeepSeekOptionsPage（继承自 DialogPage，需要 VS interop 运行时），
/// 无法在纯单元测试环境中实例化，属于集成测试范畴。
/// </summary>
public class InlinePredictionManagerTests
{
    private static readonly string NL = Environment.NewLine;

    #region FormatPrediction

    [Fact]
    public void FormatPrediction_PlainText_ReturnsTrimmed()
    {
        var result = FormatPrediction("  public void Test() { }  ");
        result.Should().Be("public void Test() { }");
    }

    [Fact]
    public void FormatPrediction_WithCodeBlockMarkers_RemovesThem()
    {
        var result = FormatPrediction($"```csharp{NL}public class Foo {{ }}{NL}```");
        result.Should().Be("public class Foo { }");
    }

    [Fact]
    public void FormatPrediction_WithLeadingCodeBlockOnly_RemovesIt()
    {
        var result = FormatPrediction($"```{NL}var x = 1;");
        result.Should().Be("var x = 1;");
    }

    [Fact]
    public void FormatPrediction_WithTrailingCodeBlockOnly_RemovesIt()
    {
        var result = FormatPrediction($"var x = 1;{NL}```");
        result.Should().Be("var x = 1;");
    }

    [Fact]
    public void FormatPrediction_Empty_ReturnsEmpty()
    {
        FormatPrediction("").Should().BeEmpty();
    }

    [Fact]
    public void FormatPrediction_Null_ReturnsEmpty()
    {
        FormatPrediction(null!).Should().BeEmpty();
    }

    [Fact]
    public void FormatPrediction_WhitespaceOnly_ReturnsEmpty()
    {
        FormatPrediction($"   {NL}  ").Should().BeEmpty();
    }

    [Fact]
    public void FormatPrediction_LanguageTaggedCodeBlock_RemovesTag()
    {
        var result = FormatPrediction($"```python{NL}print('hello'){NL}```");
        result.Should().Be("print('hello')");
    }

    [Fact]
    public void FormatPrediction_ExcessiveBlankLines_ReducedToTwo()
    {
        var result = FormatPrediction($"line1{NL}{NL}{NL}{NL}{NL}line2");
        result.Should().Be($"line1{NL}{NL}line2");
    }

    #endregion

    #region NormalizeLineBreaks

    [Fact]
    public void NormalizeLineBreaks_WindowsStyle_PreservesLines()
    {
        var result = NormalizeLineBreaks($"line1{NL}line2{NL}line3");
        result.Should().Be($"line1{NL}line2{NL}line3");
    }

    [Fact]
    public void NormalizeLineBreaks_UnixStyle_ConvertsToEnvironment()
    {
        var result = NormalizeLineBreaks("line1\nline2\nline3");
        result.Should().Be($"line1{NL}line2{NL}line3");
    }

    [Fact]
    public void NormalizeLineBreaks_MixedStyle_UnifiesCorrectly()
    {
        var result = NormalizeLineBreaks($"line1\r\nline2\nline3\r\nline4");
        result.Should().Be($"line1{NL}line2{NL}line3{NL}line4");
    }

    [Fact]
    public void NormalizeLineBreaks_SingleLine_ReturnsUnchanged()
    {
        NormalizeLineBreaks("just one line").Should().Be("just one line");
    }

    [Fact]
    public void NormalizeLineBreaks_Empty_ReturnsEmpty()
    {
        NormalizeLineBreaks("").Should().BeEmpty();
    }

    [Fact]
    public void NormalizeLineBreaks_Null_ReturnsNull()
    {
        NormalizeLineBreaks(null!).Should().BeNull();
    }

    [Fact]
    public void NormalizeLineBreaks_OnlyCarriageReturn_Normalizes()
    {
        var result = NormalizeLineBreaks("a\rb\rc");
        result.Should().Be($"a{NL}b{NL}c");
    }

    #endregion

    #region RemoveBlankLines

    [Fact]
    public void RemoveBlankLines_NoBlankLines_ReturnsSame()
    {
        var result = RemoveBlankLines($"line1{NL}line2{NL}line3");
        result.Should().Be($"line1{NL}line2{NL}line3");
    }

    [Fact]
    public void RemoveBlankLines_SingleBlankLine_KeepsIt()
    {
        var result = RemoveBlankLines($"line1{NL}{NL}line2");
        result.Should().Be($"line1{NL}{NL}line2");
    }

    [Fact]
    public void RemoveBlankLines_TwoBlankLines_KeepsTwo()
    {
        // 3 consecutive newlines (2 blank lines) — regex {3,} matches, replaced with NL+NL
        var result = RemoveBlankLines($"line1{NL}{NL}{NL}line2");
        result.Should().Be($"line1{NL}{NL}line2");
    }

    [Fact]
    public void RemoveBlankLines_ManyBlankLines_ReducedToTwo()
    {
        var result = RemoveBlankLines($"top{NL}{NL}{NL}{NL}{NL}{NL}{NL}{NL}bottom");
        result.Should().Be($"top{NL}{NL}bottom");
    }

    [Fact]
    public void RemoveBlankLines_Empty_ReturnsEmpty()
    {
        RemoveBlankLines("").Should().BeEmpty();
    }

    [Fact]
    public void RemoveBlankLines_Null_ReturnsNull()
    {
        RemoveBlankLines(null!).Should().BeNull();
    }

    [Fact]
    public void RemoveBlankLines_OnlyBlankLines_ReturnsReduced()
    {
        var result = RemoveBlankLines($"{NL}{NL}{NL}{NL}{NL}").Trim();
        result.Should().BeEmpty();
    }

    #endregion

    #region Reflection Helpers

    private static string FormatPrediction(string prediction)
        => CallPrivateStatic<string>("FormatPrediction", prediction);

    private static string NormalizeLineBreaks(string text)
        => CallPrivateStatic<string>("NormalizeLineBreaks", text);

    private static string RemoveBlankLines(string text)
        => CallPrivateStatic<string>("RemoveBlankLines", text);

    private static T CallPrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(InlinePredictionManager).GetMethod(methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (T)method!.Invoke(null, args)!;
    }

    #endregion
}
