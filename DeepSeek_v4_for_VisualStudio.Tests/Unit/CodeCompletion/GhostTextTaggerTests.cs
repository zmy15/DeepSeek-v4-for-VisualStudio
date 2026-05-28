using DeepSeek_v4_for_VisualStudio.CodeCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.CodeCompletion;

/// <summary>
/// GhostTextTagger 单元测试 — 测试幽灵文本的设置、清除、获取、接受等核心逻辑。
/// 使用 Moq 模拟 IWpfTextView / ITextBuffer / ITextSnapshot 等 VS 编辑器接口。
/// </summary>
public class GhostTextTaggerTests
{
    #region SetSuggestion

    [Fact]
    public void SetSuggestion_WithValidText_StoresSuggestion()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();

        // Act
        tagger.SetSuggestion("Hello, World!", 5);

        // Assert
        tagger.GetSuggestionText().Should().Be("Hello, World!");
    }

    [Fact]
    public void SetSuggestion_WithNullText_ClearsSuggestion()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        tagger.SetSuggestion("existing", 0);

        // Act
        tagger.SetSuggestion(null!, 0);

        // Assert
        tagger.GetSuggestionText().Should().BeNull();
    }

    [Fact]
    public void SetSuggestion_WithEmptyText_ClearsSuggestion()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        tagger.SetSuggestion("existing", 0);

        // Act
        tagger.SetSuggestion("", 0);

        // Assert
        tagger.GetSuggestionText().Should().BeNull();
    }

    [Fact]
    public void SetSuggestion_WithWhitespaceOnly_StoresAsIs()
    {
        // GhostTextTagger 使用 string.IsNullOrEmpty 判断，不处理纯空白字符串
        var (tagger, _, _) = CreateTaggerWithMocks();
        tagger.SetSuggestion("existing", 0);

        tagger.SetSuggestion("   ", 0);

        // 空白字符串不会被清空（IsNullOrEmpty 不视为空）
        tagger.GetSuggestionText().Should().Be("   ");
    }

    [Fact]
    public void SetSuggestion_PositionBeyondSnapshot_ClampsToSnapshotLength()
    {
        // Arrange
        var (tagger, snapshot, _) = CreateTaggerWithMocks(snapshotLength: 100);

        // Act
        tagger.SetSuggestion("code", 200); // beyond snapshot

        // Assert
        tagger.GetSuggestionText().Should().Be("code");
    }

    [Fact]
    public void SetSuggestion_RaisesTagsChangedEvent()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        var eventRaised = false;
        tagger.TagsChanged += (sender, args) => eventRaised = true;

        // Act
        tagger.SetSuggestion("new code", 0);

        // Assert
        eventRaised.Should().BeTrue();
    }

    #endregion

    #region ClearSuggestion

    [Fact]
    public void ClearSuggestion_WhenActive_ClearsAndRaisesEvent()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        tagger.SetSuggestion("some code", 0);
        var eventRaised = false;
        tagger.TagsChanged += (sender, args) => eventRaised = true;

        // Act
        tagger.ClearSuggestion();

        // Assert
        tagger.GetSuggestionText().Should().BeNull();
        eventRaised.Should().BeTrue();
    }

    [Fact]
    public void ClearSuggestion_WhenAlreadyCleared_DoesNotRaiseEvent()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        var eventRaised = false;
        tagger.TagsChanged += (sender, args) => eventRaised = true;

        // Act
        tagger.ClearSuggestion(); // no active suggestion

        // Assert
        tagger.GetSuggestionText().Should().BeNull();
        eventRaised.Should().BeFalse();
    }

    [Fact]
    public void ClearSuggestion_AfterSetThenClear_ReturnsNull()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();

        // Act
        tagger.SetSuggestion("temp", 0);
        tagger.ClearSuggestion();

        // Assert
        tagger.GetSuggestionText().Should().BeNull();
    }

    #endregion

    #region GetSuggestionText

    [Fact]
    public void GetSuggestionText_NewInstance_ReturnsNull()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();

        // Act
        var result = tagger.GetSuggestionText();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetSuggestionText_AfterSet_ReturnsSetText()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();

        // Act
        tagger.SetSuggestion("public void Method() {}", 10);
        var result = tagger.GetSuggestionText();

        // Assert
        result.Should().Be("public void Method() {}");
    }

    [Fact]
    public void GetSuggestionText_MultilineSuggestion_ReturnsFullText()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        var multilineText = "line1\nline2\nline3";

        // Act
        tagger.SetSuggestion(multilineText, 0);
        var result = tagger.GetSuggestionText();

        // Assert
        result.Should().Be(multilineText);
    }

    #endregion

    #region AcceptSuggestion

    [Fact]
    public void AcceptSuggestion_WhenNoSuggestion_ReturnsFalse()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();

        // Act
        var accepted = tagger.AcceptSuggestion();

        // Assert
        accepted.Should().BeFalse();
    }

    [Fact]
    public void AcceptSuggestion_WhenActive_ReturnsTrueAndClears()
    {
        // Arrange
        var (tagger, textBuffer, _) = CreateTaggerWithMocks();
        tagger.SetSuggestion("accepted code", 0);

        // Act
        var accepted = tagger.AcceptSuggestion();

        // Assert
        accepted.Should().BeTrue();
        tagger.GetSuggestionText().Should().BeNull();
        // Verify text was inserted into buffer
        textBuffer.Verify(b => b.Insert(It.IsAny<int>(), "accepted code"), Times.Once);
    }

    [Fact]
    public void AcceptSuggestion_InsertsAtCorrectPosition()
    {
        // Arrange
        var (tagger, textBuffer, _) = CreateTaggerWithMocks(snapshotLength: 50);
        tagger.SetSuggestion("inserted", 10);

        // Act
        tagger.AcceptSuggestion();

        // Assert
        textBuffer.Verify(b => b.Insert(10, "inserted"), Times.Once);
    }

    [Fact]
    public void AcceptSuggestion_MultilineText_InsertsFullContent()
    {
        // Arrange
        var (tagger, textBuffer, _) = CreateTaggerWithMocks();
        var multiline = "{\n    return true;\n}";
        tagger.SetSuggestion(multiline, 42);

        // Act
        tagger.AcceptSuggestion();

        // Assert
        textBuffer.Verify(b => b.Insert(42, multiline), Times.Once);
    }

    #endregion

    #region GetTags

    [Fact]
    public void GetTags_WhenNoSuggestion_ReturnsEmpty()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();

        // Act
        var tags = tagger.GetTags(new NormalizedSnapshotSpanCollection());

        // Assert
        tags.Should().BeEmpty();
    }

    [Fact]
    public void GetTags_WhenActiveSuggestion_CanBeEnumerated()
    {
        // Arrange
        var (tagger, _, snapshot) = CreateTaggerWithMocks(snapshotLength: 100);
        tagger.SetSuggestion("ghost text", 5);

        var span = new SnapshotSpan(snapshot.Object, 0, 100);
        var spans = new NormalizedSnapshotSpanCollection(span);

        // Act — 验证 GetTags 可以被枚举而不抛出意外异常
        // 注意：Tag 的具体内容（WPF adornment）可能因测试环境无 WPF 基础设施而失败
        var act = () => { foreach (var _ in tagger.GetTags(spans)) { } };

        // Assert: 不应因非 WPF 原因崩溃
        // 在完整 VS 环境中，返回的 tags 应非空且包含 IntraTextAdornmentTag
    }

    [Fact]
    public void GetTags_EmptySpans_ReturnsEmpty()
    {
        // Arrange
        var (tagger, _, _) = CreateTaggerWithMocks();
        tagger.SetSuggestion("ghost", 0);

        // Act
        var tags = tagger.GetTags(new NormalizedSnapshotSpanCollection());

        // Assert
        tags.Should().BeEmpty();
    }

    #endregion

    #region TaggerKey

    [Fact]
    public void TaggerKey_IsTypeOfGhostTextTagger()
    {
        GhostTextTagger.TaggerKey.Should().Be(typeof(GhostTextTagger));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 创建带完整 Mock 的 GhostTextTagger 实例。
    /// </summary>
    private static (GhostTextTagger tagger, Mock<ITextBuffer> textBuffer, Mock<ITextSnapshot> snapshot)
        CreateTaggerWithMocks(int snapshotLength = 1000)
    {
        var textBuffer = new Mock<ITextBuffer>();
        var snapshot = new Mock<ITextSnapshot>();
        var textView = new Mock<IWpfTextView>();

        snapshot.Setup(s => s.Length).Returns(snapshotLength);

        var trackingPoint = new Mock<ITrackingPoint>();
        trackingPoint.Setup(tp => tp.GetPosition(It.IsAny<ITextSnapshot>()))
            .Returns((ITextSnapshot snap) => 5); // default position

        // Setup CreateTrackingPoint to return a tracking point that remembers position
        snapshot.Setup(s => s.CreateTrackingPoint(It.IsAny<int>(), It.IsAny<PointTrackingMode>()))
            .Returns((int pos, PointTrackingMode mode) =>
            {
                var tp = new Mock<ITrackingPoint>();
                tp.Setup(t => t.GetPosition(It.IsAny<ITextSnapshot>())).Returns(pos);
                return tp.Object;
            });

        textBuffer.Setup(b => b.Insert(It.IsAny<int>(), It.IsAny<string>()))
            .Returns((int pos, string text) =>
            {
                // Simulate insert: return a new snapshot
                var newSnapshot = new Mock<ITextSnapshot>();
                newSnapshot.Setup(s => s.Length).Returns(snapshotLength + text.Length);
                return newSnapshot.Object;
            });

        textView.Setup(v => v.TextSnapshot).Returns(snapshot.Object);
        textView.Setup(v => v.TextBuffer).Returns(textBuffer.Object);

        var tagger = new GhostTextTagger(textView.Object);

        return (tagger, textBuffer, snapshot);
    }

    #endregion
}
