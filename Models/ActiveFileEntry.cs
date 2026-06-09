using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 活跃文件条目 — 追踪会话中工具读取/写入过的文件。
    /// 用于生成 Working Set 摘要，注入 System Prompt 帮助 AI 感知当前上下文。
    /// </summary>
    public class ActiveFileEntry
    {
        /// <summary>绝对文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>最后访问该文件的工具名（read_file / write_file / replace_in_file 等）</summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>最后访问时的对话轮次号（1-based）</summary>
        public int LastAccessTurn { get; set; }

        /// <summary>累计访问次数</summary>
        public int AccessCount { get; set; }

        /// <summary>是否为写操作（true = 写入/编辑，false = 读取）</summary>
        public bool IsWrite { get; set; }
    }

    /// <summary>
    /// 活跃文件上下文 — 管理当前会话中工具访问过的文件列表。
    /// 提供 LRU 淘汰、摘要生成和 Top-K 路径查询。
    /// 
    /// 设计参考 CodeWhale working_set.rs。
    /// </summary>
    public class ActiveFileContext
    {
        /// <summary>最大追踪文件数（LRU 淘汰阈值）</summary>
        public const int MaxTrackedFiles = 24;

        /// <summary>
        /// 进入 System Prompt 摘要块的最大文件数。
        /// 小于 MaxTrackedFiles，防止摘要块过大影响缓存。
        /// </summary>
        public const int MaxPromptEntries = 15;

        private readonly List<ActiveFileEntry> _entries = new();
        private readonly object _lock = new();

        /// <summary>
        /// 记录一次文件访问。
        /// 如果文件已存在则更新 LastAccessTurn 和 AccessCount；
        /// 如果达到 MaxTrackedFiles 则淘汰最久未访问的条目。
        /// </summary>
        /// <param name="filePath">绝对文件路径</param>
        /// <param name="toolName">工具名</param>
        /// <param name="currentTurn">当前对话轮次号</param>
        /// <param name="isWrite">是否为写操作</param>
        public void ObserveFileAccess(string filePath, string toolName, int currentTurn, bool isWrite)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            lock (_lock)
            {
                // 查找已有条目
                ActiveFileEntry existing = null;
                foreach (var e in _entries)
                {
                    if (string.Equals(e.FilePath, filePath, System.StringComparison.OrdinalIgnoreCase))
                    {
                        existing = e;
                        break;
                    }
                }

                if (existing != null)
                {
                    existing.LastAccessTurn = currentTurn;
                    existing.AccessCount++;
                    existing.ToolName = toolName;
                    // 写操作优先级高于读操作：一旦写过，标记保持为写
                    if (isWrite) existing.IsWrite = true;
                }
                else
                {
                    // LRU 淘汰：如果已达上限，移除最久未访问的
                    while (_entries.Count >= MaxTrackedFiles)
                    {
                        ActiveFileEntry lru = null;
                        int minTurn = int.MaxValue;
                        foreach (var e in _entries)
                        {
                            if (e.LastAccessTurn < minTurn)
                            {
                                minTurn = e.LastAccessTurn;
                                lru = e;
                            }
                        }
                        if (lru != null)
                            _entries.Remove(lru);
                        else
                            break;
                    }

                    _entries.Add(new ActiveFileEntry
                    {
                        FilePath = filePath,
                        ToolName = toolName,
                        LastAccessTurn = currentTurn,
                        AccessCount = 1,
                        IsWrite = isWrite
                    });
                }
            }
        }

        /// <summary>
        /// 生成 Working Set 摘要块，用于注入 System Prompt。
        /// 按最近访问排序，最多 MaxPromptEntries 个。
        /// </summary>
        /// <param name="workspaceRoot">工作区根目录（用于生成相对路径）</param>
        /// <returns>Markdown 格式的 Working Set 摘要，或 null（无条目时）</returns>
        public string GetSummaryBlock(string workspaceRoot)
        {
            lock (_lock)
            {
                if (_entries.Count == 0)
                    return null;

                var sorted = GetSortedForPrompt();

                var lines = new List<string>
                {
                    "## Active Working Set",
                };

                if (!string.IsNullOrEmpty(workspaceRoot))
                    lines.Add($"Workspace: {workspaceRoot}");

                lines.Add("Recently accessed files (prioritize these):");

                int count = 0;
                foreach (var entry in sorted)
                {
                    if (count >= MaxPromptEntries) break;

                    string relativePath = GetRelativePath(workspaceRoot, entry.FilePath);
                    string action = entry.IsWrite ? "written" : "read";
                    string turnsAgo = entry.LastAccessTurn > 0
                        ? $"{entry.LastAccessTurn} turn(s) ago"
                        : "current turn";
                    lines.Add($"- {relativePath} ({action} {turnsAgo}, {entry.AccessCount}x)");
                    count++;
                }

                lines.Add("When in doubt, use tools to verify and keep changes focused on the working set.");
                return string.Join("\n", lines);
            }
        }

        /// <summary>
        /// 获取 Top-K 最近访问的文件路径（相对路径）。
        /// </summary>
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <param name="limit">最大返回数</param>
        public List<string> GetTopPaths(string workspaceRoot, int limit = 24)
        {
            lock (_lock)
            {
                var sorted = GetSortedForPrompt();
                var result = new List<string>();
                foreach (var entry in sorted)
                {
                    if (result.Count >= limit) break;
                    result.Add(GetRelativePath(workspaceRoot, entry.FilePath));
                }
                return result;
            }
        }

        /// <summary>
        /// 清除所有追踪条目。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// 按 (IsWrite DESC, LastAccessTurn DESC, AccessCount DESC) 排序。
        /// 写入的文件排前面，最近访问的排前面，高频访问的排前面。
        /// </summary>
        private List<ActiveFileEntry> GetSortedForPrompt()
        {
            var sorted = new List<ActiveFileEntry>(_entries);
            sorted.Sort((a, b) =>
            {
                // 写操作优先
                int writeCmp = b.IsWrite.CompareTo(a.IsWrite);
                if (writeCmp != 0) return writeCmp;
                // 最近访问优先
                int turnCmp = b.LastAccessTurn.CompareTo(a.LastAccessTurn);
                if (turnCmp != 0) return turnCmp;
                // 高频访问优先
                return b.AccessCount.CompareTo(a.AccessCount);
            });
            return sorted;
        }

        /// <summary>
        /// 将绝对路径转为相对于工作区根目录的路径。
        /// </summary>
        private static string GetRelativePath(string workspaceRoot, string absolutePath)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                return absolutePath;

            string normalizedRoot = workspaceRoot.Replace('/', '\\').TrimEnd('\\') + '\\';
            string normalizedPath = absolutePath.Replace('/', '\\');

            if (normalizedPath.StartsWith(normalizedRoot, System.StringComparison.OrdinalIgnoreCase))
                return normalizedPath.Substring(normalizedRoot.Length).Replace('\\', '/');

            return absolutePath;
        }
    }
}
