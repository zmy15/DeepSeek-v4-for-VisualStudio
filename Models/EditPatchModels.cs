using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // 编辑补丁模型 — 支持 apply_patch / insert_edit_into_file / create_file 三种编辑方式
    // ========================================================================

    /// <summary>
    /// 编辑操作类型。
    /// </summary>
    public enum EditOperationType
    {
        /// <summary>apply_patch — 自定义 diff 格式补丁（首选，最快）</summary>
        ApplyPatch,

        /// <summary>insert_edit_into_file — 完整文件重写，用 ...existing code... 标记未改区域</summary>
        InsertEditIntoFile,

        /// <summary>create_file — 创建新文件</summary>
        CreateFile,

        /// <summary>delete_file — 删除文件</summary>
        DeleteFile,

        /// <summary>move_file — 重命名/移动文件</summary>
        MoveFile,
    }

    /// <summary>
    /// Patch 文件操作声明类型。
    /// </summary>
    public enum PatchFileAction
    {
        Update,
        Add,
        Delete,
    }

    /// <summary>
    /// 单个 Patch Hunk：@@ 上下文定位 + 变更行。
    /// </summary>
    public class PatchHunk
    {
        /// <summary>@@ 行上下文定位列表（如类名、函数名）</summary>
        public List<string> ContextMarkers { get; set; } = new();

        /// <summary>Hunk 内的所有行（包含 - / + / 空格前缀）</summary>
        public List<PatchLine> Lines { get; set; } = new();

        /// <summary>原始 Hunk 文本（用于匹配失败时的 healing）</summary>
        public string RawText { get; set; } = string.Empty;

        /// <summary>是否为文件末尾 Hunk（由 *** End of File 标记）。匹配时优先从文件末尾搜索。</summary>
        public bool IsEof { get; set; }
    }

    /// <summary>
    /// Patch 中的单行。
    /// </summary>
    public class PatchLine
    {
        /// <summary>行类型: " " = 上下文, "-" = 删除, "+" = 新增</summary>
        public char Type { get; set; } = ' ';

        /// <summary>行的文本内容（不含前缀字符）</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// 文件重建块（对应参考实现 peek_next_section 中的 Chunk）。
    /// 描述在原始文件的某个位置删除/插入哪些行。
    /// 使用文件重建方式而非文本搜索替换，从根本上避免 AI 重复闭合符号等问题。
    /// </summary>
    public class FileChunk
    {
        /// <summary>在原始文件行数组中的起始索引（0-based），指向第一个被替换的行</summary>
        public int OrigIndex { get; set; }

        /// <summary>要删除的行列表</summary>
        public List<string> DelLines { get; set; } = new();

        /// <summary>要插入的行列表</summary>
        public List<string> InsLines { get; set; } = new();
    }

    /// <summary>
    /// 一个完整的 Patch 操作（对应一个 *** Begin Patch / *** End Patch 块）。
    /// </summary>
    public class PatchOperation
    {
        /// <summary>操作类型</summary>
        public PatchFileAction Action { get; set; }

        /// <summary>目标文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>移动/重命名目标路径（仅 MoveFile）</summary>
        public string? MoveToPath { get; set; }

        /// <summary>所有 Hunk 列表</summary>
        public List<PatchHunk> Hunks { get; set; } = new();

        /// <summary>原始 Patch 文本（用于 healing）</summary>
        public string RawText { get; set; } = string.Empty;
    }

    /// <summary>
    /// insert_edit_into_file 操作：完整文件内容 + ...existing code... 标记。
    /// </summary>
    public class InsertEditOperation
    {
        /// <summary>目标文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>AI 输出的完整内容（含 ...existing code... 标记）</summary>
        public string FullContent { get; set; } = string.Empty;

        /// <summary>...existing code... 标记字符串（固定值）</summary>
        public const string ExistingCodeMarker = "...existing code...";
    }

    /// <summary>
    /// 编辑匹配级别。
    /// </summary>
    public enum MatchLevel
    {
        /// <summary>精确字符串匹配</summary>
        Exact = 1,

        /// <summary>空白弹性匹配（忽略缩进/空白差异）</summary>
        WhitespaceFlexible = 2,

        /// <summary>模糊匹配（忽略空白 + 标点符号差异）</summary>
        Fuzzy = 3,

        /// <summary>Levenshtein 相似度匹配</summary>
        Levenshtein = 4,
    }

    /// <summary>
    /// 单个文本编辑操作（对应编辑器中一次替换）。
    /// </summary>
    public class TextEditOperation
    {
        /// <summary>起始行号（0-based）</summary>
        public int StartLine { get; set; }

        /// <summary>起始列（0-based）</summary>
        public int StartColumn { get; set; }

        /// <summary>结束行号（0-based）</summary>
        public int EndLine { get; set; }

        /// <summary>结束列（0-based）</summary>
        public int EndColumn { get; set; }

        /// <summary>替换后的新文本</summary>
        public string NewText { get; set; } = string.Empty;

        /// <summary>匹配到的原始文本（用于日志）</summary>
        public string MatchedText { get; set; } = string.Empty;

        /// <summary>使用的匹配级别</summary>
        public MatchLevel MatchLevelUsed { get; set; }
    }

    /// <summary>
    /// 编辑应用结果。
    /// </summary>
    public class EditApplyResult
    {
        /// <summary>是否全部应用成功</summary>
        public bool Success { get; set; }

        /// <summary>目标文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>编辑操作类型</summary>
        public EditOperationType OperationType { get; set; }

        /// <summary>应用的文本编辑列表</summary>
        public List<TextEditOperation> AppliedEdits { get; set; } = new();

        /// <summary>失败原因（如有）</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>匹配失败的 Hunk（需要 healing）</summary>
        public List<PatchHunk>? FailedHunks { get; set; }

        /// <summary>匹配失败的 ...existing code... 区间（需要 healing）</summary>
        public List<string>? FailedRegions { get; set; }

        /// <summary>编辑后发现的新诊断错误</summary>
        public List<string>? NewDiagnostics { get; set; }

        /// <summary>编辑后的完整文件内容（行尾已标准化为 CRLF）。供调用方通过 VS SDK 写入。</summary>
        [JsonIgnore]
        public string? FinalContent { get; set; }
    }
}

