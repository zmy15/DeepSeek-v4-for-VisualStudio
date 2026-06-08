using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 主题相关方法：检测 VS 主题、切换浅色/深色模式、更新 WPF 控件颜色。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Theme

        /// <summary>
        /// 主题切换按钮点击：在 Auto → Dark → Light 之间循环。
        /// </summary>
        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var nextMode = _themeService.UserThemeMode switch
            {
                ThemeMode.Auto => ThemeMode.Dark,
                ThemeMode.Dark => ThemeMode.Light,
                ThemeMode.Light => ThemeMode.Auto,
                _ => ThemeMode.Auto
            };
            _themeService.UserThemeMode = nextMode;
            UpdateThemeToggleIcon();
        }

        /// <summary>
        /// VS 主题或用户设置变更时触发。
        /// </summary>
        private void OnThemeChanged(bool isLight)
        {
            if (_isApplyingTheme) return;
            _isApplyingTheme = true;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ApplyWpfTheme(isLight);
                UpdateThemeToggleIcon();
                ReloadWebViewForTheme();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Theme] Failed to apply theme: {ex.Message}");
            }
            finally
            {
                _isApplyingTheme = false;
            }
        }

        /// <summary>
        /// 更新主题切换按钮图标。
        /// </summary>
        private void UpdateThemeToggleIcon()
        {
            try
            {
                if (ThemeToggleIcon == null) return;
                ThemeToggleIcon.Text = _themeService.UserThemeMode switch
                {
                    ThemeMode.Auto => "🌓",
                    ThemeMode.Dark => "🌙",
                    ThemeMode.Light => "☀️",
                    _ => "🌓"
                };

                var tooltip = _themeService.UserThemeMode switch
                {
                    ThemeMode.Auto => $"主题: 自动 ({(_themeService.IsLight ? "浅色" : "深色")})",
                    ThemeMode.Dark => "主题: 深色",
                    ThemeMode.Light => "主题: 浅色",
                    _ => "切换主题"
                };
                ThemeToggleButton.ToolTip = tooltip;
            }
            catch { }
        }

        /// <summary>
        /// 应用 WPF 控件颜色主题。
        /// </summary>
        private void ApplyWpfTheme(bool isLight)
        {
            if (isLight)
            {
                ApplyWpfLightTheme();
            }
            else
            {
                ApplyWpfDarkTheme();
            }
        }

        /// <summary>
        /// 应用 WPF 深色主题颜色（原有样式）。
        /// </summary>
        private void ApplyWpfDarkTheme()
        {
            var panelBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            var panelBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            var textColor = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            var mutedText = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            var accentBlue = new SolidColorBrush(Color.FromRgb(0x6C, 0xAF, 0xD9));
            var accentGreen = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
            var inputBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            var diffBarBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x20));

            ApplyWpfColors(panelBg, panelBorder, textColor, mutedText, accentBlue, accentGreen, inputBg, diffBarBg);
        }

        /// <summary>
        /// 应用 WPF 浅色主题颜色。
        /// </summary>
        private void ApplyWpfLightTheme()
        {
            var panelBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            var panelBorder = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            var textColor = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            var mutedText = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            var accentBlue = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            var accentGreen = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x7A));
            var inputBg = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            var diffBarBg = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));

            ApplyWpfColors(panelBg, panelBorder, textColor, mutedText, accentBlue, accentGreen, inputBg, diffBarBg);
        }

        /// <summary>
        /// 将主题颜色应用到所有 WPF 控件。
        /// </summary>
        private void ApplyWpfColors(
            SolidColorBrush panelBg, SolidColorBrush panelBorder,
            SolidColorBrush textColor, SolidColorBrush mutedText,
            SolidColorBrush accentBlue, SolidColorBrush accentGreen,
            SolidColorBrush inputBg, SolidColorBrush diffBarBg)
        {
            try
            {
                bool isLight = textColor.Color.R < 0x80;

                // ── 替换 ComboBox / ComboBoxItem / CheckBox 隐式样式 ──
                ReplaceComboBoxStyles(isLight, panelBg, panelBorder, textColor, mutedText, accentBlue);

                // ── 会话选择栏 ──
                ApplyBorderBrush(FindParentBorder(SessionComboBox), panelBg, panelBorder);
                if (SessionComboBox != null) SessionComboBox.Foreground = textColor;

                // ── 状态栏 ──
                ApplyBorderBrush(FindParentBorder(StatusLabel), panelBg, panelBorder);
                if (StatusLabel != null) StatusLabel.Foreground = mutedText;

                // ── Diff 全局控制栏 ──
                if (DiffGlobalBar != null) DiffGlobalBar.Background = diffBarBg;
                if (DiffGlobalLabel != null) DiffGlobalLabel.Foreground = textColor;
                if (DiffGlobalDetail != null) DiffGlobalDetail.Foreground = isLight
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x7A, 0x3A))
                    : new SolidColorBrush(Color.FromRgb(0x90, 0xB0, 0x90));
                if (UndoAllButton != null) UndoAllButton.Foreground = textColor;
                if (AcceptAllButton != null) AcceptAllButton.Foreground = textColor;

                // ── 输入区 ──
                if (InputTextBox != null)
                {
                    InputTextBox.Foreground = textColor;
                    InputTextBox.CaretBrush = textColor;
                }
                if (InputPlaceholder != null)
                    InputPlaceholder.Foreground = mutedText;
                ApplyBorderBrush(FindParentBorder(InputTextBox), inputBg, panelBorder);

                // ── 审批控制栏 ──
                ApplyBorderBrush(FindParentBorder(ApprovalModeComboBox), panelBg, panelBorder);
                // Update the "审批模式:" label (TextBlock sibling of ApprovalModeComboBox)
                UpdateApprovalLabel(panelBg, textColor);
                if (ApprovalModeComboBox != null) ApprovalModeComboBox.Foreground = textColor;

                // ── 模型/Effort/WebSearch ComboBox 文字颜色 ──
                if (ModelComboBox != null) ModelComboBox.Foreground = textColor;
                if (EffortComboBox != null) EffortComboBox.Foreground = textColor;
                if (WebSearchEngineComboBox != null) WebSearchEngineComboBox.Foreground = textColor;

                // ── CheckBox 文字颜色 ──
                if (ThinkingCheckBox != null) ThinkingCheckBox.Foreground = textColor;

                // ── 按钮文字颜色 ──
                if (ClearButton != null) ClearButton.Foreground = textColor;
                if (SendButton != null) SendButton.Foreground = accentBlue;
                if (NewChatButton != null) NewChatButton.Foreground = accentGreen;
                if (StopButton != null) StopButton.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60));
                if (UploadButton != null) UploadButton.Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
                if (DeleteSessionButton != null) DeleteSessionButton.Foreground = mutedText;
                if (WebSearchToggleButton != null) WebSearchToggleButton.Foreground = textColor;
                if (McpConfigButton != null) McpConfigButton.Foreground = textColor;
                if (AddContextButton != null) AddContextButton.Foreground = accentBlue;

                // ── 添加上下文弹出菜单项颜色 ──
                UpdatePopupMenuItems(isLight, panelBg, textColor);

                // ── Skill/Agent 建议弹出框 ──
                UpdateSuggestionPopups(isLight, panelBg, panelBorder, textColor);

                // ── 余额栏 ──
                if (BalanceBar != null)
                {
                    ApplyBorderBrush(BalanceBar, panelBg, panelBorder);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Theme] ApplyWpfColors error: {ex.Message}");
            }
        }

        private static void ApplyBorderBrush(Border? border, SolidColorBrush bg, SolidColorBrush borderBrush)
        {
            if (border == null) return;
            border.Background = bg;
            border.BorderBrush = borderBrush;
        }

        /// <summary>
        /// 向上查找最近的 Border 父元素。
        /// </summary>
        private static Border? FindParentBorder(DependencyObject? child)
        {
            if (child == null) return null;
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is Border border)
                    return border;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        /// <summary>
        /// 替换 ComboBox / ComboBoxItem / CheckBox 隐式样式为当前主题版本。
        /// </summary>
        private void ReplaceComboBoxStyles(bool isLight,
            SolidColorBrush panelBg, SolidColorBrush panelBorder,
            SolidColorBrush textColor, SolidColorBrush mutedText,
            SolidColorBrush accentBlue)
        {
            var resources = this.Resources;

            // ComboBox toggle color
            var toggleBg = isLight
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
                : new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            var toggleHoverBg = isLight
                ? new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8))
                : new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
            var toggleHoverBorder = isLight
                ? new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            var arrowColor = isLight
                ? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            var dropdownBg = isLight
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5))
                : new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            var itemHighlightBg = isLight
                ? new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0))
                : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
            var itemSelectedBg = isLight
                ? new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7))
                : new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));

            // ── ComboBox style ──
            var comboStyle = new Style(typeof(ComboBox));
            comboStyle.Setters.Add(new Setter(ComboBox.BackgroundProperty, toggleBg));
            comboStyle.Setters.Add(new Setter(ComboBox.ForegroundProperty, textColor));
            comboStyle.Setters.Add(new Setter(ComboBox.BorderBrushProperty, panelBorder));
            comboStyle.Setters.Add(new Setter(ComboBox.BorderThicknessProperty, new Thickness(1)));
            comboStyle.Setters.Add(new Setter(ComboBox.PaddingProperty, new Thickness(8, 3, 0, 3)));
            comboStyle.Setters.Add(new Setter(ComboBox.FontSizeProperty, 12.0));
            comboStyle.Setters.Add(new Setter(ComboBox.MinHeightProperty, 22.0));
            comboStyle.Setters.Add(new Setter(ComboBox.SnapsToDevicePixelsProperty, true));

            var comboTemplate = new ControlTemplate(typeof(ComboBox));
            // ToggleButton
            var toggleBtnFactory = new FrameworkElementFactory(typeof(ToggleButton), "ToggleButton");
            toggleBtnFactory.SetValue(ToggleButton.BackgroundProperty, new TemplateBindingExtension(ComboBox.BackgroundProperty));
            toggleBtnFactory.SetValue(ToggleButton.BorderBrushProperty, new TemplateBindingExtension(ComboBox.BorderBrushProperty));
            toggleBtnFactory.SetValue(ToggleButton.BorderThicknessProperty, new TemplateBindingExtension(ComboBox.BorderThicknessProperty));
            toggleBtnFactory.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = System.Windows.Data.BindingMode.TwoWay
            });
            toggleBtnFactory.SetValue(ToggleButton.FocusableProperty, false);
            toggleBtnFactory.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);

            var toggleBtnTemplate = new ControlTemplate(typeof(ToggleButton));
            var toggleBorder = new FrameworkElementFactory(typeof(Border), "ToggleBorder");
            toggleBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ToggleButton.BackgroundProperty));
            toggleBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(ToggleButton.BorderBrushProperty));
            toggleBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(ToggleButton.BorderThicknessProperty));
            toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            toggleBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var toggleGrid = new FrameworkElementFactory(typeof(Grid));
            var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(20));
            toggleGrid.AppendChild(col1);
            toggleGrid.AppendChild(col2);

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(Grid.ColumnProperty, 0);
            contentPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 3, 0, 3));
            contentPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            contentPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            toggleGrid.AppendChild(contentPresenter);

            var arrowPath = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            arrowPath.SetValue(Grid.ColumnProperty, 1);
            arrowPath.SetValue(System.Windows.Shapes.Path.DataProperty, System.Windows.Media.Geometry.Parse("M0,0 L4,4 L8,0"));
            arrowPath.SetValue(System.Windows.Shapes.Path.FillProperty, arrowColor);
            arrowPath.SetValue(System.Windows.Shapes.Path.StrokeProperty, arrowColor);
            arrowPath.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.0);
            arrowPath.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrowPath.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            toggleGrid.AppendChild(arrowPath);

            toggleBorder.AppendChild(toggleGrid);
            toggleBtnTemplate.VisualTree = toggleBorder;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, toggleHoverBg, "ToggleBorder"));
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, toggleHoverBorder, "ToggleBorder"));
            toggleBtnTemplate.Triggers.Add(hoverTrigger);

            toggleBtnFactory.SetValue(ToggleButton.TemplateProperty, toggleBtnTemplate);
            comboTemplate.VisualTree = CreateComboRoot(toggleBtnFactory, dropdownBg, panelBorder);

            comboStyle.Setters.Add(new Setter(ComboBox.TemplateProperty, comboTemplate));
            resources[typeof(ComboBox)] = comboStyle;

            // ── ComboBoxItem style ──
            var comboItemStyle = new Style(typeof(ComboBoxItem));
            comboItemStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, dropdownBg));
            comboItemStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, textColor));
            comboItemStyle.Setters.Add(new Setter(ComboBoxItem.PaddingProperty, new Thickness(8, 4, 8, 4)));

            var itemTemplate = new ControlTemplate(typeof(ComboBoxItem));
            var itemBorder = new FrameworkElementFactory(typeof(Border), "Border");
            itemBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ComboBoxItem.BackgroundProperty));
            itemBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(ComboBoxItem.PaddingProperty));
            itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            itemBorder.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            itemTemplate.VisualTree = itemBorder;

            var highlightTrigger = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, itemHighlightBg, "Border"));
            itemTemplate.Triggers.Add(highlightTrigger);

            var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, itemSelectedBg, "Border"));
            itemTemplate.Triggers.Add(selectedTrigger);

            comboItemStyle.Setters.Add(new Setter(ComboBoxItem.TemplateProperty, itemTemplate));
            resources[typeof(ComboBoxItem)] = comboItemStyle;

            // ── CheckBox style ──
            var checkBoxStyle = new Style(typeof(CheckBox));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.ForegroundProperty, textColor));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.FontSizeProperty, 12.0));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            resources[typeof(CheckBox)] = checkBoxStyle;
        }

        private static FrameworkElementFactory CreateComboRoot(
            FrameworkElementFactory toggleBtnFactory,
            SolidColorBrush dropdownBg, SolidColorBrush panelBorder)
        {
            var rootGrid = new FrameworkElementFactory(typeof(Grid));
            rootGrid.AppendChild(toggleBtnFactory);

            var contentSite = new FrameworkElementFactory(typeof(ContentPresenter), "ContentSite");
            contentSite.SetValue(UIElement.IsHitTestVisibleProperty, false);
            contentSite.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            contentSite.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
            contentSite.SetValue(ContentPresenter.ContentTemplateSelectorProperty, new TemplateBindingExtension(ComboBox.ItemTemplateSelectorProperty));
            contentSite.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 3, 25, 3));
            contentSite.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentSite.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            rootGrid.AppendChild(contentSite);

            var popup = new FrameworkElementFactory(typeof(Popup), "Popup");
            popup.SetValue(Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Bottom);
            popup.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(UIElement.FocusableProperty, false);
            popup.SetValue(Popup.PopupAnimationProperty, System.Windows.Controls.Primitives.PopupAnimation.Slide);
            popup.SetValue(Popup.SnapsToDevicePixelsProperty, true);

            var dropGrid = new FrameworkElementFactory(typeof(Grid), "DropDown");
            dropGrid.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));
            dropGrid.SetValue(FrameworkElement.MaxHeightProperty, new TemplateBindingExtension(ComboBox.MaxDropDownHeightProperty));
            dropGrid.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var dropBorder = new FrameworkElementFactory(typeof(Border), "DropDownBorder");
            dropBorder.SetValue(Border.BackgroundProperty, dropdownBg);
            dropBorder.SetValue(Border.BorderBrushProperty, panelBorder);
            dropBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            dropBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            dropGrid.AppendChild(dropBorder);

            var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewer.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 6, 4, 6));
            scrollViewer.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var itemsHost = new FrameworkElementFactory(typeof(StackPanel));
            itemsHost.SetValue(StackPanel.IsItemsHostProperty, true);
            itemsHost.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Contained);
            scrollViewer.AppendChild(itemsHost);
            dropGrid.AppendChild(scrollViewer);

            popup.AppendChild(dropGrid);
            rootGrid.AppendChild(popup);

            return rootGrid;
        }

        /// <summary>
        /// 更新审批模式标签的 TextBlock 颜色。
        /// </summary>
        private void UpdateApprovalLabel(SolidColorBrush bg, SolidColorBrush textColor)
        {
            try
            {
                if (ApprovalModeComboBox?.Parent is StackPanel sp)
                {
                    foreach (var child in sp.Children)
                    {
                        if (child is TextBlock tb && tb.Text.Contains("审批"))
                        {
                            tb.Foreground = textColor;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 更新添加上下文弹出菜单项的颜色。
        /// </summary>
        private void UpdatePopupMenuItems(bool isLight, SolidColorBrush bg, SolidColorBrush textColor)
        {
            try
            {
                var menuBg = isLight
                    ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5))
                    : new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
                var menuBorder = isLight
                    ? new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
                    : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

                var menuItems = new[] { AddActiveDocMenuItem, AddProjectFileMenuItem, AddAllFilesMenuItem,
                    AddSelectionMenuItem, AddDebugMenuItem };
                foreach (var item in menuItems)
                {
                    if (item != null)
                    {
                        item.Background = menuBg;
                        item.Foreground = textColor;
                    }
                }

                // Update the popup border
                if (AddContextPopup?.Child is Border popupBorder)
                {
                    popupBorder.Background = menuBg;
                    popupBorder.BorderBrush = menuBorder;
                }
            }
            catch { }
        }

        /// <summary>
        /// 更新 Skill/Agent 建议弹出框颜色。
        /// </summary>
        private void UpdateSuggestionPopups(bool isLight, SolidColorBrush bg, SolidColorBrush border, SolidColorBrush textColor)
        {
            try
            {
                var mutedColor = isLight
                    ? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
                    : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                var listTextColor = isLight
                    ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
                    : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
                var listSubColor = isLight
                    ? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
                    : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));

                // Skill popup
                UpdatePopupBorder(SkillSuggestionPopup, bg, border);
                if (SkillPopupTitle != null) SkillPopupTitle.Foreground = mutedColor;
                if (SkillSuggestionListBox != null) SkillSuggestionListBox.Foreground = listTextColor;

                // Agent popup
                UpdatePopupBorder(AgentSuggestionPopup, bg, border);
                if (AgentPopupTitle != null) AgentPopupTitle.Foreground = mutedColor;
                if (AgentSuggestionListBox != null) AgentSuggestionListBox.Foreground = listTextColor;
            }
            catch { }
        }

        private static void UpdatePopupBorder(Popup? popup, SolidColorBrush bg, SolidColorBrush border)
        {
            if (popup?.Child is Border popupBorder)
            {
                popupBorder.Background = bg;
                popupBorder.BorderBrush = border;
            }
        }

        /// <summary>
        /// 主题变更后重新加载 WebView2 内容。
        /// 如果 WebView 已就绪且有消息，则重新渲染所有消息。
        /// </summary>
        private async void ReloadWebViewForTheme()
        {
            try
            {
                if (ChatWebView?.CoreWebView2 == null) return;
                if (_messages.Count == 0) return;

                // 重新生成完整 HTML 页面
                string newHtml = ChatHtmlService.BuildInitialPage(_messages);
                ChatWebView.CoreWebView2.NavigateToString(newHtml);
                Logger.Info("[Theme] WebView2 reloaded with new theme");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Theme] Failed to reload WebView2: {ex.Message}");
            }
        }

        #endregion
    }
}
