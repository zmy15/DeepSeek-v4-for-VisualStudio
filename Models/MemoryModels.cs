using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 记忆作用域。
    /// </summary>
    public enum MemoryScope
    {
        /// <summary>用户记忆 — 跨所有工作区和会话，存储偏好和通用知识</summary>
        User,

        /// <summary>会话记忆 — 当前对话范围内，存储临时上下文</summary>
        Session,

        /// <summary>仓库记忆 — 当前解决方案范围内，存储项目约定和构建命令等</summary>
        Repo
    }

    /// <summary>
    /// 记忆文件/目录条目摘要（用于 list 操作返回）。
    /// </summary>
    public class MemoryEntry
    {
        /// <summary>条目名称（文件含扩展名，目录以 / 结尾）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>是否为目录</summary>
        public bool IsDirectory { get; set; }

        /// <summary>文件大小（字节），目录为 0</summary>
        public long SizeBytes { get; set; }

        /// <summary>最后修改时间</summary>
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// 记忆视图结果。
    /// </summary>
    public class MemoryViewResult
    {
        /// <summary>查看的文件路径（相对于记忆根目录）</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>文件内容（当查看文件时）</summary>
        public string? Content { get; set; }

        /// <summary>目录条目列表（当查看目录时）</summary>
        public List<MemoryEntry>? Entries { get; set; }

        /// <summary>是否为目录列表</summary>
        public bool IsDirectoryListing { get; set; }

        /// <summary>行范围起始（1-based，文件查看时）</summary>
        public int? ViewStartLine { get; set; }

        /// <summary>行范围结束（1-based，文件查看时）</summary>
        public int? ViewEndLine { get; set; }

        /// <summary>总行数（文件查看时）</summary>
        public int? TotalLines { get; set; }

        /// <summary>记忆作用域</summary>
        public MemoryScope Scope { get; set; }
    }
}
