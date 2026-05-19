using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑器 Diff 预览管理服务（方案三：VS SDK 原生差异查看器）。
    /// 使用 <see cref="DiffViewerService"/> 创建 VS 内置的差异对比视图，
    /// 替代原有的自定义 LCS 算法 + 文本标记注入方案。
    ///
    /// 工作流：
    /// 1. 调用方将新代码写入编辑器缓冲区
    /// 2. 调用 <see cref="BeginDiffPreview"/> 弹出差异查看浮窗
    /// 3. 用户点击「确认变更」→ 保留新代码，关闭浮窗
    /// 4. 用户点击「撤销」→ 回退缓冲区到原始代码，关闭浮窗
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

        // 每个 buffer 对应的活跃差异预览窗口
        private readonly Dictionary<ITextBuffer, DiffViewerWindow> _activeWindows = new();
        private readonly object _windowsLock = new();

        // 待处理 diff 存储（按文件路径，用于未打开的文件）
        private readonly Dictionary<string, PendingFileDiff> _pendingDiffs = new();
        private readonly object _pendingLock = new();

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
        /// 获取活跃差异预览的 buffer 数量。
        /// </summary>
        public int GetActiveCount()
        {
            lock (_windowsLock)
            {
                return _activeWindows.Count;
            }
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
            }

            try
            {
                // ── 先触发撤销回调（回退缓冲区内容）──
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

        #endregion

        #region Public API — Pending Diffs

        /// <summary>
        /// 为未在编辑器中打开的文件注册待处理 diff。
        /// 当用户稍后打开该文件时，会通过 <see cref="TryActivatePendingDiff"/> 自动激活预览。
        /// </summary>
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
        /// 全局确认：关闭所有活跃差异预览窗口，丢弃所有待处理 diff。
        /// </summary>
        public void AcceptAllChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

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

            Logger.Info($"[EditorDiff] 已全局确认: {windows.Count} 个活跃会话 + {pendingCount} 个待处理 diff");
        }

        /// <summary>
        /// 全局撤销：先回退所有活跃会话的缓冲区内容到原始代码，
        /// 再关闭所有差异预览窗口，最后丢弃所有待处理 diff。
        /// </summary>
        public void UndoAllChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            List<DiffViewerWindow> windows;
            lock (_windowsLock)
            {
                windows = _activeWindows.Values.ToList();
                _activeWindows.Clear();
            }

            // ── 先触发每个窗口的撤销回调（回退缓冲区内容）──
            foreach (var window in windows)
            {
                try { window.PerformUndo(); }
                catch { /* ignore */ }
            }

            // ── 再关闭所有窗口 ──
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

            Logger.Info($"[EditorDiff] 已全局撤销: {windows.Count} 个活跃会话已回退 + {pendingCount} 个待处理 diff 已丢弃");
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
    }

    #endregion
}
