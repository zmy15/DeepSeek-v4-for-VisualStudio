using DeepSeek_v4_for_VisualStudio.Services.EditTools;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 验证 apply_patch 对同一 hunk 内多个分离编辑段的解析和重建。
/// </summary>
public class ApplyPatchToolTests
{
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
}
