using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.View.InlineEdit
{
    /// <summary>
    /// Inline Edit 指令条（P1-B）。
    ///
    /// 轻量无边框浮窗，锚定在选区起始行附近：
    /// - Ready 态：输入指令，Enter 提交（不关窗，由命令方决定后续），Esc 关闭
    /// - Busy  态：显示生成中状态，Esc 触发 CancelRequested（取消 LLM 调用）
    /// - Error 态：红色错误提示并回到 Ready，支持原地重试
    ///
    /// 使用编辑器视觉树内的 Popup 承载，避免 VS 编辑器继续抢走键盘命令。
    /// </summary>
    internal sealed class InlineEditBarWindow
    {
        private const double BarWidth = 600;
        private const double EstimatedHeight = 64;

        private readonly Point _anchorScreenPhysical;

        private static readonly Brush BgBrush = Hex("#252526");
        private static readonly Brush InputBgBrush = Hex("#1E1E1E");
        private static readonly Brush InputFgBrush = Hex("#D4D4D4");
        private static readonly Brush BorderAccentBrush = Hex("#007ACC");
        private static readonly Brush HintBrush = Hex("#9A9A9A");
        private static readonly Brush ErrorBrush = Hex("#F48771");

        private readonly FrameworkElement _placementTarget;
        private readonly TextBox _input;
        private readonly TextBlock _hintText;
        private readonly TextBlock _statusText;
        private readonly Border _statusBorder;
        private readonly Popup _popup;
        private readonly Border _root;

        private bool _busy;
        private TaskCompletionSource<string?> _submittedTcs = NewTcs();
        private TaskCompletionSource<bool> _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Enter 提交（窗口保持打开；命令方通过 ResetSubmit 复位以支持重试）。</summary>
        public Task<string?> WaitForSubmitAsync() => _submittedTcs.Task;

        public Task WaitForCloseAsync() => _closedTcs.Task;

        /// <summary>Esc 取消生成（仅 Busy 态触发）。</summary>
        public event Action? CancelRequested;

        public bool IsActive => _popup.IsOpen;

        public InlineEditBarWindow(Point anchorScreenPhysical, FrameworkElement placementTarget)
        {
            _anchorScreenPhysical = anchorScreenPhysical;
            _placementTarget = placementTarget ?? throw new ArgumentNullException(nameof(placementTarget));

            var hint = LocalizationService.Instance;
            _input = new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(6, 5, 6, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = InputBgBrush,
                Foreground = InputFgBrush,
                BorderBrush = Hex("#3F3F46"),
                CaretBrush = InputFgBrush,
                Margin = new Thickness(0),
            };

            _hintText = new TextBlock
            {
                Text = hint["inlineEdit.hint"],
                Foreground = HintBrush,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            var inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_input, 0);
            Grid.SetColumn(_hintText, 1);
            inputRow.Children.Add(_input);
            inputRow.Children.Add(_hintText);

            // 占位符需在 TextBox 挂入布局后附加（宿主 Grid 叠加在其上层）
            AttachPlaceholder(_input, hint["inlineEdit.placeholder"]);

            _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            _statusBorder = new Border
            {
                Child = _statusText,
                Padding = new Thickness(2, 6, 2, 0),
                Visibility = Visibility.Collapsed,
            };

            var stack = new StackPanel();
            stack.Children.Add(inputRow);
            stack.Children.Add(_statusBorder);

            _root = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(8),
                Background = BgBrush,
                BorderBrush = BorderAccentBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.45,
                },
            };

            _popup = new Popup
            {
                Child = _root,
                PlacementTarget = _placementTarget,
                Placement = PlacementMode.RelativePoint,
                AllowsTransparency = true,
                StaysOpen = true,
                Width = BarWidth,
            };
            FocusManager.SetIsFocusScope(_popup, true);
            KeyboardNavigation.SetDirectionalNavigation(_root, KeyboardNavigationMode.Contained);
            KeyboardNavigation.SetTabNavigation(_root, KeyboardNavigationMode.Contained);

            _root.PreviewKeyDown += OnBarPreviewKeyDown;
            _popup.Closed += (_, _) => _closedTcs.TrySetResult(true);
            _popup.Opened += (_, _) =>
            {
                Reposition();
                FocusInput();
            };
        }

        // ────────────────────────── 公开 API ──────────────────────────

        public void ShowBar()
        {
            Reposition();
            _popup.IsOpen = true;
            FocusInput();
        }

        public void SetBusy(string message)
        {
            _busy = true;
            _input.IsEnabled = false;
            _hintText.Text = LocalizationService.Instance["inlineEdit.escToCancel"];
            ShowStatus(message, "#9CDCFE");
        }

        public void SetError(string message)
        {
            _busy = false;
            _input.IsEnabled = true;
            _hintText.Text = LocalizationService.Instance["inlineEdit.hint"];
            ShowStatus(message, null);   // null → ErrorBrush
            FocusInput(selectAll: true);
        }

        public void CloseGracefully() => Close();

        public void Close()
        {
            _popup.IsOpen = false;
        }

        /// <summary>提交后复位 TCS，允许同一窗口再次提交（原地重试）。</summary>
        public void ResetSubmit()
        {
            if (_submittedTcs.Task.IsCompleted)
                _submittedTcs = NewTcs();
        }

        // ────────────────────────── VS 命令过滤回调 ──────────────────────────

        public void Submit()
        {
            if (_busy || !_popup.IsOpen) return;
            var text = _input.Text?.Trim() ?? string.Empty;
            if (text.Length == 0) return;
            _submittedTcs.TrySetResult(text);
        }

        public void Cancel()
        {
            if (!_popup.IsOpen) return;
            if (_busy) CancelRequested?.Invoke();
            else Close();
        }

        public void Backspace()
        {
            if (!_popup.IsOpen || _busy) return;
            var text = _input.Text ?? string.Empty;
            var selectionStart = _input.SelectionStart;
            var selectionLength = _input.SelectionLength;

            if (selectionLength > 0)
            {
                _input.Text = text.Remove(selectionStart, selectionLength);
                _input.CaretIndex = selectionStart;
            }
            else if (_input.CaretIndex > 0)
            {
                var caret = _input.CaretIndex;
                _input.Text = text.Remove(caret - 1, 1);
                _input.CaretIndex = caret - 1;
            }
        }

        public void DeleteForward()
        {
            if (!_popup.IsOpen || _busy) return;
            var text = _input.Text ?? string.Empty;
            var selectionStart = _input.SelectionStart;
            var selectionLength = _input.SelectionLength;

            if (selectionLength > 0)
            {
                _input.Text = text.Remove(selectionStart, selectionLength);
                _input.CaretIndex = selectionStart;
            }
            else if (_input.CaretIndex < text.Length)
            {
                _input.Text = text.Remove(_input.CaretIndex, 1);
                _input.CaretIndex = _input.CaretIndex;
            }
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        private void OnBarPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_busy) return;
                string text = _input.Text?.Trim() ?? string.Empty;
                if (text.Length == 0) return;
                _submittedTcs.TrySetResult(text);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_busy) CancelRequested?.Invoke();
                else Close();
            }
        }

        private void Reposition()
        {
            Point anchor;
            try
            {
                anchor = _placementTarget.PointFromScreen(_anchorScreenPhysical);
            }
            catch
            {
                anchor = new Point(40, 40);
            }

            double left = anchor.X - 40;
            double top = anchor.Y + 22;
            double height = double.IsNaN(_root.ActualHeight) || _root.ActualHeight <= 0
                ? EstimatedHeight
                : _root.ActualHeight;

            left = Math.Max(0, Math.Min(left, Math.Max(0, _placementTarget.ActualWidth - BarWidth - 4)));
            if (top + height > _placementTarget.ActualHeight)
                top = Math.Max(0, anchor.Y - height - 28);

            _popup.HorizontalOffset = left;
            _popup.VerticalOffset = top;
        }

        private void FocusInput(bool selectAll = false)
        {
            _input.Focus();
            Keyboard.Focus(_input);
            FocusManager.SetFocusedElement(_root, _input);
            if (selectAll && !string.IsNullOrEmpty(_input.Text))
                _input.SelectAll();
            else
                _input.CaretIndex = _input.Text?.Length ?? 0;

            // VS 编辑器可能在浮窗初次渲染后把键盘焦点拉回去，延迟再校准一次。
            _popup.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_popup.IsOpen || _busy)
                    return;

                _input.Focus();
                Keyboard.Focus(_input);
                FocusManager.SetFocusedElement(_popup, _input);
            });
        }

        private void ShowStatus(string message, string? hexColor)
        {
            _statusText.Text = message;
            _statusText.Foreground = hexColor != null ? Hex(hexColor) : ErrorBrush;
            _statusBorder.Visibility = Visibility.Visible;
        }

        private static void AttachPlaceholder(TextBox box, string placeholder)
        {
            var holder = new TextBlock
            {
                Text = placeholder,
                Foreground = Hex("#6B6B6B"),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var host = new Grid { Background = Brushes.Transparent };
            Panel? originalParent = null;
            int originalIndex = -1;

            // WPF 不允许同一元素有两个 logical parent。TextBox 可能已被加到 inputRow，
            // 因此必须先断开，再挂到 placeholder host 上。
            if (box.Parent is Panel parent)
            {
                originalIndex = parent.Children.IndexOf(box);
                parent.Children.RemoveAt(originalIndex);
                originalParent = parent;
                if (parent is Grid g)
                    Grid.SetColumn(host, Grid.GetColumn(box));
            }

            host.Children.Add(box);
            host.Children.Add(holder);

            void UpdateVisibility()
            {
                holder.Visibility = string.IsNullOrEmpty(box.Text) && !box.IsKeyboardFocusWithin
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            box.TextChanged += (_, _) => UpdateVisibility();
            box.GotKeyboardFocus += (_, _) => UpdateVisibility();
            box.LostKeyboardFocus += (_, _) => UpdateVisibility();

            if (originalParent != null)
                originalParent.Children.Insert(originalIndex, host);
        }

        private static SolidColorBrush Hex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static TaskCompletionSource<string?> NewTcs()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

    }
}
