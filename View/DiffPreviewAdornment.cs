using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Diff 预览装饰器。在编辑器视口顶部显示「确认变更」和「撤销」按钮，
        /// 并在每个被修改块的首行右侧浮动「撤销此块」按钮。
        /// 块级操作通过 <see cref="EditorDiffMarkerService"/> 访问 Workspace hunks。
        /// </summary>
        internal sealed class DiffPreviewAdornment
        {
            private readonly IWpfTextView _view;
            private readonly IAdornmentLayer _adornmentLayer;
            private UIElement? _toolbar;
            private UIElement? _hunkButtonsHost;
            private static readonly double HunkButtonWidth = 62;

            /// <summary>纯新增块的高亮色（半透明绿）。</summary>
            private static readonly Color InsertHighlightColor = Color.FromArgb(0x30, 0x2E, 0x9E, 0x58);

            /// <summary>修改块的高亮色（半透明红）。</summary>
            private static readonly Color ModifyHighlightColor = Color.FromArgb(0x30, 0xC7, 0x4E, 0x3A);

            public const string AdornmentLayerName = "DeepSeekDiffPreviewAdornment";

            #region Constructors

            public DiffPreviewAdornment(IWpfTextView view)
            {
                _view = view ?? throw new ArgumentNullException(nameof(view));
                _adornmentLayer = view.GetAdornmentLayer(AdornmentLayerName);

                _view.LayoutChanged += OnLayoutChanged;

                // 订阅 diff 会话变更（新建/确认/撤销时刷新编辑器内嵌按钮）
                EditorDiffMarkerService.Instance.PendingDiffCountChanged += OnPendingDiffChanged;

                RefreshAdornment();
            }

            private void OnPendingDiffChanged()
            {
                if (_view is null) return;
                if (!_view.IsClosed)
                {
                    _view.VisualElement.Dispatcher.BeginInvoke(
                        new Action(RefreshAdornment),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            #endregion

            #region Event Handlers

            private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
            {
                RefreshAdornment();
            }

            #endregion

            #region Adornment Management

            private void RefreshAdornment()
            {
                // When EditorDiffHost is handling the full inline diff display,
                // skip entirely to avoid removing its adornments.
                var session = EditorDiffMarkerService.Instance.GetActiveSessionForBuffer(_view.TextBuffer);
                if (session != null)
                    return;

                _adornmentLayer.RemoveAllAdornments();
                _toolbar = null;
                _hunkButtonsHost = null;
            }

            /// <summary>
            /// 为每个未撤销的块绘制行区域红绿高亮：
            /// 纯新增行 → 绿色，修改行 → 红色。纯删除在现文件中无可见行，跳过。
            /// 半透明矩形按行绘制，不拦截鼠标事件；滚动时通过 LayoutChanged 重建。
            /// </summary>
            private void AddHunkHighlights(
                System.Collections.Generic.IReadOnlyList<Models.DiffHunkInfo> hunks)
            {
                var snapshot = _view.TextSnapshot;
                var textViewLines = _view.TextViewLines;
                if (textViewLines == null) return;

                double width = Math.Max(0, _view.ViewportWidth) + 4;

                foreach (var hunk in hunks)
                {
                    if (hunk.IsReverted || hunk.IsAccepted) continue;
                    if (hunk.NewStartLine < 0 || hunk.NewLineCount <= 0) continue;

                    var brush = new SolidColorBrush(
                        hunk.IsPureInsert ? InsertHighlightColor : ModifyHighlightColor);

                    int start = hunk.NewStartLine;
                    int end = Math.Min(start + hunk.NewLineCount, snapshot.LineCount);

                    for (int i = start; i < end; i++)
                    {
                        var line = snapshot.GetLineFromLineNumber(i);
                        var textLine = textViewLines.GetTextViewLineContainingBufferPosition(line.Start);
                        if (textLine == null) continue; // 当前视口外：滚动触发 LayoutChanged 后重建

                        var rect = new Rectangle
                        {
                            Fill = brush,
                            IsHitTestVisible = false,
                            Width = width,
                            Height = textLine.Height,
                        };
                        Canvas.SetLeft(rect, -2);
                        Canvas.SetTop(rect, textLine.TextTop);

                        _adornmentLayer.AddAdornment(
                            AdornmentPositioningBehavior.OwnerControlled,
                            new SnapshotSpan(snapshot, line.Start, 0),
                            null, rect, null);
                    }
                }
            }

            /// <summary>
            /// 在每个未处理块的首行右侧绘制「保留此块 + 撤销此块」按钮组。
            /// 使用 OwnerControlled 定位：按钮水平贴视口右侧，垂直对齐块首行。
            /// </summary>
            private void AddHunkRevertButtons(
                InlineDiffSession session,
                System.Collections.Generic.IReadOnlyList<Models.DiffHunkInfo> hunks)
            {
                double viewportRight = _view.ViewportRight;
                double groupWidth = HunkButtonWidth * 2 + 4;
                double left = viewportRight - groupWidth - 10;

                for (int i = 0; i < hunks.Count; i++)
                {
                    var hunk = hunks[i];
                    if (hunk.IsReverted || hunk.IsAccepted) continue;

                    // 定位块首行：NewStartLine 是当前文件中的起始行（0-based）
                    if (hunk.NewStartLine < 0) continue;

                    int lineNumber = hunk.NewStartLine;
                    var snapshot = _view.TextSnapshot;
                    if (lineNumber >= snapshot.LineCount) continue;

                    var line = snapshot.GetLineFromLineNumber(lineNumber);
                    var textLine = _view.TextViewLines?.GetTextViewLineContainingBufferPosition(line.Start);
                    if (textLine == null) continue;

                    // ── 保留 + 撤销 按钮组 ──
                    var keepBtn = CreateKeepHunkButton(i);
                    keepBtn.Margin = new Thickness(0, 0, 4, 0);
                    var revertBtn = CreateHunkButton(i);

                    var btnGroup = new StackPanel { Orientation = Orientation.Horizontal };
                    btnGroup.Children.Add(keepBtn);
                    btnGroup.Children.Add(revertBtn);

                    Canvas.SetLeft(btnGroup, left);
                    Canvas.SetTop(btnGroup, textLine.TextTop - 2);

                    _adornmentLayer.AddAdornment(
                        AdornmentPositioningBehavior.OwnerControlled,
                        new SnapshotSpan(snapshot, line.Start, Math.Min(1, line.Length)),
                        null, btnGroup, null);
                }
            }

            private Button CreateHunkButton(int hunkIndex)
            {
                var btn = new Button
                {
                    Content = LocalizationService.Instance["diff.revertHunk"],
                    Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x40, 0x40)),
                    BorderThickness = new Thickness(1),
                    MinWidth = HunkButtonWidth,
                    MaxWidth = HunkButtonWidth,
                    MinHeight = 20,
                    FontSize = 9,
                    Padding = new Thickness(2, 1, 2, 1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = LocalizationService.Instance["diff.revertHunk"],
                };

                btn.Click += (s, e) =>
                {
                    bool ok = EditorDiffMarkerService.Instance.RevertHunkForBuffer(
                        _view.TextBuffer, hunkIndex);

                    // 撤销成功后刷新编辑器内嵌标记
                    if (ok)
                    {
                        RefreshAdornment();
                        Logger.Info($"[DiffAdornment] 已撤销块 [{hunkIndex}]");
                    }
                };

                return btn;
            }

            private Button CreateKeepHunkButton(int hunkIndex)
            {
                var btn = new Button
                {
                    Content = LocalizationService.Instance["diff.keepHunk"],
                    Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x33)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x8F, 0x47)),
                    BorderThickness = new Thickness(1),
                    MinWidth = HunkButtonWidth,
                    MaxWidth = HunkButtonWidth,
                    MinHeight = 20,
                    FontSize = 9,
                    Padding = new Thickness(2, 1, 2, 1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = LocalizationService.Instance["diff.keepHunk"],
                };

                btn.Click += (s, e) =>
                {
                    bool ok = EditorDiffMarkerService.Instance.AcceptHunkForBuffer(
                        _view.TextBuffer, hunkIndex);

                    // 保留成功后刷新编辑器内嵌标记
                    if (ok)
                    {
                        RefreshAdornment();
                        Logger.Info($"[DiffAdornment] 已保留块 [{hunkIndex}]");
                    }
                };

                return btn;
            }

            private UIElement CreateToolbar(InlineDiffSession session)
            {
                var container = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6, 10, 6),
                    Opacity = 0.95,
                };

                var stackPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                };

                string fileName = System.IO.Path.GetFileName(session.Change.FilePath);
                var statsText = new TextBlock
                {
                    Text = fileName + " · " + LocalizationService.Instance["status.diffPreviewing"],
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 14, 0),
                };
                stackPanel.Children.Add(statsText);

                var keepAllBtn = CreateButton(
                    LocalizationService.Instance["diff.confirmChanges"],
                    Color.FromRgb(0x2E, 0x6B, 0x33),
                    Color.FromRgb(0x3F, 0x8F, 0x47),
                    () =>
                    {
                        // 保留全部 → 写穿模式确认并清除撤销追踪（不重写文件）
                        EditorDiffMarkerService.Instance.ConfirmAllForBuffer(_view.TextBuffer);
                        Logger.Info("[DiffAdornment] 用户点击「保留全部」");
                    });
                keepAllBtn.Margin = new Thickness(0, 0, 8, 0);
                stackPanel.Children.Add(keepAllBtn);

                var undoBtn = CreateButton(
                    LocalizationService.Instance["diff.undoChanges"],
                    Color.FromRgb(0x8B, 0x2E, 0x2E),
                    Color.FromRgb(0xB8, 0x40, 0x40),
                    () =>
                    {
                        // 撤销全部 → 恢复磁盘 Baseline 并关闭浮动窗口
                        EditorDiffMarkerService.Instance.DismissSessionForBuffer(_view.TextBuffer);
                        Logger.Info("[DiffAdornment] 用户点击「撤销全部」");
                    });
                stackPanel.Children.Add(undoBtn);

                container.Child = stackPanel;
                return container;
            }

            private static Button CreateButton(string text, Color bgColor, Color borderColor, Action onClick)
            {
                var btn = new Button
                {
                    Content = text,
                    Background = new SolidColorBrush(bgColor),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(borderColor),
                    BorderThickness = new Thickness(1),
                    MinWidth = 90,
                    MinHeight = 28,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(12, 4, 12, 4),
                };

                btn.Click += (s, e) => onClick();
                return btn;
            }

            #endregion
        }
    }
