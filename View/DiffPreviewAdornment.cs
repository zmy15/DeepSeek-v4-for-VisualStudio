using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Diff 预览装饰器。在编辑器视口顶部显示「确认变更」和「撤销」按钮，
    /// 在编辑器视口顶部显示「确认变更」和「撤销」按钮，
    /// 以及变更统计信息。仅当 <see cref="EditorDiffMarkerService"/> 中有活跃会话时显示。
    /// </summary>
    internal sealed class DiffPreviewAdornment
    {
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _adornmentLayer;
        private UIElement? _toolbar;

        public const string AdornmentLayerName = "DeepSeekDiffPreviewAdornment";

        #region Constructors

        public DiffPreviewAdornment(IWpfTextView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _adornmentLayer = view.GetAdornmentLayer(AdornmentLayerName);

            _view.LayoutChanged += OnLayoutChanged;

            // 差异着色由 DiffViewerWindow 原生处理，装饰器仅检查 IsPreviewActive 来决定是否显示按钮

            RefreshAdornment();
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
            _adornmentLayer.RemoveAllAdornments();

            bool isActive = EditorDiffMarkerService.Instance.IsPreviewActive(_view.TextBuffer);
            if (!isActive)
            {
                _toolbar = null;
                return;
            }

            _toolbar = CreateToolbar();

            Canvas.SetLeft(_toolbar, 8);
            Canvas.SetTop(_toolbar, 6);

            _adornmentLayer.AddAdornment(
                AdornmentPositioningBehavior.ViewportRelative,
                null, null, _toolbar, null);
        }

        private UIElement CreateToolbar()
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

            var statsText = new TextBlock
            {
                Text = "📊 差异预览中",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
            };
            stackPanel.Children.Add(statsText);

            var confirmBtn = CreateButton(
                "✅ 确认变更",
                Color.FromRgb(0x1B, 0x5E, 0x20),
                Color.FromRgb(0x2E, 0x7D, 0x32),
                () =>
                {
                    EditorDiffMarkerService.Instance.ConfirmChanges(_view.TextBuffer);
                    Logger.Info("[DiffAdornment] 用户点击「确认变更」");
                });
            stackPanel.Children.Add(confirmBtn);

            var spacer = new TextBlock { Width = 10 };
            stackPanel.Children.Add(spacer);

            var undoBtn = CreateButton(
                "↩️ 撤销",
                Color.FromRgb(0x8B, 0x2E, 0x2E),
                Color.FromRgb(0xB8, 0x40, 0x40),
                () =>
                {
                    EditorDiffMarkerService.Instance.UndoChanges(_view.TextBuffer);
                    Logger.Info("[DiffAdornment] 用户点击「撤销」");
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
