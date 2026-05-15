using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

public class StringExtensionsTests
{
    [Fact]
    public void Truncate_ShorterThanMax_ReturnsOriginal()
    {
        var result = "hello".Truncate(100);

        result.Should().Be("hello");
    }

    [Fact]
    public void Truncate_LongerThanMax_TruncatesWithEllipsis()
    {
        var result = "this is a very long string that exceeds the limit".Truncate(20);

        result.Should().HaveLength(20);
        result.Should().EndWith("\u2026"); // ellipsis char
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsOriginal()
    {
        var input = "1234567890";
        var result = input.Truncate(10);

        result.Should().Be("1234567890");
    }

    [Fact]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        var result = string.Empty.Truncate(10);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Truncate_MinimalValidLength_ReturnsEllipsis()
    {
        // Truncate(1) returns just the ellipsis char
        var result = "test".Truncate(1);

        result.Should().Be("\u2026"); // just ellipsis
    }
}
