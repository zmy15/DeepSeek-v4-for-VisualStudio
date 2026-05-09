using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑器 Diff 标记管理服务。在代码写入编辑器后，使用 VS SDK 的
    /// <see cref="ITextDifferencingSelectorService"/> 计算新旧快照差异，
    /// 将删除行临时插回缓冲区并用红色标记，新增行用绿色标记。
    /// 用户可通过「确认」移除标记保留变更，或「撤销」回退到原始代码。
    /// </summary>
    public class EditorDiffMarkerService
    {
        private static EditorDiffMarkerService? _instance;
        private static readonly object _instanceLock = new();

        private readonly Dictionary<ITextBuffer, DiffPreviewSession> _sessions = new();
        private readonly object _sessionsLock = new();

        // ── 待处理 diff 存储（按文件路径，用于未打开的文件）──
        private readonly Dictionary<string, PendingFileDiff> _pendingDiffs = new();
        private readonly object _pendingLock = new();

        /// <summary>
        /// 当任意 buffer 的预览状态变更时触发（用于通知 tagger 重绘）。
        /// </summary>
        public event Action<ITextBuffer>? PreviewStateChanged;

        /// <summary>
        /// 当待处理 diff 数量变更时触发（用于 UI 刷新全局按钮）。
        /// </summary>
        public event Action? PendingDiffCountChanged;

        /// <summary>
        /// 获取服务单例。
        /// </summary>
        public static EditorDiffMarkerService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new EditorDiffMarkerService();
                    }
                }
                return _instance;
            }
        }

        #region Public API

        /// <summary>
        /// 开始 Diff 预览。将新代码写入编辑器，计算差异，临时插回删除行，
        /// 并触发标记渲染。之后用户可调用 <see cref="ConfirmChanges"/> 或 <see cref="UndoChanges"/>。
        /// </summary>
        /// <param name="textView">目标文本视图</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="newContent">AI 生成的新代码</param>
        public void BeginDiffPreview(IWpfTextView textView, string originalContent, string newContent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                throw new ArgumentNullException(nameof(textView));

            var buffer = textView.TextBuffer;

            // ── 如果该 buffer 已有活跃会话，先确认旧会话 ──
            ClearSession(buffer);

            if (string.IsNullOrEmpty(newContent))
                return;

            try
            {
                // ── 1. 使用项目自带的 CodeDiffService 计算行级差异 ──
                var diffLines = CodeDiffService.ComputeDiff(originalContent, newContent);

                var addedCount = diffLines.Count(d => d.Type == DiffLineType.Added);
                var deletedCount = diffLines.Count(d => d.Type == DiffLineType.Deleted);

                // 无差异时跳过
                if (addedCount == 0 && deletedCount == 0)
                    return;

                // ── 2. 第一阶段编辑：将新内容写入 buffer ──
                string oldContentSaved = originalContent;
                using (var edit = buffer.CreateEdit())
                {
                    var snapshot = buffer.CurrentSnapshot;
                    if (snapshot.Length > 0)
                        edit.Replace(0, snapshot.Length, newContent);
                    else
                        edit.Insert(0, newContent);
                    edit.Apply();
                }

                // ── 3. 构建删除行插入计划 ──
                // 遍历 diffLines，找到连续删除块，计算其在 new buffer 中的插入位置
                var insertionPlan = BuildInsertionPlan(diffLines);

                // ── 4. 第二阶段编辑：从下往上插回删除行（避免位置偏移）──
                var trackedInsertions = new List<TrackedInsertion>();
                if (insertionPlan.Count > 0)
                {
                    using (var edit = buffer.CreateEdit())
                    {
                        foreach (var insertion in insertionPlan.OrderByDescending(ip => ip.InsertAfterNewLine))
                        {
                            var snapshot = buffer.CurrentSnapshot;
                            int lineIndex = insertion.InsertAfterNewLine;

                            // 边界保护
                            if (lineIndex < 0) lineIndex = 0;
                            if (lineIndex >= snapshot.LineCount)
                                lineIndex = snapshot.LineCount - 1;

                            var targetLine = snapshot.GetLineFromLineNumber(lineIndex);
                            int insertPos = targetLine.End.Position;

                            string deletedText = string.Join("\n", insertion.DeletedContents);
                            edit.Insert(insertPos, "\n" + deletedText);

                            // 创建跟踪区间（这里先记录信息，编辑后再获取精确的 ITrackingSpan）
                            // 暂时用位置信息存储，确认/撤销时重新定位
                            trackedInsertions.Add(new TrackedInsertion
                            {
                                // 编辑后将在 Apply 之后重新绑定 TrackingSpan
                                Content = deletedText,
                            });
                        }

                        edit.Apply();
                    }

                    // ── 编辑完成后，为每个插入的删除块创建 ITrackingSpan ──
                    RebindTrackingSpans(buffer, diffLines, trackedInsertions, insertionPlan);
                }

                // ── 5. 收集新增行的区间（用于绿色标记）──
                var addedLineSpans = new List<SnapshotSpan>();
                var currentSnapshot = buffer.CurrentSnapshot;

                foreach (var dline in diffLines)
                {
                    if (dline.Type == DiffLineType.Added && dline.NewLineNumber.HasValue)
                    {
                        int newLineIdx = dline.NewLineNumber.Value - 1; // 0-based
                        if (newLineIdx >= 0 && newLineIdx < currentSnapshot.LineCount)
                        {
                            var line = currentSnapshot.GetLineFromLineNumber(newLineIdx);
                            if (!IsLineTemporarilyInserted(line, trackedInsertions, currentSnapshot))
                            {
                                addedLineSpans.Add(new SnapshotSpan(line.Start, line.End));
                            }
                        }
                    }
                }

                // ── 6. 存储会话 ──
                var session = new DiffPreviewSession
                {
                    OriginalContent = oldContentSaved,
                    InsertedDeletedLines = trackedInsertions,
                    AddedLineSpans = addedLineSpans,
                    IsActive = true,
                };

                lock (_sessionsLock)
                {
                    _sessions[buffer] = session;
                }

                // ── 7. 通知 tagger 更新 ──
                PreviewStateChanged?.Invoke(buffer);

                Logger.Info($"[EditorDiff] 预览已激活: +{addedCount} -{deletedCount} 行");
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiff] BeginDiffPreview 失败: {ex.Message}", ex);
                // 出错时清理
                ClearSession(buffer);
            }
        }

        /// <summary>
        /// 确认变更：移除临时插入的红色删除行，清除所有标记。
        /// </summary>
        public void ConfirmChanges(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var session = GetActiveSession(buffer);
            if (session == null || !session.IsActive)
                return;

            try
            {
                // 删除所有临时插入的删除行
                if (session.InsertedDeletedLines.Count > 0)
                {
                    using (var edit = buffer.CreateEdit())
                    {
                        foreach (var insertion in session.InsertedDeletedLines.OrderByDescending(i =>
                        {
                            var span = i.TrackingSpan?.GetSpan(buffer.CurrentSnapshot);
                            return span?.Start.Position ?? 0;
                        }))
                        {
                            var span = insertion.TrackingSpan?.GetSpan(buffer.CurrentSnapshot);
                            if (span != null && span.Value.Length > 0)
                            {
                                // 删除整行（包括前导换行符）
                                var snapshot = buffer.CurrentSnapshot;
                                var startLine = span.Value.Start.GetContainingLine();
                                var endLine = span.Value.End.GetContainingLine();

                                int deleteStart = startLine.Start.Position;
                                int deleteEnd = endLine.EndIncludingLineBreak.Position;

                                // 确保不超过快照边界
                                if (deleteEnd > snapshot.Length)
                                    deleteEnd = snapshot.Length;

                                edit.Delete(deleteStart, deleteEnd - deleteStart);
                            }
                        }
                        edit.Apply();
                    }
                }

                Logger.Info("[EditorDiff] 变更已确认");
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiff] ConfirmChanges 失败: {ex.Message}", ex);
            }
            finally
            {
                ClearSession(buffer);
                PreviewStateChanged?.Invoke(buffer);
            }
        }

        /// <summary>
        /// 撤销变更：回退缓冲区到原始内容，清除所有标记。
        /// </summary>
        public void UndoChanges(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var session = GetActiveSession(buffer);
            if (session == null || !session.IsActive)
                return;

            try
            {
                string original = session.OriginalContent ?? string.Empty;

                using (var edit = buffer.CreateEdit())
                {
                    var snapshot = buffer.CurrentSnapshot;
                    if (snapshot.Length > 0)
                        edit.Replace(0, snapshot.Length, original);
                    else
                        edit.Insert(0, original);
                    edit.Apply();
                }

                Logger.Info("[EditorDiff] 变更已撤销");
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiff] UndoChanges 失败: {ex.Message}", ex);
            }
            finally
            {
                ClearSession(buffer);
                PreviewStateChanged?.Invoke(buffer);
            }
        }

        /// <summary>
        /// 获取指定 buffer 的活跃预览会话，无活跃会话时返回 null。
        /// </summary>
        public DiffPreviewSession? GetActiveSession(ITextBuffer buffer)
        {
            lock (_sessionsLock)
            {
                return _sessions.TryGetValue(buffer, out var session) ? session : null;
            }
        }

        /// <summary>
        /// 检查指定 buffer 是否处于 diff 预览状态。
        /// </summary>
        public bool IsPreviewActive(ITextBuffer buffer)
        {
            var session = GetActiveSession(buffer);
            return session != null && session.IsActive;
        }

        /// <summary>
        /// 获取所有活跃会话（用于批量清理）。
        /// </summary>
        public IReadOnlyList<KeyValuePair<ITextBuffer, DiffPreviewSession>> GetAllActiveSessions()
        {
            lock (_sessionsLock)
            {
                return _sessions.Where(kv => kv.Value.IsActive).ToList();
            }
        }

        /// <summary>
        /// 为未在编辑器中打开的文件注册待处理 diff。
        /// 当用户稍后打开该文件时，会通过 <see cref="TryActivatePendingDiff"/> 自动激活预览。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="newContent">AI 生成的新代码</param>
        public void RegisterPendingDiff(string filePath, string originalContent, string newContent)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            lock (_pendingLock)
            {
                _pendingDiffs[filePath] = new PendingFileDiff
                {
                    FilePath = filePath,
                    OriginalContent = originalContent ?? string.Empty,
                    NewContent = newContent ?? string.Empty,
                };
            }

            PendingDiffCountChanged?.Invoke();
            Logger.Info($"[EditorDiff] 已注册待处理 diff: {System.IO.Path.GetFileName(filePath)}");
        }

        /// <summary>
        /// 当文件在编辑器中打开时调用。检查是否有待处理 diff，如果有则激活预览。
        /// </summary>
        /// <param name="textView">新打开的文本视图</param>
        /// <returns>是否成功激活了待处理 diff</returns>
        public bool TryActivatePendingDiff(IWpfTextView textView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                return false;

            // 获取文件路径
            string? filePath = null;
            if (textView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                typeof(Microsoft.VisualStudio.Text.ITextDocument),
                out Microsoft.VisualStudio.Text.ITextDocument textDocument))
            {
                filePath = textDocument.FilePath;
            }

            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            PendingFileDiff? pending;
            lock (_pendingLock)
            {
                if (!_pendingDiffs.TryGetValue(filePath, out pending))
                    return false;

                _pendingDiffs.Remove(filePath);
            }

            // ── 激活预览 ──
            // 注意：此时 buffer 中已经是新内容（从磁盘读取的），我们只需插入删除行
            BeginDiffPreview(textView, pending.OriginalContent, pending.NewContent);

            PendingDiffCountChanged?.Invoke();
            Logger.Info($"[EditorDiff] 已激活待处理 diff: {System.IO.Path.GetFileName(filePath)}");
            return true;
        }

        /// <summary>
        /// 全局确认：确认所有活跃会话中的变更，并丢弃所有待处理 diff。
        /// </summary>
        public void AcceptAllChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // ── 确认所有活跃会话 ──
            List<ITextBuffer> activeBuffers;
            lock (_sessionsLock)
            {
                activeBuffers = _sessions.Where(kv => kv.Value.IsActive).Select(kv => kv.Key).ToList();
            }

            foreach (var buffer in activeBuffers)
            {
                ConfirmChanges(buffer);
            }

            // ── 丢弃所有待处理 diff ──
            int pendingCount;
            lock (_pendingLock)
            {
                pendingCount = _pendingDiffs.Count;
                _pendingDiffs.Clear();
            }

            if (pendingCount > 0)
                PendingDiffCountChanged?.Invoke();

            Logger.Info($"[EditorDiff] 已全局确认: {activeBuffers.Count} 个活跃会话 + {pendingCount} 个待处理 diff");
        }

        /// <summary>
        /// 全局撤销：撤销所有活跃会话并丢弃所有待处理 diff。
        /// </summary>
        public void UndoAllChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // ── 撤销所有活跃会话 ──
            List<ITextBuffer> activeBuffers;
            lock (_sessionsLock)
            {
                activeBuffers = _sessions.Where(kv => kv.Value.IsActive).Select(kv => kv.Key).ToList();
            }

            foreach (var buffer in activeBuffers)
            {
                UndoChanges(buffer);
            }

            // ── 丢弃所有待处理 diff（这些文件尚未打开，不需要撤销编辑，只需丢弃记录）──
            int pendingCount;
            lock (_pendingLock)
            {
                pendingCount = _pendingDiffs.Count;
                _pendingDiffs.Clear();
            }

            if (pendingCount > 0)
                PendingDiffCountChanged?.Invoke();

            Logger.Info($"[EditorDiff] 已全局撤销: {activeBuffers.Count} 个活跃会话 + {pendingCount} 个待处理 diff");
        }

        /// <summary>
        /// 获取活跃会话数量。
        /// </summary>
        public int GetActiveCount()
        {
            lock (_sessionsLock)
            {
                return _sessions.Count(kv => kv.Value.IsActive);
            }
        }

        /// <summary>
        /// 获取待处理 diff 数量。
        /// </summary>
        public int GetPendingCount()
        {
            lock (_pendingLock)
            {
                return _pendingDiffs.Count;
            }
        }

        /// <summary>
        /// 获取变更文件总数（活跃 + 待处理）。
        /// </summary>
        public int GetTotalChangeCount()
        {
            return GetActiveCount() + GetPendingCount();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 根据 DiffLine 列表构建删除行插入计划。
        /// 返回应在 new buffer 何处插入哪些删除行。
        /// </summary>
        private List<DeletionInsertionPlan> BuildInsertionPlan(List<DiffLine> diffLines)
        {
            var plan = new List<DeletionInsertionPlan>();
            int i = 0;

            while (i < diffLines.Count)
            {
                // 收集连续的删除行
                if (diffLines[i].Type == DiffLineType.Deleted)
                {
                    var deletedContents = new List<string>();
                    int? insertAfterNewLine = null;

                    // 查找插入位置：前一个非删除行的 NewLineNumber
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (diffLines[j].NewLineNumber.HasValue)
                        {
                            insertAfterNewLine = diffLines[j].NewLineNumber.Value - 1; // 0-based
                            break;
                        }
                    }
                    // 如果没有前面的行，插入到开头
                    if (insertAfterNewLine == null)
                        insertAfterNewLine = -1; // 表示插入到文件开头

                    while (i < diffLines.Count && diffLines[i].Type == DiffLineType.Deleted)
                    {
                        deletedContents.Add(diffLines[i].Content ?? string.Empty);
                        i++;
                    }

                    plan.Add(new DeletionInsertionPlan
                    {
                        InsertAfterNewLine = insertAfterNewLine.Value,
                        DeletedContents = deletedContents,
                    });
                }
                else
                {
                    i++;
                }
            }

            return plan;
        }

        /// <summary>
        /// 编辑完成后，为临时插入的删除行创建 ITrackingSpan。
        /// </summary>
        private void RebindTrackingSpans(
            ITextBuffer buffer,
            List<DiffLine> diffLines,
            List<TrackedInsertion> trackedInsertions,
            List<DeletionInsertionPlan> insertionPlan)
        {
            var snapshot = buffer.CurrentSnapshot;

            // 重新遍历 diffLines 找出连续删除块
            int trackedIdx = 0;
            int i = 0;

            while (i < diffLines.Count && trackedIdx < trackedInsertions.Count)
            {
                if (diffLines[i].Type == DiffLineType.Deleted)
                {
                    int deleteLineCount = 0;
                    while (i < diffLines.Count && diffLines[i].Type == DiffLineType.Deleted)
                    {
                        deleteLineCount++;
                        i++;
                    }

                    // 找到这条删除块在 buffer 中的位置
                    // 删除行被插入在 InsertAfterNewLine 行之后
                    if (trackedIdx < insertionPlan.Count)
                    {
                        var plan = insertionPlan[trackedIdx];
                        int insertAfter = plan.InsertAfterNewLine;

                        // 找到插入位置对应的行
                        int searchLine = insertAfter >= 0 ? insertAfter + 1 : 0;
                        if (searchLine < snapshot.LineCount)
                        {
                            var startLine = snapshot.GetLineFromLineNumber(searchLine);
                            var endLine = snapshot.GetLineFromLineNumber(
                                Math.Min(searchLine + deleteLineCount - 1, snapshot.LineCount - 1));

                            var span = new SnapshotSpan(startLine.Start, endLine.EndIncludingLineBreak);
                            var trackingSpan = snapshot.CreateTrackingSpan(
                                span, SpanTrackingMode.EdgeExclusive);

                            trackedInsertions[trackedIdx].TrackingSpan = trackingSpan;
                        }
                    }

                    trackedIdx++;
                }
                else
                {
                    i++;
                }
            }
        }

        /// <summary>
        /// 检查某行是否属于临时插入的删除行。
        /// </summary>
        private bool IsLineTemporarilyInserted(
            ITextSnapshotLine line,
            List<TrackedInsertion> trackedInsertions,
            ITextSnapshot snapshot)
        {
            foreach (var insertion in trackedInsertions)
            {
                var span = insertion.TrackingSpan?.GetSpan(snapshot);
                if (span != null)
                {
                    if (line.Start.Position >= span.Value.Start.Position &&
                        line.End.Position <= span.Value.End.Position)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 清除指定 buffer 的会话。
        /// </summary>
        private void ClearSession(ITextBuffer buffer)
        {
            lock (_sessionsLock)
            {
                _sessions.Remove(buffer);
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Diff 预览会话。每个 <see cref="ITextBuffer"/> 最多一个活跃会话。
    /// </summary>
    public class DiffPreviewSession
    {
        /// <summary>修改前的原始代码（用于撤销）。</summary>
        public string OriginalContent { get; set; } = string.Empty;

        /// <summary>临时插入的删除行列表（红色标记）。</summary>
        public List<TrackedInsertion> InsertedDeletedLines { get; set; } = new();

        /// <summary>新增行的区间列表（绿色标记）。</summary>
        public List<SnapshotSpan> AddedLineSpans { get; set; } = new();

        /// <summary>是否处于活跃预览状态。</summary>
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// 追踪一次临时插入的删除文本。
    /// </summary>
    public class TrackedInsertion
    {
        /// <summary>跟踪插入位置的跨度（用于确认时删除）。</summary>
        public ITrackingSpan? TrackingSpan { get; set; }

        /// <summary>插入的文本内容。</summary>
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// 待处理文件 diff（文件尚未在编辑器中打开）。
    /// </summary>
    public class PendingFileDiff
    {
        /// <summary>文件完整路径。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>修改前的原始代码。</summary>
        public string OriginalContent { get; set; } = string.Empty;

        /// <summary>AI 生成的新代码。</summary>
        public string NewContent { get; set; } = string.Empty;
    }

    /// <summary>
    /// 删除行插入计划（内部使用）。
    /// </summary>
    internal class DeletionInsertionPlan
    {
        /// <summary>在 new buffer 的哪一行之后插入（0-based，-1 表示开头）。</summary>
        public int InsertAfterNewLine { get; set; }

        /// <summary>要插入的删除行内容列表。</summary>
        public List<string> DeletedContents { get; set; } = new();
    }

    #endregion
}
