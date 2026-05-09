using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// Diff 标记器。从 <see cref="EditorDiffMarkerService"/> 获取活跃的 diff 预览会话，
    /// 为新增行返回绿色 <see cref="PredefinedTextMarkerTags.Add"/> 标记，
    /// 为临时插回的删除行返回红色 <see cref="PredefinedTextMarkerTags.Delete"/> 标记。
    /// </summary>
    internal sealed class DiffMarkerTagger : ITagger<ITextMarkerTag>
    {
        private readonly ITextBuffer _buffer;

        #region Constructors

        /// <summary>
        /// 为指定文本缓冲区初始化 <see cref="DiffMarkerTagger"/> 实例，
        /// 并订阅服务状态变更事件。
        /// </summary>
        public DiffMarkerTagger(ITextBuffer buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

            // 订阅服务状态变更，当 diff 预览开始时触发重绘
            EditorDiffMarkerService.Instance.PreviewStateChanged += OnPreviewStateChanged;
        }

        #endregion

        #region ITagger<ITextMarkerTag> Implementation

        /// <summary>
        /// 当标记集发生变化时触发，通知编辑器重新查询 <see cref="GetTags"/>。
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        /// <summary>
        /// 返回指定跨度内的 diff 标记。
        /// </summary>
        public IEnumerable<ITagSpan<ITextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0)
                yield break;

            var session = EditorDiffMarkerService.Instance.GetActiveSession(_buffer);
            if (session == null || !session.IsActive)
                yield break;

            ITextSnapshot snapshot = spans[0].Snapshot;

            // ── 红色标记：临时插入的删除行 ──
            foreach (var insertion in session.InsertedDeletedLines)
            {
                if (insertion.TrackingSpan == null)
                    continue;

                SnapshotSpan? trackedSpan;
                try
                {
                    trackedSpan = insertion.TrackingSpan.GetSpan(snapshot);
                }
                catch
                {
                    // 追踪区间在当前快照中已失效
                    continue;
                }

                if (trackedSpan == null)
                    continue;

                var span = trackedSpan.Value;

                // 检查是否与请求的 spans 有交集
                foreach (var requestedSpan in spans)
                {
                    if (span.IntersectsWith(requestedSpan))
                    {
                        yield return new TagSpan<ITextMarkerTag>(
                            span,
                            new TextMarkerTag("delete"));
                        break;
                    }
                }
            }

            // ── 绿色标记：新增行 ──
            foreach (var addedSpan in session.AddedLineSpans)
            {
                // 将旧快照的区间映射到当前快照
                SnapshotSpan mappedSpan;
                try
                {
                    if (addedSpan.Snapshot == snapshot)
                    {
                        mappedSpan = addedSpan;
                    }
                    else
                    {
                        // 快照不匹配，尝试通过 TrackingSpan 映射
                        var trackingSpan = addedSpan.Snapshot.CreateTrackingSpan(
                            addedSpan, SpanTrackingMode.EdgeExclusive);
                        mappedSpan = trackingSpan.GetSpan(snapshot);
                    }
                }
                catch
                {
                    continue;
                }

                foreach (var requestedSpan in spans)
                {
                    if (mappedSpan.IntersectsWith(requestedSpan))
                    {
                        yield return new TagSpan<ITextMarkerTag>(
                            mappedSpan,
                            new TextMarkerTag("add"));
                        break;
                    }
                }
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// 当 diff 预览状态变更时，通知编辑器重新获取标记。
        /// </summary>
        private void OnPreviewStateChanged(ITextBuffer changedBuffer)
        {
            // 仅对关联的 buffer 触发重绘
            if (changedBuffer == _buffer)
            {
                var snapshot = _buffer.CurrentSnapshot;
                var entireSpan = new SnapshotSpan(snapshot, 0, snapshot.Length);
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(entireSpan));
            }
        }

        #endregion
    }
}
