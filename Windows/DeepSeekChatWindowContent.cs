using Microsoft.VisualStudio.Extensibility.UI;

namespace DeepSeek_v4_for_VisualStudio.Windows
{
    /// <summary>
    /// A remote user control to use as tool window UI content.
    /// </summary>
    internal class DeepSeekChatWindowContent : RemoteUserControl
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DeepSeekChatWindowContent" /> class.
        /// </summary>
        public DeepSeekChatWindowContent(DeepSeekChatWindowData dataContext) : base(dataContext)
        {
        }
        public override async Task ControlLoadedAsync(CancellationToken cancellationToken)
        {
            await base.ControlLoadedAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // DataContext 由 ToolWindow 负责 dispose
            }
            base.Dispose(disposing);
        }
    }
}
