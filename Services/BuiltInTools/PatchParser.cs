using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    #region Patch Types

    /// <summary>补丁块操作类型</summary>
    internal enum PatchOperationType { Update, Add, Delete }

    /// <summary>补丁行类型</summary>
    internal enum PatchLineType { Context, Add, Remove }

    /// <summary>补丁行</summary>
    internal struct PatchLine
    {
        public PatchLineType Type;
        public string Text;
    }

    /// <summary>单个 hunk（一个 @@ 块）</summary>
    internal class PatchHunk
    {
        public List<string> ContextLines { get; set; } = new();
        public List<string> RemoveLines { get; set; } = new();
        public List<string> AddLines { get; set; } = new();
        public List<PatchLine> AllLines { get; set; } = new();
        /// <summary>@@ 标记文本（如类名/函数名），用于上下文匹配失败时的 fallback 定位</summary>
        public string? ContextMarker { get; set; }
    }

    /// <summary>解析后的补丁块</summary>
    internal class ParsedPatch
    {
        public string Operation { get; set; } = "update";
        public string FilePath { get; set; } = string.Empty;
        public List<PatchHunk> Hunks { get; set; } = new();
    }

    #endregion

    /// <summary>
    /// apply_patch 补丁格式解析器（静态工具类）。
    /// 解析 *** Begin Patch / *** End Patch 块，提取 Update/Add/Delete File 操作和 hunk。
    /// </summary>
    internal static class PatchParser
    {
        /// <summary>
        /// 解析 *** Begin Patch / *** End Patch 块。
        /// </summary>
        public static List<ParsedPatch> ParseBlocks(string patchText)
        {
            var patches = new List<ParsedPatch>();

            var beginSplit = patchText.Split(new[] { "*** Begin Patch" }, StringSplitOptions.None);
            foreach (var block in beginSplit.Skip(1))
            {
                int endIdx = block.IndexOf("*** End Patch", StringComparison.OrdinalIgnoreCase);
                if (endIdx < 0) continue;

                string blockContent = block.Substring(0, endIdx).Trim();
                var patch = new ParsedPatch();
                PatchHunk? currentHunk = null;

                var lines = blockContent.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                foreach (var rawLine in lines)
                {
                    string line = rawLine.TrimEnd();

                    if (line.StartsWith("*** Update File:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "update";
                        patch.FilePath = line.Substring("*** Update File:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }
                    if (line.StartsWith("*** Add File:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "add";
                        patch.FilePath = line.Substring("*** Add File:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }
                    if (line.StartsWith("*** Delete File:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "delete";
                        patch.FilePath = line.Substring("*** Delete File:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }
                    if (line.StartsWith("*** Move to:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "add";
                        patch.FilePath = line.Substring("*** Move to:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }

                    if (line.StartsWith("@@"))
                    {
                        currentHunk = new PatchHunk
                        {
                            ContextMarker = line.Substring(2).Trim()
                        };
                        patch.Hunks.Add(currentHunk);
                        continue;
                    }

                    if (currentHunk == null)
                    {
                        currentHunk = new PatchHunk();
                        patch.Hunks.Add(currentHunk);
                    }

                    if (line.StartsWith("- "))
                    {
                        string text = line.Substring(2);
                        currentHunk.RemoveLines.Add(text);
                        currentHunk.ContextLines.Add(text);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Remove, Text = text });
                    }
                    else if (line.StartsWith("+ "))
                    {
                        string text = line.Substring(2);
                        currentHunk.AddLines.Add(text);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Add, Text = text });
                    }
                    else if (line.Length > 0 && line[0] == ' ')
                    {
                        string text = line.Substring(1);
                        currentHunk.ContextLines.Add(text);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Context, Text = text });
                    }
                    else if (line.Length > 0 && line != "*** End Patch")
                    {
                        currentHunk.ContextLines.Add(line);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Context, Text = line });
                    }
                }

                if (!string.IsNullOrEmpty(patch.FilePath))
                    patches.Add(patch);
            }

            return patches;
        }

        /// <summary>在源文件中查找上下文匹配的起始行（精确匹配）</summary>
        public static int FindContextMatch(string[] sourceLines, List<string> contextLines, List<string> removeLines)
        {
            if (contextLines.Count == 0) return -1;

            var searchPattern = new List<string>();
            searchPattern.AddRange(contextLines);
            searchPattern.AddRange(removeLines);

            for (int i = 0; i <= sourceLines.Length - searchPattern.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < searchPattern.Count; j++)
                {
                    if (!string.Equals(sourceLines[i + j].Trim(), searchPattern[j].Trim(), StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }

            if (removeLines.Count > 0 && contextLines.Count > 0)
            {
                for (int i = 0; i <= sourceLines.Length - contextLines.Count; i++)
                {
                    bool match = true;
                    for (int j = 0; j < contextLines.Count; j++)
                    {
                        if (!string.Equals(sourceLines[i + j].Trim(), contextLines[j].Trim(), StringComparison.Ordinal))
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) return i;
                }
            }

            return -1;
        }

        /// <summary>宽松上下文匹配：仅使用第1个和最后1个上下文行定位</summary>
        public static int FindLooseContextMatch(string[] sourceLines, List<string> contextLines)
        {
            if (contextLines.Count < 2) return -1;

            string firstCtx = contextLines.First().Trim();
            string lastCtx = contextLines.Last().Trim();

            for (int i = 0; i < sourceLines.Length; i++)
            {
                if (string.Equals(sourceLines[i].Trim(), firstCtx, StringComparison.Ordinal))
                {
                    for (int j = i + 1; j < sourceLines.Length; j++)
                    {
                        if (string.Equals(sourceLines[j].Trim(), lastCtx, StringComparison.Ordinal))
                            return i;
                    }
                }
            }

            for (int i = 0; i < sourceLines.Length; i++)
            {
                if (string.Equals(sourceLines[i].Trim(), firstCtx, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        /// <summary>使用 @@ 标记文本在源文件中定位（fallback）</summary>
        public static int FindContextByMarker(string[] sourceLines, string? marker)
        {
            if (string.IsNullOrEmpty(marker)) return -1;

            for (int i = 0; i < sourceLines.Length; i++)
            {
                if (sourceLines[i].Contains(marker, StringComparison.Ordinal))
                    return i;
            }

            for (int i = 0; i < sourceLines.Length; i++)
            {
                if (sourceLines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }
    }
}
