using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using BuiltInApplyPatchTool = DeepSeek_v4_for_VisualStudio.Services.BuiltInTools.ApplyPatchTool;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 验证 apply_patch 对同一 hunk 内多个分离编辑段的解析和重建。
/// </summary>
public class ApplyPatchToolTests
{
    [Fact]
    public void ParseExpectedLineNumberedContent_ParsesCommonFormats()
    {
        var parsed = BuiltInApplyPatchTool.ParseExpectedLineNumberedContent(
            "1|class Program\n2: {\n3→    static void Main() {}\n4.}");

        parsed.Success.Should().BeTrue();
        parsed.Lines.Should().Equal(
            "class Program",
            "{",
            "    static void Main() {}",
            "}");
    }

    [Fact]
    public void VerifyExpectedContent_MatchesActualFileContent()
    {
        string expected = "1|class Program\n2|{\n3|    static void Main() {}\n4|}";
        string actual = "class Program\r\n{\r\n    static void Main() {}\r\n}";

        string result = BuiltInApplyPatchTool.VerifyExpectedContent(
            expected, actual, "Program.cs", expectDeleted: false);

        result.Should().NotStartWith("Error: ");
    }

    [Fact]
    public void VerifyExpectedContent_ReturnsFirstDifferenceAndLineNumberedCurrentState()
    {
        string expected = "1|alpha\n2|expected\n3|omega";
        string actual = "alpha\nactual\nomega";

        string result = BuiltInApplyPatchTool.VerifyExpectedContent(
            expected, actual, "sample.cs", expectDeleted: false);

        result.Should().StartWith("Error: ");
        result.Should().Contain("2|expected");
        result.Should().Contain("2|actual");
        result.Should().Contain("1|alpha");
        result.Should().Contain("3|omega");
    }

    [Fact]
    public void HunkToChunk_SplitsSeparatedEditsIntoMultipleSegments()
    {
        var hunk = new PatchHunk
        {
            Lines =
            {
                new PatchLine { Type = ' ', Text = "void Run()" },
                new PatchLine { Type = ' ', Text = "{" },
                new PatchLine { Type = '-', Text = "    alpha();" },
                new PatchLine { Type = '+', Text = "    alpha1();" },
                new PatchLine { Type = ' ', Text = "    beta();" },
                new PatchLine { Type = '-', Text = "    gamma();" },
                new PatchLine { Type = '+', Text = "    gamma1();" },
                new PatchLine { Type = ' ', Text = "}" },
            },
        };

        var (chunk, contextLines) = ApplyPatchTool.HunkToChunk(hunk);

        chunk.Should().NotBeNull();
        chunk!.Segments.Should().HaveCount(2);
        chunk.Segments[0].Offset.Should().Be(2);
        chunk.Segments[0].DelLines.Should().Equal("    alpha();");
        chunk.Segments[0].InsLines.Should().Equal("    alpha1();");
        chunk.Segments[1].Offset.Should().Be(4);
        chunk.Segments[1].DelLines.Should().Equal("    gamma();");
        chunk.Segments[1].InsLines.Should().Equal("    gamma1();");
        contextLines.Should().Equal(
            "void Run()",
            "{",
            "    alpha();",
            "    beta();",
            "    gamma();",
            "}");
    }

    [Fact]
    public void ReconstructFile_AppliesSeparatedEditsInOrder()
    {
        string[] original =
        {
            "void Run()",
            "{",
            "    alpha();",
            "    beta();",
            "    gamma();",
            "}",
        };
        var chunks = new List<FileChunk>
        {
            new FileChunk
            {
                OrigIndex = 2,
                DelLines = { "    alpha();" },
                InsLines = { "    alpha1();" },
            },
            new FileChunk
            {
                OrigIndex = 4,
                DelLines = { "    gamma();" },
                InsLines = { "    gamma1();" },
            },
        };

        string result = ApplyPatchTool.ReconstructFile(original, chunks);

        result.Replace("\r\n", "\n").Should().Be(
            "void Run()\n{\n    alpha1();\n    beta();\n    gamma1();\n}");
    }

    [Fact]
    public void ApplySinglePatch_PreservesContextBetweenMultipleReplacements()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-multi-{Guid.NewGuid():N}.txt");
        string original = "void Run()\n{\n    alpha();\n    beta();\n    gamma();\n}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            var hunk = new PatchHunk
            {
                Lines =
                {
                    new PatchLine { Type = ' ', Text = "void Run()" },
                    new PatchLine { Type = ' ', Text = "{" },
                    new PatchLine { Type = '-', Text = "    alpha();" },
                    new PatchLine { Type = '+', Text = "    alpha1();" },
                    new PatchLine { Type = ' ', Text = "    beta();" },
                    new PatchLine { Type = '-', Text = "    gamma();" },
                    new PatchLine { Type = '+', Text = "    gamma1();" },
                    new PatchLine { Type = ' ', Text = "}" },
                },
            };
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks = { hunk },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, original);

            result.Success.Should().BeTrue();
            result.AppliedEdits.Should().HaveCount(2);
            result.AppliedEdits.Select(e => e.StartLine).Should().Equal(2, 4);
            result.FinalContent.Should().NotBeNull();
            result.FinalContent!.Replace("\r\n", "\n").Should().Be(
                "void Run()\n{\n    alpha1();\n    beta();\n    gamma1();\n}\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplySinglePatch_PreservesClosingTokenInsertedBeforeMatchingContext()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-closing-token-{Guid.NewGuid():N}.txt");
        string original = "config.IsVision.Should().BeTrue();\n}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            var hunk = new PatchHunk
            {
                Lines =
                {
                    new PatchLine { Type = ' ', Text = "config.IsVision.Should().BeTrue();" },
                    new PatchLine { Type = '+', Text = "    }" },
                    new PatchLine { Type = ' ', Text = "}" },
                },
            };
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks = { hunk },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, original);

            result.Success.Should().BeTrue();
            result.AppliedEdits.Should().HaveCount(1);
            result.FinalContent.Should().NotBeNull();
            result.FinalContent!.Replace("\r\n", "\n").Should().Be(
                "config.IsVision.Should().BeTrue();\n    }\n}\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void TrimTrailingDuplicateClosingTokens_PreservesInsertionContainingOnlyClosingToken()
    {
        var segment = new FileChunkSegment
        {
            Offset = 0,
            InsLines = { "    }" },
        };
        string[] fileLines = { "}" };

        ApplyPatchTool.TrimTrailingDuplicateClosingTokens(segment, 0, fileLines);

        segment.InsLines.Should().Equal("    }");
    }

    [Fact]
    public void TrimTrailingDuplicateClosingTokens_RemovesDuplicateAfterInsertedCode()
    {
        var segment = new FileChunkSegment
        {
            Offset = 1,
            InsLines = { "    newStatement();", "}" },
        };
        string[] fileLines = { "    existing();", "}" };

        ApplyPatchTool.TrimTrailingDuplicateClosingTokens(segment, 1, fileLines);

        segment.InsLines.Should().Equal("    newStatement();");
    }
}
