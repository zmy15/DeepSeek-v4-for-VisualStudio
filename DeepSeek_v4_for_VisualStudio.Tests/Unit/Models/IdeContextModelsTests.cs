using System;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models
{
    /// <summary>
    /// IdeContextSnapshot 鏍煎紡鍖栧櫒鍗曞厓娴嬭瘯锛圥1-A锛夈€?    /// </summary>
    public class IdeContextModelsTests
    {
        private static IdeContextSnapshot Snapshot(Action<IdeContextSnapshot>? setup = null)
        {
            var s = new IdeContextSnapshot { FilePath = @"C:\repo\src\Renderer.cpp", CursorLine = 10, CursorColumn = 5 };
            setup?.Invoke(s);
            return s;
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 绌烘€?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        [Fact]
        public void ToPromptBlock_EmptySnapshot_ReturnsNull()
        {
            var s = new IdeContextSnapshot();
            s.HasContent.Should().BeFalse();
            s.ToPromptBlock().Should().BeNull();
        }

        [Fact]
        public void HasSelection_False_WhenWhitespaceOnly()
        {
            var s = Snapshot(x => x.SelectionText = "   ");
            s.HasSelection.Should().BeFalse();
        }

        [Fact]
        public void HasContent_True_WithFilePathOnly()
        {
            var s = new IdeContextSnapshot { FilePath = @"C:\x\a.cs" };
            s.HasContent.Should().BeTrue();
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 鍩烘湰鏍煎紡 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        [Fact]
        public void ToPromptBlock_FileOnly_ContainsHeaderCursorAndPath()
        {
            var block = Snapshot().ToPromptBlock();

            block.Should().NotBeNull();
            block!.Should().StartWith("[IDE Context]");
            block.Should().Contain("Active File: C:\\repo\\src\\Renderer.cpp");
            block.Should().Contain("Cursor: line 10, col 5");
            block.Should().NotContain("Symbol:");
            block.Should().NotContain("Selection (");
            block.Should().NotContain("Diagnostics:");
        }

        [Fact]
        public void ToPromptBlock_RelativePath_WhenWorkspaceRootMatches()
        {
            var block = Snapshot().ToPromptBlock(@"C:\Repo");
            block.Should().Contain("Active File: src\\Renderer.cpp");
            block.Should().NotContain("C:\\Repo");
        }

        [Fact]
        public void ToPromptBlock_AbsolutePath_WhenOutsideWorkspaceRoot()
        {
            var block = Snapshot().ToPromptBlock(@"D:\other");
            block.Should().Contain(@"C:\repo\src\Renderer.cpp");
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 绗﹀彿涓庡綋鍓嶈 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        [Fact]
        public void ToPromptBlock_Symbol_IncludedWithCurrentLine()
        {
            var block = Snapshot(s =>
            {
                s.SymbolAtCursor = "Draw";
                s.SymbolLineText = "    void Renderer::Draw()   ";
            }).ToPromptBlock();

            block.Should().Contain("Symbol: Draw");
            block.Should().Contain("Current Line: void Renderer::Draw()");
        }

        [Fact]
        public void SymbolLineText_TruncatedToLimit()
        {
            var block = Snapshot(s =>
            {
                s.SymbolAtCursor = "X";
                s.SymbolLineText = new string('a', 250);
            }).ToPromptBlock();

            block!.Should().Contain(new string('a', 200));      // truncate at limit 200
            block.Should().NotContain(new string('a', 201));
            block.Should().Contain("…");
        }

        [Fact]
        public void SymbolLineText_ShortLine_KeptAsIs()
        {
            var block = Snapshot(s => { s.SymbolAtCursor = "X"; s.SymbolLineText = "short line"; })
                .ToPromptBlock();
            block!.Should().Contain("Current Line: short line");
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 閫夊尯 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        [Fact]
        public void ToPromptBlock_Selection_SingleLineLabel_AndFenceLanguage()
        {
            var block = Snapshot(s =>
            {
                s.SelectionText = "foo->Update();";
                s.SelectionStartLine = 7;
                s.SelectionEndLine = 7;
            }).ToPromptBlock();

            block!.Should().Contain("Selection (line 7):");
            block.Should().Contain("```cpp");
            block.Should().Contain("foo->Update();");
        }

        [Fact]
        public void ToPromptBlock_Selection_RangeLabel()
        {
            var block = Snapshot(s =>
            {
                s.SelectionText = "a\nb\nc";
                s.SelectionStartLine = 3;
                s.SelectionEndLine = 5;
            }).ToPromptBlock();

            block!.Should().Contain("Selection (lines 3-5):");
        }

        [Fact]
        public void Selection_TruncatedByLineCount_AppendsNote()
        {
            var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line-{i}"));
            var block = Snapshot(s =>
            {
                s.SelectionText = text;
                s.SelectionStartLine = 1;
                s.SelectionEndLine = 100;
            }).ToPromptBlock();

            block!.Should().Contain("(selection truncated)");
            block!.Should().Contain("line-40");      // keep first 40 lines
            block.Should().NotContain("line-41");   // line 41 was cut
        }

        [Fact]
        public void Selection_TruncatedByCharBudget_AppendsNoteAndEllipsis()
        {
            var longLine = new string('x', 3000); // one line exceeds char budget
            var block = Snapshot(s =>
            {
                s.SelectionText = longLine;
                s.SelectionStartLine = 1;
                s.SelectionEndLine = 1;
            }).ToPromptBlock();

            block!.Should().Contain("…");
            block.Should().Contain("(selection truncated)");
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 璇婃柇 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        [Fact]
        public void Diagnostics_CountsFormatted_WithSingularPlural()
        {
            var block = Snapshot(s =>
            {
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 12, Message = "E1" });
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 13, Message = "E2" });
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "warning", Line = 14, Message = "W1" });
            }).ToPromptBlock();

            block!.Should().Contain("Diagnostics: 2 errors / 1 warning");
            block.Should().Contain("- error line 12: E1");
            block.Should().Contain("- warning line 14: W1");
        }

        [Fact]
        public void Diagnostics_SingularForms()
        {
            var block = Snapshot(s =>
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 1, Message = "only" })
            ).ToPromptBlock();

            block!.Should().Contain("Diagnostics: 1 error / 0 warnings");
        }

        [Fact]
        public void Diagnostics_ErrorsListedFirst_AndCappedAtSix()
        {
            var block = Snapshot(s =>
            {
                for (int i = 0; i < 8; i++)
                    s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "warning", Line = i + 1, Message = $"W{i}" });
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 99, Message = "CRITICAL" });
            }).ToPromptBlock();

            block!.Should().Contain("- error line 99: CRITICAL");       // errors first
            block.Should().Match(b => System.Text.RegularExpressions.Regex.IsMatch(b!, @"\(\+3 more\)")); // 9 total, show 6
            block.Should().NotContain("W6");                            // W6 was cut
        }

        [Fact]
        public void Diagnostics_MessageTruncatedTo120()
        {
            var msg = new string('m', 300);
            var block = Snapshot(s =>
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 2, Message = msg })
            ).ToPromptBlock();

            block!.Length.Should().BeLessThan(msg.Length); // 鎴柇鐢熸晥
            block.Should().Contain(new string('m', 120));
            block.Should().NotContain(new string('m', 121));
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 鍥存爮璇█鏄犲皠 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        [Theory]
        [InlineData("a.cs", "csharp")]
        [InlineData("b.cpp", "cpp")]
        [InlineData("c.H", "cpp")]
        [InlineData("d.py", "python")]
        [InlineData("e.unknown", "")]
        public void GetFenceLanguage_MapsCommonExtensions(string file, string expected)
        {
            IdeContextSnapshot.GetFenceLanguage(file).Should().Be(expected);
        }

        // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€ 绗﹀彿鎻愬彇锛圕ase B 楠屾敹鏍稿績锛?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        // 鍒楀彿璇箟锛?-based锛屾寚鍚戝厜鏍囧乏渚у瓧绗︼紱鍏夋爣鍋滃湪璇嶅熬鍚庝竴鏍兼椂宸﹀亸濂藉綊灞炲墠璇?
        [Theory]
        [InlineData("void Renderer::Draw()", 7, "Renderer")]
        [InlineData("void Renderer::Draw()", 13, "Renderer")]
        [InlineData("foo->Update();", 6, "Update")]
        public void ExtractIdentifierAt_InsideOrAdjacentWord_ReturnsWord(string line, int col, string expected)
        {
            IdeContextSnapshot.ExtractIdentifierAt(line, col).Should().Be(expected);
        }

        [Fact]
        public void ExtractIdentifierAt_OnSeparatorWithoutLeftWord_ReturnsNull()
        {
            IdeContextSnapshot.ExtractIdentifierAt("a + b", 2).Should().BeNull();       // 鍏夋爣鍦?'+' 涓婏紝宸︿晶闈炶瘝
            IdeContextSnapshot.ExtractIdentifierAt("::Draw()", 1).Should().BeNull();
        }

        [Fact]
        public void ExtractIdentifierAt_SpaceAfterWord_PrefersLeftWord()
        {
            // 璁捐琛屼负锛堜笌涓绘祦缂栬緫鍣ㄤ竴鑷达級锛氫袱璇嶄箣闂寸殑绌烘牸浣嶅綊灞炲乏渚ц瘝
            IdeContextSnapshot.ExtractIdentifierAt("foo bar", 3).Should().Be("foo");
        }

        [Theory]
        [InlineData("_count123", 0, "_count123")]
        [InlineData("x", 0, null)]
        public void ExtractIdentifierAt_WordShapeRules(string line, int col, string? expected)
        {
            IdeContextSnapshot.ExtractIdentifierAt(line, col).Should().Be(expected);
        }

        [Fact]
        public void ExtractIdentifierAt_ColumnBeyondEol_ClampsToLastChar()
        {
            IdeContextSnapshot.ExtractIdentifierAt("abc", 999).Should().Be("abc");
            IdeContextSnapshot.ExtractIdentifierAt("abc ", 999).Should().Be("abc"); // 鏀舵暃鍒扮┖鏍煎悗鍥為€€
        }

        [Fact]
        public void ExtractIdentifierAt_EmptyLine_ReturnsNull()
        {
            IdeContextSnapshot.ExtractIdentifierAt("", 0).Should().BeNull();
            IdeContextSnapshot.ExtractIdentifierAt("   ", 1).Should().BeNull();
        }

        #region Debugger Frame

        [Fact]
        public void ToPromptBlock_DebuggerFrame_FormatsFunctionLocationAndLocals()
        {
            var s = new IdeContextSnapshot();
            var f = new IdeDebuggerFrame { Function = "Program.Main()", File = @"C:\Repo\Program.cs", Line = 42 };
            f.Locals.Add(new IdeDebuggerValue { Name = "count", Value = "3" });
            f.Locals.Add(new IdeDebuggerValue { Name = "name", Value = "\"abc\"" });
            s.DebuggerFrame = f;

            var block = s.ToPromptBlock(@"C:\Repo");

            block.Should().NotBeNull();
            block.Should().Contain("Debugger: paused");
            block.Should().Contain("Frame: Program.Main()");
            block.Should().Contain("Program.cs:42");
            block.Should().Contain("Locals (2)");
            block.Should().Contain("- count = 3");
            block.Should().Contain("- name = \"abc\"");
        }

        [Fact]
        public void ToPromptBlock_DebuggerFrame_TruncatesLongValues_AndShowsMoreCount()
        {
            var s = new IdeContextSnapshot();
            var f = new IdeDebuggerFrame { Function = new string('F', 200) };
            for (int i = 0; i < 14; i++)
                f.Locals.Add(new IdeDebuggerValue { Name = "v" + i, Value = new string('x', 300) });
            s.DebuggerFrame = f;

            var block = s.ToPromptBlock();

            block.Should().Contain("…");
            block.Should().Contain("(+2 more)");
            block.Should().Contain("Locals (14)");
        }

        [Fact]
        public void ToPromptBlock_DebuggerFrameOnly_StillInjects()
        {
            // 断点命中但无编辑器视图：仅调试器帧也应构成注入内容
            var s = new IdeContextSnapshot
            {
                DebuggerFrame = new IdeDebuggerFrame { Function = "App.Run", File = @"C:\x.cs", Line = 7 },
            };

            s.HasContent.Should().BeTrue();
            var block = s.ToPromptBlock();
            block.Should().Contain("Debugger: paused");
            block.Should().Contain("Frame: App.Run");
        }

        [Fact]
        public void ToPromptBlock_NoDebugger_NoSection()
        {
            var s = new IdeContextSnapshot { FilePath = @"C:\a.cs" };
            s.DebuggerFrame.Should().BeNull();
            var block = s.ToPromptBlock();
            block.Should().NotContain("Debugger:");
        }

        #endregion
    }
}
