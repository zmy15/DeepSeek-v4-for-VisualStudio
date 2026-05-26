using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class ContextModelsTests
{
    #region ContextStats

    [Fact]
    public void ContextStats_Defaults_AreSetCorrectly()
    {
        var stats = new ContextStats();

        stats.EstimatedTokens.Should().Be(0);
        stats.TokenBudget.Should().Be(900_000);
        stats.MessageCount.Should().Be(0);
        stats.TurnCount.Should().Be(0);
        stats.CompressedTurns.Should().Be(0);
        stats.SystemPromptTokens.Should().Be(0);
        stats.ToolResultTokens.Should().Be(0);
        stats.CompressedSummaryTokens.Should().Be(0);
        stats.SearchContextTokens.Should().Be(0);
    }

    [Fact]
    public void ContextStats_UsageRatio_WhenEmpty_IsZero()
    {
        var stats = new ContextStats { EstimatedTokens = 0, TokenBudget = 900_000 };

        stats.UsageRatio.Should().Be(0);
        stats.UsagePercent.Should().Be(0);
    }

    [Fact]
    public void ContextStats_UsageRatio_HalfUsed_Is0Point5()
    {
        var stats = new ContextStats { EstimatedTokens = 450_000, TokenBudget = 900_000 };

        stats.UsageRatio.Should().BeApproximately(0.5, 0.001);
        stats.UsagePercent.Should().BeApproximately(50, 0.1);
    }

    [Fact]
    public void ContextStats_UsageRatio_Full_Is1()
    {
        var stats = new ContextStats { EstimatedTokens = 900_000, TokenBudget = 900_000 };

        stats.UsageRatio.Should().Be(1.0);
        stats.UsagePercent.Should().Be(100);
    }

    [Fact]
    public void ContextStats_UsageRatio_ZeroBudget_ReturnsZero()
    {
        var stats = new ContextStats { EstimatedTokens = 100, TokenBudget = 0 };

        stats.UsageRatio.Should().Be(0);
    }

    [Fact]
    public void ContextStats_GetDetailedReport_ContainsKeyMetrics()
    {
        var stats = new ContextStats
        {
            EstimatedTokens = 5000,
            TokenBudget = 10000,
            MessageCount = 10,
            TurnCount = 5,
            CompressedTurns = 2,
        };

        var report = stats.GetDetailedReport();

        report.Should().Contain("5,000");
        report.Should().Contain("10,000");
        report.Should().Contain("50.0%");
        report.Should().Contain("10");
        report.Should().Contain("5");
        report.Should().Contain("2");
    }

    #endregion

    #region CompressedTurnSummary

    [Fact]
    public void CompressedTurnSummary_Defaults_AreSetCorrectly()
    {
        var summary = new CompressedTurnSummary();

        summary.Summary.Should().BeEmpty();
        summary.FromTurn.Should().Be(0);
        summary.ToTurn.Should().Be(0);
        summary.OriginalTokens.Should().Be(0);
        summary.CompressedTokens.Should().Be(0);
        summary.CompressedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void CompressedTurnSummary_CompressionRatio_WhenCompressed_ReturnsPositiveRatio()
    {
        var summary = new CompressedTurnSummary
        {
            OriginalTokens = 1000,
            CompressedTokens = 200,
        };

        summary.CompressionRatio.Should().BeApproximately(0.8, 0.01); // 80% 压缩
    }

    [Fact]
    public void CompressedTurnSummary_CompressionRatio_NoOriginal_ReturnsZero()
    {
        var summary = new CompressedTurnSummary { OriginalTokens = 0 };

        summary.CompressionRatio.Should().Be(0);
    }

    [Fact]
    public void CompressedTurnSummary_CompressionRatio_FullCompression_Is1()
    {
        var summary = new CompressedTurnSummary
        {
            OriginalTokens = 1000,
            CompressedTokens = 0,
        };

        summary.CompressionRatio.Should().Be(1.0); // 100% 压缩率
    }

    #endregion

    #region CompressionConfig

    [Fact]
    public void CompressionConfig_Defaults_AreSetCorrectly()
    {
        var config = new CompressionConfig();

        config.CompressionThreshold.Should().Be(0.85);
        config.AggressiveThreshold.Should().Be(0.95);
        config.PreserveRecentTurns.Should().Be(3);
        config.MinTurnsToCompress.Should().Be(2);
        config.AutoCompressEnabled.Should().BeTrue();
        config.CompressionPrompt.Should().NotBeNullOrEmpty();
        config.CompressionPrompt.Should().Contain("{0}");
    }

    [Fact]
    public void CompressionConfig_CanCustomizeAllProperties()
    {
        var config = new CompressionConfig
        {
            CompressionThreshold = 0.8,
            AggressiveThreshold = 0.9,
            PreserveRecentTurns = 5,
            MinTurnsToCompress = 3,
            AutoCompressEnabled = false,
            CompressionPrompt = "Summarize: {0}",
        };

        config.CompressionThreshold.Should().Be(0.8);
        config.AggressiveThreshold.Should().Be(0.9);
        config.PreserveRecentTurns.Should().Be(5);
        config.MinTurnsToCompress.Should().Be(3);
        config.AutoCompressEnabled.Should().BeFalse();
        config.CompressionPrompt.Should().Be("Summarize: {0}");
    }

    #endregion
}
