using System;
using System.IO;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Benchmark;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Benchmark
{
    /// <summary>
    /// Benchmark 报告生成器单元测试（P3，序号 27/28）。
    /// </summary>
    public class BenchmarkReportGeneratorTests : IDisposable
    {
        private readonly string _dir;

        public BenchmarkReportGeneratorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ds-bench-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Summarize_ComputesRatesAndFailureBreakdown()
        {
            AgentSessionMetrics S(Func<AgentSessionMetrics, AgentSessionMetrics> f)
                => f(new AgentSessionMetrics { SessionId = Guid.NewGuid().ToString("N") });

            // TurnCount / FirstTurnTtftMs 为计算属性，通过 Turns 集合构造
            var sessions = new[]
            {
                S(s => { s.Result = AgentSessionResult.Success;
                         s.Turns.Add(new AgentTurnMetrics { Turn = 1 });
                         s.Turns.Add(new AgentTurnMetrics { Turn = 2 });
                         s.Agents.Add("Ask"); return s; }),
                S(s => { s.Result = AgentSessionResult.Success;
                         for (int t = 1; t <= 4; t++) s.Turns.Add(new AgentTurnMetrics { Turn = t });
                         s.Agents.Add("Edit"); s.TaskCategory = "compile_fix"; return s; }),
                S(s => { s.Result = AgentSessionResult.Failure; s.FailureCategory = AgentFailureCategory.Context;
                         s.Agents.Add("Edit"); s.TaskCategory = "cross_file"; return s; }),
                S(s => { s.Result = AgentSessionResult.Failure; s.FailureCategory = AgentFailureCategory.None;
                         s.Agents.Add("Ask"); return s; }),
                S(s => { s.Result = AgentSessionResult.Cancelled; s.Agents.Add("Ask"); return s; }),
            };

            var a = BenchmarkReportGenerator.Summarize(sessions);

            a.Total.Should().Be(5);
            a.Success.Should().Be(2);
            a.Failure.Should().Be(2);
            a.Cancelled.Should().Be(1);
            a.FailureContext.Should().Be(1);
            a.FailureUnlabeled.Should().Be(1);
            a.ByAgent["Ask"].Should().Be(3);
            a.ByTaskCategory["compile_fix"].Total.Should().Be(1);
            a.ByTaskCategory["compile_fix"].Success.Should().Be(1);
            a.AvgTurns.Should().BeApproximately(1.2, 0.01);
        }

        [Fact]
        public void SummarizeDirectory_SkipsCorruptFiles_AndReadsValidSessions()
        {
            File.WriteAllText(Path.Combine(_dir, "agent-session_bad.json"), "{ not json !!!");

            var good = new AgentSessionMetrics
            {
                Result = AgentSessionResult.Success,
            };
            good.Turns.Add(new AgentTurnMetrics { Turn = 1, TtftMs = 800, DurationMs = 1500 });
            File.WriteAllText(Path.Combine(_dir, "agent-session_good.json"),
                System.Text.Json.JsonSerializer.Serialize(good));

            var a = BenchmarkReportGenerator.SummarizeDirectory(_dir);

            a.Total.Should().Be(1);
            a.Success.Should().Be(1);
            a.AvgTtftMs.Should().Be(800);
        }

        [Fact]
        public void ToMarkdown_ContainsKeySections()
        {
            var sessions = new[]
            {
                new AgentSessionMetrics { Result = AgentSessionResult.Failure,
                    FailureCategory = AgentFailureCategory.Context },
            };
            var md = BenchmarkReportGenerator.ToMarkdown(BenchmarkReportGenerator.Summarize(sessions));

            md.Should().Contain("Failures by category");
            md.Should().Contain("By agent");
            md.Should().Contain("Context");
        }

        [Fact]
        public void SummarizeDirectory_MissingDir_ReturnsEmptyAggregate()
        {
            var a = BenchmarkReportGenerator.SummarizeDirectory(
                Path.Combine(_dir, "does-not-exist"));
            a.Total.Should().Be(0);
        }
    }
}
