using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services.Telemetry
{
    /// <summary>
    /// 会话级指标采集器（P0 可观测性）。
    ///
    /// 设计原则：
    /// - 非侵入：View 创建并挂到 <see cref="AgentContext"/>，BaseAgent 通过 Context?.Metrics 写入；
    /// - 零依赖：不接数据库、不上报网络，仅内存累积 → 完成时导出单个 JSON 文件；
    /// - 永不抛出：所有采集/导出失败只记日志，绝不影响 Agent 主流程。
    ///
    /// 数据流：
    ///   RunAgentWorkflowAsync 创建 → BaseAgent 工具循环写入逐轮指标 → 完成时导出 JSON。
    /// </summary>
    public sealed class AgentMetricsCollector
    {
        private readonly object _lock = new();
        private readonly AgentSessionMetrics _session = new();
        private readonly Stopwatch _sessionClock = Stopwatch.StartNew();

        private AgentTurnMetrics? _openTurn;
        private long _requestStartTicks;
        private bool _firstTokenSeen;
        private bool _completed;

        /// <summary>会话是否已完成（完成后不再接受写入）</summary>
        public bool IsCompleted { get { lock (_lock) return _completed; } }

        /// <summary>会话 ID</summary>
        public string SessionId { get { lock (_lock) return _session.SessionId; } }

        /// <summary>导出目录覆盖（仅测试用；null = 默认 %LocalAppData%\DeepSeekVS\telemetry）</summary>
        internal static string? ExportDirectoryOverride;

        public AgentMetricsCollector()
        {
            _session.SessionId = NewSessionId();
            _session.StartedAt = DateTime.Now;
            _session.ExtensionVersion = typeof(AgentMetricsCollector).Assembly.GetName().Version?.ToString();
        }

        /// <summary>标记会话开始。</summary>
        public void BeginSession(string? model, string agentType, string? userPrompt,
            string? contextDebugJson = null)
        {
            try
            {
                lock (_lock)
                {
                    _session.Model = model;
                    _session.UserPromptSnippet = Truncate(userPrompt, 200);
                    _session.ContextDebug = contextDebugJson;
                    if (!string.IsNullOrEmpty(agentType))
                        _session.Agents.Add(agentType);
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] BeginSession 异常: {ex.Message}"); }
        }

        /// <summary>Handoff 链切换 Agent 时追加记录。</summary>
        public void SwitchAgent(string agentType)
        {
            try
            {
                if (string.IsNullOrEmpty(agentType)) return;
                lock (_lock)
                {
                    if (_session.Agents.LastOrDefault() != agentType)
                        _session.Agents.Add(agentType);
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] SwitchAgent 异常: {ex.Message}"); }
        }

        /// <summary>
        /// 标注 Benchmark 任务信息（仅基准运行时调用；日常会话不标注）。
        /// </summary>
        public void SetTaskInfo(string taskCategory, string? taskId = null)
        {
            try
            {
                lock (_lock)
                {
                    _session.TaskCategory = taskCategory;
                    _session.TaskId = taskId;
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] SetTaskInfo 异常: {ex.Message}"); }
        }

        /// <summary>开始一轮 LLM 请求（工具循环每轮迭代调用一次）。</summary>
        public void BeginTurn(int round)
        {
            try
            {
                lock (_lock)
                {
                    if (_completed) return;
                    _openTurn = new AgentTurnMetrics { Turn = round };
                    _requestStartTicks = Stopwatch.GetTimestamp();
                    _firstTokenSeen = false;
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] BeginTurn 异常: {ex.Message}"); }
        }

        /// <summary>收到首个 token（thinking 或 content 均算）。每轮仅首次生效。</summary>
        public void RecordFirstToken()
        {
            try
            {
                lock (_lock)
                {
                    if (_completed || _firstTokenSeen || _openTurn == null || _requestStartTicks == 0) return;
                    _firstTokenSeen = true;
                    _openTurn.TtftMs = TicksToMs(_requestStartTicks);
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] RecordFirstToken 异常: {ex.Message}"); }
        }

        /// <summary>流断点续传重试计数。</summary>
        public void RecordStreamRetry()
        {
            try
            {
                lock (_lock)
                {
                    if (_completed || _openTurn == null) return;
                    _openTurn.StreamRetries++;
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] RecordStreamRetry 异常: {ex.Message}"); }
        }

        /// <summary>
        /// 结束当前轮：记录耗时与 usage。usage 参数取自 API 返回的最后一帧；
        /// 若该轮未经过 BeginTurn（如非工具循环路径），自动补建轮次。
        /// </summary>
        public void EndTurn(int round, int promptTokens, int completionTokens,
            int cacheHitTokens, int cacheMissTokens)
        {
            try
            {
                lock (_lock)
                {
                    if (_completed) return;
                    var turn = EnsureOpenTurnLocked(round);
                    long elapsed = _requestStartTicks > 0 ? TicksToMs(_requestStartTicks) : 0;
                    turn.DurationMs = Math.Max(elapsed, turn.DurationMs);
                    turn.InputTokens = promptTokens;
                    turn.OutputTokens = completionTokens;
                    turn.CacheHitTokens = cacheHitTokens;
                    turn.CacheMissTokens = cacheMissTokens;
                    if (!_session.Turns.Contains(turn))
                        _session.Turns.Add(turn);
                    _openTurn = null;
                    _requestStartTicks = 0;
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] EndTurn 异常: {ex.Message}"); }
        }

        /// <summary>记录一次工具调用（含超时等待的总耗时）。</summary>
        public void RecordToolCall(int turn, string toolName, long durationMs, bool success, string? errorSnippet)
        {
            try
            {
                lock (_lock)
                {
                    if (_completed) return;
                    var metric = new ToolCallMetric
                    {
                        Turn = turn,
                        ToolName = toolName ?? "unknown",
                        DurationMs = durationMs,
                        Success = success,
                        ErrorSnippet = success ? null : Truncate(errorSnippet, 160),
                    };
                    var owner = _openTurn;
                    if (owner == null || owner.Turn != turn)
                        owner = _session.Turns.LastOrDefault(t => t.Turn == turn);
                    if (owner == null)
                    {
                        owner = new AgentTurnMetrics { Turn = turn };
                        _session.Turns.Add(owner);
                    }
                    owner.Tools.Add(metric);
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] RecordToolCall 异常: {ex.Message}"); }
        }

        /// <summary>标记当前/最近一轮被强制终止的原因（安全上限、循环检测等）。</summary>
        public void MarkTerminated(string reason)
        {
            try
            {
                lock (_lock)
                {
                    var target = _openTurn ?? _session.Turns.LastOrDefault();
                    if (target != null && string.IsNullOrEmpty(target.TerminatedReason))
                        target.TerminatedReason = reason;
                }
            }
            catch (Exception ex) { Logger.Warn($"[Telemetry] MarkTerminated 异常: {ex.Message}"); }
        }

        /// <summary>会话成功完成。</summary>
        public void CompleteSuccess()
        {
            CompleteCore(AgentSessionResult.Success, AgentFailureCategory.None, null);
        }

        /// <summary>会话失败完成。category=None 表示待人工标注（Benchmark 复盘时归入 Model/Context/Host）。</summary>
        public void CompleteFailure(AgentFailureCategory category = AgentFailureCategory.None, string? detail = null)
        {
            CompleteCore(AgentSessionResult.Failure, category, detail);
        }

        /// <summary>用户主动取消。</summary>
        public void CompleteCancelled()
        {
            CompleteCore(AgentSessionResult.Cancelled, AgentFailureCategory.None, null);
        }

        /// <summary>构建当前会话快照 JSON（无论是否完成均可调用，供调试）。</summary>
        public string BuildJson()
        {
            lock (_lock)
            {
                if (!_completed)
                    _session.DurationMs = _sessionClock.ElapsedMilliseconds;
                return _session.ToJson();
            }
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        private void CompleteCore(AgentSessionResult result, AgentFailureCategory category, string? detail)
        {
            string? json = null;
            string fileName = string.Empty;
            try
            {
                lock (_lock)
                {
                    if (_completed) return;
                    _completed = true;
                    _session.Result = result;
                    _session.FailureCategory = category;
                    _session.FailureDetail = detail is null ? null : Truncate(detail, 400);
                    _session.CompletedAt = DateTime.Now;
                    _session.DurationMs = _sessionClock.ElapsedMilliseconds;
                    json = _session.ToJson();
                    fileName = $"agent-session_{_session.StartedAt:yyyyMMdd_HHmmss}_{_session.SessionId}.json";
                }
                ExportJson(json, fileName);
                Logger.Info($"[Telemetry] 会话指标已导出: {fileName} ({result}, {_session.TurnCount} 轮, " +
                            $"{_session.ToolCallCount} 次工具调用)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Telemetry] 导出会话指标失败: {ex.Message}");
            }
        }

        private static void ExportJson(string json, string fileName)
        {
            string dir = ExportDirectoryOverride
                         ?? Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "DeepSeekVS", "telemetry");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), json);
            PruneOldSessions(dir, keepNewest: 100);
        }

        /// <summary>清理历史导出文件，仅保留最新 N 个，避免目录无限增长。</summary>
        private static void PruneOldSessions(string dir, int keepNewest)
        {
            try
            {
                var files = new DirectoryInfo(dir)
                    .GetFiles("agent-session_*.json")
                    .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = keepNewest; i < files.Count; i++)
                    files[i].Delete();
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        private AgentTurnMetrics EnsureOpenTurnLocked(int round)
        {
            if (_openTurn == null)
            {
                _openTurn = new AgentTurnMetrics { Turn = round };
                _requestStartTicks = _requestStartTicks == 0 ? Stopwatch.GetTimestamp() : _requestStartTicks;
            }
            return _openTurn;
        }

        private long TicksToMs(long startTicks)
        {
            return (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);
        }

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static string NewSessionId()
        {
            return DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture)
                   + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
