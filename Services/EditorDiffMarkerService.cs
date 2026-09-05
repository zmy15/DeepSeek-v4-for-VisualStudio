using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using DeepSeek_v4_for_VisualStudio.View.Hosts;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑器 Diff 预览管理服务。
    /// 支持两种模式：旧版 write-then-preview（兼容路径）和新的 preview-then-commit（InlineDiffSession）。
    /// </summary>
    public class EditorDiffMarkerService
    {
        #region Singleton

        private static EditorDiffMarkerService? _instance;
        private static readonly object _instanceLock = new();

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

        #endregion

        #region Fields

        // 每个 buffer 对应的活跃差异预览窗口（旧版 write-then-preview）
        private readonly Dictionary<ITextBuffer, DiffViewerWindow> _activeWindows = new();
        private readonly object _windowsLock = new();

        // 待处理 diff 存储（按文件路径，用于未打开的文件）
        private readonly Dictionary<string, PendingFileDiff> _pendingDiffs = new();
        private readonly object _pendingLock = new();

        // ── 新版 Session-based diff ──
        private readonly InlineDiffSessionManager _sessionManager = new();
        private readonly Dictionary<ITextBuffer, EditorDiffHost> _editorHosts = new();
        private readonly object _editorHostsLock = new();

        /// <summary>待处理 diff 的默认过期时间（30 分钟）。</summary>
        private static readonly TimeSpan PendingDiffTtl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// 当待处理 diff 数量变更时触发（用于 UI 刷新全局按钮）。
        /// </summary>
        public event Action? PendingDiffCountChanged;

        #endregion

        #region Public API — Diff Preview

        /// <summary>
        /// 开始 Diff 预览。弹出 VS SDK 原生差异查看浮窗，
        /// 展示旧代码与新代码的差异（自动红绿着色、支持内联/并排切换）。
        /// </summary>
        /// <param name="textView">目标文本视图（其缓冲区应已包含新代码）</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="newContent">AI 生成的新代码（当前缓冲区内容）</param>
        public void BeginDiffPreview(IWpfTextView textView, string originalContent, string newContent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                throw new ArgumentNullException(nameof(textView));

            var buffer = textView.TextBuffer;

            // 如果该 buffer 已有活跃预览窗口，先关闭
            CloseExistingWindow(buffer);

            if (string.IsNullOrEmpty(newContent) || originalContent == newContent)
                return;

            try
            {
                // 获取文件路径作为标题
                string? filePath = null;
                if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                {
                    filePath = System.IO.Path.GetFileName(textDoc.FilePath);
                }

                string title = filePath ?? "代码对比";

                // 创建差异查看浮窗
                var window = new DiffViewerWindow(
                    oldContent: originalContent,
                    newContent: newContent,
                    title: title,
                    onAccept: () =>
                    {
                        // 确认：新代码已写入缓冲区，无需额外操作
                        Logger.Info($"[EditorDiff] 用户确认变更: {title}");
                        RemoveWindow(buffer);
                    },
                    onUndo: () =>
                    {
                        // 撤销：回退缓冲区到原始代码
                        UndoChangesInternal(buffer, originalContent);
                        Logger.Info($"[EditorDiff] 用户撤销变更: {title}");
                        RemoveWindow(buffer);
                    });

                // 注册并显示
                lock (_windowsLock)
                {
                    _activeWindows[buffer] = window;
                }

                window.Closed += (s, e) =>
                {
                    RemoveWindow(buffer);
                };

                window.Show(); // 非模态浮窗

                // ── 通知 UI 刷新全局 diff 控制栏 ──
                PendingDiffCountChanged?.Invoke();
                Logger.Info($"[EditorDiff] 活跃 diff 窗口已创建: {title} (活跃={_activeWindows.Count}, 待处理={GetPendingCount()})");
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiff] BeginDiffPreview 失败: {ex.Message}", ex);
                RemoveWindow(buffer);
            }
        }

        /// <summary>
        /// 检查指定 buffer 是否有活跃的差异预览。
        /// </summary>
        public bool IsPreviewActive(ITextBuffer buffer)
        {
            lock (_windowsLock)
            {
                return _activeWindows.ContainsKey(buffer);
            }
        }

        /// <summary>
        /// 新版 Inline Diff 预览（preview-then-commit）。
        /// 预览阶段不修改 sourceBuffer，用户确认后才通过 ProposalCommitCoordinator 提交。
        /// </summary>
        /// <param name="textView">目标 WPF 文本视图（缓冲区应仍为原始内容）</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="proposedContent">AI 建议的新代码</param>
        public InlineDiffSession? CreateInlineDiffPreview(
            IWpfTextView textView, string originalContent, string proposedContent,
            IReadOnlyList<ProposedTextChange>? textChanges = null,
            Editing.StagedEditWorkspace? workspace = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                throw new ArgumentNullException(nameof(textView));

            if (string.IsNullOrEmpty(proposedContent) || originalContent == proposedContent)
                return null;

            // 获取文件路径
            string filePath = GetFilePathFromBuffer(textView.TextBuffer)
                ?? textView.TextBuffer.GetHashCode().ToString();

            // 构建 PreparedChangeSet
            var change = new PreparedChangeSet
            {
                FilePath = filePath,
                Operation = ProposedFileOperation.Modify,
                BaselineText = originalContent,
                ProposedText = proposedContent,
                TextChanges = textChanges ?? Array.Empty<ProposedTextChange>(),
                ContentTypeName = textView.TextBuffer.ContentType.TypeName,
                SaveBehavior = ProposalSaveBehavior.KeepDocumentDirty,
            };

            // ── 同一文档已有活跃写穿会话（AI 在预览期间再次编辑同一文件）→ 原地刷新 ──
            // 旧会话的 Baseline / Hunks 已过期，直接创建会被单文档单 Session 约束拒绝。
            if (workspace != null &&
                _sessionManager.TryGetSession(textView.TextBuffer, out var existingSession) &&
                existingSession != null)
            {
                RefreshWriteThroughSession(existingSession, change, workspace, textView.TextBuffer);
                Logger.Info($"[EditorDiff] Inline Diff Session 已刷新: {existingSession.SessionId.Substring(0, 8)} ({Path.GetFileName(filePath)})");
                return existingSession;
            }

            // 创建 Session
            var session = _sessionManager.CreateSession(textView, change);

            if (session == null)
            {
                Logger.Warn($"[EditorDiff] 无法创建 Session: {Path.GetFileName(filePath)}");
                return null;
            }

            // ── 写穿模式：关联 Workspace，撤销时恢复磁盘 Baseline ──
            if (workspace != null)
            {
                session.Workspace = workspace;
            }

            // 订阅 Session 事件，通知 UI
            session.StateChanged += (s, state) =>
            {
                if (state == InlineDiffSessionState.Committed ||
                    state == InlineDiffSessionState.Dismissed)
                {
                    PendingDiffCountChanged?.Invoke();
                }
            };

            // 显示：先登记宿主，待编辑器首帧渲染后再挂载覆盖层，避免阻塞视图创建
            var editorHost = new EditorDiffHost(textView);

            lock (_editorHostsLock)
            {
                _editorHosts[textView.TextBuffer] = editorHost;
            }

            textView.VisualElement.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!textView.IsClosed && session.State == InlineDiffSessionState.Showing)
                        editorHost.Show(session);
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);

            PendingDiffCountChanged?.Invoke();
            Logger.Info($"[EditorDiff] Inline Diff Session 已创建: {session.SessionId.Substring(0, 8)} ({Path.GetFileName(filePath)})");

            return session;
        }

        /// <summary>
        /// 原地刷新已有写穿会话：把撤销追踪移交新 Workspace、替换变更内容，
        /// 并通过 EditorDiffHost 重建只读视图与 hunk 按钮。
        /// </summary>
        private void RefreshWriteThroughSession(
            InlineDiffSession session,
            PreparedChangeSet change,
            Editing.StagedEditWorkspace workspace,
            ITextBuffer buffer)
        {
            var oldWorkspace = session.Workspace;
            if (oldWorkspace != null && !ReferenceEquals(oldWorkspace, workspace))
                oldWorkspace.DiscardFile(change.FilePath);

            session.ReplaceChange(change);
            session.Workspace = workspace;

            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var editorHost))
                    editorHost.RefreshSession(session);
            }
        }

        /// <summary>
        /// 获取活跃 Session 数量（新版）。
        /// </summary>
        public int GetActiveSessionCount() => _sessionManager.ActiveCount;

        /// <summary>
        /// 获取差异预览的数量（旧版活跃窗口 + 新版 Session）。
        /// </summary>
        public int GetActiveCount()
        {
            lock (_windowsLock)
            {
                return _activeWindows.Count;
            }
        }

        /// <summary>
        /// 编辑器内嵌挂件：获取指定 buffer 的活跃 Session 及差异块（Hunks）。
        /// 返回 null 表示该 buffer 无活跃 Session。
        /// </summary>
        public InlineDiffSession? GetActiveSessionForBuffer(ITextBuffer buffer)
        {
            if (_sessionManager.TryGetSession(buffer, out var session))
                return session;
            return null;
        }

        /// <summary>
        /// 编辑器内嵌挂件：获取指定 buffer 当前待处理的差异块（含已撤销状态）。
        /// </summary>
        public IReadOnlyList<Models.DiffHunkInfo> GetHunksForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null)
                return Array.Empty<Models.DiffHunkInfo>();
            return session.Workspace.GetHunks(session.Change.FilePath);
        }

        /// <summary>
        /// 编辑器内嵌挂件：撤销指定 buffer 的某个差异块，并刷新编辑器。
        /// </summary>
        public bool RevertHunkForBuffer(ITextBuffer buffer, int hunkIndex)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null) return false;

            bool ok = session.Workspace.RestoreSingleHunk(session.Change.FilePath, hunkIndex);
            if (ok)
            {
                // 同步 VS 编辑器缓冲区（写穿已落盘，此处刷新内存 buffer）
                PendingDiffCountChanged?.Invoke();
                Logger.Info($"[EditorDiff] 编辑器内嵌：已撤销块 [{hunkIndex}] ({System.IO.Path.GetFileName(session.Change.FilePath)})");
            }
            return ok;
        }

        /// <summary>
        /// 编辑器内嵌挂件：保留指定 buffer 的某个差异块（磁盘不变，仅确认该块）。
        /// </summary>
        public bool AcceptHunkForBuffer(ITextBuffer buffer, int hunkIndex)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null) return false;

            bool ok = session.Workspace.AcceptSingleHunk(session.Change.FilePath, hunkIndex);
            if (ok)
            {
                PendingDiffCountChanged?.Invoke();
                Logger.Info($"[EditorDiff] 编辑器内嵌：已保留块 [{hunkIndex}] ({System.IO.Path.GetFileName(session.Change.FilePath)})");
            }
            return ok;
        }

        /// <summary>
        /// 编辑器内嵌挂件：保留全部。写穿模式下磁盘即最终内容，
        /// 只确认并清除撤销追踪（不重写文件）；非写穿模式走正常 Commit 流程。
        /// </summary>
        public void ConfirmAllForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session == null) return;

            CloseEditorHost(buffer);

            if (session.Workspace != null)
            {
                session.ConfirmWriteThrough();
            }
            else
            {
                _ = session.CommitAsync(System.Threading.CancellationToken.None);
            }
        }

        /// <summary>
        /// 编辑器内嵌挂件：撤销全部。恢复 Baseline 并关闭浮动 Diff 窗口。
        /// </summary>
        public void DismissSessionForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session == null) return;

            CloseEditorHost(buffer);
            session.Dismiss();
        }
        #endregion

        #region Public API — Confirm / Undo

        /// <summary>
        /// 确认变更：关闭指定 buffer 的差异预览窗口。
        /// （新代码已写入缓冲区，确认只是关闭预览）
        /// </summary>
        public void ConfirmChanges(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CloseExistingWindow(buffer);
            Logger.Info("[EditorDiff] 变更已确认");
        }

        /// <summary>
        /// 撤销变更：回退缓冲区到原始代码，关闭预览窗口。
        /// 整个操作在 lock 内完成以避免竞态条件。
        /// </summary>
        public void UndoChanges(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DiffViewerWindow? window;
            lock (_windowsLock)
            {
                if (!_activeWindows.TryGetValue(buffer, out window))
                    return;
                _activeWindows.Remove(buffer);

                // ── 在 lock 内执行撤销和关闭，防止 BeginDiffPreview 竞态覆盖 ──
                try
                {
                    window.PerformUndo();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[EditorDiff] UndoChanges 回退失败: {ex.Message}", ex);
                }

                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[EditorDiff] UndoChanges 关闭窗口失败: {ex.Message}", ex);
                }
            }

            PendingDiffCountChanged?.Invoke();
        }

        #endregion

        #region Public API — Pending Diffs

        /// <summary>
        /// 为未在编辑器中打开的文件注册待处理 diff。
        /// 当用户稍后打开该文件时，会通过 <see cref="TryActivatePendingDiff"/> 自动激活预览。
        /// 注册前清理过期条目，防止内存泄漏。
        /// </summary>
        public void RegisterPendingDiff(string filePath, string originalContent, string newContent)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            lock (_pendingLock)
            {
                // 清理过期条目
                var now = DateTime.UtcNow;
                var expiredKeys = _pendingDiffs
                    .Where(kv => now - kv.Value.RegisteredAt > PendingDiffTtl)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in expiredKeys)
                    _pendingDiffs.Remove(key);

                _pendingDiffs[filePath] = new PendingFileDiff
                {
                    FilePath = filePath,
                    OriginalContent = originalContent ?? string.Empty,
                    NewContent = newContent ?? string.Empty,
                    RegisteredAt = now,
                };
            }

            PendingDiffCountChanged?.Invoke();
            Logger.Info($"[EditorDiff] 已注册待处理 diff: {System.IO.Path.GetFileName(filePath)}");
        }

        /// <summary>
        /// 当文件在编辑器中打开时调用。检查是否有待处理 diff，如果有则激活预览。
        /// </summary>
        public bool TryActivatePendingDiff(IWpfTextView textView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                return false;

            // 获取文件路径
            string? filePath = null;
            if (textView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                typeof(ITextDocument), out ITextDocument textDocument))
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

            // 激活预览（当前 buffer 中已是新内容，从磁盘读取的）
            BeginDiffPreview(textView, pending.OriginalContent, pending.NewContent);

            PendingDiffCountChanged?.Invoke();
            Logger.Info($"[EditorDiff] 已激活待处理 diff: {System.IO.Path.GetFileName(filePath)}");
            return true;
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

        #region Public API — Batch Operations

        /// <summary>
        /// 全局确认：接受所有新版 Session 的变更，关闭所有旧版 diff 窗口，
        /// 丢弃所有待处理 diff。
        /// </summary>
        public async void AcceptAllChanges()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 新版：接受所有 Session
            await _sessionManager.AcceptAllAsync(CancellationToken.None);

            // 旧版：关闭所有活跃窗口
            List<DiffViewerWindow> windows;
            lock (_windowsLock)
            {
                windows = _activeWindows.Values.ToList();
                _activeWindows.Clear();
            }

            foreach (var window in windows)
            {
                try { window.PerformAccept(); }
                catch { /* ignore */ }
                try { window.Close(); }
                catch { /* ignore */ }
            }

            int pendingCount;
            lock (_pendingLock)
            {
                pendingCount = _pendingDiffs.Count;
                _pendingDiffs.Clear();
            }

            if (pendingCount > 0)
                PendingDiffCountChanged?.Invoke();

            Logger.Info($"[EditorDiff] 已全局确认: {windows.Count} 个旧版窗口 + {pendingCount} 个待处理 diff");
        }

        /// <summary>
        /// 全局撤销：新版 Session(s) + 旧版窗口 + 待处理 diff。
        /// </summary>
        public void UndoAllChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 新版：撤销所有 Session
            _sessionManager.DismissAll();

            // 旧版：关闭所有活跃窗口
            List<DiffViewerWindow> windows;
            lock (_windowsLock)
            {
                windows = _activeWindows.Values.ToList();
                _activeWindows.Clear();
            }

            foreach (var window in windows)
            {
                try { window.PerformUndo(); }
                catch { /* ignore */ }
            }

            foreach (var window in windows)
            {
                try { window.Close(); }
                catch { /* ignore */ }
            }

            int pendingCount;
            lock (_pendingLock)
            {
                pendingCount = _pendingDiffs.Count;
                _pendingDiffs.Clear();
            }

            if (pendingCount > 0)
                PendingDiffCountChanged?.Invoke();

            Logger.Info($"[EditorDiff] 已全局撤销: {windows.Count} 个旧版窗口 + {pendingCount} 个待处理 diff");
        }

        #endregion

        #region Private Methods

        private void CloseExistingWindow(ITextBuffer buffer)
        {
            DiffViewerWindow? existing;
            lock (_windowsLock)
            {
                if (!_activeWindows.TryGetValue(buffer, out existing))
                    return;
                _activeWindows.Remove(buffer);
            }

            try { existing.Close(); }
            catch { /* ignore */ }

            // ── 通知 UI 刷新 ──
            PendingDiffCountChanged?.Invoke();
        }

        private void RemoveWindow(ITextBuffer buffer)
        {
            lock (_windowsLock)
            {
                _activeWindows.Remove(buffer);
            }

            // ── 通知 UI 刷新 ──
            PendingDiffCountChanged?.Invoke();
        }

        private void UndoChangesInternal(ITextBuffer buffer, string originalContent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                using (var edit = buffer.CreateEdit())
                {
                    var snapshot = buffer.CurrentSnapshot;
                    if (snapshot.Length > 0)
                        edit.Replace(0, snapshot.Length, originalContent);
                    else
                        edit.Insert(0, originalContent);
                    edit.Apply();
                }

                Logger.Info("[EditorDiff] 缓冲区已回退到原始内容");
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiff] 回退缓冲区失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从 ITextBuffer 获取关联的文件路径。
        /// </summary>
        private static string? GetFilePathFromBuffer(ITextBuffer buffer)
        {
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                return textDoc.FilePath;
            return null;
        }

        /// <summary>
        /// Close the editor-embedded diff host (remove from adornment layer).
        /// </summary>
        private void CloseEditorHost(ITextBuffer buffer)
        {
            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var host))
                {
                    host.Close();
                    _editorHosts.Remove(buffer);
                }
            }
        }


        #endregion
    }

    #region Supporting Types

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

        /// <summary>注册时间（UTC），用于 TTL 过期清理。</summary>
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    }

    #endregion
}
