using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Linq;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// 浮动窗口 Diff 宿主。
    /// 复用现有的 <see cref="DiffViewerWindow"/> 作为第一阶段稳定兜底方案。
    /// 后续可替换为 ToolWindowHost 或 DocumentTabHost。
    /// 支持逐块撤销（通过 workspace 的 hunks）。
    /// </summary>
    public sealed class FloatingWindowDiffHost : IDiffHost
    {
        private DiffViewerWindow? _window;
        private DiffViewerHandle? _currentHandle;

        public void Show(InlineDiffSession session)
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }

            // 确保上一个 handle 已被 Dispose，避免 VisualElement 父级冲突
            if (_currentHandle != null && !ReferenceEquals(_currentHandle, session.ViewerHandle))
            {
                try { _currentHandle.Dispose(); } catch { }
            }

            _window = new DiffViewerWindow(
                session.Change.BaselineText,
                session.Change.ProposedText,
                System.IO.Path.GetFileName(session.Change.FilePath),
                onAccept: () =>
                {
                    // ── 写穿模式：磁盘即最终内容，只确认并清除追踪；
                    //    避免 CommitAsync 把整个 Proposal 重写回缓冲区，
                    //    覆盖用户已逐块撤销的内容 ──
                    if (session.Workspace != null)
                    {
                        session.ConfirmWriteThrough();
                    }
                    else
                    {
                        _ = session.CommitAsync(System.Threading.CancellationToken.None);
                    }
                },
                onUndo: () =>
                {
                    session.Dismiss();
                });

            // 使用 Session 提供的 Viewer 替换窗口默认创建的 Viewer
            _currentHandle = session.ViewerHandle;
            _window.SetViewerHandle(session.ViewerHandle);

            // ── 逐块撤销/保留：加载 hunks + 绑定回调 ──
            if (session.Workspace != null)
            {
                var hunks = session.Workspace.GetHunks(session.Change.FilePath);

                _window.SetHunks(hunks, session.Change.FilePath,
                    onRevertHunk: hunkIndex =>
                    {
                        // ── 撤销单块 ──
                        if (!session.Workspace!.RestoreSingleHunk(
                                session.Change.FilePath, hunkIndex))
                            return;

                        Logger.Info($"[FloatingHost] 已撤销块 [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");

                        // ── 所有块均已处理 → 确认写穿并关闭窗口 ──
                        if (!session.Workspace.HasPendingHunks(session.Change.FilePath))
                        {
                            session.ConfirmWriteThrough();
                            Close();
                            return;
                        }

                        RebuildViewerForPendingHunks(session, _window);
                    },
                    onKeepHunk: hunkIndex =>
                    {
                        // ── 保留单块（磁盘不变，仅确认该块）──
                        if (!session.Workspace!.AcceptSingleHunk(
                                session.Change.FilePath, hunkIndex))
                            return;

                        Logger.Info($"[FloatingHost] 已保留块 [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");

                        // ── 所有块均已处理 → 确认写穿并关闭窗口 ──
                        if (!session.Workspace.HasPendingHunks(session.Change.FilePath))
                        {
                            session.ConfirmWriteThrough();
                            Close();
                            return;
                        }

                        RebuildViewerForPendingHunks(session, _window);
                    });
            }

            _window.Show();
            Logger.Info($"[FloatingHost] Diff 窗口已显示: {session.SessionId.Substring(0, 8)}");
        }

        /// <summary>
        /// 逐块保留/撤销后重建窗口内的 Diff Viewer。
        /// 左侧使用「仅含待处理块」的显示基线（已保留块并入新基线），
        /// 因此已保留/已撤销的块不再红绿高亮，视图中只剩待处理块及其按钮。
        /// </summary>
        private void RebuildViewerForPendingHunks(InlineDiffSession session, DiffViewerWindow window)
        {
            try
            {
                var workspace = session.Workspace!;

                // 写穿模式下磁盘已改，重新创建只读 preview 显示当前状态
                string currentContent = workspace.ReadFile(session.Change.FilePath);
                string displayBaseline = workspace.BuildPendingOnlyBaseline(session.Change.FilePath);

                var oldHandle = session.ViewerHandle;
                session.ReplaceViewerHandle(
                    DiffViewerService.Instance.CreateReadOnlyPreview(
                        displayBaseline, currentContent, session.Change.ContentTypeName));
                _currentHandle = session.ViewerHandle;
                window.ReplaceViewer(session.ViewerHandle, session);

                try { oldHandle.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Error($"[FloatingHost] 重建 Diff Viewer 失败: {ex.Message}", ex);
            }
        }

        public void Activate()
        {
            _window?.Activate();
        }

        public void Close()
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }

            if (_currentHandle != null)
            {
                try { _currentHandle.Dispose(); } catch { }
                _currentHandle = null;
            }
        }
    }
}
