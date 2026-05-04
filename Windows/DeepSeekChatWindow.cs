using DeepSeek_v4_for_VisualStudio.Models;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Windows
{
    /// <summary>
    /// A sample tool window.
    /// </summary>
    [VisualStudioContribution]
    public class DeepSeekChatWindow : ToolWindow
    {

        /// <summary>
        /// Initializes a new instance of the <see cref="DeepSeekChatWindow" /> class.
        /// </summary>
        private readonly DeepSeekChatWindowData _dataContext;

        public DeepSeekChatWindow(VisualStudioExtensibility extensibility)
            : base(extensibility)
        {
            this.Title = "DeepSeek Chat";
            this._dataContext = new DeepSeekChatWindowData(this.Extensibility);
        }

        /// <inheritdoc />
        public override ToolWindowConfiguration ToolWindowConfiguration => new()
        {
            // Use this object initializer to set optional parameters for the tool window.
            Placement = ToolWindowPlacement.Floating,
            DockDirection = Dock.Right,
            AllowAutoCreation = true,
        };

        /// <inheritdoc />
        public override async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await base.InitializeAsync(cancellationToken);

            // 解析当前解决方案路径，按项目隔离对话
            await _dataContext.ResolveSolutionPathAsync(cancellationToken);

            // 尝试加载该项目的对话历史
            _dataContext.LoadConversation();

            // 如果没有历史消息，显示欢迎语
            if (_dataContext.Messages.Count == 0)
            {
                _dataContext.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "你好！我是 DeepSeek Chat，你的 AI 编程助手。\n\n我可以帮你：\n- 解释代码\n- 修复 Bug\n- 重构代码\n- 生成测试\n- 回答技术问题\n\n开始提问吧！",
                    Timestamp = DateTime.Now
                });
            }
        }

        /// <inheritdoc />
        public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IRemoteUserControl>(new DeepSeekChatWindowContent(_dataContext));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dataContext.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
