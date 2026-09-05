using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DeepSeek_v4_for_VisualStudio.View.InlineEdit
{
    /// <summary>
    /// Keeps editor commands away from the text buffer while the inline bar is active.
    /// VS may keep its own keyboard routing active even when a WPF Popup has input focus.
    /// </summary>
    internal sealed class InlineEditCommandFilter : IOleCommandTarget
    {
        private readonly InlineEditBarWindow _bar;
        private readonly IVsTextView _textViewAdapter;
        private readonly IOleCommandTarget _next;

        public InlineEditCommandFilter(InlineEditBarWindow bar, IVsTextView textViewAdapter)
        {
            _bar = bar ?? throw new ArgumentNullException(nameof(bar));
            _textViewAdapter = textViewAdapter ?? throw new ArgumentNullException(nameof(textViewAdapter));
            _textViewAdapter.AddCommandFilter(this, out _next!);
        }

        public void Dispose()
        {
            try
            {
                _textViewAdapter.RemoveCommandFilter(this);
            }
            catch
            {
                // The view may already be closing.
            }
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (_bar.IsActive && pguidCmdGroup == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)nCmdID)
                {
                    case VSConstants.VSStd2KCmdID.RETURN:
                        _bar.Submit();
                        return VSConstants.S_OK;
                    case VSConstants.VSStd2KCmdID.CANCEL:
                        _bar.Cancel();
                        return VSConstants.S_OK;
                    case VSConstants.VSStd2KCmdID.BACKSPACE:
                        _bar.Backspace();
                        return VSConstants.S_OK;
                    case VSConstants.VSStd2KCmdID.DELETE:
                        _bar.DeleteForward();
                        return VSConstants.S_OK;
                }
            }

            return _next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (_bar.IsActive && pguidCmdGroup == VSConstants.VSStd2K)
            {
                for (var i = 0; i < cCmds; i++)
                {
                    switch ((VSConstants.VSStd2KCmdID)prgCmds[i].cmdID)
                    {
                        case VSConstants.VSStd2KCmdID.RETURN:
                        case VSConstants.VSStd2KCmdID.CANCEL:
                        case VSConstants.VSStd2KCmdID.BACKSPACE:
                        case VSConstants.VSStd2KCmdID.DELETE:
                            prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                            return VSConstants.S_OK;
                    }
                }
            }

            return _next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }
    }
}
