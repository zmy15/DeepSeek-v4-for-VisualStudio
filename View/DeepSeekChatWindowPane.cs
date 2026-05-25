using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 工具窗口窗格，对标共享项�?TerminalWindowTurbo�?
    /// 宿主 DeepSeekChatControl (WPF UserControl with WebView2)�?
    /// </summary>
    [Guid("8F3A9C2D-1E5B-4F6A-9C8D-2E3F5A7B1D4E")]
    public class DeepSeekChatWindowPane : ToolWindowPane
    {
        /// <summary>
        /// 初始化工具窗口�?
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

            // 订阅语言变更以动态更新标�?
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
        /// 窗口创建完成，将 Package 引用传入 UserControl�?
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
        /// 窗口销毁时释放资源�?
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
