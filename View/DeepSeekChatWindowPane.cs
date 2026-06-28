using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 工具窗口窗格，对标共享项目 TerminalWindowTurbo。
    /// 宿主 DeepSeekChatControl (WPF UserControl with WebView2)。
    /// 实现 IVsWindowFrameNotify3 以处理窗口显示/隐藏/移动/尺寸变更事件，
    /// 修复 GitHub issue #31: Auto-Hide → Pinned 切换时 WebView2 HWND 穿透覆盖其他面板。
    /// </summary>
    [Guid("8F3A9C2D-1E5B-4F6A-9C8D-2E3F5A7B1D4E")]
    public class DeepSeekChatWindowPane : ToolWindowPane, IVsWindowFrameNotify3
    {
        /// <summary>
        /// 初始化工具窗口。
        /// </summary>
        public DeepSeekChatWindowPane() : base(null)
        {
            DiagnosticLog.Write("[DeepSeek Pane] Constructor: starting...");
            try
            {
                this.Caption = LocalizationService.Instance["chat.windowTitle"];
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: caption='{this.Caption}'");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: caption failed ({ex.GetType().Name}: {ex.Message}), using fallback");
                this.Caption = "DeepSeek Chat";
            }

            try
            {
                this.Content = new DeepSeekChatControl();
                DiagnosticLog.Write("[DeepSeek Pane] Constructor: DeepSeekChatControl created OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: DeepSeekChatControl FAILED: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    DiagnosticLog.Write($"[DeepSeek Pane] Constructor inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                throw;
            }

            // 订阅语言变更以动态更新标题
            try
            {
                LocalizationService.Instance.LanguageChanged += (_, _) =>
                {
                    this.Caption = LocalizationService.Instance["chat.windowTitle"];
                };
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: LanguageChanged subscription failed: {ex.Message}");
            }

            DiagnosticLog.Write("[DeepSeek Pane] Constructor: completed");
        }

        /// <summary>
        /// 窗口创建完成，将 Package 引用传入 UserControl。
        /// </summary>
        protected override void OnCreate()
        {
            DiagnosticLog.Write("[DeepSeek Pane] OnCreate: starting...");
            try
            {
                base.OnCreate();
                if (Content is DeepSeekChatControl control)
                {
                    control.StartControl((DeepSeek_v4_for_VisualStudioPackage)Package);
                    DiagnosticLog.Write("[DeepSeek Pane] OnCreate: StartControl completed OK");
                }
                else
                {
                    DiagnosticLog.Write($"[DeepSeek Pane] OnCreate: Content type is {Content?.GetType().FullName ?? "null"} (expected DeepSeekChatControl)");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] OnCreate FAILED: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Pane] OnCreate stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    DiagnosticLog.Write($"[DeepSeek Pane] OnCreate inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                throw;
            }
        }

        /// <summary>
        /// 窗口销毁时释放资源。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.Dispose(disposing);
        }

        #region IVsWindowFrameNotify3 — 修复 GitHub issue #31 (WPF Airspace)

        /// <summary>
        /// 窗口显示/隐藏状态变更通知。
        /// __FRAMESHOW: FRAMESHOW_WinShown=1 (可见), FRAMESHOW_WinHidden=2 / FRAMESHOW_WinClosed=0 (隐藏)。
        /// 当面板从 Auto-Hide 弹出或 Pin 住切换时，控制 WebView2 的 Visibility
        /// 以防止其 HWND 渲染层穿透到其他 VS 面板上方。
        /// </summary>
        public int OnShow(int fShow)
        {
            try
            {
                if (Content is DeepSeekChatControl control)
                {
                    // fShow == 1 → 面板可见（Pinned 或 Auto-Hide 弹出）
                    // fShow == 0/2 → 面板隐藏（Auto-Hide 缩回或关闭）
                    control.SetWebViewVisibility(fShow == 1);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] OnShow({fShow}) error: {ex.Message}");
            }
            return VSConstants.S_OK;
        }

        /// <summary>
        /// 窗口移动通知（Dock 位置变化时触发）。
        /// 当前仅记录诊断日志，无特殊处理。
        /// </summary>
        public int OnMove(int x, int y, int w, int h)
        {
            DiagnosticLog.Write($"[DeepSeek Pane] OnMove: x={x}, y={y}, w={w}, h={h}");
            return VSConstants.S_OK;
        }

        /// <summary>
        /// 窗口尺寸变更通知（拖拽调整大小时触发）。
        /// 当前仅记录诊断日志，无特殊处理。
        /// </summary>
        public int OnSize(int x, int y, int w, int h)
        {
            DiagnosticLog.Write($"[DeepSeek Pane] OnSize: x={x}, y={y}, w={w}, h={h}");
            return VSConstants.S_OK;
        }

        /// <summary>
        /// 窗口可 Dock 状态变更通知。
        /// </summary>
        public int OnDockableChange(int fDockable, int x, int y, int w, int h)
        {
            DiagnosticLog.Write($"[DeepSeek Pane] OnDockableChange: fDockable={fDockable}, x={x}, y={y}, w={w}, h={h}");
            return VSConstants.S_OK;
        }

        /// <summary>
        /// 窗口关闭通知，允许拦截或修改保存选项。
        /// </summary>
        public int OnClose(ref uint pgrfSaveOptions)
        {
            return VSConstants.S_OK;
        }

        #endregion
    }
}
