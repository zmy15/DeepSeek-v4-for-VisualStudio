using DeepSeek_v4_for_VisualStudio.Services.InlineEdit;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.InlineEdit
{
    /// <summary>
    /// InlineEditService 输出解析单元测试（P1-B）。
    /// </summary>
    public class InlineEditServiceTests
    {
        [Fact]
        public void ExtractReplacement_PlainCode_ReturnsTrimmed()
        {
            var raw = "int x = 1;\nint y = 2;";
            InlineEditService.ExtractReplacement(raw)
                .Should().Be("int x = 1;\nint y = 2;");
        }

        [Fact]
        public void ExtractReplacement_FencedWithLanguage_ReturnsBody()
        {
            var raw = "```cpp\nfoo->Update();\nbar();\n```";
            InlineEditService.ExtractReplacement(raw)
                .Should().Be("foo->Update();\nbar();");
        }

        [Fact]
        public void ExtractReplacement_FencedWithoutClosing_ReturnsRest()
        {
            var raw = "```\nonly-open-fence();";
            InlineEditService.ExtractReplacement(raw)
                .Should().Contain("only-open-fence();");
        }

        [Fact]
        public void ExtractReplacement_ProseBeforeFence_ReturnsFenceBodyOnly()
        {
            var raw = "Here is the replacement:\n```csharp\nvar a = 1;\n```\nHope it helps!";
            var result = InlineEditService.ExtractReplacement(raw);
            result.Should().Be("var a = 1;");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ExtractReplacement_EmptyInput_ReturnsEmpty(string? raw)
        {
            InlineEditService.ExtractReplacement(raw!).Should().BeEmpty();
        }
    }
}
