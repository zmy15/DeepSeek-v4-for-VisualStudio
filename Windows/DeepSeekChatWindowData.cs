using DeepSeek_v4_for_VisualStudio.Models;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;
using System.Runtime.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Windows
{
    /// <summary>
    /// ViewModel for the DeepSeekChatWindowContent remote user control.
    /// </summary>
    [DataContract]
    internal class DeepSeekChatWindowData : NotifyPropertyChangedObject, IDisposable
    {
        private readonly VisualStudioExtensibility _extensibility;
        private CancellationTokenSource? _currentStreamingCts;

        /// <summary>
        /// 当前客户端上下文 — 由 Command 层在打开工具窗口时注入
        /// 用于访问编辑器、项目系统等 VS IDE 功能
        /// </summary>
        internal static IClientContext? CurrentClientContext { get; set; }

        public DeepSeekChatWindowData(VisualStudioExtensibility extensibility)
        {
            _extensibility = extensibility;

            SendCommand = new AsyncCommand(SendMessageAsync);
            ClearCommand = new AsyncCommand(ClearMessagesAsync);
            StopCommand = new AsyncCommand(StopGenerationAsync);
            CopyMessageCommand = new AsyncCommand(CopyMessageAsync);
            InsertCodeCommand = new AsyncCommand(InsertCodeToEditorAsync);
        }

        // ─── 可观察属性 ───

        private string _inputText = string.Empty;
        [DataMember]
        public string InputText
        {
            get => _inputText;
            set => SetProperty(ref _inputText, value);
        }

        private bool _isGenerating;
        [DataMember]
        public bool IsGenerating
        {
            get => _isGenerating;
            set => SetProperty(ref _isGenerating, value);
        }

        private string _statusText = string.Empty;
        [DataMember]
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>消息列表</summary>
        [DataMember]
        public ObservableList<ChatMessage> Messages { get; } = new();

        // ─── 命令 ───

        [DataMember] public AsyncCommand SendCommand { get; }
        [DataMember] public AsyncCommand ClearCommand { get; }
        [DataMember] public AsyncCommand StopCommand { get; }
        [DataMember] public AsyncCommand CopyMessageCommand { get; }
        [DataMember] public AsyncCommand InsertCodeCommand { get; }

        // ─── 公共方法 ───

        public void AddMessage(ChatMessage message)
        {
            Messages.Add(message);
        }

        // ─── 命令实现 ───

        private async Task SendMessageAsync(object? parameter, CancellationToken cancellationToken)
        {
            var userText = InputText?.Trim();
            if (string.IsNullOrEmpty(userText)) return;

            Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = userText,
                Timestamp = DateTime.Now
            });

            InputText = string.Empty;

            var assistantMessage = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                Timestamp = DateTime.Now,
                IsStreaming = true
            };
            Messages.Add(assistantMessage);

            IsGenerating = true;
            StatusText = "DeepSeek 思考中...";

            _currentStreamingCts?.Cancel();
            _currentStreamingCts = new CancellationTokenSource();

            try
            {
                await GenerateStreamingResponseAsync(assistantMessage, userText, _currentStreamingCts.Token);
            }
            catch (OperationCanceledException)
            {
                assistantMessage.Content += "\n\n*[已停止]*";
            }
            catch (Exception ex)
            {
                assistantMessage.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
            }
            finally
            {
                assistantMessage.IsStreaming = false;
                IsGenerating = false;
                StatusText = string.Empty;
                _currentStreamingCts = null;
            }
        }

        /// <summary>
        /// 流式生成响应 — 替换为实际的 DeepSeek API 调用
        /// </summary>
        private async Task GenerateStreamingResponseAsync(
            ChatMessage assistantMessage,
            string userInput,
            CancellationToken ct)
        {
            // TODO: 替换为 DeepSeek API 调用
            // var client = new DeepSeekClient(apiKey);
            // await foreach (var chunk in client.ChatStreamAsync(messages, ct))
            //     assistantMessage.Content += chunk;

            var simulatedResponse = GenerateSimulatedResponse(userInput);
            var words = simulatedResponse.Split(' ');

            foreach (var word in words)
            {
                ct.ThrowIfCancellationRequested();
                assistantMessage.Content += word + " ";
                await Task.Delay(40, ct);
            }
        }

        private static string GenerateSimulatedResponse(string input)
        {
            if (input.Contains("解释") || input.Contains("explain"))
            {
                return "这段代码定义了一个类，它包含以下主要部分：\n\n" +
                       "```csharp\npublic class Example\n{\n" +
                       "    private readonly string _name;\n" +
                       "    public Example(string name) => _name = name;\n" +
                       "    public string GetGreeting() => $\"Hello, {_name}!\";\n" +
                       "}\n```\n\n" +
                       "这是一个简单的示例类，使用了 C# 的表达式体成员语法。";
            }
            if (input.Contains("修复") || input.Contains("fix"))
            {
                return "我发现了以下问题并提供修复建议：\n\n" +
                       "**问题**: 变量未初始化就使用了。\n\n" +
                       "**修复**: 在使用前添加初始化代码。";
            }
            return $"我理解你的问题：「{input}」\n\n" +
                   "这是一个很好的问题！让我帮你分析一下...\n\n" +
                   "根据最佳实践，我建议采用以下方案：\n\n" +
                   "1. 首先，确保理解需求\n" +
                   "2. 然后，选择合适的架构模式\n" +
                   "3. 最后，编写可测试的代码\n\n" +
                   "需要我进一步展开某个方面吗？";
        }

        private Task ClearMessagesAsync(object? parameter, CancellationToken cancellationToken)
        {
            Messages.Clear();
            return Task.CompletedTask;
        }

        private Task StopGenerationAsync(object? parameter, CancellationToken cancellationToken)
        {
            _currentStreamingCts?.Cancel();
            return Task.CompletedTask;
        }

        private Task CopyMessageAsync(object? parameter, CancellationToken ct)
        {
            if (parameter is string text)
            {
                try { System.Windows.Clipboard.SetText(text); } catch { }
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 插入代码到当前活动编辑器的光标位置
        /// 使用 EditorExtensibility.EditAsync + ITextDocumentEditor.Insert
        /// </summary>
        private async Task InsertCodeToEditorAsync(object? parameter, CancellationToken ct)
        {
            if (parameter is not string code || string.IsNullOrEmpty(code)) return;

            var context = CurrentClientContext;
            if (context is null) return;

            try
            {
                // 1. 获取当前活动文本视图（只读快照）
                var textView = await _extensibility.Editor().GetActiveTextViewAsync(context, ct);
                if (textView is null) return;

                // 2. 通过 EditAsync 获取文档编辑器（可写）
                await _extensibility.Editor().EditAsync(editBatch =>
                {
                    var document = textView.Document;                      // ITextDocumentSnapshot
                    var docEditor = document.AsEditable(editBatch);       // ITextDocumentEditor
                    var caretPos = textView.Selection.Start.Offset;    // 光标位置 (int)

                    docEditor.Insert(caretPos, code);
                }, ct);
            }
            catch (OperationCanceledException)
            {
                // 用户取消了操作
            }
        }

        public void Dispose()
        {
            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            Messages.Clear();
        }
    }
}
