using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;

namespace DeepSeek_v4_for_VisualStudio.Commands
{
    /// <summary>
    /// �� "��ͼ �� ��������" �� "��׼������" ����ʾ DeepSeek Chat ���ߴ��ڵ����
    /// �ṩ������ڵ㣬ȷ���û������ҵ���
    /// - cmdidShowChatWindow:        ��ͼ �� ��������
    /// - cmdidShowChatWindowToolbar: ��׼��������ť
    /// </summary>
    internal sealed class ShowChatWindowCommand
    {
        /// <summary>
        /// ��ͼ �� �������� ���� ID��
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// ��׼��������ť���� ID��
        /// </summary>
        public const int ToolbarCommandId = 0x0101;

        /// <summary>
        /// ��� GUID���� VSCommandTable.vsct �е� guidDeepSeekCmdSet һ�£���
        /// </summary>
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        private readonly AsyncPackage _package;

        /// <summary>
        /// ��ʼ�����ע��������ڵ㵽�˵�����
        /// </summary>
        private ShowChatWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            // ��ͼ �� ��������
            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);

            // ��׼��������ť
            var toolbarCommandId = new CommandID(CommandSet, ToolbarCommandId);
            var toolbarItem = new MenuCommand(Execute, toolbarCommandId);
            commandService.AddCommand(toolbarItem);
        }

        /// <summary>
        /// �������
        /// </summary>
        public static ShowChatWindowCommand? Instance { get; private set; }

        /// <summary>
        /// ��ʼ������� Package.InitializeAsync ���ã���
        /// </summary>
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            DiagnosticLog.Write("[DeepSeek Cmd] InitializeAsync: starting...");
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                DiagnosticLog.Write("[DeepSeek Cmd] InitializeAsync: switched to main thread");

                var rawService = await package.GetServiceAsync(typeof(IMenuCommandService));
                var commandService = rawService as OleMenuCommandService;
                if (commandService == null)
                {
                    DiagnosticLog.Write(
                        $"[DeepSeek Cmd] InitializeAsync: IMenuCommandService type = {rawService?.GetType().FullName ?? "null"}, expected OleMenuCommandService");
                    throw new InvalidOperationException(
                        $"Failed to get OleMenuCommandService. Actual type: {rawService?.GetType().FullName ?? "null"}");
                }

                Instance = new ShowChatWindowCommand(package, commandService);
                DiagnosticLog.Write("[DeepSeek Cmd] InitializeAsync: 2 commands registered OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Cmd] InitializeAsync FAILED: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Cmd] InitializeAsync stack: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// ����������ʱ�� DeepSeek Chat ���ߴ��ڡ�
        /// </summary>
        private void Execute(object sender, EventArgs e)
        {
            DiagnosticLog.Write("[DeepSeek Cmd] Execute: menu item clicked, opening tool window...");
            _ = _package.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    DiagnosticLog.Write("[DeepSeek Cmd] Execute: calling ShowToolWindowAsync...");
                    await _package.ShowToolWindowAsync(
                        typeof(View.DeepSeekChatWindowPane),
                        0,
                        create: true,
                        cancellationToken: _package.DisposalToken);
                    DiagnosticLog.Write("[DeepSeek Cmd] Execute: ShowToolWindowAsync completed OK");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"[DeepSeek Cmd] Execute FAILED: {ex.GetType().Name}: {ex.Message}");
                    DiagnosticLog.Write($"[DeepSeek Cmd] Execute stack: {ex.StackTrace}");
                    if (ex.InnerException != null)
                        DiagnosticLog.Write($"[DeepSeek Cmd] Execute inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            });
        }
    }
}
