namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 单个差异块（Hunk）描述 — 支持逐块撤销。
    ///
    /// 一个 Hunk 代表文件中一行或一段连续行的变化区域。
    /// 通过 OldStartLine/NewStartLine 分别在 Baseline 和当前文件中定位。
    /// </summary>
    public sealed class DiffHunkInfo
    {
        /// <summary>块在 Baseline（原始文件）中的起始行号（0-based）。纯新增行为 -1。</summary>
        public int OldStartLine { get; set; } = -1;

        /// <summary>块在 Baseline 中的行数。纯新增行为 0。</summary>
        public int OldLineCount { get; set; }

        /// <summary>块在当前文件中的起始行号（0-based）。纯删除行为 -1。</summary>
        public int NewStartLine { get; set; } = -1;

        /// <summary>块在当前文件中的行数。纯删除行为 0。</summary>
        public int NewLineCount { get; set; }

        /// <summary>该块在 Baseline 中的原始文本（撤销时恢复用）。</summary>
        public string OldText { get; set; } = string.Empty;

        /// <summary>该块当前文本。</summary>
        public string NewText { get; set; } = string.Empty;

        /// <summary>是否已撤销（该块的修改已回滚到 Baseline）。</summary>
        public bool IsReverted { get; set; }

        /// <summary>是否已被用户保留（接受该块修改，不再提示撤销）。</summary>
        public bool IsAccepted { get; set; }

        /// <summary>是否为纯新增。</summary>
        public bool IsPureInsert => OldLineCount == 0 && NewLineCount > 0;

        /// <summary>是否为纯删除。</summary>
        public bool IsPureDelete => OldLineCount > 0 && NewLineCount == 0;

        /// <summary>是否为修改（有删有增或单行内容变化）。</summary>
        public bool IsModify => OldLineCount > 0 && NewLineCount > 0;
    }
}