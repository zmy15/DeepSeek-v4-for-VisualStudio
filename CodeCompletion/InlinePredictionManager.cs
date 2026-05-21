using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// 内联预测管理器。管理单个 <see cref="IWpfTextView"/> 的幽灵文本代码补全。
    /// 使用 <see cref="GhostTextTagger"/> 渲染灰色装饰文本，
    /// 通过 <see cref="DeepSeekApiService"/> 调用 DeepSeek API 获取预测。
    /// </summary>
    internal class InlinePredictionManager
    {
        #region Constants

        private const string AUTOCOMPLETE_MARKER = "\n **AUTOCOMPLETE_HERE** \n";
        private const int MAX_CACHE_SIZE = 10;

        /// <summary>
        /// 代码补全请求的系统提示词。
        /// </summary>
        private const string COPILOT_SYSTEM_PROMPT =
            "You are a code completion assistant. Complete the code at the AUTOCOMPLETE_HERE marker. " +
            "ONLY output the completion code that replaces the marker. " +
            "Do NOT repeat the existing code. Do NOT include explanations. " +
            "Match the indentation, style and naming conventions of the surrounding code. " +
            "If you cannot determine a meaningful completion, output nothing.";

        #endregion

        #region Properties

        private readonly DeepSeekOptionsPage options;
        private readonly IWpfTextView view;
        private readonly ConcurrentDictionary<string, string> cache = new();
        private readonly DispatcherTimer typingTimer;

        private CancellationTokenSource cancellationTokenSource;
        private bool showingAutoComplete;
        private bool suppressNextSuggestion;

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化 <see cref="InlinePredictionManager"/> 实例，绑定到指定文本视图。
        /// </summary>
        /// <param name="options">扩展选项页。</param>
        /// <param name="view">要绑定的文本视图。</param>
        public InlinePredictionManager(DeepSeekOptionsPage options, IWpfTextView view)
        {
            this.options = options;
            this.view = view;

            if (!options.CopilotEnabled)
            {
                Logger.Info("[补全] Copilot 未启用，跳过初始化");
                return;
            }

            Logger.Info($"[补全] 初始化完成: 防抖间隔={options.CopilotSuggestionInterval}ms");
            typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(options.CopilotSuggestionInterval) };
            typingTimer.Tick += TypingTimer_Tick;

            this.view.TextBuffer.Changed += TextBuffer_Changed;
            this.view.Closed += OnViewClosed;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 重启打字防抖定时器，允许触发新的预测请求（如用户按 Enter 后）。
        /// </summary>
        public void RestartTimer()
        {
            if (typingTimer == null)
            {
                return;
            }

            typingTimer.Stop();
            typingTimer.Start();
        }

        /// <summary>
        /// 通知建议已被接受。当连续补全选项关闭时，
        /// 下一次缓冲区变更不会触发新的预测请求。
        /// </summary>
        public void NotifySuggestionAccepted()
        {
            if (!options.CopilotNextEditSuggestions)
            {
                suppressNextSuggestion = true;
            }
        }

        /// <summary>
        /// 将光标周围的代码发送给 DeepSeek API，
        /// 获取预测后在编辑器中显示为内联幽灵文本。
        /// </summary>
        public async Task ShowAutocompleteAsync()
        {
            try
            {
                if (showingAutoComplete)
                {
                    return;
                }

                showingAutoComplete = true;
                cancellationTokenSource?.Cancel();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = new();

                CleanCache();

                int caretPosition = view.Caret.Position.BufferPosition.Position;

                string filePath = string.Empty;
                if (view.TextDataModel.DocumentBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
                {
                    filePath = textDocument.FilePath;
                }

                string codeUp = GetCodeUpToCurrentPosition(caretPosition);
                string codeDown = GetCodeBelowCurrentPosition(caretPosition);

                string code = codeUp + AUTOCOMPLETE_MARKER + codeDown;

                string codeNormalized = NormalizeLineBreaks(code);
                codeNormalized = RemoveBlankLines(codeNormalized).Trim();

                string cacheKey = $"{filePath}:{codeNormalized}";

                if (cache.TryGetValue(cacheKey, out string cachedPrediction))
                {
                    Logger.Info("[补全] 缓存命中，直接显示");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationTokenSource.Token);

                    DisplayPrediction(cachedPrediction);
                    return;
                }

                // ── Call DeepSeek API for completion ──
                Logger.Info($"[补全] 请求 API 预测，上下文长度={code.Length}");
                string prediction = await GetPredictionFromApiAsync(code, cancellationTokenSource.Token);

                if (cancellationTokenSource.Token.IsCancellationRequested || string.IsNullOrWhiteSpace(prediction))
                {
                    Logger.Info("[补全] API 返回空或已取消");
                    return;
                }

                prediction = FormatPrediction(code, prediction);

                if (string.IsNullOrWhiteSpace(prediction))
                {
                    return;
                }

                cache[cacheKey] = prediction;

                Logger.Info($"[补全] 预测完成: 长度={prediction.Length}, 缓存条目={cache.Count}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationTokenSource.Token);

                DisplayPrediction(prediction);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[补全] 任务已取消");
            }
            catch (Exception ex)
            {
                Logger.Error($"[补全] ShowAutocompleteAsync 异常: {ex.Message}", ex);
            }
            finally
            {
                showingAutoComplete = false;
            }
        }

        #endregion

        #region Private Methods - Prediction

        /// <summary>
        /// 调用 DeepSeek API 获取代码补全预测（关闭思考模式以加速响应）。
        /// </summary>
        private async Task<string> GetPredictionFromApiAsync(string code, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(options.ApiKey))
                {
                    return null;
                }

                using var apiService = new DeepSeekApiService(options.ApiKey, options.SelectedModel);

                // Disable thinking for completions (faster response)
                apiService.ConfigureThinking(false);

                var messages = new List<ChatApiMessage>
                {
                    new() { Role = "system", Content = COPILOT_SYSTEM_PROMPT },
                    new() { Role = "user", Content = code }
                };

                // Use non-streaming for faster completion results
                string result = await apiService.CompleteAsync(messages, cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"[补全] API 预测错误: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 使用 <see cref="GhostTextTagger"/> 显示预测为幽灵文本。必须在 UI 线程调用。
        /// </summary>
        private void DisplayPrediction(string prediction)
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                int caretPosition = view.Caret.Position.BufferPosition.Position;
                tagger.SetSuggestion(prediction, caretPosition);
            }
        }

        /// <summary>
        /// Retrieves the code from the current caret position up to the start
        /// of the method or the beginning of the document.
        /// </summary>
        private string GetCodeUpToCurrentPosition(int caretPosition)
        {
            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;
            ITextSnapshotLine currentLine = snapshot.GetLineFromPosition(caretPosition);
            StringBuilder codeUpToCurrentPosition = new();

            Regex methodStartRegex = new(
                @"^\s*(public|private|protected|internal|static|\s)*\s*(void|int|string|bool|char|class|struct|[A-Za-z0-9_<>]+)\s+[A-Za-z0-9_]+\s*\(",
                RegexOptions.Compiled);

            while (currentLine.LineNumber >= 0)
            {
                string lineText = currentLine.GetText().Trim();

                codeUpToCurrentPosition.Insert(0, lineText + Environment.NewLine);

                if (methodStartRegex.IsMatch(lineText))
                {
                    break;
                }

                if (currentLine.LineNumber == 0)
                {
                    break;
                }

                currentLine = snapshot.GetLineFromLineNumber(currentLine.LineNumber - 1);
            }

            return codeUpToCurrentPosition.ToString();
        }

        /// <summary>
        /// Retrieves the code from the caret position down to the end of the
        /// current method or document.
        /// </summary>
        private string GetCodeBelowCurrentPosition(int caretPosition)
        {
            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;
            ITextSnapshotLine currentLine = snapshot.GetLineFromPosition(caretPosition);
            StringBuilder codeBelow = new();

            int startLine = currentLine.LineNumber;
            int openBraces = 0;
            bool insideMethod = false;

            for (int i = startLine; i < snapshot.LineCount; i++)
            {
                string lineText = snapshot.GetLineFromLineNumber(i).GetText();
                codeBelow.AppendLine(lineText);

                foreach (char c in lineText)
                {
                    if (c == '{')
                    {
                        openBraces++;
                        insideMethod = true;
                    }
                    else if (c == '}')
                    {
                        openBraces--;
                    }
                }

                if (insideMethod && openBraces <= 0)
                {
                    break;
                }
            }

            return codeBelow.ToString();
        }

        /// <summary>
        /// Removes the original code from the prediction while maintaining
        /// formatting and line breaks.
        /// </summary>
        private string FormatPrediction(string originalCode, string prediction)
        {
            originalCode = NormalizeLineBreaks(originalCode);
            originalCode = RemoveBlankLines(originalCode).Trim();

            prediction = prediction?.Trim() ?? string.Empty;

            // Remove code block markers if present
            prediction = Regex.Replace(prediction, @"^```[\w]*\s*", "");
            prediction = Regex.Replace(prediction, @"\s*```$", "");

            List<string> originalLines = originalCode.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
            List<string> predictionLines = prediction.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();

            // Find AUTOCOMPLETE_HERE marker and remove everything before it in prediction
            int markerIdx = originalLines.FindIndex(l => l.Contains("AUTOCOMPLETE_HERE"));
            if (markerIdx >= 0)
            {
                // Remove lines from prediction that match lines before the marker
                for (int i = 0; i < markerIdx && i < predictionLines.Count; i++)
                {
                    if (predictionLines[i].Trim() == originalLines[i].Trim())
                    {
                        predictionLines[i] = string.Empty;
                    }
                }

                // Also remove lines after the marker in original code from prediction
                for (int i = markerIdx + 1; i < originalLines.Count && i < predictionLines.Count; i++)
                {
                    if (predictionLines.Count > i && predictionLines[i].Trim() == originalLines[i].Trim())
                    {
                        predictionLines[i] = string.Empty;
                    }
                }
            }

            prediction = string.Join(Environment.NewLine, predictionLines.Where(l => !string.IsNullOrWhiteSpace(l) || l == string.Empty));
            prediction = RemoveBlankLines(prediction).Trim();

            return prediction;
        }

        #endregion

        #region Private Methods - Utility

        /// <summary>
        /// 将换行符统一为 Environment.NewLine。
        /// </summary>
        private static string NormalizeLineBreaks(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        /// <summary>
        /// 移除连续空行，最多保留两个空行。
        /// </summary>
        private static string RemoveBlankLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return Regex.Replace(text, @"(\s*\r?\n){3,}", Environment.NewLine + Environment.NewLine);
        }

        /// <summary>
        /// 缓存超限时清理最旧的条目（保留最近一半）。
        /// </summary>
        private void CleanCache()
        {
            if (cache.Count > MAX_CACHE_SIZE)
            {
                List<string> keysToRemove = cache.Keys.Take(cache.Count - (MAX_CACHE_SIZE / 2)).ToList();

                foreach (string key in keysToRemove)
                {
                    cache.TryRemove(key, out _);
                }
            }
        }

        #endregion

        #region Private Methods - Event Handlers

        /// <summary>
        /// 用户编辑缓冲区时重启打字防抖定时器，并清除当前幽灵文本。
        /// </summary>
        private void TextBuffer_Changed(object sender, TextContentChangedEventArgs e)
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                tagger.ClearSuggestion();
            }

            if (suppressNextSuggestion)
            {
                suppressNextSuggestion = false;
                return;
            }

            typingTimer?.Stop();
            typingTimer?.Start();
        }

        /// <summary>
        /// 打字防抖到期时触发：请求新的代码预测。
        /// </summary>
        private async void TypingTimer_Tick(object sender, EventArgs e)
        {
            typingTimer?.Stop();

            if (!showingAutoComplete)
            {
                Logger.Info("[补全] 防抖到期，请求预测");
                await ShowAutocompleteAsync();
            }
        }

        /// <summary>
        /// 视图关闭时清理所有资源和事件订阅。
        /// </summary>
        private void OnViewClosed(object sender, EventArgs e)
        {
            try
            {
                view.TextBuffer.Changed -= TextBuffer_Changed;
                view.Closed -= OnViewClosed;

                if (typingTimer != null)
                {
                    typingTimer.Stop();
                    typingTimer.Tick -= TypingTimer_Tick;
                }

                cancellationTokenSource?.Cancel();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null!;

                if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
                {
                    tagger.ClearSuggestion();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[补全] OnViewClosed 异常: {ex.Message}", ex);
            }
        }

        #endregion
    }
}
