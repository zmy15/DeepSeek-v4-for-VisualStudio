using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        private const int MAX_CACHE_SIZE = 10;
        private const int FIM_MAX_TOKENS = 256;

        #endregion

        #region Properties

        private readonly DeepSeekOptionsPage options;
        private readonly IWpfTextView view;
        private readonly ITextStructureNavigator structureNavigator;
        private readonly ConcurrentDictionary<string, string> cache = new();
        private readonly DispatcherTimer? typingTimer;

        private CancellationTokenSource? cancellationTokenSource;
        private bool showingAutoComplete;
        private bool suppressNextSuggestion;

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化 <see cref="InlinePredictionManager"/> 实例，绑定到指定文本视图。
        /// </summary>
        /// <param name="options">扩展选项页。</param>
        /// <param name="view">要绑定的文本视图。</param>
        /// <param name="structureNavigator">文本结构导航器，用于获取方法/块级上下文边界。</param>
        public InlinePredictionManager(DeepSeekOptionsPage options, IWpfTextView view, ITextStructureNavigator structureNavigator)
        {
            this.options = options;
            this.view = view;
            this.structureNavigator = structureNavigator;

            if (!options.AutoCompleteEnabled)
            {
                Logger.Info(LocalizationService.Instance["autocomplete.notEnabled"]);
                return;
            }

            Logger.Info(string.Format(LocalizationService.Instance["autocomplete.initialized"], options.AutoCompleteDelay));
            typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(options.AutoCompleteDelay) };
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
            if (!options.AutoCompleteContinueAfterAccept)
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

                string codeUpNormalized = RemoveBlankLines(NormalizeLineBreaks(codeUp)).Trim();
                string codeDownNormalized = RemoveBlankLines(NormalizeLineBreaks(codeDown)).Trim();

                string cacheKey = $"{filePath}:{codeUpNormalized}|{codeDownNormalized}";

                if (cache.TryGetValue(cacheKey, out string cachedPrediction))
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.cacheHit"]);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationTokenSource.Token);

                    DisplayPrediction(cachedPrediction);
                    return;
                }

                // ── Call DeepSeek FIM API for completion ──
                int totalContextLen = codeUpNormalized.Length + codeDownNormalized.Length;
                Logger.Info(string.Format(LocalizationService.Instance["autocomplete.requestingApi"], totalContextLen));
                string? prediction = await GetPredictionFromApiAsync(codeUpNormalized, codeDownNormalized, cancellationTokenSource.Token);

                if (cancellationTokenSource.Token.IsCancellationRequested || string.IsNullOrWhiteSpace(prediction))
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.apiEmpty"]);
                    return;
                }

                prediction = FormatPrediction(prediction!);

                if (string.IsNullOrWhiteSpace(prediction))
                {
                    return;
                }

                cache[cacheKey] = prediction;

                Logger.Info(string.Format(LocalizationService.Instance["autocomplete.predictionDone"], prediction.Length, cache.Count));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationTokenSource.Token);

                DisplayPrediction(prediction);
            }
            catch (OperationCanceledException)
            {
                Logger.Info(LocalizationService.Instance["autocomplete.taskCancelled"]);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.showAutocompleteError"], ex.Message), ex);
            }
            finally
            {
                showingAutoComplete = false;
            }
        }

        #endregion

        #region Private Methods - Prediction

        /// <summary>
        /// 调用 DeepSeek FIM（Fill-In-the-Middle）API 获取代码补全预测。
        /// 使用 beta/completions 端点，以 prompt/suffix 模式替代 chat/completions。
        /// </summary>
        private async Task<string?> GetPredictionFromApiAsync(string prompt, string suffix, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(options.ApiKey))
                {
                    return null;
                }

                using var apiService = new DeepSeekApiService(options.ApiKey, options.SelectedModel);

                // FIM 补全：temperature=0 确保确定性输出，适合代码补全
                string result = await apiService.FimCompletionAsync(
                    prompt,
                    string.IsNullOrEmpty(suffix) ? null : suffix,
                    FIM_MAX_TOKENS,
                    cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.apiError"], ex.Message), ex);
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
        /// 使用 <see cref="ITextStructureNavigator"/> 获取光标所在的结构块边界，
        /// 提取从块起始到光标位置的代码作为 FIM prompt（前缀上下文）。
        /// </summary>
        private string GetCodeUpToCurrentPosition(int caretPosition)
        {
            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;
            SnapshotSpan enclosingSpan = GetEnclosingStructureSpan(snapshot, caretPosition);
            int contextStart = enclosingSpan.Start.Position;

            if (contextStart >= caretPosition)
            {
                contextStart = Math.Max(0, caretPosition - 500); // fallback: ~500 chars above
            }

            return snapshot.GetText(contextStart, caretPosition - contextStart);
        }

        /// <summary>
        /// 使用 <see cref="ITextStructureNavigator"/> 获取光标所在的结构块边界，
        /// 提取从光标位置到块末尾的代码作为 FIM suffix（后缀上下文）。
        /// </summary>
        private string GetCodeBelowCurrentPosition(int caretPosition)
        {
            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;
            SnapshotSpan enclosingSpan = GetEnclosingStructureSpan(snapshot, caretPosition);
            int contextEnd = enclosingSpan.End.Position;

            if (contextEnd <= caretPosition)
            {
                contextEnd = Math.Min(snapshot.Length, caretPosition + 500); // fallback: ~500 chars below
            }

            return snapshot.GetText(caretPosition, contextEnd - caretPosition);
        }

        /// <summary>
        /// 使用 <see cref="ITextStructureNavigator"/> 迭代获取光标所在的结构块。
        /// 从光标位置开始，逐层向外扩展到方法/类级别（最多 5 层），
        /// 为 FIM 补全提供足够的方法级上下文。
        /// </summary>
        private SnapshotSpan GetEnclosingStructureSpan(ITextSnapshot snapshot, int caretPosition)
        {
            const int MaxNestingLevels = 5;

            var currentSpan = new SnapshotSpan(snapshot, caretPosition, 0);

            for (int level = 0; level < MaxNestingLevels; level++)
            {
                try
                {
                    SnapshotSpan enclosing = structureNavigator.GetSpanOfEnclosing(currentSpan);
                    if (enclosing.IsEmpty || enclosing == currentSpan)
                    {
                        break; // no more enclosing structure found
                    }

                    currentSpan = enclosing;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    // C++ 语言服务在代码语法不完整时可能抛出 COM 异常（E_FAIL），
                    // 此时停止向外扩展，使用已获取的上下文即可。
                    Logger.Info($"[补全] GetSpanOfEnclosing COM 异常 (level={level}): {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    // 其他异常也安全处理，不中断补全流程
                    Logger.Info($"[补全] GetSpanOfEnclosing 异常 (level={level}): {ex.Message}");
                    break;
                }
            }

            return currentSpan;
        }

        /// <summary>
        /// 清理 FIM API 返回的预测文本。
        /// FIM 端点直接返回补全内容，无需剥离前缀/后缀。
        /// </summary>
        private static string FormatPrediction(string prediction)
        {
            prediction = prediction?.Trim() ?? string.Empty;

            // Remove code block markers if present
            prediction = Regex.Replace(prediction, @"^```[\w]*\s*", "");
            prediction = Regex.Replace(prediction, @"\s*```$", "");

            return RemoveBlankLines(prediction).Trim();
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
#pragma warning disable VSTHRD100 // async void 是 WPF 事件处理器的必要模式，异常已在内部捕获
        private async void TypingTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                typingTimer?.Stop();

                if (!showingAutoComplete)
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.debounceTriggered"]);
                    await ShowAutocompleteAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.timerTickError"], ex.Message), ex);
            }
        }
#pragma warning restore VSTHRD100

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
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.viewClosedError"], ex.Message), ex);
            }
        }

        #endregion
    }
}
