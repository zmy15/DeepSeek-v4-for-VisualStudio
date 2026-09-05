using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 旧 DialogPage 与 VS2026 Unified Settings（新版设置界面）之间的双向同步桥。
    ///
    /// 单一事实源仍是 <see cref="DeepSeekOptionsPage.Instance"/>：
    /// - 推（旧→新）：持久化装载完成后 / 旧页 OnApply 后，把非敏感子集批写到 Unified 存储，
    ///   使新 UI 显示与运行时一致的当前值；
    /// - 拉（新→旧）：订阅 Unified 存储变更，用户在新 UI 改值时回写 Instance 并触发热更新链；
    /// - ApiKey 等敏感字段永不进入本桥（云同步/导出泄漏面）。
    ///
    /// 服务获取：Unified Settings 文档注明 ISettingsManager "available as a VS service (via
    /// service SVsUnifiedSettingsManager)"；该 SVs 类型不在 NuGet SDK 程序集内，故以携带
    /// 官方接口 GUID（2f26e586-…）的占位类型作为服务键查询。服务缺失（如旧版 VS）时整桥
    /// 静默降级为 no-op，不影响既有功能。
    /// </summary>
    internal static class UnifiedSettingsSync
    {
        /// <summary>SVsUnifiedSettingsManager 服务键。实测取自 Dev18
        /// Microsoft.Internal.VisualStudio.Interop.dll。服务 GUID ≠ ISettingsManager 接口 GUID。</summary>
        private const string ManagerServiceGuidString = "E3684F31-344E-42EA-9047-B620FDC7AC25";

        private const string WriterCallerId = "DeepSeek_v4_for_VisualStudio";
        private const string CategoryPrefix = "deepseekGeneral.";

        [System.Runtime.InteropServices.ComVisible(true)]
        [System.Runtime.InteropServices.Guid(ManagerServiceGuidString)]
        private sealed class SVsUnifiedSettingsManagerPlaceholder
        {
        }

        /// <summary>moniker → 属性读写器映射（与 DeepSeekUnifiedSettings.cs 声明一一对应）。</summary>
        private static readonly (string Moniker, Func<DeepSeekOptionsPage, object?> Get, Action<DeepSeekOptionsPage, object?> Set)[] Bindings =
        {
            (CategoryPrefix + "deepseekSystemPrompt", p => p.SystemPrompt, (p, v) => p.SystemPrompt = (string?)v ?? string.Empty),
            (CategoryPrefix + "deepseekSystemPromptEn", p => p.SystemPromptEn, (p, v) => p.SystemPromptEn = (string?)v ?? string.Empty),
            (CategoryPrefix + "deepseekModel", p => p.SelectedModel, (p, v) => p.SelectedModel = (string?)v ?? "deepseek-v4-pro"),
            (CategoryPrefix + "deepseekThinking", p => p.IsThinkingEnabled, (p, v) => p.IsThinkingEnabled = (bool)v!),
            (CategoryPrefix + "deepseekReasoningEffort", p => p.ReasoningEffort, (p, v) => p.ReasoningEffort = (string?)v ?? "high"),
            (CategoryPrefix + "deepseekWebSearch", p => p.EnableWebSearch, (p, v) => p.EnableWebSearch = (bool)v!),
            (CategoryPrefix + "deepseekSearchProvider", p => p.SearchProvider, (p, v) => p.SearchProvider = (string?)v ?? "DuckDuckGo"),
            (CategoryPrefix + "deepseekShowDiffMarkers", p => p.ShowDiffMarkersInEditor, (p, v) => p.ShowDiffMarkersInEditor = (bool)v!),
            (CategoryPrefix + "deepseekOcrEngine", p => p.OcrEngine, (p, v) => p.OcrEngine = (string?)v ?? "Windows Built-in"),
            (CategoryPrefix + "deepseekAutoCompleteEnabled", p => p.AutoCompleteEnabled, (p, v) => p.AutoCompleteEnabled = (bool)v!),
            (CategoryPrefix + "deepseekAutoCompleteDelay", p => p.AutoCompleteDelay, (p, v) => p.AutoCompleteDelay = (int)v!),
            (CategoryPrefix + "deepseekAutoCompleteContinueAfterAccept", p => p.AutoCompleteContinueAfterAccept, (p, v) => p.AutoCompleteContinueAfterAccept = (bool)v!),
            (CategoryPrefix + "deepseekContextStats", p => p.ShowContextStats, (p, v) => p.ShowContextStats = (bool)v!),
            (CategoryPrefix + "deepseekIdeContext", p => p.EnableIdeContextInjection, (p, v) => p.EnableIdeContextInjection = (bool)v!),
            (CategoryPrefix + "deepseekTelemetryExport", p => p.EnableTelemetryExport, (p, v) => p.EnableTelemetryExport = (bool)v!),
            (CategoryPrefix + "deepseekAutoCompression", p => p.EnableAutoCompression, (p, v) => p.EnableAutoCompression = (bool)v!),
            (CategoryPrefix + "deepseekTokenBudget", p => p.TokenBudget, (p, v) => p.TokenBudget = (int)v!),
            (CategoryPrefix + "deepseekCompressionThreshold", p => p.CompressionThreshold, (p, v) => p.CompressionThreshold = (int)v!),
            (CategoryPrefix + "deepseekPreserveRecentTurns", p => p.PreserveRecentTurns, (p, v) => p.PreserveRecentTurns = (int)v!),
            (CategoryPrefix + "deepseekEnableRag", p => p.EnableRag, (p, v) => p.EnableRag = (bool)v!),
            (CategoryPrefix + "deepseekRagTopK", p => p.RagTopK, (p, v) => p.RagTopK = (int)v!),
            (CategoryPrefix + "deepseekLlmTimeoutSeconds", p => p.LlmTimeoutSeconds, (p, v) => p.LlmTimeoutSeconds = (int)v!),
            (CategoryPrefix + "deepseekLanguage", p => p.Language, (p, v) => p.Language = (string?)v ?? "auto"),
            (CategoryPrefix + "deepseekMaxToolCallRounds", p => p.MaxToolCallRounds, (p, v) => p.MaxToolCallRounds = (int)v!),
            (CategoryPrefix + "deepseekMaxRepeatedSameCall", p => p.MaxRepeatedSameCall, (p, v) => p.MaxRepeatedSameCall = (int)v!),
            (CategoryPrefix + "deepseekMaxConsecutiveErrors", p => p.MaxConsecutiveErrors, (p, v) => p.MaxConsecutiveErrors = (int)v!),
            (CategoryPrefix + "deepseekEnableAutoBuild", p => p.EnableAutoBuild, (p, v) => p.EnableAutoBuild = (bool)v!),
            (CategoryPrefix + "deepseekApprovalMode", p => p.ApprovalMode, (p, v) => p.ApprovalMode = (string?)v ?? "SmartBlock"),
            (CategoryPrefix + "deepseekThemeMode", p => p.ThemeModeString, (p, v) => p.ThemeModeString = (string?)v ?? "Auto"),
            (CategoryPrefix + "deepseekInputBoxHeight", p => p.InputBoxHeight, (p, v) => p.InputBoxHeight = (int)v!),
            (CategoryPrefix + "deepseekBottomAreaScalePercent", p => p.BottomAreaScalePercent, (p, v) => p.BottomAreaScalePercent = (int)v!),
            (CategoryPrefix + "deepseekWebView2ZoomPercent", p => p.WebView2ZoomPercent, (p, v) => p.WebView2ZoomPercent = (int)v!),
        };

        private static ISettingsReader? _reader;
        private static ISettingsManager? _manager;
        private static IDisposable? _subscription;
        private static volatile bool _echoSuppressing;
        private static bool _bridgeDisabled;

        /// <summary>宿主包（由 Package 初始化时注入）；未注入前推送/订阅均为 no-op。</summary>
        internal static AsyncPackage? Host { get; set; }

        /// <summary>旧页 OnApply 后调用：把页面当前值批写到 Unified 存储（fire-and-forget）。</summary>
        public static void PushFromPage(DeepSeekOptionsPage page)
        {
            var host = Host;
            if (host == null || _bridgeDisabled || _manager == null || _reader == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await PushToStoreAsync(page, host, reason: "onApply");
                }
                catch (Exception ex)
                {
                    Utils.DiagnosticLog.Write($"[USync] push(onApply) failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 初始化桥接：获取服务 → 激活 VSEXT 宿主 → 等待注册可见 → 订阅 + 首次推值。
        /// 任何一步失败仅记录诊断日志并停用桥接（fail-open，不影响主流程）；
        /// 等待注册期间不阻塞调用方（本方法由包侧 fire-and-forget 调用）。
        /// </summary>
        public static async Task InitializeAsync(IAsyncServiceProvider serviceProvider, AsyncPackage package)
        {
            if (_bridgeDisabled) return;
            try
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

                var svc = await serviceProvider.GetServiceAsync(typeof(SVsUnifiedSettingsManagerPlaceholder));
                _manager = svc as ISettingsManager;
                if (_manager == null)
                {
                    _bridgeDisabled = true;
                    Utils.DiagnosticLog.Write("[USync] ISettingsManager unavailable — bridge disabled (old page unaffected)");
                    return;
                }
                _reader = _manager.GetReader();
                Utils.DiagnosticLog.Write("[USync] ISettingsManager acquired");

                // ── 激活 VSEXT 扩展宿主（形态②）：促使 SettingCategory 声明进入引擎目录 ──
                try
                {
                    var ext = await serviceProvider.GetServiceAsync(
                        typeof(Microsoft.VisualStudio.Extensibility.VisualStudioExtensibility));
                    Utils.DiagnosticLog.Write($"[USync] extensibility host activation: {(ext != null ? "ok" : "null")}");
                }
                catch (Exception ex)
                {
                    Utils.DiagnosticLog.Write($"[USync] host activation attempt failed: {ex.Message}");
                }

                // ── 订阅先行（对未注册 moniker 订阅同样有效，变更后仍会回调）──
                var monikers = new List<string>();
                foreach (var b in Bindings) monikers.Add(b.Moniker);
                _subscription = _reader.SubscribeToChanges(
                    update => HandleStorageChange(update, package),
                    monikers.ToArray());
                Utils.DiagnosticLog.Write($"[USync] subscribed to {monikers.Count} setting monikers");

                // ── 等待注册可见（引擎目录加载可能滞后于宿主激活）──
                // 同时探测多种候选 moniker 形态，定位引擎实际使用的命名空间前缀
                bool registered = false;
                var probeVariants = new[]
                {
                    Bindings[0].Moniker,                                                    // deepseekGeneral.deepseekThinking
                    "DeepSeek_v4_for_VisualStudio." + Bindings[0].Moniker,                  // 扩展名前缀
                    "deepseekchat." + Bindings[0].Moniker,
                    Bindings[0].Moniker.Replace(CategoryPrefix, "deepseekGeneral."),
                };
                for (int i = 0; i < 24; i++)
                {
                    foreach (var variant in probeVariants.Distinct())
                    {
                        try
                        {
                            var probeReg = _reader.GetValue<object>(variant, SettingReadOptions.RequireRegistration);
                            if (probeReg.Outcome != SettingRetrievalOutcome.NotRegistered)
                            {
                                registered = true;
                                Utils.DiagnosticLog.Write($"[USync] registered form found after {i * 5}s: {variant} → {probeReg.Outcome}");
                                break;
                            }
                        }
                        catch { }
                    }
                    if (registered) break;
                    await Task.Delay(5000, package.DisposalToken);
                }
                if (!registered)
                {
                    Utils.DiagnosticLog.Write("[USync] monikers still not registered after 120s — initial push skipped (will retry on old-page apply)");
                    return; // 订阅保持；旧页 OnApply 时会再次尝试推送
                }

                // ── 首次推送：让新 UI 反映当前 Instance 值（而非声明默认值）──
                var instance = DeepSeekOptionsPage.Instance;
                if (instance != null)
                {
                    await PushToStoreAsync(instance, package, reason: "initial");
                }
            }
            catch (OperationCanceledException)
            {
                // 包卸载期间的取消属正常路径
            }
            catch (Exception ex)
            {
                _bridgeDisabled = true;
                Utils.DiagnosticLog.Write($"[USync] init failed (bridge disabled): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>把 Instance 当前值写入 Unified 存储（Enqueue 全部后一次 Commit）。</summary>
        private static async Task PushToStoreAsync(DeepSeekOptionsPage page, AsyncPackage package, string reason)
        {
            if (_bridgeDisabled || _manager == null) return;
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            // 回声抑制：本次提交引发的变更通知不做回写（窗口期覆盖通知到达延迟）
            _echoSuppressing = true;
            try
            {
                // 逐项 Enqueue+Commit：InternalError 时可精确定位问题设置项
                int okCount = 0;
                var failures = new List<string>();
                foreach (var b in Bindings)
                {
                    try
                    {
                        var value = b.Get(page);
                        var writer = _manager.GetWriter(WriterCallerId, DeepSeek_v4_for_VisualStudioPackage.PackageGuid);
                        SettingChangeResult r;
                        if (value is bool bv) r = writer.EnqueueChange(b.Moniker, bv);
                        else if (value is int iv) r = writer.EnqueueChange(b.Moniker, iv);
                        else if (value is string sv) r = writer.EnqueueChange(b.Moniker, sv);
                        else continue;

                        var c = writer.Commit("DeepSeek sync " + b.Moniker);
                        if (c.Outcome == SettingCommitOutcome.Success) okCount++;
                        else
                        {
                            failures.Add($"{b.Moniker}={c.Outcome}{(string.IsNullOrEmpty(c.Message) ? "" : "(" + c.Message + ")")}");
                            Utils.DiagnosticLog.Write($"[USync] commit failed: {b.Moniker} → {c.Outcome} {c.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{b.Moniker}=EX:{ex.Message}");
                    }
                }

                Utils.DiagnosticLog.Write(
                    $"[USync] push({reason}) ok={okCount}/{Bindings.Length}"
                    + (failures.Count > 0 ? " failures=" + string.Join("; ", failures) : ""));

                // 全量回读：RequireRegistration 区分「引擎未识别 moniker」与「已注册未持久化」
                foreach (var b in Bindings)
                {
                    try
                    {
                        var probeReg = _reader.GetValue<object>(b.Moniker, SettingReadOptions.RequireRegistration);
                        var probeAny = _reader.GetValue<object>(b.Moniker, SettingReadOptions.NoRequirements);
                        Utils.DiagnosticLog.Write(
                            $"[USync] readback {b.Moniker} reg={probeReg.Outcome} any={probeAny.Outcome}"
                            + (probeAny.Outcome == SettingRetrievalOutcome.Success ? $" value={probeAny.Value}" : ""));
                    }
                    catch { }
                }
            }
            finally
            {
                _ = Task.Delay(1500).ContinueWith(_ => _echoSuppressing = false);
            }
        }

        /// <summary>Unified 存储变更 → 读回值 → 写入 Instance → 触发热更新链。</summary>
        private static void HandleStorageChange(SettingsUpdate update, AsyncPackage package)
        {
            if (_bridgeDisabled || _reader == null) return;
            if (_echoSuppressing) return;

            try
            {
                _ = package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                    var instance = DeepSeekOptionsPage.Instance;
                    if (instance == null) return;

                    int applied = 0;
                    var changedMonikers = update.ChangedSettingMonikers;
                    foreach (var b in Bindings.Where(b => changedMonikers.Contains(b.Moniker)))
                    {
                        try
                        {
                            var retrieval = _reader.GetValue<object>(b.Moniker, SettingReadOptions.NoRequirements);
                            if (retrieval.Outcome != SettingRetrievalOutcome.Success) continue;
                            var raw = retrieval.Value;
                            if (raw is bool bv) { b.Set(instance, bv); applied++; }
                            else if (raw is int iv) { b.Set(instance, iv); applied++; }
                            else if (raw is long lv) { b.Set(instance, (int)lv); applied++; }
                            else if (raw is string sv) { b.Set(instance, sv); applied++; }
                        }
                        catch
                        {
                            // 单项读取失败跳过（值类型不符等）
                        }
                    }

                    if (applied > 0)
                    {
                        try
                        {
                            instance.SaveSettingsToStorage();
                        }
                        catch (Exception ex)
                        {
                            Utils.DiagnosticLog.Write($"[USync] save pulled values failed: {ex.Message}");
                        }

                        instance.ApplyRuntimeHotUpdates();
                        Utils.DiagnosticLog.Write($"[USync] pulled {applied} value(s) from Unified Settings → Instance (saved)");
                    }
                });
            }
            catch (Exception ex)
            {
                Utils.DiagnosticLog.Write($"[USync] pull dispatch failed: {ex.Message}");
            }
        }
    }
}
