using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Services.Benchmark
{
    /// <summary>
    /// Benchmark 报告生成器（P3，序号 27/28 的离线部分）。
    ///
    /// 读取 %LocalAppData%\DeepSeekVS\telemetry\ 下的 agent-session_*.json，
    /// 聚合为 Markdown 报告：成功率、失败三分类分布（报告 §22 ——
    /// "Benchmark 不是为了打分，而是回答为什么失败"）、轮次/工具/Token/TTFT 均值。
    ///
    /// 纯静态无 VS 依赖，可被单测与外部脚本复用。
    /// </summary>
    public static class BenchmarkReportGenerator
    {
        public sealed class Aggregate
        {
            public int Total { get; set; }
            public int Success { get; set; }
            public int Failure { get; set; }
            public int Cancelled { get; set; }

            public int FailureModel { get; set; }
            public int FailureContext { get; set; }
            public int FailureHost { get; set; }
            public int FailureSystem { get; set; }
            public int FailureUnlabeled { get; set; }

            public double AvgTurns { get; set; }
            public double AvgToolCalls { get; set; }
            public double AvgTtftMs { get; set; }
            public double AvgDurationMs { get; set; }
            public long TotalInputTokens { get; set; }
            public long TotalOutputTokens { get; set; }
            public double? AvgCacheHitRate { get; set; }

            /// <summary>按 Agent 链首元素统计</summary>
            public Dictionary<string, int> ByAgent { get; } = new();

            /// <summary>按任务类别统计（仅标注了 task_category 的会话）</summary>
            public Dictionary<string, (int Total, int Success)> ByTaskCategory { get; } = new();
        }

        /// <summary>从遥测目录汇总。损坏/无关文件静默跳过。</summary>
        public static Aggregate SummarizeDirectory(string telemetryDir)
        {
            if (!Directory.Exists(telemetryDir)) return new Aggregate();

            var sessions = new List<AgentSessionMetrics>();
            foreach (var file in Directory.GetFiles(telemetryDir, "agent-session_*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<AgentSessionMetrics>(
                        File.ReadAllText(file));
                    if (s != null) sessions.Add(s);
                }
                catch
                {
                    // 半截文件/手改坏档 → 跳过，不让单个脏数据毁掉整份报告
                }
            }
            return Summarize(sessions);
        }

        public static Aggregate Summarize(IEnumerable<AgentSessionMetrics> sessions)
        {
            var list = sessions?.ToList() ?? new List<AgentSessionMetrics>();
            var a = new Aggregate { Total = list.Count };

            long ttftSum = 0; int ttftCount = 0;
            double cacheSum = 0; int cacheCount = 0;

            foreach (var s in list)
            {
                switch (s.Result)
                {
                    case AgentSessionResult.Success: a.Success++; break;
                    case AgentSessionResult.Cancelled: a.Cancelled++; break;
                    case AgentSessionResult.Failure:
                        a.Failure++;
                        switch (s.FailureCategory)
                        {
                            case AgentFailureCategory.Model: a.FailureModel++; break;
                            case AgentFailureCategory.Context: a.FailureContext++; break;
                            case AgentFailureCategory.Host: a.FailureHost++; break;
                            case AgentFailureCategory.System: a.FailureSystem++; break;
                            default: a.FailureUnlabeled++; break;
                        }
                        break;
                }

                a.AvgTurns += s.TurnCount;
                a.AvgToolCalls += s.ToolCallCount;
                a.AvgDurationMs += s.DurationMs;
                a.TotalInputTokens += s.InputTokens;
                a.TotalOutputTokens += s.OutputTokens;
                if (s.FirstTurnTtftMs is long t) { ttftSum += t; ttftCount++; }
                if (s.CacheHitRate is double c) { cacheSum += c; cacheCount++; }

                var agentKey = s.Agents.FirstOrDefault() ?? "(none)";
                a.ByAgent.TryGetValue(agentKey, out var agentCount);
                a.ByAgent[agentKey] = agentCount + 1;

                if (!string.IsNullOrEmpty(s.TaskCategory))
                {
                    a.ByTaskCategory.TryGetValue(s.TaskCategory!, out var cur);
                    a.ByTaskCategory[s.TaskCategory!] =
                        (cur.Total + 1, cur.Success + (s.Result == AgentSessionResult.Success ? 1 : 0));
                }
            }

            int denom = Math.Max(a.Total, 1);
            a.AvgTurns /= denom;
            a.AvgToolCalls /= denom;
            a.AvgDurationMs /= denom;
            a.AvgTtftMs = ttftCount > 0 ? (double)ttftSum / ttftCount : 0;
            a.AvgCacheHitRate = cacheCount > 0 ? cacheSum / cacheCount : null;
            return a;
        }

        public static string ToMarkdown(Aggregate a, string title = "VS-Agent Benchmark Report")
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine($"Sessions: {a.Total} |  Success: {a.Success} ({Percent(a.Success, a.Total)}) | " +
                          $"Error: Failure: {a.Failure} |  Cancelled: {a.Cancelled}");
            sb.AppendLine();
            sb.AppendLine("## Failures by category");
            sb.AppendLine("| Model | Context | Host | System | Unlabeled |");
            sb.AppendLine("|------:|--------:|-----:|-------:|----------:|");
            sb.AppendLine($"| {a.FailureModel} | {a.FailureContext} | {a.FailureHost} | {a.FailureSystem} | {a.FailureUnlabeled} |");
            sb.AppendLine();
            sb.AppendLine("## Averages / totals");
            sb.AppendLine($"- Turns: {a.AvgTurns:F1} | Tool calls: {a.AvgToolCalls:F1} | " +
                          $"TTFT: {a.AvgTtftMs:F0} ms | Duration: {a.AvgDurationMs:F0} ms");
            sb.AppendLine($"- Tokens: in {a.TotalInputTokens:N0} / out {a.TotalOutputTokens:N0}" +
                          (a.AvgCacheHitRate is double c ? $" | Cache hit: {c:P1}" : ""));
            sb.AppendLine();
            sb.AppendLine("## By agent");
            foreach (var kv in a.ByAgent.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- {kv.Key}: {kv.Value}");
            if (a.ByTaskCategory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## By task category");
                sb.AppendLine("| Category | Total | Success | Rate |");
                sb.AppendLine("|----------|------:|--------:|-----:|");
                foreach (var kv in a.ByTaskCategory.OrderBy(kv => kv.Key))
                    sb.AppendLine($"| {kv.Key} | {kv.Value.Total} | {kv.Value.Success} | {Percent(kv.Value.Success, kv.Value.Total)} |");
            }
            return sb.ToString();
        }

        private static string Percent(int part, int total)
            => total > 0 ? $"{100.0 * part / total:F0}%" : "-";
    }
}
