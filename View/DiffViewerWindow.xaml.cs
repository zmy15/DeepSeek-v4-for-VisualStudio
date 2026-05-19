using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.ComponentModel;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// VS SDK 原生差异对比浮窗。
    /// 使用 <see cref="IWpfDifferenceViewer"/> 展示新旧代码差异，
    /// 支持内联/并排模式切换、差异导航，并提供确认/撤销操作。
    /// </summary>
    public partial class DiffViewerWindow : Window
    {
        #region Fields

        private readonly DiffViewerService _diffService;
        private IWpfDifferenceViewer? _viewer;
        private readonly string _sessionKey;
        private readonly string _oldContent;
        private readonly string _newContent;
        private readonly Action? _onAccept;
        private readonly Action? _onUndo;
        private bool _isClosing;

        #endregion

        #region Constructors

        /// <summary>
        /// 创建差异查看器浮窗。
        /// </summary>
        /// <param name="oldContent">修改前的原始代码</param>
        /// <param name="newContent">AI 生成的新代码</param>
        /// <param name="title">窗口标题附加信息（如文件名）</param>
        /// <param name="onAccept">用户点击「确认变更」时的回调</param>
        /// <param name="onUndo">用户点击「撤销」时的回调</param>
        public DiffViewerWindow(
            string oldContent,
            string newContent,
            string? title = null,
            Action? onAccept = null,
            Action? onUndo = null)
        {
            InitializeComponent();

            _diffService = DiffViewerService.Instance;
            _sessionKey = $"diff_{Guid.NewGuid():N}";
            _oldContent = oldContent ?? string.Empty;
            _newContent = newContent ?? string.Empty;
            _onAccept = onAccept;
            _onUndo = onUndo;

            if (!string.IsNullOrEmpty(title))
            {
                Title = string.Format("{0} — {1}", LocalizationService.Instance["diff.windowTitle"], title);
            }

            // 创建并嵌入差异查看器
            Loaded += OnLoaded;
        }

        #endregion

        #region Event Handlers

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // 通过 MEF 获取服务并创建查看器
                _viewer = _diffService.CreateDiffViewer(
                    _oldContent,
                    _newContent,
                    viewMode: DifferenceViewMode.Inline);

                // 嵌入查看器到宿主容器
                DiffViewerHost.Child = _viewer.VisualElement;

                // 更新统计
                UpdateStats();

                // 订阅差异缓冲区事件
                _viewer.DifferenceBuffer.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewerWindow] 创建查看器失败: {ex.Message}", ex);
                BottomStatusLabel.Text = string.Format(LocalizationService.Instance["diff.createFailed"], ex.Message);
            }
        }

        private void OnSnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
        {
            // 差异计算完成，更新统计
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateStats();
            }));
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                if (_viewer != null && !_viewer.IsClosed)
                {
                    _viewer.DifferenceBuffer.SnapshotDifferenceChanged -= OnSnapshotDifferenceChanged;
                }
                _diffService.CloseSession(_sessionKey);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewerWindow] 清理异常: {ex.Message}");
            }
        }

        #endregion

        #region Button Handlers

        private void InlineModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewer == null || _viewer.IsClosed) return;

            try
            {
                SetViewMode(DifferenceViewMode.Inline);
                InlineModeButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x26, 0x4F, 0x78));
                InlineModeButton.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3F, 0x6A, 0x9A));
                SideBySideModeButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3C, 0x3C, 0x3C));
                SideBySideModeButton.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewerWindow] 切换模式失败: {ex.Message}");
            }
        }

        private void SideBySideModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewer == null || _viewer.IsClosed) return;

            try
            {
                SetViewMode(DifferenceViewMode.SideBySide);
                SideBySideModeButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x26, 0x4F, 0x78));
                SideBySideModeButton.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3F, 0x6A, 0x9A));
                InlineModeButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3C, 0x3C, 0x3C));
                InlineModeButton.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewerWindow] 切换模式失败: {ex.Message}");
            }
        }

        private void PrevDiffButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewer == null || _viewer.IsClosed) return;
            try { _viewer.ScrollToPreviousChange(wrap: true); }
            catch (Exception ex) { Logger.Warn($"[DiffViewerWindow] 导航失败: {ex.Message}"); }
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewer == null || _viewer.IsClosed) return;
            try { _viewer.ScrollToNextChange(wrap: true); }
            catch (Exception ex) { Logger.Warn($"[DiffViewerWindow] 导航失败: {ex.Message}"); }
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _onAccept?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewerWindow] 确认回调异常: {ex.Message}", ex);
            }
            finally
            {
                CloseWindow();
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _onUndo?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewerWindow] 撤销回调异常: {ex.Message}", ex);
            }
            finally
            {
                CloseWindow();
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 供 EditorDiffMarkerService 全局撤销调用：
        /// 触发 onUndo 回调以回退缓冲区内容，但不关闭窗口（由调用方统一关闭）。
        /// </summary>
        public void PerformUndo()
        {
            try
            {
                _onUndo?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewerWindow] PerformUndo 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 供 EditorDiffMarkerService 全局确认调用：
        /// 触发 onAccept 回调，但不关闭窗口（由调用方统一关闭）。
        /// </summary>
        public void PerformAccept()
        {
            try
            {
                _onAccept?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewerWindow] PerformAccept 异常: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods

        private void SetViewMode(DifferenceViewMode mode)
        {
            if (_viewer == null) return;
            
            if (_viewer is IDifferenceViewer3 v3)
                v3.ViewMode = mode;
            else if (_viewer is IDifferenceViewer2 v2)
                v2.ViewMode = mode;
            else
                _viewer.ViewMode = mode;
        }

        private void UpdateStats()
        {
            try
            {
                if (_viewer == null || _viewer.IsClosed) return;

                var diff = _viewer.DifferenceBuffer.CurrentSnapshotDifference;
                if (diff == null) return;

                int addedCount = 0;
                int removedCount = 0;

                foreach (var lineDiff in diff.LineDifferences)
                {
                    switch (lineDiff.DifferenceType)
                    {
                        case DifferenceType.Add:
                            addedCount += lineDiff.Right.Length;
                            break;
                        case DifferenceType.Remove:
                            removedCount += lineDiff.Left.Length;
                            break;
                    }
                }

                StatsLabel.Text = $"+{addedCount} 行新增  -{removedCount} 行删除";
            }
            catch
            {
                StatsLabel.Text = "计算中…";
            }
        }

        private void CloseWindow()
        {
            _isClosing = true;
            try
            {
                if (_viewer != null && !_viewer.IsClosed)
                {
                    _viewer.DifferenceBuffer.SnapshotDifferenceChanged -= OnSnapshotDifferenceChanged;
                }
            }
            catch { /* ignore */ }

            Close();
        }

        #endregion
    }
}
