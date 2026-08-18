using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// InlineDiffSession 管理器。
    ///
    /// 职责：
    /// - 创建和销毁 Session。
    /// - 确保单文档单 Session（同一 Buffer 或同一路径）。
    /// - 管理待处理 Proposal（文件尚未在编辑器中打开时）。
    /// - 提供全局「全部保留」/「全部撤销」操作。
    /// </summary>
    public sealed class InlineDiffSessionManager : IDisposable
    {
        #region Fields

        // 以 ITextBuffer 为键（已打开文档）
        private readonly Dictionary<ITextBuffer, InlineDiffSession> _activeByBuffer = new();

        // 以规范化路径为键（未打开文件）
        private readonly Dictionary<string, PreparedChangeSet> _pendingByPath
            = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new();
        private readonly ProposalCommitCoordinator _coordinator = new();

        private bool _disposed;

        #endregion

        #region Events

        /// <summary>活跃 Session 数量变更事件</summary>
        public event EventHandler? SessionCountChanged;

        #endregion

        #region Properties

        /// <summary>活跃 Session 数量</summary>
        public int ActiveCount
        {
            get { lock (_lock) return _activeByBuffer.Count; }
        }

        /// <summary>待处理 Proposal 数量</summary>
        public int PendingCount
        {
            get { lock (_lock) return _pendingByPath.Count; }
        }

        /// <summary>变更总数（活跃 + 待处理）</summary>
        public int TotalChangeCount => ActiveCount + PendingCount;

        #endregion

        #region Public API — Session Creation

        /// <summary>
        /// 为已打开文档创建 Diff Session。
        /// </summary>
        /// <param name="textView">当前 WPF 文本视图</param>
        /// <param name="change">变更提案（ProposedText 已就绪）</param>
        /// <returns>创建的 Session；如果同一文档已有活跃 Session 则返回 null</returns>
        public InlineDiffSession? CreateSession(
            IWpfTextView textView, PreparedChangeSet change)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null) throw new ArgumentNullException(nameof(textView));
            if (change == null) throw new ArgumentNullException(nameof(change));

            var sourceBuffer = textView.TextBuffer;
            var baselineSnapshot = sourceBuffer.CurrentSnapshot;

            lock (_lock)
            {
                // 单文档单 Session
                if (_activeByBuffer.ContainsKey(sourceBuffer))
                {
                    Logger.Warn($"[SessionManager] 文档已有活跃 Session: {Path.GetFileName(change.FilePath)}");
                    return null;
                }
            }

            // 获取文件路径用于日志
            string? filePath = null;
            if (sourceBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
            {
                filePath = textDoc.FilePath;
            }

            // 创建只读 Diff Viewer
            DiffViewerHandle viewerHandle;
            try
            {
                viewerHandle = DiffViewerService.Instance.CreateReadOnlyPreview(
                    change.BaselineText,
                    change.ProposedText,
                    change.ContentTypeName,
                    DifferenceViewMode.Inline);
            }
            catch (Exception ex)
            {
                Logger.Error($"[SessionManager] 创建 Diff Viewer 失败: {ex.Message}", ex);
                return null;
            }

            // 创建 CommitTarget
            var commitTarget = new Editing.OpenBufferCommitTarget(
                sourceBuffer,
                baselineSnapshot,
                GetUndoHistoryRegistry());

            // 创建 Session
            var session = new InlineDiffSession(
                change,
                commitTarget,
                viewerHandle,
                sourceBuffer,
                baselineSnapshot)
            {
                Coordinator = _coordinator,
            };

            session.MarkShowing();

            lock (_lock)
            {
                _activeByBuffer[sourceBuffer] = session;
            }

            session.StateChanged += OnSessionStateChanged;
            session.Disposed += (s, e) =>
            {
                lock (_lock)
                {
                    _activeByBuffer.Remove(sourceBuffer);
                }
            };

            Logger.Info($"[SessionManager] Session 已创建: {session.SessionId.Substring(0, 8)} " +
                $"({Path.GetFileName(filePath ?? change.FilePath)})");
            SessionCountChanged?.Invoke(this, EventArgs.Empty);

            return session;
        }

        /// <summary>
        /// 尝试获取指定 buffer 的活跃 Session（供编辑器内嵌挂件查询）。
        /// </summary>
        public bool TryGetSession(ITextBuffer buffer, out InlineDiffSession? session)
        {
            lock (_lock)
            {
                return _activeByBuffer.TryGetValue(buffer, out session);
            }
        }

        /// <summary>
        /// 注册待处理 Proposal（文件不在编辑器中打开时）。
        /// 当用户稍后打开文件时，通过 <see cref="TryActivatePending"/> 自动激活。
        /// </summary>
        public void RegisterPending(PreparedChangeSet change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            if (string.IsNullOrWhiteSpace(change.FilePath)) return;

            var normalizedPath = Path.GetFullPath(change.FilePath);

            lock (_lock)
            {
                _pendingByPath[normalizedPath] = change;
            }

            SessionCountChanged?.Invoke(this, EventArgs.Empty);
            Logger.Info($"[SessionManager] 待处理 Proposal 已注册: {Path.GetFileName(normalizedPath)}");
        }

        /// <summary>
        /// 当文件在编辑器中打开时，检查是否有待处理 Proposal 并激活。
        /// </summary>
        public bool TryActivatePending(IWpfTextView textView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string? filePath = null;
            if (textView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                typeof(ITextDocument), out ITextDocument textDoc))
            {
                filePath = textDoc.FilePath;
            }

            if (string.IsNullOrWhiteSpace(filePath)) return false;

            PreparedChangeSet? pending;
            lock (_lock)
            {
                if (!_pendingByPath.TryGetValue(filePath, out pending))
                    return false;

                _pendingByPath.Remove(filePath);
            }

            var session = CreateSession(textView, pending);
            if (session != null)
            {
                Logger.Info($"[SessionManager] 待处理 Proposal 已激活: {Path.GetFileName(filePath)}");
                SessionCountChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            return false;
        }

        #endregion

        #region Public API — Batch Operations

        /// <summary>
        /// 接受所有活跃 Session 的变更。
        ///
        /// - 写穿模式 Session（关联 Workspace）：磁盘已是最终内容，直接 ConfirmWriteThrough
        ///   确认并清除撤销追踪，不重写文件/缓冲区（避免 FileCommitTarget 裸写盘触发
        ///   VS「文件已在磁盘上修改」弹窗，也避免覆盖用户逐块撤销后的内容）。
        /// - 普通预览 Session：走批量提交，且优先使用 Session 自带的 CommitTarget
        ///   （已打开文档 → OpenBufferCommitTarget，通过 buffer+编辑器 Save 提交）。
        /// </summary>
        public async Task<BatchApplyResult> AcceptAllAsync(CancellationToken cancellationToken)
        {
            List<InlineDiffSession> sessions;
            lock (_lock)
            {
                sessions = _activeByBuffer.Values.ToList();
            }

            // ── 分流：写穿模式直接确认，普通模式收集后批量提交 ──
            var normalSessions = new List<InlineDiffSession>();
            foreach (var session in sessions)
            {
                if (session.Workspace != null)
                {
                    try { session.ConfirmWriteThrough(); }
                    catch (Exception ex) { Logger.Warn($"[SessionManager] 写穿确认失败: {ex.Message}"); }
                }
                else
                {
                    normalSessions.Add(session);
                }
            }

            BatchApplyResult result;
            if (normalSessions.Count == 0)
            {
                result = BatchApplyResult.AllOk(Array.Empty<ApplyResult>());
            }
            else
            {
                // 构建 Batch + 优先使用 Session 自带的 CommitTarget
                var changes = normalSessions.Select(s => s.Change).ToList();
                var batch = new PreparedChangeBatch { Changes = changes };

                var preferredTargets = new Dictionary<string, IProposalCommitTarget>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var session in normalSessions)
                    preferredTargets[session.Change.FilePath] = session.CommitTarget;

                result = await _coordinator.CommitBatchAsync(batch, preferredTargets, cancellationToken);
            }

            // 清理已提交的 Session
            foreach (var session in sessions)
            {
                if (session.State == InlineDiffSessionState.Committed)
                    session.Dispose();
            }

            // 清理待处理
            lock (_lock) { _pendingByPath.Clear(); }

            SessionCountChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }

        /// <summary>
        /// 撤销所有活跃 Session（不修改任何文件）。
        /// </summary>
        public void DismissAll()
        {
            List<InlineDiffSession> sessions;
            lock (_lock)
            {
                sessions = _activeByBuffer.Values.ToList();
                _activeByBuffer.Clear();
                _pendingByPath.Clear();
            }

            foreach (var session in sessions)
            {
                try { session.Dismiss(); }
                catch (Exception ex) { Logger.Warn($"[SessionManager] Dismiss 失败: {ex.Message}"); }
            }

            SessionCountChanged?.Invoke(this, EventArgs.Empty);
            Logger.Info($"[SessionManager] 已撤销全部: {sessions.Count} 个 Session");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DismissAll();
        }

        #endregion

        #region Private Helpers

        private void OnSessionStateChanged(object? sender, InlineDiffSessionState state)
        {
            if (state is InlineDiffSessionState.Committed or
                InlineDiffSessionState.Dismissed or
                InlineDiffSessionState.Disposed)
            {
                SessionCountChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private static ITextUndoHistoryRegistry GetUndoHistoryRegistry()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var componentModel = (IComponentModel)
                Package.GetGlobalService(typeof(SComponentModel));
            return componentModel.DefaultExportProvider.GetExport<ITextUndoHistoryRegistry>()!.Value;
        }

        #endregion
    }
}
