using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Telemetry;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Telemetry
{
    /// <summary>
    /// AgentMetricsCollector 单元测试（P0 可观测性）。
    /// 通过 ExportDirectoryOverride 将导出重定向到临时目录。
    /// </summary>
    public class AgentMetricsCollectorTests : IDisposable
    {
        private readonly string _tempDir;

        public AgentMetricsCollectorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "deepseek-telemetry-tests", Guid.NewGuid().ToString("N"));
            AgentMetricsCollector.ExportDirectoryOverride = _tempDir;
        }

        public void Dispose()
        {
            AgentMetricsCollector.ExportDirectoryOverride = null;
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }

        // ──────────────── 轮次指标 ────────────────

        [Fact]
        public void BeginTurn_EndTurn_RecordsTokensAndDuration()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("deepseek-v4-pro", "Ask", "hello");
            c.BeginTurn(1);
            c.EndTurn(1, promptTokens: 1000, completionTokens: 200, cacheHitTokens: 800, cacheMissTokens: 200);

            var json = c.BuildJson();
            var session = JsonSerializer.Deserialize<AgentSessionMetrics>(json);

            session.Should().NotBeNull();
            session!.TurnCount.Should().Be(1);
            session.Turns[0].Turn.Should().Be(1);
            session.Turns[0].InputTokens.Should().Be(1000);
            session.Turns[0].OutputTokens.Should().Be(200);
            session.InputTokens.Should().Be(1000);
            session.OutputTokens.Should().Be(200);
            session.DurationMs.Should().BeGreaterOrEqualTo(0);
        }

        [Fact]
        public void RecordFirstToken_SetsTtftOnCurrentTurn_OnlyOnce()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.BeginTurn(1);
            c.RecordFirstToken();
            c.RecordFirstToken(); // 第二次应被忽略（首轮唯一）
            c.RecordFirstToken();
            c.EndTurn(1, 10, 10, 0, 0);

            var s = Deserialize(c);
            s!.Turns.Should().ContainSingle();
            s.Turns[0].TtftMs.Should().NotBeNull();
            s.FirstTurnTtftMs.Should().Be(s.Turns[0].TtftMs);
        }

        [Fact]
        public void RecordFirstToken_BeforeBeginTurn_IsIgnored()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            // 未 BeginTurn，直接回调不应抛异常也不产生轮次
            var act = () => c.RecordFirstToken();
            act.Should().NotThrow();
            c.BuildJson().Should().Contain("\"turn_count\": 0");
        }

        [Fact]
        public void EndTurn_WithoutBeginTurn_CreatesImplicitTurn()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.EndTurn(1, 500, 50, 400, 100);

            var s = Deserialize(c);
            s!.TurnCount.Should().Be(1);
            s.Turns[0].InputTokens.Should().Be(500);
        }

        [Fact]
        public void RecordStreamRetry_IncrementsCounterOnOpenTurn()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Edit", null);
            c.BeginTurn(2);
            c.RecordStreamRetry();
            c.RecordStreamRetry();
            c.EndTurn(2, 0, 0, 0, 0);

            var s = Deserialize(c);
            s!.Turns.Single(t => t.Turn == 2).StreamRetries.Should().Be(2);
        }

        // ──────────────── 工具指标 ────────────────

        [Fact]
        public void RecordToolCall_AttachesToMatchingTurn_AndTruncatesError()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Build", null);
            c.BeginTurn(1);
            c.EndTurn(1, 0, 0, 0, 0);
            c.BeginTurn(2);

            long longError = new string('x', 500).Length; // 500 chars
            c.RecordToolCall(1, "read_file", 12, success: true, null);
            c.RecordToolCall(2, "build_solution", 3000, success: false, new string('e', 500));
            c.EndTurn(2, 0, 0, 0, 0); // 关闭轮次使其进入会话快照

            var s = Deserialize(c);
            var turn1 = s!.Turns.Single(t => t.Turn == 1);
            var turn2 = s.Turns.Single(t => t.Turn == 2);

            turn1.Tools.Should().ContainSingle();
            turn1.Tools[0].ToolName.Should().Be("read_file");
            turn1.Tools[0].Success.Should().BeTrue();
            turn1.Tools[0].ErrorSnippet.Should().BeNull();

            turn2.Tools[0].Success.Should().BeFalse();
            turn2.Tools[0].ErrorSnippet!.Length.Should().BeLessThanOrEqualTo(201); // 160 + 省略号
            s.ToolCallCount.Should().Be(2);
        }

        [Fact]
        public void RecordToolCall_WithUnknownRound_CreatesImplicitTurn()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.RecordToolCall(9, "list_dir", 5, success: true, null);

            var s = Deserialize(c);
            s!.Turns.Single(t => t.Turn == 9).Tools.Should().ContainSingle();
        }

        // ──────────────── 终止原因 ────────────────

        [Theory]
        [InlineData("safety_limit")]
        [InlineData("loop_detected")]
        [InlineData("consecutive_errors")]
        [InlineData("whitelist_rejection")]
        public void MarkTerminated_SetsReasonOnLatestTurn(string reason)
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Plan", null);
            c.BeginTurn(3);
            c.MarkTerminated(reason);
            c.EndTurn(3, 0, 0, 0, 0);

            var s = Deserialize(c);
            s!.Turns.Single(t => t.Turn == 3).TerminatedReason.Should().Be(reason);
        }

        // ──────────────── 会话完成与导出 ────────────────

        [Fact]
        public void CompleteSuccess_ExportsJsonFile_AndSetsAggregates()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("deepseek-v4-pro", "Ask", "修复编译错误");
            c.SwitchAgent("Edit");
            c.BeginTurn(1);
            c.RecordFirstToken();
            c.RecordToolCall(1, "read_file", 20, true, null);
            c.EndTurn(1, 1500, 300, 1200, 300);
            c.CompleteSuccess();

            c.IsCompleted.Should().BeTrue();
            var files = Directory.GetFiles(_tempDir, "agent-session_*.json");
            files.Should().HaveCount(1);

            var s = JsonSerializer.Deserialize<AgentSessionMetrics>(File.ReadAllText(files[0]));
            s!.Result.Should().Be(AgentSessionResult.Success);
            s.FailureCategory.Should().Be(AgentFailureCategory.None);
            s.Agents.Should().Equal("Ask", "Edit");
            s.Model.Should().Be("deepseek-v4-pro");
            s.UserPromptSnippet.Should().Be("修复编译错误");
            s.CompletedAt.Should().NotBeNull();
            s.CacheHitRate.Should().BeApproximately(0.8, 0.001);
            s.ExtensionVersion.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void CompleteFailure_PreservesCategoryAndDetail()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Edit", null);
            c.CompleteFailure(AgentFailureCategory.System, "HttpRequestException: timeout");

            var file = SingleExportedFile();
            var s = JsonSerializer.Deserialize<AgentSessionMetrics>(File.ReadAllText(file));
            s!.Result.Should().Be(AgentSessionResult.Failure);
            s.FailureCategory.Should().Be(AgentFailureCategory.System);
            s.FailureDetail.Should().Contain("timeout");
        }

        [Fact]
        public void CompleteCancelled_MarksResultCancelled()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.CompleteCancelled();

            var s = JsonSerializer.Deserialize<AgentSessionMetrics>(File.ReadAllText(SingleExportedFile()));
            s!.Result.Should().Be(AgentSessionResult.Cancelled);
        }

        [Fact]
        public void Complete_IsIdempotent_ExportsSingleFile()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.CompleteSuccess();
            c.CompleteFailure(AgentFailureCategory.Model, "should be ignored");
            c.CompleteSuccess();

            Directory.GetFiles(_tempDir, "agent-session_*.json").Should().HaveCount(1);
            var s = JsonSerializer.Deserialize<AgentSessionMetrics>(File.ReadAllText(SingleExportedFile()));
            s!.Result.Should().Be(AgentSessionResult.Success);
        }

        [Fact]
        public void Writes_AfterCompletion_AreIgnoredWithoutThrowing()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.CompleteSuccess();

            var act = () =>
            {
                c.BeginTurn(1);
                c.RecordFirstToken();
                c.RecordStreamRetry();
                c.EndTurn(1, 1, 1, 1, 1);
                c.RecordToolCall(1, "read_file", 1, true, null);
                c.SwitchAgent("Edit");
                c.MarkTerminated("loop_detected");
            };
            act.Should().NotThrow();

            var s = JsonSerializer.Deserialize<AgentSessionMetrics>(File.ReadAllText(SingleExportedFile()));
            s!.TurnCount.Should().Be(0);
        }

        [Fact]
        public void SwitchAgent_DedupesConsecutiveSameAgent()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.SwitchAgent("Ask");   // 连续重复应去重
            c.SwitchAgent("Edit");
            c.SwitchAgent("Edit");  // 连续重复应去重
            c.SwitchAgent("Build");

            var s = JsonSerializer.Deserialize<AgentSessionMetrics>(c.BuildJson());
            s!.Agents.Should().Equal("Ask", "Edit", "Build");
        }

        // ──────────────── 聚合统计 ────────────────

        [Fact]
        public void CacheHitRate_IsNullWhenNoCacheableData()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.BeginTurn(1);
            c.EndTurn(1, 0, 0, 0, 0);

            var s = Deserialize(c);
            s!.CacheHitRate.Should().BeNull();
        }

        [Fact]
        public void CacheHitRate_ComputesAcrossTurns()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Edit", null);
            c.BeginTurn(1);
            c.EndTurn(1, 100, 10, 90, 10);
            c.BeginTurn(2);
            c.EndTurn(2, 100, 10, 30, 70);

            var s = Deserialize(c);
            s!.CacheHitRate.Should().BeApproximately(120.0 / 200.0, 0.001);
        }

        [Fact]
        public void UserPromptSnippet_TruncatedTo200Chars()
        {
            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", new string('u', 500));

            var s = Deserialize(c);
            s!.UserPromptSnippet!.Length.Should().BeLessThanOrEqualTo(201);
        }

        [Fact]
        public void SessionId_IsUniquePerCollector()
        {
            var a = new AgentMetricsCollector();
            var b = new AgentMetricsCollector();
            a.SessionId.Should().NotBeNullOrWhiteSpace();
            b.SessionId.Should().NotBeNullOrWhiteSpace();
            a.SessionId.Should().NotBe(b.SessionId);
        }

        // ──────────────── 导出目录清理 ────────────────

        [Fact]
        public void Export_PrunesOldSessions_KeepingNewest100()
        {
            Directory.CreateDirectory(_tempDir);
            for (int i = 0; i < 105; i++)
            {
                File.WriteAllText(
                    Path.Combine(_tempDir, $"agent-session_00000000000000_{i:D4}.json"), "{}");
            }

            var c = new AgentMetricsCollector();
            c.BeginSession("m", "Ask", null);
            c.CompleteSuccess();

            Directory.GetFiles(_tempDir, "agent-session_*.json").Should().HaveCount(100);
        }

        // ──────────────── 辅助方法 ────────────────

        private AgentSessionMetrics? Deserialize(AgentMetricsCollector c)
        {
            return JsonSerializer.Deserialize<AgentSessionMetrics>(c.BuildJson());
        }

        private string SingleExportedFile()
        {
            var files = Directory.GetFiles(_tempDir, "agent-session_*.json");
            files.Length.Should().BeGreaterThan(0);
            return files[0];
        }
    }
}
