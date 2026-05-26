namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class EditPatchServiceTests
{
    private readonly EditPatchService _service;

    public EditPatchServiceTests()
    {
        // 使用测试 API Key 创建服务（不需要真实 API 调用）
        var apiService = new DeepSeekApiService("test-key");
        _service = new EditPatchService(apiService);
    }

    #region ParsePatches

    [Fact]
    public void ParsePatches_EmptyInput_ReturnsEmptyList()
    {
        var patches = _service.ParsePatches("");

        patches.Should().BeEmpty();
    }

    [Fact]
    public void ParsePatches_NullInput_ReturnsEmptyList()
    {
        var patches = _service.ParsePatches(null!);

        patches.Should().BeEmpty();
    }

    [Fact]
    public void ParsePatches_SingleUpdatePatch_ParsesCorrectly()
    {
        var aiOutput = @"*** Begin Patch
*** Update File: F:\project\src\Program.cs
@@ class Program
+ using System;
- using System.Collections;
*** End Patch";

        var patches = _service.ParsePatches(aiOutput);

        patches.Should().HaveCount(1);
        patches[0].Action.Should().Be(PatchFileAction.Update);
        patches[0].FilePath.Should().Be(@"F:\project\src\Program.cs");
        patches[0].Hunks.Should().HaveCount(1);
        patches[0].Hunks[0].Lines.Should().HaveCount(2);
        patches[0].Hunks[0].Lines[0].Type.Should().Be('+');
        patches[0].Hunks[0].Lines[0].Text.Should().Be(" using System;");
        patches[0].Hunks[0].Lines[1].Type.Should().Be('-');
        patches[0].Hunks[0].Lines[1].Text.Should().Be(" using System.Collections;");
    }

    [Fact]
    public void ParsePatches_AddFilePatch_ParsesCorrectly()
    {
        var aiOutput = @"*** Begin Patch
*** Add File: F:\project\src\NewFile.cs
@@
+ public class NewFile { }
*** End Patch";

        var patches = _service.ParsePatches(aiOutput);

        patches.Should().HaveCount(1);
        patches[0].Action.Should().Be(PatchFileAction.Add);
        patches[0].FilePath.Should().Be(@"F:\project\src\NewFile.cs");
    }

    [Fact]
    public void ParsePatches_DeleteFilePatch_ParsesCorrectly()
    {
        var aiOutput = @"*** Begin Patch
*** Delete File: F:\project\src\OldFile.cs
*** End Patch";

        var patches = _service.ParsePatches(aiOutput);

        patches.Should().HaveCount(1);
        patches[0].Action.Should().Be(PatchFileAction.Delete);
    }

    [Fact]
    public void ParsePatches_MultiplePatches_AllParsed()
    {
        var aiOutput = @"*** Begin Patch
*** Update File: F:\project\src\FileA.cs
@@ class A
+ new line
*** End Patch
Some text between patches
*** Begin Patch
*** Update File: F:\project\src\FileB.cs
@@ class B
- old line
*** End Patch";

        var patches = _service.ParsePatches(aiOutput);

        patches.Should().HaveCount(2);
        patches[0].FilePath.Should().Be(@"F:\project\src\FileA.cs");
        patches[1].FilePath.Should().Be(@"F:\project\src\FileB.cs");
    }

    [Fact]
    public void ParsePatches_PatchWithMoveTo_ParsesMovePath()
    {
        var aiOutput = @"*** Begin Patch
*** Move to: F:\project\src\Renamed.cs
*** Update File: F:\project\src\Original.cs
@@
*** End Patch";

        var patches = _service.ParsePatches(aiOutput);

        patches.Should().HaveCount(1);
        patches[0].MoveToPath.Should().Be(@"F:\project\src\Renamed.cs");
    }

    #endregion

    #region ParseInsertEdits

    [Fact]
    public void ParseInsertEdits_EmptyInput_ReturnsEmptyList()
    {
        var edits = _service.ParseInsertEdits("");

        edits.Should().BeEmpty();
    }

    [Fact]
    public void ParseInsertEdits_CodeBlockWithFilePath_ParsesCorrectly()
    {
        var aiOutput = @"```insert_edit_into_file: F:\project\src\Program.cs
using System;

namespace MyApp
{
    ...existing code...
    
    public void NewMethod() { }
}
```";

        var edits = _service.ParseInsertEdits(aiOutput);

        edits.Should().HaveCount(1);
        edits[0].FilePath.Should().Be(@"F:\project\src\Program.cs");
        edits[0].FullContent.Should().Contain("using System;");
        edits[0].FullContent.Should().Contain("NewMethod");
    }

    [Fact]
    public void ParseInsertEdits_EditFormat_ParsesCorrectly()
    {
        var aiOutput = @"```edit: F:\project\src\Test.cs
...existing code...
+ new feature code
```";

        var edits = _service.ParseInsertEdits(aiOutput);

        edits.Should().HaveCount(1);
        edits[0].FilePath.Should().Be(@"F:\project\src\Test.cs");
    }

    #endregion

    #region DetectOperationType

    [Fact]
    public void DetectOperationType_PatchFormat_ReturnsApplyPatch()
    {
        var aiOutput = @"*** Begin Patch
*** Update File: test.cs
*** End Patch";

        var type = _service.DetectOperationType(aiOutput);

        type.Should().Be(EditOperationType.ApplyPatch);
    }

    [Fact]
    public void DetectOperationType_InsertEditFormat_ReturnsInsertEditIntoFile()
    {
        var aiOutput = @"```insert_edit_into_file: test.cs
...existing code...
```";

        var type = _service.DetectOperationType(aiOutput);

        type.Should().Be(EditOperationType.InsertEditIntoFile);
    }

    [Fact]
    public void DetectOperationType_ExistingCodeMarker_ReturnsInsertEditIntoFile()
    {
        var aiOutput = "Some text ...existing code... more text";

        var type = _service.DetectOperationType(aiOutput);

        type.Should().Be(EditOperationType.InsertEditIntoFile);
    }

    [Fact]
    public void DetectOperationType_EmptyInput_ReturnsCreateFile()
    {
        var type = _service.DetectOperationType("");

        type.Should().Be(EditOperationType.CreateFile);
    }

    #endregion

    #region MatchWithFallback

    [Fact]
    public void MatchWithFallback_ExactMatch_ReturnsCorrectPosition()
    {
        var fileContent = "line1\nline2\nline3\ntarget line\nline5";
        var searchText = "target line";

        var pos = _service.MatchWithFallback(fileContent, searchText, out var level);

        pos.Should().Be(18); // 0-based position
        level.Should().Be(MatchLevel.Exact);
    }

    [Fact]
    public void MatchWithFallback_WhitespaceDifference_UsesWhitespaceFlexible()
    {
        var fileContent = "line1\nline2\n  target   line  \nline5";
        var searchText = "target line";

        var pos = _service.MatchWithFallback(fileContent, searchText, out var level);

        pos.Should().BeGreaterOrEqualTo(0);
        level.Should().BeOneOf(MatchLevel.Exact, MatchLevel.WhitespaceFlexible);
    }

    [Fact]
    public void MatchWithFallback_NotFound_ReturnsNegativeOne()
    {
        var fileContent = "completely\ndifferent\ncontent";
        var searchText = "something not in the file at all xyz";

        var pos = _service.MatchWithFallback(fileContent, searchText, out var level);

        pos.Should().Be(-1);
    }

    [Fact]
    public void MatchWithFallback_EmptySearch_ReturnsNegative()
    {
        var fileContent = "some content";
        var searchText = "";

        var pos = _service.MatchWithFallback(fileContent, searchText, out var level);

        pos.Should().Be(-1); // 空搜索不应匹配到文件开头（修复：原 return 0 导致纯新增 hunk 插入到错误位置）
    }

    [Fact]
    public void MatchWithFallback_AtStartOfFile_ReturnsZero()
    {
        var fileContent = "first line\nsecond line\nthird line";
        var searchText = "first line";

        var pos = _service.MatchWithFallback(fileContent, searchText, out var level);

        pos.Should().Be(0);
        level.Should().Be(MatchLevel.Exact);
    }

    #endregion
}
