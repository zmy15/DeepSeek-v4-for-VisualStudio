using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // 通用编辑工具模型 — 支持 replace_string / multi_replace_string / apply_patch / insert_edit
    // 参考: vscode-copilot-chat abstractReplaceStringTool.tsx / editFileToolResult.tsx
    // ========================================================================

    /// <summary>
    /// 单次字符串替换输入（replace_string_in_file / multi_replace_string_in_file 共用）。
    /// 参考: IAbstractReplaceStringInput
    /// </summary>
    public class ReplaceStringInput
    {
        /// <summary>文件绝对路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>要替换的原始文本（必须精确匹配，包含至少3行上下文）</summary>
        public string OldString { get; set; } = string.Empty;

        /// <summary>替换后的新文本</summary>
        public string NewString { get; set; } = string.Empty;
    }

    /// <summary>
    /// 准备好的编辑操作（单文件），包含文档快照和生成的 TextEdit。
    /// 参考: IPrepareEdit
    /// </summary>
    public class PreparedEdit
    {
        /// <summary>文件绝对路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>原始输入</summary>
        public ReplaceStringInput Input { get; set; } = new();

        /// <summary>Healing 修正后的输入（如有）</summary>
        public ReplaceStringInput? HealedInput { get; set; }

        /// <summary>生成的编辑结果</summary>
        public GeneratedEditResult GeneratedEdit { get; set; } = new();

        /// <summary>文件编辑前的完整内容（用于 diff 计算）</summary>
        public string? OriginalContent { get; set; }
    }

    /// <summary>
    /// 生成的编辑结果（成功 / 失败）。
    /// 参考: IPrepareEdit.generatedEdit
    /// </summary>
    public class GeneratedEditResult
    {
        /// <summary>是否成功生成编辑</summary>
        public bool Success { get; set; }

        /// <summary>成功时的 TextEdit 列表</summary>
        public List<TextEditOperation> TextEdits { get; set; } = new();

        /// <summary>失败时的错误描述</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 多文件替换工具参数（multi_replace_string_in_file）。
    /// 参考: IMultiReplaceStringToolParams
    /// </summary>
    public class MultiReplaceStringParams
    {
        /// <summary>操作说明（用于日志）</summary>
        public string Explanation { get; set; } = string.Empty;

        /// <summary>替换操作列表</summary>
        public List<ReplaceStringInput> Replacements { get; set; } = new();
    }

    /// <summary>
    /// 单文件替换工具参数（replace_string_in_file）。
    /// 参考: IReplaceStringToolParams
    /// </summary>
    public class ReplaceStringParams
    {
        /// <summary>操作说明（用于日志）</summary>
        public string Explanation { get; set; } = string.Empty;

        /// <summary>文件绝对路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>要替换的原始文本</summary>
        public string OldString { get; set; } = string.Empty;

        /// <summary>替换后的新文本</summary>
        public string NewString { get; set; } = string.Empty;
    }

    /// <summary>
    /// 已编辑文件的结果摘要（用于 UI 渲染和日志）。
    /// 参考: IEditedFile
    /// </summary>
    public class EditedFileResult
    {
        /// <summary>操作类型</summary>
        public PatchFileAction Operation { get; set; }

        /// <summary>文件绝对路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>编辑前的诊断（如有）</summary>
        public List<string>? ExistingDiagnostics { get; set; }

        /// <summary>编辑后的新诊断（如有）</summary>
        public List<string>? NewDiagnostics { get; set; }

        /// <summary>失败原因（成功时为 null）</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Healing 是否被使用</summary>
        public bool WasHealed { get; set; }

        /// <summary>Healing 描述（如有）</summary>
        public string? HealingDescription { get; set; }
    }

    /// <summary>
    /// 编辑工具日志条目。
    /// </summary>
    public class EditToolLogEntry
    {
        /// <summary>时间戳</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>日志级别</summary>
        public string Level { get; set; } = "INFO";

        /// <summary>消息</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>操作耗时（毫秒）</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// 编辑应用摘要 — 一次编辑调用的整体结果。
    /// </summary>
    public class EditToolResult
    {
        /// <summary>是否全部成功</summary>
        public bool AllSucceeded { get; set; }

        /// <summary>成功文件数</summary>
        public int SuccessCount { get; set; }

        /// <summary>失败文件数</summary>
        public int FailureCount { get; set; }

        /// <summary>Healing 使用次数</summary>
        public int HealingCount { get; set; }

        /// <summary>每个文件的结果</summary>
        public List<EditedFileResult> Files { get; set; } = new();

        /// <summary>操作日志</summary>
        public List<EditToolLogEntry> Logs { get; set; } = new();

        /// <summary>错误摘要（如有）</summary>
        public string? ErrorSummary { get; set; }
    }
}
