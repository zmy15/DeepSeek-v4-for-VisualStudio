using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// 内联幽灵文本标记器。在光标位置之后以灰色装饰器渲染代码建议，
    /// 用户按 Tab 接受、Esc 取消。
    /// </summary>
    internal sealed class GhostTextTagger : ITagger<IntraTextAdornmentTag>
    {
        #region Constants

        /// <summary>
        /// 文本视图属性包中存储标记器实例的键。
        /// </summary>
        internal static readonly object TaggerKey = typeof(GhostTextTagger);

        #endregion

        #region Properties

        private readonly IWpfTextView view;
        private string suggestionText;
        private int suggestionPosition;
        private ITrackingPoint trackingPoint;

        /// <summary>
        /// 当标记集发生变化时触发，通知编辑器重新查询 <see cref="GetTags"/>。
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        #endregion

        #region Constructors

        /// <summary>
        /// 为指定文本视图初始化 <see cref="GhostTextTagger"/> 实例。
        /// </summary>
        /// <param name="view">需要提供幽灵文本装饰的文本视图。</param>
        public GhostTextTagger(IWpfTextView view)
        {
            this.view = view;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 在指定缓冲区位置以灰色幽灵文本形式显示代码建议。
        /// </summary>
        /// <param name="text">要渲染的建议文本。</param>
        /// <param name="position">建议起始的缓冲区位置。</param>
        public void SetSuggestion(string text, int position)
        {
            if (string.IsNullOrEmpty(text))
            {
                ClearSuggestion();
                return;
            }

            ITextSnapshot snapshot = view.TextSnapshot;

            if (position > snapshot.Length)
            {
                position = snapshot.Length;
            }

            suggestionText = text;
            suggestionPosition = position;
            trackingPoint = snapshot.CreateTrackingPoint(position, PointTrackingMode.Negative);

            Logger.Info(string.Format(LocalizationService.Instance["autocomplete.ghostTextShown"], position, text.Length));
            RaiseTagsChanged();
        }

        /// <summary>
        /// 清除当前显示的幽灵文本建议。
        /// </summary>
        public void ClearSuggestion()
        {
            if (suggestionText == null)
            {
                return;
            }

            suggestionText = null;
            trackingPoint = null;

            Logger.Info(LocalizationService.Instance["autocomplete.ghostTextCleared"]);
            RaiseTagsChanged();
        }

        /// <summary>
        /// 返回当前的建议文本，若无活动建议则返回 null。
        /// </summary>
        public string GetSuggestionText()
        {
            return suggestionText;
        }

        /// <summary>
        /// 接受当前建议，将文本插入缓冲区并自动格式化。
        /// </summary>
        /// <returns>成功接受返回 true；无活动建议返回 false。</returns>
        public bool AcceptSuggestion()
        {
            if (suggestionText == null || trackingPoint == null)
            {
                return false;
            }

            string text = suggestionText;
            ITextSnapshot snapshot = view.TextSnapshot;
            int position = trackingPoint.GetPosition(snapshot);

            ClearSuggestion();

            view.TextBuffer.Insert(position, text);

            Logger.Info(string.Format(LocalizationService.Instance["autocomplete.suggestionAccepted"], position, text.Length));
            FormatInsertedText(view.TextBuffer.CurrentSnapshot, position, text.Length);

            return true;
        }

        /// <summary>
        /// 返回请求跨度内的幽灵文本装饰标记。
        /// </summary>
        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (suggestionText == null || trackingPoint == null || spans.Count == 0)
            {
                yield break;
            }

            ITextSnapshot snapshot = spans[0].Snapshot;
            int position = trackingPoint.GetPosition(snapshot);

            if (position > snapshot.Length)
            {
                yield break;
            }

            SnapshotPoint point = new(snapshot, position);
            bool inRange = false;

            foreach (SnapshotSpan span in spans)
            {
                if (span.Contains(point) || span.End == point)
                {
                    inRange = true;
                    break;
                }
            }

            if (!inRange)
            {
                yield break;
            }

            UIElement adornment = CreateGhostTextElement();

            SnapshotSpan adornmentSpan = new(point, 0);

            yield return new TagSpan<IntraTextAdornmentTag>(
                adornmentSpan,
                new IntraTextAdornmentTag(adornment, null, PositionAffinity.Successor));
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 创建渲染灰色幽灵文本的 WPF 元素，支持多行显示。
        /// </summary>
        private UIElement CreateGhostTextElement()
        {
            string[] lines = suggestionText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            if (lines.Length == 1)
            {
                return CreateTextBlock(lines[0]);
            }

            StackPanel panel = new() { Orientation = Orientation.Vertical };

            for (int i = 0; i < lines.Length; i++)
            {
                panel.Children.Add(CreateTextBlock(lines[i]));
            }

            return panel;
        }

        /// <summary>
        /// 为单行幽灵文本创建灰色 <see cref="TextBlock"/>。
        /// </summary>
        private TextBlock CreateTextBlock(string text)
        {
            TextBlock block = new()
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontFamily = GetEditorFontFamily(),
                FontSize = GetEditorFontSize(),
                Opacity = 0.7
            };

            return block;
        }

        /// <summary>
        /// 获取文本编辑器使用的字体族。
        /// </summary>
        private FontFamily GetEditorFontFamily()
        {
            try
            {
                IWpfTextViewLineCollection lines = view.TextViewLines;

                if (lines != null && lines.IsValid && lines.Count > 0)
                {
                    System.Windows.Media.TextFormatting.TextRunProperties props = lines[0].GetCharacterFormatting(lines[0].Start);

                    if (props != null)
                    {
                        return props.Typeface.FontFamily;
                    }
                }
            }
            catch
            {
                // Fall back to default.
            }

            return new FontFamily("Consolas");
        }

        /// <summary>
        /// 获取文本编辑器使用的字体大小。
        /// </summary>
        private double GetEditorFontSize()
        {
            try
            {
                IWpfTextViewLineCollection lines = view.TextViewLines;

                if (lines != null && lines.IsValid && lines.Count > 0)
                {
                    System.Windows.Media.TextFormatting.TextRunProperties props = lines[0].GetCharacterFormatting(lines[0].Start);

                    if (props != null)
                    {
                        return props.FontRenderingEmSize;
                    }
                }
            }
            catch
            {
                // Fall back to default.
            }

            return 13.0;
        }

        /// <summary>
        /// 仅触发建议所在 span 的 <see cref="TagsChanged"/> 事件，
        /// 避免全缓冲区重新查询标记。
        /// </summary>
        private void RaiseTagsChanged()
        {
            if (trackingPoint == null)
            {
                ITextSnapshot snapshot = view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                return;
            }

            ITextSnapshot currentSnapshot = view.TextSnapshot;
            int position = trackingPoint.GetPosition(currentSnapshot);
            int length = suggestionText?.Length ?? 0;

            // 只通知建议所在的小范围 span，编辑器仅重新查询该区域
            int safeStart = Math.Max(0, position - 1);
            int safeEnd = Math.Min(currentSnapshot.Length, position + length + 1);
            var span = new SnapshotSpan(currentSnapshot, safeStart, safeEnd - safeStart);
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span));
        }

        /// <summary>
        /// 通过 VS 标准 <see cref="IOleCommandTarget"/> 执行 FORMATSELECTION 命令，
        /// 格式化刚插入的代码补全文本。
        /// </summary>
        private void FormatInsertedText(ITextSnapshot snapshot, int startPosition, int length)
        {
            try
            {
                SnapshotSpan insertedSpan = new(snapshot, startPosition, length);
                view.Selection.Select(insertedSpan, false);

                Guid cmdGroup = VSConstants.VSStd2K;
                ThreadHelper.ThrowIfNotOnUIThread();

                if (view.Properties.TryGetProperty(typeof(Microsoft.VisualStudio.TextManager.Interop.IVsTextView), out Microsoft.VisualStudio.TextManager.Interop.IVsTextView textViewAdapter))
                {
                    Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget target = (Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget)textViewAdapter;
                    target.Exec(ref cmdGroup, (uint)VSConstants.VSStd2KCmdID.FORMATSELECTION, 0, IntPtr.Zero, IntPtr.Zero);
                }

                view.Selection.Clear();
                view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, Math.Min(startPosition + length, view.TextSnapshot.Length)));
            }
            catch (Exception)
            {
                // Formatting is best-effort; do not block the accept.
            }
        }

        #endregion
    }
}
