using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Inline Diff 会话状态。
    /// </summary>
    public enum InlineDiffSessionState
    {
        Created,
        Showing,
        Applying,
        Committed,
        Dismissed,
        Conflicted,
        Failed,
        Disposed,
    }

    /// <summary>
    /// 单文件 Inline Diff 会话。
    /// 持有冻结的 Baseline 和 Proposal，管理只读 Diff Viewer 的生命周期，
    /// 委托提交给 <see cref="ProposalCommitCoordinator"/>。
    ///
    /// 预览阶段不修改任何真实文件或 Buffer，
    /// 仅在用户确认后通过 Coordinator 提交。
    /// </summary>
    public sealed class InlineDiffSession : IDisposable
    {
        #region Properties

        /// <summary>唯一标识</summary>
        public string SessionId { get; private set; }

        /// <summary>关联的变更提案</summary>
        public PreparedChangeSet Change { get; private set; }

        /// <summary>提交目标（决定如何写入）</summary>
        public IProposalCommitTarget CommitTarget { get; }

        /// <summary>提交协调器引用（用于 CommitAsync）</summary>
        public ProposalCommitCoordinator? Coordinator { get; set; }

        // ── 真实文档引用（仅当文档已打开时存在）──

        /// <summary>源 TextBuffer（已打开文档时不为 null）</summary>
        public ITextBuffer? SourceBuffer { get; }

        /// <summary>创建 Session 时的 Snapshot 基准</summary>
        public ITextSnapshot? SourceBaselineSnapshot { get; }

        // ── Diff 视图组件 ──

        /// <summary>冻结的原始内容 Buffer（只读显示用）</summary>
        public ITextBuffer BaselineDisplayBuffer => ViewerHandle.BaselineBuffer;

        /// <summary>建议内容 Buffer（只读显示用）</summary>
        public ITextBuffer ProposalBuffer => ViewerHandle.ProposalBuffer;

        /// <summary>差异缓冲区</summary>
        public IDifferenceBuffer DifferenceBuffer => ViewerHandle.DifferenceBuffer;

        /// <summary>WPF 差异查看器</summary>
        public IWpfDifferenceViewer Viewer => ViewerHandle.Viewer;

        /// <summary>Diff 视图句柄（统一管理缓冲区 + Viewer 生命周期）</summary>
        public DiffViewerHandle ViewerHandle { get; private set; }

        /// <summary>
        /// 写穿模式的撤销追踪器（可选）。
        /// 设置后：
        /// - Commit → 调用 <see cref="Editing.StagedEditWorkspace.ConfirmAll"/>（保留磁盘内容）
        /// - Dismiss → 调用 <see cref="Editing.StagedEditWorkspace.RestoreToBaseline"/>（恢复磁盘 Baseline）
        /// 用于 Agent 直接落盘后由用户决定保留/撤销的场景。
        /// </summary>
        public Editing.StagedEditWorkspace? Workspace { get; set; }

        /// <summary>当前会话状态</summary>
        public InlineDiffSessionState State { get; private set; } = InlineDiffSessionState.Created;

        #endregion

        #region Events

        /// <summary>状态变更事件</summary>
        public event EventHandler<InlineDiffSessionState>? StateChanged;

        /// <summary>Session 已释放事件</summary>
        public event EventHandler? Disposed;

        #endregion

        #region Constructor

        public InlineDiffSession(
            PreparedChangeSet change,
            IProposalCommitTarget commitTarget,
            DiffViewerHandle viewerHandle,
            ITextBuffer? sourceBuffer = null,
            ITextSnapshot? sourceBaselineSnapshot = null)
        {
            SessionId = change.ChangeId;
            Change = change ?? throw new ArgumentNullException(nameof(change));
            CommitTarget = commitTarget ?? throw new ArgumentNullException(nameof(commitTarget));
            ViewerHandle = viewerHandle ?? throw new ArgumentNullException(nameof(viewerHandle));

            SourceBuffer = sourceBuffer;
            SourceBaselineSnapshot = sourceBaselineSnapshot;
        }

        /// <summary>
        /// 替换 ViewerHandle（逐块撤销后重新基于当前内容创建只读预览时使用）。
        /// 调用方负责 Dispose 旧 handle。
        /// </summary>
        public void ReplaceViewerHandle(DiffViewerHandle newHandle)
        {
            ViewerHandle = newHandle ?? throw new ArgumentNullException(nameof(newHandle));
        }

        /// <summary>
        /// 替换变更提案（AI 在预览期间再次编辑同一文件时刷新会话内容）。
        /// 调用方需同步移交 Workspace 并通过 Host 重建 Viewer。
        /// </summary>
        public void ReplaceChange(PreparedChangeSet change)
        {
            Change = change ?? throw new ArgumentNullException(nameof(change));
            SessionId = change.ChangeId;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 标记 Session 为显示状态。
        /// </summary>
        public void MarkShowing()
        {
            TransitionTo(InlineDiffSessionState.Showing);
        }

        /// <summary>
        /// 提交变更。委托给 <see cref="ProposalCommitCoordinator"/> 执行。
        /// 幂等：已 Committed/Applying/Disposed 状态下重复调用会立即返回。
        /// </summary>
        public async Task<ApplyResult> CommitAsync(CancellationToken cancellationToken)
        {
            if (State == InlineDiffSessionState.Committed)
                return ApplyResult.Ok(Change.FilePath);

            if (State == InlineDiffSessionState.Applying)
                throw new InvalidOperationException("Session 正在提交中，不能重复操作。");

            if (State == InlineDiffSessionState.Disposed)
                throw new ObjectDisposedException(nameof(InlineDiffSession));

            TransitionTo(InlineDiffSessionState.Applying);

            try
            {
                ApplyResult result;

                if (Coordinator != null)
                {
                    result = await Coordinator.CommitSingleAsync(this, cancellationToken);
                }
                else
                {
                    // 无 Coordinator → 直接调用 CommitTarget
                    var preflight = await CommitTarget.PreflightAsync(Change, cancellationToken);
                    if (!preflight.CanProceed)
                    {
                        TransitionTo(InlineDiffSessionState.Conflicted);
                        return ApplyResult.Conflict(Change.FilePath, preflight.Reason ?? "预检失败");
                    }

                    result = await CommitTarget.CommitAsync(Change, cancellationToken);
                }

                // 确认提交后，若关联了写穿 Workspace，保留磁盘内容并清除撤销追踪
                if (result.Success && Workspace != null)
                {
                    Workspace.ConfirmAll();
                }

                if (result.Success)
                {
                    TransitionTo(InlineDiffSessionState.Committed);
                    // Committed 是终态：立即释放 Diff Viewer，并让 SessionManager 移除索引。
                    // 这样同一文档可以立刻创建下一次 Inline Diff。
                    Dispose();
                }
                else if (result.IsConflict)
                    TransitionTo(InlineDiffSessionState.Conflicted);
                else
                    TransitionTo(InlineDiffSessionState.Failed);

                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"[InlineDiffSession] 提交异常: {Change.FilePath} — {ex.Message}", ex);
                TransitionTo(InlineDiffSessionState.Failed);
                return ApplyResult.Failed(Change.FilePath, ex.Message);
            }
        }

        /// <summary>
        /// 撤销变更。在写穿模式下，若关联了 Workspace 且尚未被其他操作确认，
        /// 将磁盘恢复到 Baseline；否则仅关闭 Viewer。
        /// </summary>
        public void Dismiss()
        {
            if (State == InlineDiffSessionState.Dismissed ||
                State == InlineDiffSessionState.Committed ||
                State == InlineDiffSessionState.Disposed)
                return;

            // ── 写穿模式：撤销 = 恢复磁盘到 Baseline ──
            if (Workspace != null && Workspace.HasAnyTrackedChanges)
            {
                Workspace.RestoreToBaseline();
            }

            TransitionTo(InlineDiffSessionState.Dismissed);
            Dispose();
        }

        /// <summary>
        /// 写穿模式下的「保留全部」：磁盘内容即最终结果。
        /// 只确认并清除撤销追踪，不重新写入文件/缓冲区，
        /// 避免 CommitAsync 把整个 Proposal 重写回缓冲区，
        /// 覆盖用户已逐块撤销的内容。
        /// </summary>
        public void ConfirmWriteThrough()
        {
            if (State == InlineDiffSessionState.Committed ||
                State == InlineDiffSessionState.Dismissed ||
                State == InlineDiffSessionState.Disposed)
                return;

            Workspace?.ConfirmAll();

            TransitionTo(InlineDiffSessionState.Committed);
            Dispose();
        }

        /// <summary>
        /// 检查是否存在冲突（仅对已打开文档有效）。
        /// </summary>
        public bool HasConflict()
        {
            if (SourceBuffer == null || SourceBaselineSnapshot == null)
                return false;

            var currentSnapshot = SourceBuffer.CurrentSnapshot;
            return currentSnapshot.Version.VersionNumber != SourceBaselineSnapshot.Version.VersionNumber
                && !string.Equals(currentSnapshot.GetText(),
                    SourceBaselineSnapshot.GetText(), StringComparison.Ordinal);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (State == InlineDiffSessionState.Disposed) return;

            TransitionTo(InlineDiffSessionState.Disposed);
            ViewerHandle.Dispose();
            Disposed?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region Private Helpers

        // 移除 range 语法，改用 Substring
        private void TransitionTo(InlineDiffSessionState newState)
        {
            if (State == newState) return;

            var oldState = State;
            State = newState;

            StateChanged?.Invoke(this, newState);
            Logger.Info($"[InlineDiffSession] {SessionId.Substring(0, 8)}: {oldState} → {newState}");
        }

        #endregion
    }
}
