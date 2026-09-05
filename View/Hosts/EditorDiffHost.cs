using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// 编辑器内嵌 Diff 宿主。将 InlineDiffHostControl（含 VS 原生 IWpfDifferenceViewer）
    /// 通过 WPF Adorner 直接覆盖在编辑器 VisualElement 上，替代浮动窗口，实现 Copilot 风格的
    /// 内联 diff 预览：同时显示原始代码和修改后代码，自动红绿着色，支持逐块撤销/保留。
    ///
    /// Adorner 与被装饰元素（编辑器）天然同位置同尺寸，避免手动计算 viewport 坐标导致的错位。
    /// </summary>
    public sealed class EditorDiffHost : IDiffHost
    {
        /// <summary>
        /// 兜底 adornment 层名（仅当 AdornerLayer 不可用时使用），
        /// 在 DiffPreviewAdornmentFactory 以 MEF AdornmentLayerDefinition 注册。
        /// </summary>
        public const string OverlayLayerName = "DeepSeekEditorDiffOverlay";

        private readonly IWpfTextView _textView;
        private InlineDiffHostControl? _hostControl;
        private EditorOverlayAdorner? _adorner;
        private IAdornmentLayer? _fallbackLayer;
        private Border? _fallbackContainer;
        private bool _isShown;
        private bool _loadedHandlerAttached;
        private bool _overlayRetryHandlerAttached;

        public EditorDiffHost(IWpfTextView textView)
        {
            _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        }

        public void Show(InlineDiffSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isShown) Close();

            _hostControl = new InlineDiffHostControl();
            _hostControl.SetViewerHandle(session.ViewerHandle);
            // diff 各子视图的缩放跟随宿主编辑器页面的 ZoomLevel
            _hostControl.HostTextView = _textView;

            _hostControl.OnAccept = () => FinishSession(session);

            _hostControl.OnUndo = () =>
            {
                session.Dismiss();
                Close();
            };

            WireHunkHandlers(session);

            _isShown = true;
            AttachHostOverlay();
            Logger.Info($"[EditorDiffHost] Inline diff shown: {session.SessionId.Substring(0, 8)}");
        }

        public void Activate() { }

        /// <summary>
        /// 绑定 hunk 撤销/保留回调并装载当前待处理块。
        /// 回调内通过 session.Workspace 动态解析，Workspace 刷新后仍然有效。
        /// </summary>
        private void WireHunkHandlers(InlineDiffSession session)
        {
            if (_hostControl == null || session.Workspace == null) return;

            var hunks = session.Workspace.GetHunks(session.Change.FilePath);
            _hostControl.SetHunks(hunks, session.Change.FilePath);

            _hostControl.OnRevertHunk = hunkIndex =>
            {
                if (!session.Workspace!.RestoreSingleHunk(session.Change.FilePath, hunkIndex))
                    return;

                Logger.Info($"[EditorDiffHost] Reverted hunk [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");

                // ── 所有块均已处理（保留/撤销）→ 确认并自动关闭 ──
                if (!session.Workspace.HasPendingHunks(session.Change.FilePath))
                {
                    FinishSession(session);
                    return;
                }

                RebuildViewerForPendingHunks(session);
            };

            _hostControl.OnKeepHunk = hunkIndex =>
            {
                if (!session.Workspace!.AcceptSingleHunk(session.Change.FilePath, hunkIndex))
                    return;

                Logger.Info($"[EditorDiffHost] Kept hunk [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");

                // ── 所有块均已处理（保留/撤销）→ 确认并自动关闭 ──
                if (!session.Workspace.HasPendingHunks(session.Change.FilePath))
                {
                    FinishSession(session);
                    return;
                }

                RebuildViewerForPendingHunks(session);
            };
        }

        /// <summary>
        /// 原地刷新会话（AI 在预览期间再次编辑同一文件时调用）：
        /// 重建只读 Diff 视图并刷新 hunk 按钮，预览立即反映最新编辑。
        /// </summary>
        public void RefreshSession(InlineDiffSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_hostControl == null) return;

            try
            {
                WireHunkHandlers(session);

                var oldHandle = session.ViewerHandle;
                session.ReplaceViewerHandle(
                    DiffViewerService.Instance.CreateReadOnlyPreview(
                        session.Change.BaselineText,
                        session.Change.ProposedText,
                        session.Change.ContentTypeName,
                        DifferenceViewMode.Inline));
                _hostControl.SetViewerHandle(session.ViewerHandle);
                try { oldHandle.Dispose(); } catch { /* 旧视图释放失败不影响刷新 */ }
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiffHost] 刷新 Diff 视图失败: {ex.Message}", ex);
            }
        }

        private void AttachHostOverlay()
        {
            var visualElement = _textView.VisualElement;
            var adornerLayer = AdornerLayer.GetAdornerLayer(visualElement);
            if (adornerLayer != null)
            {
                _adorner = new EditorOverlayAdorner(visualElement, _hostControl!);
                adornerLayer.Add(_adorner);
                DetachOverlayRetryHandlers(visualElement);
                return;
            }

            visualElement.LayoutUpdated += OnVisualElementLayoutUpdated;
            _overlayRetryHandlerAttached = true;

            if (!visualElement.IsLoaded)
            {
                visualElement.Loaded += OnVisualElementLoaded;
                _loadedHandlerAttached = true;
                return;
            }

            ShowFallbackOverlay();
        }

        private void OnVisualElementLoaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedHandlerAttached) return;

            _textView.VisualElement.Loaded -= OnVisualElementLoaded;
            _loadedHandlerAttached = false;
            if (!_isShown) return;

            ClearHostOverlay();
            AttachHostOverlay();
        }

        private void OnVisualElementLayoutUpdated(object? sender, EventArgs e)
        {
            if (!_overlayRetryHandlerAttached) return;

            var visualElement = _textView.VisualElement;
            visualElement.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!_isShown || !_overlayRetryHandlerAttached) return;
                    if (AdornerLayer.GetAdornerLayer(visualElement) == null) return;

                    ClearHostOverlay();
                    AttachHostOverlay();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ClearHostOverlay()
        {
            if (_adorner != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(_textView.VisualElement);
                _adorner.Detach();
                adornerLayer?.Remove(_adorner);
                _adorner.DetachChild();
                _adorner = null;
            }

            if (_fallbackLayer != null)
            {
                if (_textView.VisualElement is FrameworkElement frameworkElement)
                    frameworkElement.SizeChanged -= OnFallbackSizeChanged;
                _textView.LayoutChanged -= OnHostLayoutChanged;
                _fallbackContainer.Child = null;
                _fallbackLayer.RemoveAllAdornments();
                _fallbackLayer = null;
                _fallbackContainer = null;
            }
        }

        private void ShowFallbackOverlay()
        {
            _fallbackLayer = _textView.GetAdornmentLayer(OverlayLayerName);
            _fallbackContainer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Child = _hostControl,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
            };
            _hostControl!.HorizontalAlignment = HorizontalAlignment.Stretch;
            _hostControl.VerticalAlignment = VerticalAlignment.Stretch;

            if (_textView.VisualElement is FrameworkElement frameworkElement)
            {
                frameworkElement.SizeChanged += OnFallbackSizeChanged;
                _textView.LayoutChanged += OnHostLayoutChanged;
                UpdateFallbackOverlayBounds(frameworkElement);
            }
            else
            {
                _fallbackContainer.Width = _textView.ViewportWidth;
                _fallbackContainer.Height = _textView.ViewportHeight;
                Canvas.SetLeft(_fallbackContainer, 0);
                Canvas.SetTop(_fallbackContainer, 0);
            }

            _fallbackLayer.AddAdornment(
                AdornmentPositioningBehavior.ViewportRelative,
                null, null, _fallbackContainer, null);
        }

        private void OnFallbackSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement frameworkElement)
                UpdateFallbackOverlayBounds(frameworkElement);
        }

        private void OnHostLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
        {
            if (_textView.VisualElement is FrameworkElement frameworkElement)
                UpdateFallbackOverlayBounds(frameworkElement);
        }

        private void UpdateFallbackOverlayBounds(FrameworkElement visualElement)
        {
            if (_fallbackContainer == null || visualElement.ActualWidth <= 0 || visualElement.ActualHeight <= 0)
                return;

            double verticalScrollBarWidth = 0;
            double horizontalScrollBarHeight = 0;
            CollectScrollBarSizes(
                visualElement,
                ref verticalScrollBarWidth,
                ref horizontalScrollBarHeight);

            _fallbackContainer.Width = visualElement.ActualWidth;
            _fallbackContainer.Height = visualElement.ActualHeight;

            // ViewportRelative 的原点在视口左上；补偿行号区与滚动条，才能覆盖整个编辑器视觉元素。
            double leftReserve = Math.Max(
                0,
                visualElement.ActualWidth - _textView.ViewportWidth - verticalScrollBarWidth);
            double topReserve = Math.Max(
                0,
                visualElement.ActualHeight - _textView.ViewportHeight - horizontalScrollBarHeight);
            Canvas.SetLeft(_fallbackContainer, -leftReserve);
            Canvas.SetTop(_fallbackContainer, -topReserve);
        }

        private static void CollectScrollBarSizes(
            DependencyObject element,
            ref double verticalScrollBarWidth,
            ref double horizontalScrollBarHeight)
        {
            if (element == null) return;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is ScrollBar scrollBar && scrollBar.Visibility == Visibility.Visible)
                {
                    if (scrollBar.Orientation == Orientation.Vertical)
                        verticalScrollBarWidth = Math.Max(verticalScrollBarWidth, scrollBar.ActualWidth);
                    else
                        horizontalScrollBarHeight = Math.Max(horizontalScrollBarHeight, scrollBar.ActualHeight);
                }

                CollectScrollBarSizes(
                    child,
                    ref verticalScrollBarWidth,
                    ref horizontalScrollBarHeight);
            }
        }

        /// <summary>
        /// 结束会话：写穿模式确认磁盘内容（清除撤销追踪），
        /// 普通模式走提交流程；随后关闭编辑器覆盖层。
        /// </summary>
        private void FinishSession(InlineDiffSession session)
        {
            if (session.Workspace != null)
                session.ConfirmWriteThrough();
            else
                _ = session.CommitAsync(System.Threading.CancellationToken.None);
            Close();
        }

        /// <summary>
        /// 逐块保留/撤销后重建 Diff 视图。
        /// 左侧使用「仅含待处理块」的显示基线（已保留块并入新基线），
        /// 因此已保留/已撤销的块不再红绿高亮，视图中只剩待处理块及其按钮。
        /// </summary>
        private void RebuildViewerForPendingHunks(InlineDiffSession session)
        {
            try
            {
                var workspace = session.Workspace!;
                var updated = workspace.GetHunks(session.Change.FilePath);
                _hostControl!.RefreshHunks(updated, session.Change.FilePath);

                string currentContent = workspace.ReadFile(session.Change.FilePath);
                string displayBaseline = workspace.BuildPendingOnlyBaseline(session.Change.FilePath);

                var oldHandle = session.ViewerHandle;
                session.ReplaceViewerHandle(
                    DiffViewerService.Instance.CreateReadOnlyPreview(
                        displayBaseline, currentContent, session.Change.ContentTypeName));
                _hostControl.SetViewerHandle(session.ViewerHandle);
                try { oldHandle.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiffHost] 重建 Diff 视图失败: {ex.Message}", ex);
            }
        }

        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_isShown) return;

            if (_adorner != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(_textView.VisualElement);
                _adorner.Detach();
                adornerLayer?.Remove(_adorner);
                _adorner.DetachChild();
                _adorner = null;
            }

            if (_loadedHandlerAttached)
            {
                _textView.VisualElement.Loaded -= OnVisualElementLoaded;
                _loadedHandlerAttached = false;
            }

            DetachOverlayRetryHandlers(_textView.VisualElement);

            if (_fallbackLayer != null)
            {
                if (_textView.VisualElement is FrameworkElement frameworkElement)
                    frameworkElement.SizeChanged -= OnFallbackSizeChanged;
                _textView.LayoutChanged -= OnHostLayoutChanged;
                _fallbackContainer.Child = null;
                _fallbackLayer.RemoveAllAdornments();
                _fallbackLayer = null;
                _fallbackContainer = null;
            }

            _hostControl?.DetachViewer();
            _hostControl = null;
            _isShown = false;
            Logger.Info("[EditorDiffHost] Inline diff closed");
        }

        private void DetachOverlayRetryHandlers(System.Windows.UIElement visualElement)
        {
            visualElement.LayoutUpdated -= OnVisualElementLayoutUpdated;
            _overlayRetryHandlerAttached = false;
        }
    }

    /// <summary>
    /// 覆盖整个被装饰元素（编辑器）的 Adorner，子元素填满编辑器区域。
    /// </summary>
    internal sealed class EditorOverlayAdorner : Adorner
    {
        private readonly UIElement _child;
        private FrameworkElement? _sizeSource;
        private bool _childAttached;

        public EditorOverlayAdorner(UIElement adornedElement, UIElement child)
            : base(adornedElement)
        {
            _child = child ?? throw new ArgumentNullException(nameof(child));
            AddVisualChild(child);
            AddLogicalChild(child);
            _childAttached = true;

            if (AdornedElement is FrameworkElement frameworkElement)
            {
                _sizeSource = frameworkElement;
                frameworkElement.SizeChanged += OnAdornedSizeChanged;
            }
        }

        public void Detach()
        {
            if (_sizeSource == null) return;

            _sizeSource.SizeChanged -= OnAdornedSizeChanged;
            _sizeSource = null;
        }

        public void DetachChild()
        {
            if (!_childAttached) return;

            RemoveVisualChild(_child);
            RemoveLogicalChild(_child);
            _childAttached = false;
        }

        protected override int VisualChildrenCount => _childAttached ? 1 : 0;

        protected override Visual GetVisualChild(int index) => _child;

        // 始终以被装饰元素（编辑器）的实际渲染尺寸为准，忽略外部约束，
        // 避免覆盖层被测量成整个窗口尺寸而溢出编辑器边界。
        protected override Size MeasureOverride(Size constraint)
        {
            var size = AdornedElement?.RenderSize ?? _child.DesiredSize;
            _child.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = AdornedElement?.RenderSize ?? finalSize;
            _child.Arrange(new Rect(0, 0, size.Width, size.Height));
            return size;
        }

        private void OnAdornedSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }

        protected override void OnRender(DrawingContext drawingContext) { }
    }
}
