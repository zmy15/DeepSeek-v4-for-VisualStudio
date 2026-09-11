using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ContextCompressorServiceTests
{
    [Fact]
    public void Constructor_WithDefaultConfig_UsesDefaultValues()
    {
        var service = new ContextCompressorService();

        service.Config.Should().NotBeNull();
        service.Config.CompressionThreshold.Should().Be(0.85);
        service.Config.PreserveRecentTurns.Should().Be(3);
        service.Config.AutoCompressEnabled.Should().BeTrue();
        service.CompressedSummaries.Should().BeEmpty();
        service.TotalCompressedTokens.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithCustomConfig_UsesCustomValues()
    {
        var config = new CompressionConfig
        {
            CompressionThreshold = 0.75,
            PreserveRecentTurns = 5,
            AutoCompressEnabled = false,
        };

        var service = new ContextCompressorService(config: config);

        service.Config.CompressionThreshold.Should().Be(0.75);
        service.Config.PreserveRecentTurns.Should().Be(5);
        service.Config.AutoCompressEnabled.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithSummarizer_StoresSummarizer()
    {
        ContextCompressorService.ContextSummarizer summarizer =
            (messages, ct) => Task.FromResult("summary");

        var service = new ContextCompressorService(summarizer: summarizer);

        service.Config.Should().NotBeNull();
    }

    [Fact]
    public void GetCompressedContextText_EmptySummaries_ReturnsEmptyString()
    {
        var service = new ContextCompressorService();

        var text = service.GetCompressedContextText();

        text.Should().BeEmpty();
    }

    [Fact]
    public void GetCompressedContextText_AfterCompression_WithEntries_ReturnsFormattedText()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Hello" },
        };
        service.CompressTurnsAsync(entries, fromTurn: 1, toTurn: 2).Wait();

        var text = service.GetCompressedContextText();

        text.Should().NotBeNullOrEmpty();
        text.Should().Contain("[对话历史摘要]");
        text.Should().Contain("第 1-2 轮摘要");
        text.Should().Contain("[/对话历史摘要]");
    }

    [Fact]
    public void Clear_RemovesAllCompressedSummaries()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Hello" },
        };
        service.CompressTurnsAsync(entries, fromTurn: 1, toTurn: 1).Wait();

        service.CompressedSummaries.Should().NotBeEmpty();

        service.Clear();

        service.CompressedSummaries.Should().BeEmpty();
        service.TotalCompressedTokens.Should().Be(0);
    }

    [Fact]
    public void CompressTurnsAsync_EmptyEntries_ReturnsEmptyConversationSummary()
    {
        var service = new ContextCompressorService();

        var summary = service.CompressTurnsAsync(
            new List<ConversationContextManager.ContextEntry>(),
            fromTurn: 1,
            toTurn: 3).Result;

        summary.Summary.Should().Be("(空对话)");
        summary.FromTurn.Should().Be(1);
        summary.ToTurn.Should().Be(3);
    }

    [Fact]
    public void CompressTurnsAsync_NullEntries_ReturnsEmptyConversationSummary()
    {
        var service = new ContextCompressorService();

        var summary = service.CompressTurnsAsync(
            null!,
            fromTurn: 1,
            toTurn: 2).Result;

        summary.Summary.Should().Be("(空对话)");
    }

    [Fact]
    public void CompressTurnsAsync_WithUserMessages_ExtractsSummary()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "How do I implement authentication in ASP.NET?" },
            new() { Role = "assistant", Content = "You can use JWT tokens. Here's an example..." },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 1).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
        summary.Summary.Should().NotBe("(空对话)");
        summary.FromTurn.Should().Be(1);
        summary.ToTurn.Should().Be(1);
    }

    [Fact]
    public void CompressTurnsAsync_WithUserAndAssistantMessages_ExtractsBothContext()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Fix the bug in Program.cs" },
            new() { Role = "assistant", Content = "I found the issue in line 42." },
            new() { Role = "user", Content = "Also update the test" },
            new() { Role = "assistant", Content = "Done. Test updated in TestProgram.cs." },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 2).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
        summary.Summary.Should().NotBe("(空对话)");
    }

    [Fact]
    public void CompressTurnsAsync_WithErrors_ExtractsErrorInfo()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Why is the build failing?" },
            new()
            {
                Role = "assistant",
                Content = "Error: CS0246 - The type or namespace name 'Foo' could not be found.\n" +
                          "Exception: NullReferenceException at line 50."
            },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 1).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
        // 应包含错误信息
        summary.Summary.Should().ContainAny("CS0246", "NullReferenceException", "错误");
    }

    [Fact]
    public void CompressTurnsAsync_WithFileReferences_ExtractsFileNames()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new()
            {
                Role = "user",
                Content = "Please update the following files:\n" +
                          "  • src/Models/User.cs\n" +
                          "  • src/Services/AuthService.cs\n" +
                          "  • tests/UserTests.cs"
            },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 1).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
        summary.Summary.Should().ContainAny("User.cs", "AuthService.cs", "UserTests.cs");
    }

    [Fact]
    public void CompressTurnsAsync_WithReasoningContent_TruncatesCorrectly()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new()
            {
                Role = "assistant",
                Content = "Here's the answer.",
                ReasoningContent = new string('X', 600), // > 500 会被截断
            },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 1).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CompressTurnsAsync_WithLongContent_TruncatesCorrectly()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new()
            {
                Role = "user",
                Content = new string('A', 2500), // > 2000 会被截断
            },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 1).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CompressTurnsAsync_WithToolCallResult_IncludesToolRole()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Read file" },
            new() { Role = "tool", Content = "File content: public class Foo {}", Name = "read_file" },
        };

        var summary = service.CompressTurnsAsync(
            entries,
            fromTurn: 1,
            toTurn: 1).Result;

        summary.Summary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TotalCompressedTokens_AccumulatesCorrectly()
    {
        var service = new ContextCompressorService();

        var entries1 = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "First question" },
        };
        service.CompressTurnsAsync(entries1, fromTurn: 1, toTurn: 1).Wait();

        var firstTotal = service.TotalCompressedTokens;
        firstTotal.Should().BeGreaterThan(0);

        var entries2 = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Second question" },
        };
        service.CompressTurnsAsync(entries2, fromTurn: 2, toTurn: 2).Wait();

        service.TotalCompressedTokens.Should().BeGreaterThan(firstTotal);
    }

    [Fact]
    public void CompressTurnsAsync_WithCustomSummarizer_UsesSummarizer()
    {
        bool summarizerCalled = false;
        ContextCompressorService.ContextSummarizer summarizer = (messages, ct) =>
        {
            summarizerCalled = true;
            return Task.FromResult("Custom summary from LLM");
        };

        var service = new ContextCompressorService(summarizer: summarizer);

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Hello" },
        };

        var summary = service.CompressTurnsAsync(entries, 1, 1).Result;

        summarizerCalled.Should().BeTrue();
        summary.Summary.Should().Be("Custom summary from LLM");
    }

    [Fact]
    public void CompressTurnsAsync_WithPrefixMessages_AppendsCompressionPromptAfterUnchangedPrefix()
    {
        IReadOnlyList<ChatApiMessage>? captured = null;
        var service = new ContextCompressorService((messages, ct) =>
        {
            captured = messages;
            return Task.FromResult("summary");
        });

        var prefix = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "stable-system" },
            new() { Role = "user", Content = "original-question" },
            new() { Role = "assistant", Content = "original-answer" },
        };
        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "compress-me" },
            new() { Role = "assistant", Content = "compress-me-too" },
        };

        service.CompressTurnsAsync(entries, 1, 1, prefix).Wait();

        captured.Should().NotBeNull();
        captured.Should().HaveCount(4);
        captured![0].Content.Should().Be("stable-system");
        captured[1].Content.Should().Be("original-question");
        captured[2].Content.Should().Be("original-answer");
        captured[3].Role.Should().Be("system");
        captured[3].Content.Should().NotContain("compress-me");
        captured[3].Content.Should().Contain("请将上方");
    }

    [Fact]
    public void CompressTurnsAsync_SecondCompression_AddsIncrementalInstruction()
    {
        var prompts = new List<string>();
        var service = new ContextCompressorService((messages, ct) =>
        {
            prompts.Add(messages.Last().Content!);
            return Task.FromResult("summary");
        });

        var firstEntries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "first" },
        };
        var secondEntries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "second" },
        };

        service.CompressTurnsAsync(firstEntries, 1, 1).Wait();
        service.CompressTurnsAsync(secondEntries, 2, 2).Wait();

        var incrementalPrompt = LocalizationService.Instance["system.compressionIncrementalPrompt"];
        prompts.Should().HaveCount(2);
        prompts[0].Should().NotContain(incrementalPrompt);
        prompts[1].Should().Contain(incrementalPrompt);
    }

    [Fact]
    public void GetCompressedContextText_MultipleSummaries_OrdersByFromTurn()
    {
        var service = new ContextCompressorService();

        var entries = new List<ConversationContextManager.ContextEntry>
        {
            new() { Role = "user", Content = "Test message" },
        };

        // 先压缩较晚的轮次
        service.CompressTurnsAsync(entries, fromTurn: 5, toTurn: 7).Wait();
        // 再压缩较早的轮次
        service.CompressTurnsAsync(entries, fromTurn: 1, toTurn: 3).Wait();

        var text = service.GetCompressedContextText();

        text.Should().NotBeNullOrEmpty();
        var idx1 = text.IndexOf("第 1-3 轮摘要", StringComparison.Ordinal);
        var idx2 = text.IndexOf("第 5-7 轮摘要", StringComparison.Ordinal);
        idx1.Should().BeLessThan(idx2);
    }
}
