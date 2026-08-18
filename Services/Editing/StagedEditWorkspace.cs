using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// Agent 多步编辑的写穿 + 撤销追踪工作区。
    ///
    /// 设计（v1.1.12 修正）：
    /// 为保证读写一致性（read_file / 构建校验必须看到最新代码），编辑内容【直接落盘】，
    /// 而不是暂存在内存。为保证"裸盘落盘"也能撤销，首次改动每个文件时记录其 Baseline
    /// （原始内容 / 原始哈希），用户撤销时通过 <see cref="RestoreToBaseline"/> 回写磁盘。
    ///
    /// 使用方式：
    /// 1. Agent 开始时创建 Workspace。
    /// 2. 编辑工具通过 Workspace.ReadFile/WriteFile 读写 —— WriteFile 直接落盘并登记 Baseline。
    /// 3. Agent 完成后调用 <see cref="ToPreparedChangeBatch"/> 生成变更 Batch（供 diff 预览）。
    /// 4. 用户保留 → 磁盘已是最终内容，无需额外提交；用户撤销 → <see cref="RestoreToBaseline"/> 回滚。
    ///
    /// 线程安全：所有公开方法通过 _lock 保护。
    /// </summary>
    public sealed class StagedEditWorkspace
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, StagedFile> _trackedFiles
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 已打开文档写入器（可选注入，推荐 EditBufferApplier.TryWriteOpenDocument）。
        /// 签名：(filePath, fullContent) => true 表示文件已在编辑器打开并通过 buffer+编辑器 Save 写入。
        /// 命中时避免 File.WriteAllText 裸写盘在 dirty buffer 场景触发 VS「文件已在磁盘上修改」弹窗；
        /// 返回 false（未打开）时回退裸写盘。磁盘仍是权威内容，写穿设计与 Baseline 追踪不受影响。
        /// </summary>
        public Func<string, string, bool>? OpenDocumentWriter { get; set; }

        /// <summary>
        /// 已打开文档冲刷器（可选注入，推荐 EditBufferApplier.TrySaveOpenDocument）。
        /// 在首次接触某文件、登记 Baseline 之前调用：若该文件在编辑器中打开且有未保存修改，
        /// 先通过编辑器 Save 落盘，保证 Baseline 捕获用户最新内容（撤销时不丢失用户编辑）。
        /// </summary>
        public Func<string, bool>? OpenDocumentFlusher { get; set; }

        /// <summary>当前追踪（有改动）的文件数</summary>
        public int StagedCount
        {
            get { lock (_lock) return _trackedFiles.Count; }
        }

        /// <summary>是否有任何被追踪的改动</summary>
        public bool HasAnyTrackedChanges
        {
            get { lock (_lock) return _trackedFiles.Count > 0; }
        }

        /// <summary>
        /// 读取文件内容。直接读取磁盘（内容已落盘）。
        /// </summary>
        public string ReadFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            return File.Exists(normalizedPath) ? File.ReadAllText(normalizedPath) : string.Empty;
        }

        /// <summary>
        /// 写入文件内容（写穿落盘）。
        /// 首次接触此文件时记录 Baseline（原始内容 + 哈希），供用户撤销时恢复。
        ///
        /// 落盘路径：
        /// - 注入 OpenDocumentWriter 且文件已在编辑器打开 → buffer 整体替换 + 编辑器 Save（锁外执行，
        ///   因内部需切换 UI 线程，持 _lock 等待 UI 线程会有死锁风险）；
        /// - 否则 → File.WriteAllText 裸写盘（原有行为）。
        /// </summary>
        public void WriteFile(string filePath, string newContent)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", nameof(filePath));

            var normalizedPath = NormalizePath(filePath);
            string content = newContent ?? string.Empty;

            // ── 首次接触 + 注入冲刷器：先把已打开文档的用户未保存内容落盘 ──
            // （在读取 Baseline 之前执行，保证 Baseline 包含用户的未保存编辑；锁外执行防死锁）
            bool shouldFlush;
            lock (_lock)
            {
                shouldFlush = OpenDocumentFlusher != null
                    && !_trackedFiles.ContainsKey(normalizedPath)
                    && File.Exists(normalizedPath);
            }

            if (shouldFlush)
            {
                try { OpenDocumentFlusher!.Invoke(normalizedPath); }
                catch (Exception ex)
                {
                    Logger.Warn($"[StagedWorkspace] 冲刷已打开文档失败: {Path.GetFileName(normalizedPath)} — {ex.Message}");
                }
            }

            lock (_lock)
            {
                // 确保目录存在
                string? dir = Path.GetDirectoryName(normalizedPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool isNewFile = !File.Exists(normalizedPath);

                // 首次接触 → 登记 Baseline（用于撤销恢复）
                if (!_trackedFiles.ContainsKey(normalizedPath))
                {
                    string baselineContent = isNewFile ? string.Empty : File.ReadAllText(normalizedPath);
                    _trackedFiles[normalizedPath] = new StagedFile
                    {
                        FilePath = normalizedPath,
                        BaselineContent = baselineContent,
                        BaselineHash = !string.IsNullOrEmpty(baselineContent) ? ComputeSha256(baselineContent) : string.Empty,
                        BaselineLastWriteTimeUtc = isNewFile ? (DateTime?)null : File.GetLastWriteTimeUtc(normalizedPath),
                        Operation = isNewFile ? ProposedFileOperation.Add : ProposedFileOperation.Modify,
                    };
                }

                // ── 无 writer：直接落盘（原有行为，锁内执行）──
                if (OpenDocumentWriter == null)
                {
                    File.WriteAllText(normalizedPath, content);
                    Logger.Info($"[StagedWorkspace] 写穿: {Path.GetFileName(normalizedPath)} ({content.Length} chars)");
                    return;
                }
            }

            // ── writer 路径：锁外执行（内部切换 UI 线程，持锁等待会有死锁风险）──
            WriteViaOpenDocumentOrDisk(normalizedPath, content);
            Logger.Info($"[StagedWorkspace] 写穿: {Path.GetFileName(normalizedPath)} ({content.Length} chars)");
        }

        /// <summary>
        /// 统一落盘：优先通过已打开文档 buffer + 编辑器 Save 写入；未打开/失败时回退裸写盘。
        /// 必须在 _lock 外调用（writer 内部可能切换 UI 线程）。
        /// </summary>
        private void WriteViaOpenDocumentOrDisk(string normalizedPath, string content)
        {
            bool written = false;
            try
            {
                if (OpenDocumentWriter != null)
                    written = OpenDocumentWriter(normalizedPath, content);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[StagedWorkspace] buffer 写入异常，回退磁盘: {Path.GetFileName(normalizedPath)} — {ex.Message}");
                written = false;
            }

            if (!written)
                File.WriteAllText(normalizedPath, content);
        }

        /// <summary>
        /// 删除文件（直接删除磁盘文件），记录原始内容供撤销恢复。
        /// </summary>
        public void DeleteFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (!_trackedFiles.ContainsKey(normalizedPath) && File.Exists(normalizedPath))
                {
                    // 首次接触 → 登记 Baseline
                    _trackedFiles[normalizedPath] = new StagedFile
                    {
                        FilePath = normalizedPath,
                        BaselineContent = File.ReadAllText(normalizedPath),
                        BaselineHash = ComputeSha256(File.ReadAllText(normalizedPath)),
                        BaselineLastWriteTimeUtc = File.GetLastWriteTimeUtc(normalizedPath),
                        Operation = ProposedFileOperation.Delete,
                    };
                }
                else if (_trackedFiles.TryGetValue(normalizedPath, out var existing))
                {
                    existing.Operation = ProposedFileOperation.Delete;
                }

                if (File.Exists(normalizedPath))
                    File.Delete(normalizedPath);
            }
        }

        /// <summary>
        /// 获取当前的文件内容（直接读磁盘）。
        /// </summary>
        public string? GetStagedContent(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            return File.Exists(normalizedPath) ? File.ReadAllText(normalizedPath) : string.Empty;
        }

        /// <summary>
        /// 将所有被追踪的变更转换为可提交的 Batch（用于 diff 预览 / 冲突检测）。
        /// </summary>
        public PreparedChangeBatch ToPreparedChangeBatch()
        {
            lock (_lock)
            {
                var changes = _trackedFiles.Values
                    .Select(f =>
                    {
                        string currentContent = File.Exists(f.FilePath)
                            ? File.ReadAllText(f.FilePath)
                            : string.Empty;
                        return new PreparedChangeSet
                        {
                            FilePath = f.FilePath,
                            Operation = f.Operation,
                            BaselineText = f.BaselineContent,
                            BaselineHash = f.BaselineHash,
                            BaselineLastWriteTimeUtc = f.BaselineLastWriteTimeUtc,
                            ProposedText = currentContent,
                        };
                    })
                    .Where(c => !string.Equals(c.BaselineText, c.ProposedText, StringComparison.Ordinal)
                                || c.Operation == ProposedFileOperation.Add
                                || c.Operation == ProposedFileOperation.Delete)
                    .ToList();

                return new PreparedChangeBatch { Changes = changes };
            }
        }

        /// <summary>
        /// 撤销所有变更：将每个被追踪文件恢复到其 Baseline 内容（写回磁盘）。
        /// 新建文件会被删除，删除的文件会被恢复。
        ///
        /// 恢复写入优先通过已打开文档 buffer + 编辑器 Save（避免 dirty buffer 场景弹窗），
        /// 未打开的文件回退裸写盘。写入在 _lock 外执行（writer 内部可能切换 UI 线程）。
        /// </summary>
        public void RestoreToBaseline()
        {
            List<StagedFile> files;
            lock (_lock)
            {
                files = _trackedFiles.Values.ToList();
            }

            foreach (var file in files)
            {
                try
                {
                    switch (file.Operation)
                    {
                        case ProposedFileOperation.Add:
                            // 新建文件 → 回滚删除
                            if (File.Exists(file.FilePath))
                                File.Delete(file.FilePath);
                            break;
                        case ProposedFileOperation.Delete:
                            // 删除的文件 → 恢复
                            if (!string.IsNullOrEmpty(file.BaselineContent))
                            {
                                EnsureDirectoryExists(file.FilePath);
                                WriteViaOpenDocumentOrDisk(file.FilePath, file.BaselineContent);
                            }
                            break;
                        default:
                            // 修改 → 恢复原文
                            WriteViaOpenDocumentOrDisk(file.FilePath, file.BaselineContent);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[StagedWorkspace] 撤销失败: {Path.GetFileName(file.FilePath)} — {ex.Message}");
                }
            }

            lock (_lock)
            {
                _trackedFiles.Clear();
            }
            Logger.Info($"[StagedWorkspace] 已将所有改动恢复到 Baseline");
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// 确认所有变更（保留已落盘内容），清除撤销追踪。
        /// </summary>
        public void ConfirmAll()
        {
            lock (_lock)
            {
                _trackedFiles.Clear();
            }
            Logger.Info("[StagedWorkspace] 已确认所有改动（清除撤销追踪）");
        }

        /// <summary>
        /// 丢弃撤销追踪（不修改磁盘 —— 落盘内容保持不变）。用于确认后的清理。
        /// </summary>
        public void Discard()
        {
            ConfirmAll();
        }

        private static string NormalizePath(string path)
            => Path.GetFullPath(path.Trim());

        private static string ComputeSha256(string content)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 获取指定文件的所有差异块（Hunks）。
        /// 返回的空列表表示文件无变化或已被确认。
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<DiffHunkInfo> GetHunks(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return Array.Empty<DiffHunkInfo>();

                string currentContent = File.Exists(normalizedPath)
                    ? File.ReadAllText(normalizedPath)
                    : string.Empty;

                // 重算 hunks（若内容已变化如部分撤销后）
                var previousHunks = file.Hunks;
                file.Hunks = ComputeHunks(file.BaselineContent, currentContent);

                // ── 回填已撤销/已保留标记 ──
                // 重算会新建实例，需按 OldText+NewText 精确匹配恢复状态：
                // 已撤销块在重算后自然消失（内容已等于 Baseline），无需回填；
                // 已保留块内容未变，按内容匹配即可恢复其标记。
                foreach (var hunk in file.Hunks)
                {
                    foreach (var prev in previousHunks)
                    {
                        if (!prev.IsReverted && !prev.IsAccepted)
                            continue;

                        if (string.Equals(prev.OldText, hunk.OldText, StringComparison.Ordinal) &&
                            string.Equals(prev.NewText, hunk.NewText, StringComparison.Ordinal))
                        {
                            hunk.IsReverted = prev.IsReverted;
                            hunk.IsAccepted = prev.IsAccepted;
                            break;
                        }
                    }
                }

                return file.Hunks;
            }
        }

        /// <summary>
        /// 撤销单个差异块：只将该块的内容恢复到 Baseline，
        /// 其他块保持不变。从当前内容中定位该块并替换。
        ///
        /// 落盘优先通过已打开文档 buffer + 编辑器 Save（写入在 _lock 外执行，
        /// writer 内部可能切换 UI 线程，持锁等待会有死锁风险）。
        /// </summary>
        /// <returns>true 表示撤销成功</returns>
        public bool RestoreSingleHunk(string filePath, int hunkIndex)
        {
            var normalizedPath = NormalizePath(filePath);

            string revertedContent;
            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return false;

                if (hunkIndex < 0 || hunkIndex >= file.Hunks.Count)
                    return false;

                var hunk = file.Hunks[hunkIndex];
                if (hunk.IsReverted || hunk.IsAccepted)
                    return false;

                string currentContent = File.Exists(normalizedPath)
                    ? File.ReadAllText(normalizedPath)
                    : string.Empty;

                revertedContent = ApplyHunkRevert(currentContent, hunk);

                // ── 标记已撤销 ──
                hunk.IsReverted = true;

                Logger.Info($"[StagedWorkspace] 已撤销单块 [{hunkIndex}] ({Path.GetFileName(normalizedPath)})");

                // 若所有块都撤销了，等价于恢复到 Baseline
                if (file.Hunks.All(h => h.IsReverted))
                {
                    // 由调用方决定是否整文件回滚标记
                }
            }

            // ── 落盘（锁外执行）──
            WriteViaOpenDocumentOrDisk(normalizedPath, revertedContent);

            return true;
        }

        /// <summary>
        /// 保留单个差异块：接受该块的修改（磁盘内容不变，写穿模式已落盘），
        /// 该块不再计入待处理，也不再显示撤销/保留按钮。
        /// </summary>
        /// <returns>true 表示保留成功</returns>
        public bool AcceptSingleHunk(string filePath, int hunkIndex)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return false;

                if (hunkIndex < 0 || hunkIndex >= file.Hunks.Count)
                    return false;

                var hunk = file.Hunks[hunkIndex];
                if (hunk.IsReverted || hunk.IsAccepted)
                    return false;

                hunk.IsAccepted = true;

                Logger.Info($"[StagedWorkspace] 已保留单块 [{hunkIndex}] ({Path.GetFileName(normalizedPath)})");
                return true;
            }
        }

        /// <summary>
        /// 是否仍有未撤销的块。
        /// </summary>
        public bool HasPendingHunks(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return false;
                return file.Hunks.Any(h => !h.IsReverted && !h.IsAccepted);
            }
        }

        /// <summary>
        /// 构建「仅含待处理块」的显示基线：在当前内容的基础上，
        /// 把所有待处理（未保留且未撤销）块回退为 Baseline 原文。
        /// 以它作为 Diff 左侧时，只有待处理块会显示为差异：
        /// 已保留块并入新基线（不再高亮），已撤销块天然与基线一致（同样不显示）。
        /// </summary>
        public string BuildPendingOnlyBaseline(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            // 先刷新 hunks（重算 + 回填已保留/已撤销标记），确保标志位最新
            GetHunks(normalizedPath);

            lock (_lock)
            {
                string currentContent = File.Exists(normalizedPath)
                    ? File.ReadAllText(normalizedPath)
                    : string.Empty;

                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return currentContent;

                string content = currentContent;

                // 从后往前回退：先替换靠后的块，前面块的行号不会被推移
                for (int i = file.Hunks.Count - 1; i >= 0; i--)
                {
                    var hunk = file.Hunks[i];
                    if (hunk.IsReverted || hunk.IsAccepted) continue;
                    content = ApplyHunkRevert(content, hunk);
                }

                return content;
            }
        }

        /// <summary>
        /// 应用单块回滚：定位 hunk 在当前内容中的行范围，替换为 Baseline 对应内容。
        /// </summary>
        private static string ApplyHunkRevert(string currentContent, DiffHunkInfo hunk)
        {
            string[] currentLines = SplitLines(currentContent);

            // 定位块在当前文件中的范围
            int newStart = Math.Max(0, hunk.NewStartLine);
            int newEnd = newStart + hunk.NewLineCount;
            if (newStart > currentLines.Length) newStart = currentLines.Length;

            // 待替换为 Baseline 内容
            string[] oldLines = SplitLines(hunk.OldText);

            var result = new List<string>();

            // 替换前的内容
            for (int i = 0; i < newStart && i < currentLines.Length; i++)
                result.Add(currentLines[i]);

            // 插入 Baseline 原内容
            result.AddRange(oldLines);

            // 替换后的内容
            for (int i = newStart + hunk.NewLineCount; i < currentLines.Length; i++)
                result.Add(currentLines[i]);

            return string.Join("\r\n", result);
        }

        /// <summary>
        /// 计算 Baseline 与当前内容之间的行级差异块。
        /// 使用经典 LCS 动态规划 + 回溯，输出连续的变化区域。
        /// </summary>
        private static List<DiffHunkInfo> ComputeHunks(string baselineText, string currentText)
        {
            var hunks = new List<DiffHunkInfo>();
            if (baselineText == currentText) return hunks;

            string[] oldLines = SplitLines(baselineText);
            string[] newLines = SplitLines(currentText);

            int n = oldLines.Length, m = newLines.Length;

            // ── LCS DP 表 ──
            int[,] dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    dp[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            // ── 回溯：收集变化块 ──
            int oi = 0, ni = 0;
            var oldBlock = new List<string>();
            var newBlock = new List<string>();
            int oldStart = 0, newStart = 0;
            bool inChange = false;

            void Flush()
            {
                if (!inChange) return;
                hunks.Add(new DiffHunkInfo
                {
                    OldStartLine = oldBlock.Count > 0 ? oldStart : -1,
                    OldLineCount = oldBlock.Count,
                    NewStartLine = newBlock.Count > 0 ? newStart : -1,
                    NewLineCount = newBlock.Count,
                    OldText = string.Join("\r\n", oldBlock),
                    NewText = string.Join("\r\n", newBlock),
                });
                oldBlock.Clear();
                newBlock.Clear();
                inChange = false;
            }

            while (oi < n && ni < m)
            {
                if (string.Equals(oldLines[oi], newLines[ni], StringComparison.Ordinal))
                {
                    Flush();
                    oi++; ni++;
                }
                else if (dp[oi + 1, ni] >= dp[oi, ni + 1])
                {
                    if (!inChange) { oldStart = oi; newStart = ni; inChange = true; }
                    oldBlock.Add(oldLines[oi]);
                    oi++;
                }
                else
                {
                    if (!inChange) { oldStart = oi; newStart = ni; inChange = true; }
                    newBlock.Add(newLines[ni]);
                    ni++;
                }
            }

            // 尾部残余归入最后一个块
            if (oi < n || ni < m)
            {
                if (!inChange && oi < n) oldStart = oi;
                if (!inChange && ni < m) newStart = ni;
                inChange = true;
                while (oi < n) { oldBlock.Add(oldLines[oi]); oi++; }
                while (ni < m) { newBlock.Add(newLines[ni]); ni++; }
            }

            Flush();

            return hunks;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            // 保留每个"逻辑行"（含末尾空行对）
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            // 移除末尾的空字符串元素（Split 产生）
            return lines;
        }

        /// <summary>
        /// 单个被追踪文件的内部状态（记录 Baseline 供撤销恢复）。
        /// </summary>
        private sealed class StagedFile
        {
            public string FilePath { get; set; } = string.Empty;
            public string BaselineContent { get; set; } = string.Empty;
            public string BaselineHash { get; set; } = string.Empty;
            public DateTime? BaselineLastWriteTimeUtc { get; set; }
            public ProposedFileOperation Operation { get; set; } = ProposedFileOperation.Modify;

            /// <summary>差异块列表（Baseline vs 当前，逐块撤销用）</summary>
            public List<DiffHunkInfo> Hunks { get; set; } = new();
        }
    }
}
