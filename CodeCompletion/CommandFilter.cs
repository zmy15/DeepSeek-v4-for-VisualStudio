using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// 命令过滤器：拦截 Tab（接受）和 Escape（取消）按键，
    /// 控制 <see cref="GhostTextTagger"/> 提供的内联幽灵文本建议。
    /// </summary>
    internal sealed class CommandFilter : IOleCommandTarget
    {
        #region Properties

        private readonly IWpfTextView view;
        private readonly IOleCommandTarget nextCommandTarget;

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化 <see cref="CommandFilter"/> 实例并将其附加到文本视图的命令链中。
        /// </summary>
        /// <param name="view">需要过滤命令的 WPF 文本视图。</param>
        /// <param name="textViewAdapter">用于注册过滤器的旧版文本视图适配器。</param>
        public CommandFilter(IWpfTextView view, IVsTextView textViewAdapter)
        {
            this.view = view;

            textViewAdapter.AddCommandFilter(this, out nextCommandTarget);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 处理传入命令：Tab 接受建议、Escape 取消建议。
        /// </summary>
        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                if (nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB)
                {
                    if (TryAcceptSuggestion())
                    {
                        return VSConstants.S_OK;
                    }
                }
                else if (nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL)
                {
                    if (TryDismissSuggestion())
                    {
                        return VSConstants.S_OK;
                    }
                }
            }

            int result = nextCommandTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

            if (pguidCmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN)
            {
                RestartAutocompleteTimer();
            }

            return result;
        }

        /// <summary>
        /// 查询命令状态：当有活动建议时，标记 Tab 和 Escape 为已支持。
        /// </summary>
        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                for (int i = 0; i < cCmds; i++)
                {
                    if (prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.TAB ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL)
                    {
                        if (HasActiveSuggestion())
                        {
                            prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                            return VSConstants.S_OK;
                        }
                    }
                }
            }

            return nextCommandTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 用户按下 Enter 后重启补全防抖定时器。
        /// </summary>
        private void RestartAutocompleteTimer()
        {
            if (view.Properties.TryGetProperty(typeof(InlinePredictionManager), out InlinePredictionManager manager))
            {
                manager.RestartTimer();
            }
        }

        /// <summary>
        /// 检查当前是否正在显示幽灵文本建议。
        /// </summary>
        private bool HasActiveSuggestion()
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                return tagger.GetSuggestionText() != null;
            }

            return false;
        }

        /// <summary>
        /// 尝试接受当前幽灵文本建议，将其插入文本缓冲区。
        /// </summary>
        /// <returns>成功接受返回 true；否则返回 false。</returns>
        private bool TryAcceptSuggestion()
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                if (tagger.AcceptSuggestion())
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.tabAccepted"]);
                    if (view.Properties.TryGetProperty(typeof(InlinePredictionManager), out InlinePredictionManager manager))
                    {
                        manager.NotifySuggestionAccepted();
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 尝试取消当前幽灵文本建议。
        /// </summary>
        /// <returns>成功取消返回 true；否则返回 false。</returns>
        private bool TryDismissSuggestion()
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                if (tagger.GetSuggestionText() != null)
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.escapeCanceled"]);
                    tagger.ClearSuggestion();
                    return true;
                }
            }

            return false;
        }

        #endregion
    }
}
