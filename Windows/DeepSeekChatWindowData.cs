using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Settings;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace DeepSeek_v4_for_VisualStudio.Windows
{
    [DataContract]
    internal class DeepSeekChatWindowData : NotifyPropertyChangedObject, IDisposable
    {
        private readonly VisualStudioExtensibility _extensibility;
        private DeepSeekApiService? _apiService;
        private CancellationTokenSource? _currentStreamingCts;
        private string? _solutionPath; // 当前解决方案路径，用于按项目持久化对话

        internal static IClientContext? CurrentClientContext { get; set; }

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

        [DataMember]
        public ObservableList<ChatMessage> Messages { get; } = new();

        /// <summary>
        /// 用于驱动 ListBox 自动滚动到最新消息。
        /// 每次消息添加或流式内容更新时，将此值设置为 Messages.Count - 1。
        /// 对于流式更新（同一索引内容增长），先设为 -1 再设回以强制触发 SelectionChanged → ScrollIntoView。
        /// </summary>
        private int _selectedMessageIndex = -1;
        [DataMember]
        public int SelectedMessageIndex
        {
            get => _selectedMessageIndex;
            set => SetProperty(ref _selectedMessageIndex, value);
        }

        // ─── 对话持久化 ───

        /// <summary>
        /// 尝试解析当前解决方案路径，用于按项目隔离对话记录。
        /// </summary>
        public async Task ResolveSolutionPathAsync(CancellationToken ct = default)
        {
            try
            {
                // 通过 VS Extensibility 的 Workspaces 服务获取当前解决方案路径
                var workspace = _extensibility.Workspaces();
                var solutions = await workspace.QuerySolutionAsync<ISolutionSnapshot>(
                    query => query,
                    ct);

                _solutionPath = solutions.FirstOrDefault()?.Path;

                if (!string.IsNullOrEmpty(_solutionPath))
                {
                    Logger.Info($"检测到解决方案: {_solutionPath}");
                }
                else
                {
                    Logger.Info("未检测到已打开的解决方案，使用默认存储");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("解析解决方案路径失败，使用默认存储", ex);
                _solutionPath = null;
            }
        }

        /// <summary>
        /// 从磁盘加载当前项目的历史对话。如果成功加载，消息会被添加到 Messages 列表中。
        /// 应在 Window 初始化时调用（在添加欢迎消息之前）。
        /// </summary>
        public void LoadConversation()
        {
            var loaded = ChatPersistenceService.Load(_solutionPath);
            if (loaded == null || loaded.Count == 0)
                return;

            Messages.Clear();
            _conversationHistory.Clear();

            foreach (var msg in loaded)
            {
                msg.IsStreaming = false; // 加载的消息不应处于流式状态
                Messages.Add(msg);

                // 重建 API 历史（仅 user/assistant 角色）
                if (msg.Role is "user" or "assistant")
                {
                    _conversationHistory.Add(new ChatApiMessage
                    {
                        Role = msg.Role,
                        Content = msg.Content ?? string.Empty,
                    });
                }
            }

            SelectedMessageIndex = Messages.Count - 1;
            Logger.Info($"已恢复 {loaded.Count} 条历史消息");
        }

        /// <summary>
        /// 将当前对话保存到磁盘。
        /// </summary>
        private void SaveConversation()
        {
            ChatPersistenceService.Save(_solutionPath, Messages.ToList());
        }

        /// <summary>
        /// 异步保存（用于命令处理器中）。
        /// </summary>
        private async Task SaveConversationAsync()
        {
            await ChatPersistenceService.SaveAsync(_solutionPath, Messages.ToList());
        }

        // ─── 系统提示词 ───
        private string _systemPrompt = string.Empty;
        [DataMember]
        public string SystemPrompt
        {
            get => _systemPrompt;
            set => SetProperty(ref _systemPrompt, value);
        }

        // ─── 模型与思考配置 ───
        private string _selectedModel = "deepseek-v4-pro";
        [DataMember]
        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetProperty(ref _selectedModel, value))
                {
                    _apiService?.UpdateModel(value);
                    Logger.Info($"模型切换至: {value}");
                }
            }
        }

        [DataMember]
        public List<string> AvailableModels { get; } = new()
        {
            "deepseek-v4-pro",
            "deepseek-v4-flash"
        };

        private bool _isThinkingEnabled = true;
        [DataMember]
        public bool IsThinkingEnabled
        {
            get => _isThinkingEnabled;
            set
            {
                if (SetProperty(ref _isThinkingEnabled, value))
                {
                    _apiService?.ConfigureThinking(value, _reasoningEffort);
                    Logger.Info($"深度思考: {value}, 推理强度: {_reasoningEffort}");
                }
            }
        }

        private string _reasoningEffort = "high";
        [DataMember]
        public string ReasoningEffort
        {
            get => _reasoningEffort;
            set
            {
                if (SetProperty(ref _reasoningEffort, value))
                {
                    _apiService?.ConfigureThinking(_isThinkingEnabled, value);
                    Logger.Info($"推理强度切换: {value}");
                }
            }
        }

        [DataMember]
        public List<string> ReasoningEfforts { get; } = new()
        {
            "high",
            "max"
        };

        // ─── 命令 ───
        [DataMember] public AsyncCommand SendCommand { get; }
        [DataMember] public AsyncCommand ClearCommand { get; }
        [DataMember] public AsyncCommand StopCommand { get; }
        [DataMember] public AsyncCommand CopyMessageCommand { get; }
        [DataMember] public AsyncCommand InsertCodeCommand { get; }
        [DataMember] public AsyncCommand OpenConfigCommand { get; }

        // ─── 历史记录 (API 格式) ───
        private readonly List<ChatApiMessage> _conversationHistory = new();

        public DeepSeekChatWindowData(VisualStudioExtensibility extensibility)
        {
            _extensibility = extensibility;
            Logger.Info("初始化 ViewModel");

            _ = InitializeApiServiceAsync();

            // ★ 关键修复：所有命令指定在主线程执行
            SendCommand = new AsyncCommand(SendMessageAsync);
            ClearCommand = new AsyncCommand(ClearMessagesAsync);
            StopCommand = new AsyncCommand(StopGenerationAsync);
            CopyMessageCommand = new AsyncCommand(CopyMessageAsync);
            InsertCodeCommand = new AsyncCommand(InsertCodeToEditorAsync);
            OpenConfigCommand = new AsyncCommand(OpenConfigAsync);
        }

        private async Task InitializeApiServiceAsync()
        {
            _apiService?.Dispose();
            string apiKey = await GetApiKeyFromConfigAsync();
            if (!string.IsNullOrEmpty(apiKey))
            {
                _apiService = new DeepSeekApiService(apiKey, _selectedModel);
                _apiService.ConfigureThinking(_isThinkingEnabled, _reasoningEffort);
                Logger.Info("API 服务初始化成功");
            }
            else
            {
                Logger.Error("API Key 为空，请检查配置");
            }

            // 从设置中加载系统提示词
            await LoadSystemPromptAsync();
        }

        private async Task LoadSystemPromptAsync()
        {
            try
            {
#pragma warning disable VSEXTPREVIEW_SETTINGS
                var result = await _extensibility.Settings().ReadEffectiveValueAsync(
                    DeepSeekSettings.SystemPromptSetting, CancellationToken.None);
                var prompt = result.ValueOrDefault(defaultValue: "");
                if (!string.IsNullOrWhiteSpace(prompt) && prompt != _systemPrompt)
                {
                    _systemPrompt = prompt;
                    Logger.Info($"系统提示词已加载 ({_systemPrompt.Length} 字符)");
                }
#pragma warning restore VSEXTPREVIEW_SETTINGS
            }
            catch (Exception ex)
            {
                Logger.Error("加载系统提示词失败", ex);
            }
        }

        private async Task<string> GetApiKeyFromConfigAsync()
        {
#pragma warning disable VSEXTPREVIEW_SETTINGS // The settings API is currently in preview and marked as experimental
            var result = await _extensibility.Settings().ReadEffectiveValueAsync(DeepSeekSettings.ApiKeySetting, CancellationToken.None);
            return result.ValueOrDefault(defaultValue: "");
#pragma warning restore VSEXTPREVIEW_SETTINGS
        }

        // ─── 命令实现 ───
        private async Task SendMessageAsync(object? parameter, CancellationToken cancellationToken)
        {
            await InitializeApiServiceAsync(); // 热重载 API 服务

            var userText = InputText?.Trim();
            if (string.IsNullOrEmpty(userText) || _apiService == null)
            {
                Logger.Info("发送被忽略：输入为空或服务未初始化");
                return;
            }

            Logger.Info($"用户发送消息: {userText[..Math.Min(userText.Length, 50)]}...");

            // 添加用户消息
            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = userText,
                Timestamp = DateTime.Now
            };
            Messages.Add(userMsg);
            _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = userText });
            SelectedMessageIndex = Messages.Count - 1;

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
                var requestMessages = BuildRequestMessages();
                Logger.Info($"开始流式请求，模型: {_selectedModel}");

                await foreach (var chunk in _apiService.ChatStreamAsync(requestMessages, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        // 思考内容可单独处理，这里仅记录
                        StatusText = "DeepSeek 思考中...";
                    }
                    else
                    {
                        // 因为命令在主线程执行，这里可以直接更新 UI 属性
                        assistantMessage.Content += chunk;
                        StatusText = "DeepSeek 回复中...";
                        // 流式更新时强制 ListBox 重新滚动到最新消息
                        // 先设为 -1 再设回以触发 SelectionChanged → ScrollIntoView
                        var lastIdx = Messages.Count - 1;
                        SelectedMessageIndex = -1;
                        SelectedMessageIndex = lastIdx;
                    }
                }

                Logger.Info($"流式回复完成，总长度: {assistantMessage.Content.Length} 字符");
                Logger.Info($"内容为: {assistantMessage.Content}");

                _conversationHistory.Add(new ChatApiMessage
                {
                    Role = "assistant",
                    Content = assistantMessage.Content
                });

                // 对话完成后自动保存
                SaveConversation();
            }
            catch (OperationCanceledException)
            {
                Logger.Info("用户停止了生成");
                assistantMessage.Content += "\n\n*[已停止]*";
                SaveConversation();
            }
            catch (Exception ex)
            {
                Logger.Error("API 调用出错", ex);
                assistantMessage.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                SaveConversation();
            }
            finally
            {
                assistantMessage.IsStreaming = false;
                IsGenerating = false;
                StatusText = string.Empty;
                _currentStreamingCts = null;
            }
        }

        private List<ChatApiMessage> BuildRequestMessages()
        {
            var messages = new List<ChatApiMessage>();

            // 系统提示词：定义 AI 的行为角色，始终放在消息列表最前面
            if (!string.IsNullOrWhiteSpace(_systemPrompt))
            {
                messages.Add(new ChatApiMessage
                {
                    Role = "system",
                    Content = _systemPrompt
                });
            }

            // 追加对话历史
            messages.AddRange(_conversationHistory.Select(m => new ChatApiMessage
            {
                Role = m.Role,
                Content = m.Content
            }));

            return messages;
        }

        private Task ClearMessagesAsync(object? parameter, CancellationToken cancellationToken)
        {
            Logger.Info("清空对话");
            Messages.Clear();
            _conversationHistory.Clear();
            ChatPersistenceService.Delete(_solutionPath);
            return Task.CompletedTask;
        }

        private Task StopGenerationAsync(object? parameter, CancellationToken cancellationToken)
        {
            Logger.Info("停止生成请求");
            _currentStreamingCts?.Cancel();
            return Task.CompletedTask;
        }

        private Task CopyMessageAsync(object? parameter, CancellationToken ct)
        {
            if (parameter is string text)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    Logger.Info("消息已复制到剪贴板");
                }
                catch (Exception ex)
                {
                    Logger.Error("复制失败", ex);
                }
            }
            return Task.CompletedTask;
        }

        private async Task InsertCodeToEditorAsync(object? parameter, CancellationToken ct)
        {
            if (parameter is not string code || string.IsNullOrEmpty(code)) return;
            var context = CurrentClientContext;
            if (context is null)
            {
                Logger.Error("ClientContext 为空，无法插入代码");
                return;
            }

            try
            {
                var textView = await _extensibility.Editor().GetActiveTextViewAsync(context, ct);
                if (textView is null) return;

                await _extensibility.Editor().EditAsync(editBatch =>
                {
                    var document = textView.Document;
                    var docEditor = document.AsEditable(editBatch);
                    var caretPos = textView.Selection.Start.Offset;
                    docEditor.Insert(caretPos, code);
                }, ct);

                Logger.Info("代码插入编辑器成功");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error("插入代码失败", ex);
            }
        }

        private async Task OpenConfigAsync(object? parameter, CancellationToken cancellationToken)
        {
            Logger.Info("打开配置页面");
            await _extensibility.Shell().ShowPromptAsync("Please configure the API Key in Tools -> Options -> DeepSeek Settings", PromptOptions.OK, cancellationToken);
        }

        public void Dispose()
        {
            Logger.Info("ViewModel 释放");

            // 释放前保存对话
            SaveConversation();

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            Messages.Clear();
            _apiService?.Dispose();
        }
    }
}