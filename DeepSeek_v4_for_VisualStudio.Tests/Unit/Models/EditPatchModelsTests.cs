namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class EditPatchModelsTests
{
    [Fact]
    public void PatchOperation_Defaults_AreSetCorrectly()
    {
        var patch = new PatchOperation();

        patch.Action.Should().Be(PatchFileAction.Update);
        patch.FilePath.Should().BeEmpty();
        patch.Hunks.Should().NotBeNull().And.BeEmpty();
        patch.RawText.Should().BeEmpty();
    }

    [Fact]
    public void PatchHunk_Defaults_AreSetCorrectly()
    {
        var hunk = new PatchHunk();

        hunk.ContextMarkers.Should().NotBeNull().And.BeEmpty();
        hunk.Lines.Should().NotBeNull().And.BeEmpty();
        hunk.RawText.Should().BeEmpty();
    }

    [Fact]
    public void PatchLine_Type_CorrectlyRepresented()
    {
        var addLine = new PatchLine { Type = '+', Text = "new code" };
        var delLine = new PatchLine { Type = '-', Text = "old code" };
        var ctxLine = new PatchLine { Type = ' ', Text = "context" };

        addLine.Type.Should().Be('+');
        addLine.Text.Should().Be("new code");

        delLine.Type.Should().Be('-');
        delLine.Text.Should().Be("old code");

        ctxLine.Type.Should().Be(' ');
        ctxLine.Text.Should().Be("context");
    }

    [Fact]
    public void InsertEditOperation_Defaults_AreSetCorrectly()
    {
        var edit = new InsertEditOperation();

        edit.FilePath.Should().BeEmpty();
        edit.FullContent.Should().BeEmpty();
    }

    [Fact]
    public void InsertEditOperation_ExistingCodeMarker_IsConstant()
    {
        InsertEditOperation.ExistingCodeMarker.Should().Be("...existing code...");
    }

    [Fact]
    public void MatchLevel_Enum_HasFourLevels()
    {
        var values = Enum.GetValues(typeof(MatchLevel));

        values.Length.Should().Be(4); // Exact, WhitespaceFlexible, Fuzzy, Levenshtein
        ((MatchLevel)values.GetValue(0)!).Should().Be(MatchLevel.Exact);
    }

    [Fact]
    public void EditOperationType_Enum_HasAllVariants()
    {
        var values = Enum.GetValues(typeof(EditOperationType));

        values.Length.Should().Be(5); // ApplyPatch, InsertEditIntoFile, CreateFile, DeleteFile, MoveFile
    }
}
