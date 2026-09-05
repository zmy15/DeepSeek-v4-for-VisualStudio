using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Inline Diff 宿主控件。
    /// 包含工具栏（模式切换 / 导航 / 保留撤销）和 Diff Viewer 宿主区域。
    /// 可嵌入 Window、ToolWindow、DocumentTab 等任意 WPF 容器。
    /// </summary>
    public partial class InlineDiffHostControl : UserControl
    {
        public const string HunkButtonsLayerName = "DeepSeekHunkButtonsLayer";

        #region Fields

        private IWpfDifferenceViewer? _viewer;
        private IWpfTextView? _inlineView;
        private IAdornmentLayer? _hunkButtonsLayer;
        private IWpfDifferenceViewer? _statusBarViewer;

        /// <summary>用户点击「保留」时的回调</summary>
        public Action? OnAccept { get; set; }

        /// <summary>用户点击「撤销」时的回调</summary>
        public Action? OnUndo { get; set; }

        /// <summary>用户点击「撤销某块」时的回调（参数：块索引）</summary>
        public Action<int>? OnRevertHunk { get; set; }

        /// <summary>Callback when user clicks [Keep this hunk] (arg: hunk index)</summary>
        public Action<int>? OnKeepHunk { get; set; }

        /// <summary>当前显示的 hunks</summary>
        private IReadOnlyList<DiffHunkInfo> _hunks = Array.Empty<DiffHunkInfo>();

        private string? _hunkFilePath;

        /// <summary>宿主编辑器视图（由 EditorDiffHost 注入，用于缩放同步）。</summary>
        private IWpfTextView? _hostTextView;

        /// <summary>
        /// 宿主编辑器视图。设置后 diff 各子视图的缩放跟随其 ZoomLevel，
        /// 与编辑器页面的缩放保持一致。
        /// </summary>
        public IWpfTextView? HostTextView
        {
            get => _hostTextView;
            set
            {
                if (ReferenceEquals(_hostTextView, value)) return;
                DetachZoomSource();
                _hostTextView = value;
                SyncZoomFromHostEditor();
            }
        }

        #endregion

        #region Constructor

        public InlineDiffHostControl()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffHost] InitializeComponent 失败，尝试嵌入式 BAML 回退: {ex.GetType().Name}: {ex.Message}");
                var assembly = typeof(InlineDiffHostControl).Assembly;
                if (!TryLoadEmbeddedBaml(assembly, this))
                {
                    var manifestResources = string.Join(", ", assembly.GetManifestResourceNames());
                    Logger.Error(
                        $"[DiffHost] Inline diff XAML load failed. Assembly={assembly.Location}; Resources=[{manifestResources}]",
                        ex);
                    throw new InvalidOperationException(
                        $"Failed to load {nameof(InlineDiffHostControl)} from {assembly.Location}. Resources=[{manifestResources}].",
                        ex);
                }
            }

            Unloaded += (_, __) => DetachZoomSource();
        }

        private static bool TryLoadEmbeddedBaml(Assembly assembly, InlineDiffHostControl control)
        {
            // g.resources 的资源名 = 程序集名 + ".g.resources"；BAML 键 = 小写的
            // "命名空间（去掉程序集名段）/文件名.baml"。两者与 x:Class 强绑定，
            // 程序集或命名空间重命名时必须同步更新。
            string manifestResourceName = $"{assembly.GetName().Name}.g.resources";
            const string BamlResourceName = "view/inlinediffhostcontrol.baml";

            try
            {
                using var resourceStream = assembly.GetManifestResourceStream(manifestResourceName);
                if (resourceStream == null)
                    return false;

                using var reader = new ResourceReader(resourceStream);
                foreach (DictionaryEntry entry in reader)
                {
                    if (!string.Equals(entry.Key as string, BamlResourceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.Value is not Stream bamlStream)
                        return false;

                    var loadBaml = typeof(XamlReader).GetMethod(
                        "LoadBaml",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(Stream), typeof(ParserContext), typeof(object), typeof(bool) },
                        modifiers: null);
                    if (loadBaml == null)
                        return false;

                    var parserContext = new ParserContext
                    {
                        BaseUri = new Uri(
                            $"pack://application:,,,/{assembly.GetName().Name};component/view/{Path.GetFileNameWithoutExtension(BamlResourceName)}.xaml",
                            UriKind.Absolute),
                    };

                    loadBaml.Invoke(null, new object?[] { bamlStream, parserContext, control, false });
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error("[DiffHost] Embedded diff XAML fallback failed.", ex);
                return false;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 直接注入预创建的 <see cref="IWpfDifferenceViewer"/>。
        /// </summary>
        public void SetViewer(IWpfDifferenceViewer viewer)
        {
            DetachViewer();
            _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));

            // 解除 host 自身的旧 child
            DiffViewerHost.Child = null;

            var veil = viewer.VisualElement;

            // 若该元素已有逻辑父级（例如之前被 attach 到另一窗口/host），先从中解除
            // WPF 不允许同一元素同时是多个元素的逻辑子元素，必须 detach 前一处
            TryDetachFromParent(veil);

            DiffViewerHost.Child = veil;

            // 订阅差异变化事件以更新统计
            _viewer.DifferenceBuffer.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;
            _inlineView = _viewer.InlineView;
            if (_inlineView != null)
                _inlineView.LayoutChanged += OnViewerLayoutChanged;

            DisableViewerBottomBars(viewer);
            SyncZoomFromHostEditor();
            QueueLayoutHunkButtons();
            HideViewerStatusBar(viewer);
            QueueHideViewerStatusBar(viewer);
            Dispatcher.BeginInvoke(
                new Action(LayoutHunkButtons),
                System.Windows.Threading.DispatcherPriority.Render);
            UpdateStats();
        }

        /// <summary>
        /// 通过预创建的 <see cref="DiffViewerHandle"/> 注入 Viewer。
        /// </summary>
        public void SetViewerHandle(DiffViewerHandle handle)
        {
            if (handle == null) throw new ArgumentNullException(nameof(handle));
            SetViewer(handle.Viewer);
        }

        public void DetachViewer()
        {
            if (_viewer != null)
            {
                _viewer.DifferenceBuffer.SnapshotDifferenceChanged -= OnSnapshotDifferenceChanged;
            }
            if (_inlineView != null)
                _inlineView.LayoutChanged -= OnViewerLayoutChanged;

            _hunkButtonsLayer?.RemoveAllAdornments();
            DetachStatusBarWatcher();
            DiffViewerHost.Child = null;
            _hunkButtonsLayer = null;
            _inlineView = null;
            _viewer = null;
        }

        private static void DisableViewerBottomBars(IWpfDifferenceViewer viewer)
        {
            if (viewer.InlineView != null)
                DisableBottomBars(viewer.InlineView);
            if (viewer.LeftView != null)
                DisableBottomBars(viewer.LeftView);
            if (viewer.RightView != null)
                DisableBottomBars(viewer.RightView);
        }

        private static void DisableBottomBars(IWpfTextView view)
        {
            try
            {
                view.Options.SetOptionValue(DefaultTextViewHostOptions.HorizontalScrollBarId, false);
                view.Options.SetOptionValue(DefaultTextViewHostOptions.RowColMarginOptionId, false);
                view.Options.SetOptionValue(DefaultTextViewHostOptions.ZoomControlId, false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffHost] 关闭 viewer 底部栏失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置并显示逐块撤销的变更块列表。
        /// </summary>
        /// <param name="hunks">变更块列表</param>
        /// <param name="filePath">文件路径（用于显示文件名）</param>
        public void SetHunks(IReadOnlyList<DiffHunkInfo> hunks, string? filePath = null)
        {
            _hunks = hunks ?? Array.Empty<DiffHunkInfo>();
            _hunkFilePath = filePath;
            LayoutHunkButtons();
        }



        /// <summary>
        /// 刷新块列表（撤销某块后更新状态）。
        /// </summary>
        public void RefreshHunks(IReadOnlyList<DiffHunkInfo> hunks, string? filePath = null)
        {
            SetHunks(hunks, filePath);
        }

        #endregion

        #region Hunk Buttons

        private void OnViewerLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
        {
            QueueLayoutHunkButtons();
        }

        private void LayoutHunkButtons()
        {
            if (_viewer == null || _viewer.IsClosed) return;

            var inlineView = _viewer.InlineView;
            if (inlineView == null) return;
            _inlineView = inlineView;
            _hunkButtonsLayer = inlineView.GetAdornmentLayer(HunkButtonsLayerName);
            _hunkButtonsLayer.RemoveAllAdornments();

            double viewportWidth = Math.Max(0, inlineView.ViewportWidth);
            if (viewportWidth <= 0) return;

            for (int i = 0; i < _hunks.Count; i++)
            {
                var hunk = _hunks[i];
                if (hunk.IsReverted || hunk.IsAccepted) continue;

                var textLine = FindInlineLineForHunk(hunk, inlineView);
                if (textLine == null) continue;

                var group = MakeHunkButtonGroup(i);
                group.HorizontalAlignment = HorizontalAlignment.Right;
                group.Margin = new Thickness(0, 0, 8, 0);

                group.Measure(new Size(viewportWidth, double.PositiveInfinity));
                double top = textLine.TextTop +
                    Math.Max(0, (textLine.TextHeight - group.DesiredSize.Height) / 2);

                var container = new Grid
                {
                    Width = viewportWidth,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                container.Children.Add(group);

                Canvas.SetLeft(container, 0);
                Canvas.SetTop(container, top);

                _hunkButtonsLayer.AddAdornment(
                    AdornmentPositioningBehavior.OwnerControlled,
                    new SnapshotSpan(textLine.Start, 0),
                    null,
                    container,
                    null);
            }
        }

        private ITextViewLine? FindInlineLineForHunk(
            DiffHunkInfo hunk,
            IWpfTextView inlineView)
        {
            var difference = _viewer?.DifferenceBuffer.CurrentSnapshotDifference;
            if (difference == null) return null;

            bool useRight = hunk.NewLineCount > 0 && hunk.NewStartLine >= 0;
            var sourceSnapshot = useRight
                ? difference.RightBufferSnapshot
                : difference.LeftBufferSnapshot;
            int lineNumber = useRight ? hunk.NewStartLine : hunk.OldStartLine;
            if (lineNumber < 0 || lineNumber >= sourceSnapshot.LineCount) return null;

            var sourcePoint = sourceSnapshot.GetLineFromLineNumber(lineNumber).Start;
            SnapshotPoint inlinePoint;
            try
            {
                inlinePoint = difference.MapToInlineSnapshot(
                    sourcePoint,
                    PositionAffinity.Successor);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (inlinePoint.Snapshot.Version != inlineView.TextSnapshot.Version)
            {
                try
                {
                    inlinePoint = inlinePoint.TranslateTo(
                        inlineView.TextSnapshot,
                        PointTrackingMode.Positive);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            return inlineView.TextViewLines?
                .GetTextViewLineContainingBufferPosition(inlinePoint);
        }

        private void QueueLayoutHunkButtons()
        {
            var inlineView = _viewer?.InlineView;
            if (inlineView == null || inlineView.IsClosed) return;

            inlineView.VisualElement.Dispatcher.BeginInvoke(
                new Action(LayoutHunkButtons),
                inlineView.VisualElement.Dispatcher == Dispatcher
                    ? System.Windows.Threading.DispatcherPriority.Render
                    : System.Windows.Threading.DispatcherPriority.Send);
            Dispatcher.BeginInvoke(
                new Action(LayoutHunkButtons),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private StackPanel MakeHunkButtonGroup(int index)
        {
            var keepBtn = new Button
            {
                Content = LocalizationService.Instance["diff.keepHunk"],
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x33)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x8F, 0x47)),
                BorderThickness = new Thickness(1),
                MinWidth = 62, MaxWidth = 62, MinHeight = 20,
                FontSize = 9,
                Padding = new Thickness(2, 1, 2, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 4, 0),
            };
            keepBtn.Click += (s, e) => OnKeepHunk?.Invoke(index);

            var revertBtn = new Button
            {
                Content = LocalizationService.Instance["diff.revertHunk"],
                Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x40, 0x40)),
                BorderThickness = new Thickness(1),
                MinWidth = 62, MaxWidth = 62, MinHeight = 20,
                FontSize = 9,
                Padding = new Thickness(2, 1, 2, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            revertBtn.Click += (s, e) => OnRevertHunk?.Invoke(index);

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(keepBtn);
            sp.Children.Add(revertBtn);
            return sp;
        }

        #endregion

        #region Button Handlers

        private void InlineModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(DifferenceViewMode.Inline);
            InlineModeButton.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
            InlineModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x6A, 0x9A));
            SideBySideModeButton.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            SideBySideModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        private void SideBySideModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(DifferenceViewMode.SideBySide);
            SideBySideModeButton.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
            SideBySideModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x6A, 0x9A));
            InlineModeButton.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            InlineModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        private void PrevDiffButton_Click(object sender, RoutedEventArgs e)
        {
            try { _viewer?.ScrollToPreviousChange(wrap: true); }
            catch (Exception ex) { Logger.Warn($"[DiffHost] 导航失败: {ex.Message}"); }
        }

        private void NextDiffButton_Click(object sender, RoutedEventArgs e)
        {
            try { _viewer?.ScrollToNextChange(wrap: true); }
            catch (Exception ex) { Logger.Warn($"[DiffHost] 导航失败: {ex.Message}"); }
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            AcceptButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            try { OnAccept?.Invoke(); }
            catch (Exception ex) { Logger.Error($"[DiffHost] 保留回调异常: {ex.Message}", ex); }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            AcceptButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            try { OnUndo?.Invoke(); }
            catch (Exception ex) { Logger.Error($"[DiffHost] 撤销回调异常: {ex.Message}", ex); }
        }

        #endregion

        #region Private Methods

        private void SetViewMode(DifferenceViewMode mode)
        {
            if (_viewer == null) return;

            ThreadHelper.ThrowIfNotOnUIThread();

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
                StatsLabel.Text = LocalizationService.Instance["status.diffCalculating"];
            }
        }

        private void OnSnapshotDifferenceChanged(object? sender, SnapshotDifferenceChangeEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateStats));
        }

        /// <summary>
        /// 将 diff 各子视图（inline / 左 / 右）的 ZoomLevel 同步为宿主编辑器的缩放，
        /// 并订阅宿主选项变化以便后续缩放时实时跟随。
        /// </summary>
        private void SyncZoomFromHostEditor()
        {
            var source = _hostTextView;
            if (source == null || source.IsClosed) return;

            source.Options.OptionChanged -= OnHostZoomChanged;
            source.Options.OptionChanged += OnHostZoomChanged;

            SyncViewOptionsFromHost(source);
            ApplyZoom(source.ZoomLevel);
        }

        private void SyncViewOptionsFromHost(IWpfTextView source)
        {
            if (_viewer == null || _viewer.IsClosed) return;

            foreach (var view in new[] { _viewer.InlineView, _viewer.LeftView, _viewer.RightView })
            {
                if (view == null) continue;

                try
                {
                    if (!ReferenceEquals(view.Options.Parent, source.Options))
                        view.Options.Parent = source.Options;

                    view.Options.SetOptionValue(
                        DefaultWpfViewOptions.AppearanceCategory,
                        source.Options.GetOptionValue<string>(
                            DefaultWpfViewOptions.AppearanceCategory));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[DiffHost] 同步视图外观失败: {ex.Message}");
                }
            }
        }

        private void OnHostZoomChanged(object? sender, EditorOptionChangedEventArgs e)
        {
            if (    e.OptionId == DefaultWpfViewOptions.ZoomLevelId.Name)
                ApplyZoom(_hostTextView?.ZoomLevel ?? 100d);
        }

        private void ApplyZoom(double zoomLevel)
        {
            if (_viewer == null || _viewer.IsClosed) return;
            if (zoomLevel <= 0) return;

            if (_viewer.InlineView is IWpfTextView inline)
                TrySetZoom(inline, zoomLevel);
            if (_viewer.LeftView is IWpfTextView left)
                TrySetZoom(left, zoomLevel);
            if (_viewer.RightView is IWpfTextView right)
                TrySetZoom(right, zoomLevel);
        }

        private static void TrySetZoom(IWpfTextView view, double zoomLevel)
        {
            try { view.ZoomLevel = (float)zoomLevel; }
            catch (Exception ex) { Logger.Warn($"[DiffHost] 同步缩放失败: {ex.Message}"); }
        }

        private void DetachZoomSource()
        {
            if (_hostTextView == null) return;
            _hostTextView.Options.OptionChanged -= OnHostZoomChanged;
            _hostTextView = null;
        }

        /// <summary>
        /// 若元素已有逻辑/可视化父级，则从父级中解除。WPF 中同一元素不能是多个宿主的孩子。
        /// </summary>
        private static void TryDetachFromParent(System.Windows.UIElement element)
        {
            try
            {
                if (element == null) return;

                var logicalParent = System.Windows.LogicalTreeHelper.GetParent(element);
                if (logicalParent is ContentControl cc && ReferenceEquals(cc.Content, element))
                {
                    cc.Content = null;
                    return;
                }
                if (logicalParent is Panel panel)
                {
                    panel.Children.Remove(element);
                    return;
                }
                if (logicalParent is Decorator decorator && ReferenceEquals(decorator.Child, element))
                {
                    decorator.Child = null;
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffHost] 解除元素父级失败: {ex.Message}");
            }
        }

        private void QueueHideViewerStatusBar(IWpfDifferenceViewer viewer)
        {
            var visualElement = viewer.VisualElement;
            void HideIfCurrent()
            {
                if (ReferenceEquals(_viewer, viewer) && !viewer.IsClosed)
                    HideViewerStatusBar(viewer);
            }

            visualElement.Dispatcher.BeginInvoke(
                new Action(HideIfCurrent),
                System.Windows.Threading.DispatcherPriority.Loaded);
            visualElement.Dispatcher.BeginInvoke(
                new Action(HideIfCurrent),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void HideViewerStatusBar(IWpfDifferenceViewer viewer)
        {
            DetachStatusBarWatcher();
            _statusBarViewer = viewer;

            if (TryCollapseStatusBarElements(viewer.VisualElement))
                return;

            if (viewer.VisualElement is FrameworkElement frameworkElement)
            {
                frameworkElement.LayoutUpdated += OnViewerStatusBarLayoutUpdated;
            }
        }

        private void OnViewerStatusBarLayoutUpdated(object? sender, EventArgs e)
        {
            if (_statusBarViewer == null || sender is not FrameworkElement element) return;

            element.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!ReferenceEquals(_statusBarViewer?.VisualElement, element)) return;
                    if (TryCollapseStatusBarElements(element))
                        DetachStatusBarWatcher();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static bool TryCollapseStatusBarElements(DependencyObject root)
        {
            bool collapsedStatusBar = CollapseElementsNamedStatusBar(root);
            if (collapsedStatusBar) return true;

            var indicator = FindStatusBarIndicator(root);
            if (indicator == null) return false;

            if (CollapseStatusBarAncestor(root, indicator))
                return true;

            indicator.Visibility = Visibility.Collapsed;
            return true;
        }

        private static bool CollapseElementsNamedStatusBar(DependencyObject element)
        {
            if (element == null) return false;

            bool collapsed = false;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is UIElement uiElement &&
                    child.GetType().Name.IndexOf("StatusBar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    uiElement.Visibility = Visibility.Collapsed;
                    collapsed = true;
                }

                collapsed |= CollapseElementsNamedStatusBar(child);
            }

            return collapsed;
        }

        private static UIElement? FindStatusBarIndicator(DependencyObject element)
        {
            if (element == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is TextBlock textBlock && IsStatusBarText(textBlock.Text))
                    return textBlock;

                if (child is ContentControl contentControl &&
                    contentControl.Content is string text &&
                    IsStatusBarText(text))
                {
                    return contentControl;
                }

                var descendant = FindStatusBarIndicator(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        private static bool IsStatusBarText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            return (text.Contains("行", StringComparison.Ordinal) &&
                    text.Contains("字符", StringComparison.Ordinal)) ||
                   (text.Contains("Ln", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains("Col", StringComparison.OrdinalIgnoreCase));
        }

        private static bool CollapseStatusBarAncestor(DependencyObject root, DependencyObject indicator)
        {
            UIElement? candidate = null;
            DependencyObject? current = indicator;

            while (current != null && !ReferenceEquals(current, root))
            {
                if (current is FrameworkElement frameworkElement &&
                    frameworkElement.ActualHeight > 0 &&
                    frameworkElement.ActualHeight <= 40)
                {
                    candidate = frameworkElement;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            if (candidate == null) return false;

            candidate.Visibility = Visibility.Collapsed;
            return true;
        }

        private void DetachStatusBarWatcher()
        {
            if (_statusBarViewer == null) return;

            _statusBarViewer.VisualElement.LayoutUpdated -= OnViewerStatusBarLayoutUpdated;
            _statusBarViewer = null;
        }

        #endregion
    }
}
