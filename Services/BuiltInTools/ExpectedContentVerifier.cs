using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// 编辑工具的预期结果校验器。预期内容使用带行号格式，例如：
    /// 1|using System;
    /// 2|
    /// 3|class Example { }
    /// </summary>
    internal static class ExpectedContentVerifier
    {
        internal const string DeletedMarker = "(deleted)";

        internal static bool IsDeletedExpectation(string expectedText)
            => string.Equals(expectedText?.Trim(), DeletedMarker,
                StringComparison.OrdinalIgnoreCase);

        internal static (bool Success, List<string> Lines, string Error)
            ParseLineNumberedContent(string expectedText)
        {
            var parsed = ParseLineNumberedFragment(expectedText, requireStartAtOne: true);
            return (parsed.Success, parsed.Lines, parsed.Error);
        }

        /// <summary>
        /// 解析连续行号片段。apply_patch 只需要校验受影响区域，
        /// 因此允许行号从任意正整数开始，例如 28|...、29|...。
        /// </summary>
        internal static (bool Success, int StartLine, List<string> Lines, string Error)
            ParseLineNumberedFragment(string expectedText, bool requireStartAtOne = false)
        {
            var lines = new List<string>();
            int startLine = 0;
            string normalized = (expectedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] rawLines = normalized.Split('\n');

            for (int i = 0; i < rawLines.Length; i++)
            {
                string rawLine = rawLines[i];
                if (i == rawLines.Length - 1 && rawLine.Length == 0)
                    continue;

                Match match = Regex.Match(
                    rawLine,
                    @"^\s*(\d+)\s*(?:\||\u2192)(.*)$");
                if (!match.Success)
                {
                    match = Regex.Match(
                        rawLine,
                        @"^\s*(\d+)\s*(?::|\.)\s?(.*)$");
                }
                if (!match.Success
                    || !int.TryParse(match.Groups[1].Value, out int lineNumber)
                    || lineNumber < 1
                    || (lines.Count == 0
                        ? requireStartAtOne && lineNumber != 1
                        : lineNumber != startLine + lines.Count))
                {
                    return (false, 0, lines,
                        LocalizationService.Instance.Format(
                            "tool.editVerify.expectedFormat", i + 1, rawLine));
                }

                if (lines.Count == 0)
                    startLine = lineNumber;

                lines.Add(match.Groups[2].Value);
            }

            if (lines.Count == 0)
            {
                return (false, 0, lines,
                    LocalizationService.Instance["tool.editVerify.missingExpected"]);
            }

            return (true, startLine, lines, string.Empty);
        }

        internal static string FormatLineNumberedContent(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return "(empty)";
            return string.Join("\n", lines.Select(
                (line, index) => $"{index + 1}|{line}"));
        }

        internal static string VerifyExpectedContent(
            string expectedText,
            string? actualContent,
            string filePath,
            bool expectDeleted = false)
        {
            var parsed = ParseLineNumberedFragment(expectedText, requireStartAtOne: true);
            if (!parsed.Success) return parsed.Error;

            return VerifyParsedLines(
                parsed.Lines, 1, actualContent, filePath, expectDeleted, requireFullContent: true);
        }

        /// <summary>
        /// 校验 expected 中声明的连续行片段，不要求 expected 覆盖整个文件。
        /// </summary>
        internal static string VerifyExpectedFragment(
            string expectedText,
            string? actualContent,
            string filePath,
            bool expectDeleted = false,
            bool requireFullContent = false)
        {
            var parsed = ParseLineNumberedFragment(
                expectedText, requireStartAtOne: requireFullContent);
            if (!parsed.Success) return parsed.Error;

            return VerifyParsedLines(
                parsed.Lines, parsed.StartLine, actualContent, filePath,
                expectDeleted, requireFullContent);
        }

        private static string VerifyParsedLines(
            List<string> expectedLines,
            int startLine,
            string? actualContent,
            string filePath,
            bool expectDeleted,
            bool requireFullContent)
        {
            string[] actualLines = SplitContentIntoLines(actualContent);

            if (expectDeleted)
            {
                if (actualContent == null)
                    return LocalizationService.Instance["tool.editVerify.verified"];

                return BuildVerificationFailure(
                    filePath,
                    1,
                    DeletedMarker,
                    actualLines.Length > 0 ? actualLines[0] : "(empty)",
                    actualLines,
                    actualContent);
            }

            int startIndex = startLine - 1;
            int maxLines = requireFullContent
                ? Math.Max(expectedLines.Count, actualLines.Length)
                : expectedLines.Count;

            for (int i = 0; i < maxLines; i++)
            {
                string expectedLine = i < expectedLines.Count
                    ? expectedLines[i]
                    : "<missing>";

                int actualIndex = startIndex + i;
                string actualLine = actualIndex >= 0 && actualIndex < actualLines.Length
                    ? actualLines[actualIndex]
                    : "<missing>";

                if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
                {
                    return BuildVerificationFailure(
                        filePath,
                        actualIndex + 1,
                        expectedLine,
                        actualLine,
                        actualLines,
                        actualContent);
                }
            }

            return LocalizationService.Instance["tool.editVerify.verified"];
        }

        internal static string BuildCurrentStateMessage(string? actualContent)
        {
            string currentState = actualContent == null
                ? "(file not found)"
                : FormatLineNumberedContent(SplitContentIntoLines(actualContent));
            return LocalizationService.Instance.Format(
                "tool.editVerify.currentState", currentState);
        }

        internal static string[] SplitContentIntoLines(string? content)
        {
            if (content == null) return Array.Empty<string>();

            string normalized = content
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (normalized.StartsWith("\uFEFF", StringComparison.Ordinal))
                normalized = normalized.Substring(1);
            if (normalized.EndsWith("\n", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1);

            return normalized.Length == 0
                ? Array.Empty<string>()
                : normalized.Split('\n');
        }

        private static string BuildVerificationFailure(
            string filePath,
            int lineNumber,
            string expectedLine,
            string actualLine,
            string[] actualLines,
            string? actualContent)
        {
            string currentState = actualContent == null
                ? "(file not found)"
                : FormatLineNumberedContent(actualLines);

            return LocalizationService.Instance.Format(
                "tool.editVerify.failed",
                lineNumber,
                filePath,
                $"{lineNumber}|{expectedLine}",
                $"{lineNumber}|{actualLine}",
                currentState);
        }
    }
}
