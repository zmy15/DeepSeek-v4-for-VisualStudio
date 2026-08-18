using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.ComponentModel;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// VS SDK 原生差异对比浮窗。
    /// 内部委托给 <see cref="InlineDiffHostControl"/>，自身仅作为 Window 壳。
    /// 视图由外部通过 <see cref="SetViewerHandle"/> 注入，不再自建，避免 child 冲突。
    /// </summary>
    public partial class DiffViewerWindow : Window
    {
        private readonly Action? _onAccept;
        private readonly Action? _onUndo;
        private bool _isClosing;

        public DiffViewerWindow(
            string oldContent,
            string newContent,
            string? title = null,
            Action? onAccept = null,
            Action? onUndo = null)
        {
            InitializeComponent();

            _onAccept = onAccept;
            _onUndo = onUndo;

            if (!string.IsNullOrEmpty(title))
                Title = $"{LocalizationService.Instance["diff.windowTitle"]} — {title}";

            DiffHost.OnAccept = () => { try { _onAccept?.Invoke(); } catch { } CloseWindow(); };
            DiffHost.OnUndo = () => { try { _onUndo?.Invoke(); } catch { } CloseWindow(); };
        }

        /// <summary>注入预创建的只读 DiffViewerHandle。</summary>
        public void SetViewerHandle(DiffViewerHandle handle)
        {
            DiffHost.SetViewerHandle(handle);
        }

        /// <summary>设置逐块撤销/保留列表。</summary>
        public void SetHunks(
            System.Collections.Generic.IReadOnlyList<DeepSeek_v4_for_VisualStudio.Models.DiffHunkInfo> hunks,
            string? filePath = null,
            Action<int>? onRevertHunk = null,
            Action<int>? onKeepHunk = null)
        {
            DiffHost.OnRevertHunk = onRevertHunk ?? DiffHost.OnRevertHunk;
            DiffHost.OnKeepHunk = onKeepHunk ?? DiffHost.OnKeepHunk;
            DiffHost.SetHunks(hunks, filePath);
        }

        /// <summary>刷新逐块撤销列表。</summary>
        public void RefreshHunks(
            System.Collections.Generic.IReadOnlyList<DeepSeek_v4_for_VisualStudio.Models.DiffHunkInfo> hunks,
            string? filePath = null)
        {
            DiffHost.RefreshHunks(hunks, filePath);
        }

        /// <summary>替换 Diff Viewer（撤销某块后重新显示当前内容）。</summary>
        public void ReplaceViewer(DiffViewerHandle newHandle, InlineDiffSession session)
        {
            DiffHost.SetViewerHandle(newHandle);
            RefreshHunks(
                session.Workspace?.GetHunks(session.Change.FilePath)
                    ?? System.Array.Empty<DeepSeek_v4_for_VisualStudio.Models.DiffHunkInfo>(),
                session.Change.FilePath);
        }

        public void PerformUndo() { try { _onUndo?.Invoke(); } catch { } }
        public void PerformAccept() { try { _onAccept?.Invoke(); } catch { } }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;
        }

        private void CloseWindow()
        {
            _isClosing = true;
            Close();
        }
    }
}
