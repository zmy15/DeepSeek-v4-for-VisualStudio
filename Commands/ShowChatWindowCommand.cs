using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;

namespace DeepSeek_v4_for_VisualStudio.Commands
{
    /// <summary>
    /// 在 "视图 → 其他窗口" 和 "标准工具栏" 中显示 DeepSeek Chat 工具窗口的命令。
    /// 提供两个入口点，确保用户容易找到：
    /// - cmdidShowChatWindow:        视图 → 其他窗口
    /// - cmdidShowChatWindowToolbar: 标准工具栏按钮
    /// </summary>
    internal sealed class ShowChatWindowCommand
    {
        /// <summary>
        /// 视图 → 其他窗口 命令 ID。
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// 标准工具栏按钮命令 ID。
        /// </summary>
        public const int ToolbarCommandId = 0x0101;

        /// <summary>
        /// 命令集 GUID（与 VSCommandTable.vsct 中的 guidDeepSeekCmdSet 一致）。
        /// </summary>
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        private readonly AsyncPackage _package;

        /// <summary>
        /// 初始化命令并注册两个入口点到菜单服务。
        /// </summary>
        private ShowChatWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            // 视图 → 其他窗口
            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);

            // 标准工具栏按钮
            var toolbarCommandId = new CommandID(CommandSet, ToolbarCommandId);
            var toolbarItem = new MenuCommand(Execute, toolbarCommandId);
            commandService.AddCommand(toolbarItem);
        }

        /// <summary>
        /// 命令单例。
        /// </summary>
        public static ShowChatWindowCommand? Instance { get; private set; }

        /// <summary>
        /// 初始化命令（由 Package.InitializeAsync 调用）。
        /// </summary>
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            System.Diagnostics.Debug.WriteLine("[DeepSeek Cmd] InitializeAsync: starting...");
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                System.Diagnostics.Debug.WriteLine("[DeepSeek Cmd] InitializeAsync: switched to main thread");

                var rawService = await package.GetServiceAsync(typeof(IMenuCommandService));
                var commandService = rawService as OleMenuCommandService;
                if (commandService == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DeepSeek Cmd] InitializeAsync: IMenuCommandService type = {rawService?.GetType().FullName ?? "null"}, expected OleMenuCommandService");
                    throw new InvalidOperationException(
                        $"Failed to get OleMenuCommandService. Actual type: {rawService?.GetType().FullName ?? "null"}");
                }

                Instance = new ShowChatWindowCommand(package, commandService);
                System.Diagnostics.Debug.WriteLine("[DeepSeek Cmd] InitializeAsync: 2 commands registered OK");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepSeek Cmd] InitializeAsync FAILED: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[DeepSeek Cmd] InitializeAsync stack: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 点击任意入口时打开 DeepSeek Chat 工具窗口。
        /// </summary>
        private void Execute(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[DeepSeek Cmd] Execute: menu item clicked, opening tool window...");
            _ = _package.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[DeepSeek Cmd] Execute: calling ShowToolWindowAsync...");
                    await _package.ShowToolWindowAsync(
                        typeof(View.DeepSeekChatWindowPane),
                        0,
                        create: true,
                        cancellationToken: _package.DisposalToken);
                    System.Diagnostics.Debug.WriteLine("[DeepSeek Cmd] Execute: ShowToolWindowAsync completed OK");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DeepSeek Cmd] Execute FAILED: {ex.GetType().Name}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[DeepSeek Cmd] Execute stack: {ex.StackTrace}");
                    if (ex.InnerException != null)
                        System.Diagnostics.Debug.WriteLine($"[DeepSeek Cmd] Execute inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            });
        }
    }
}
