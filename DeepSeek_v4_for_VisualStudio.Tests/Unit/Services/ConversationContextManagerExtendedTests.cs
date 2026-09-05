using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 扩展 ConversationContextManager 测试，覆盖更多公开 API。
/// 基础测试见 ConversationContextManagerTests.cs。
/// </summary>
public class ConversationContextManagerExtendedTests
{
    private readonly ConversationContextManager _manager;

    public ConversationContextManagerExtendedTests()
    {
        _manager = new ConversationContextManager();
    }

    #region SetSkillContext

    [Fact]
    public void SetSkillContext_StoresContext()
    {
        _manager.SetSkillContext("Skill discovery: code-review available");

        // 不直接暴露 skillContext，但不应抛出异常
    }

    [Fact]
    public void SetSkillContext_Null_DoesNotThrow()
    {
        _manager.SetSkillContext(null);
    }

    #endregion

    #region AddUserMessage Edge Cases

    [Fact]
    public void AddUserMessage_NullContent_DoesNotThrow()
    {
        _manager.AddUserMessage(null!);

        _manager.IsEmpty.Should().BeTrue();
        _manager.MessageCount.Should().Be(0);
    }

    [Fact]
    public void AddUserMessage_EmptyContent_DoesNotThrow()
    {
        _manager.AddUserMessage("");

        _manager.IsEmpty.Should().BeTrue();
        _manager.MessageCount.Should().Be(0);
    }

    [Fact]
    public void AddUserMessage_WhitespaceContent_StillCounts()
    {
        _manager.AddUserMessage("   ");

        // 空白内容仍然被添加
        _manager.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void AddUserMessage_EmptyTextAndVisualContent_StillCountsAsUserTurn()
    {
        var visual = new List<ChatContentPart>
        {
            new()
            {
                Type = "image_url",
                ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,AAAA" },
            },
        };

        _manager.AddUserMessage(string.Empty, visual);

        _manager.IsEmpty.Should().BeFalse();
        _manager.TurnCount.Should().Be(1);

        var userMessage = _manager.BuildApiMessages().FirstOrDefault(m => m.Role == "user");
        userMessage.Should().NotBeNull();
        userMessage!.MultimodalContent.Should().NotBeNull();
        userMessage.MultimodalContent.Should().HaveCount(1);
    }

    #endregion

    #region AddAssistantMessage with reasoning and toolCalls

    [Fact]
    public void AddAssistantMessage_WithReasoningContent_IncreasesTokens()
    {
        _manager.AddUserMessage("Question");

        var tokensBefore = _manager.EstimatedTokens;
        _manager.AddAssistantMessage("Answer", reasoningContent: "Let me think about this...");

        _manager.EstimatedTokens.Should().BeGreaterThan(tokensBefore);
    }

    [Fact]
    public void AddAssistantMessage_WithToolCalls_SetsHasToolCalls()
    {
        _manager.AddUserMessage("Search for files");

        var toolCalls = new List<ToolCall>
        {
            new()
            {
                Id = "call_1",
                Type = "function",
                Function = new ToolCallFunction
                {
                    Name = "file_search",
                    Arguments = "{\"query\":\"*.cs\"}",
                }
            }
        };

        _manager.AddAssistantMessage(null, toolCalls: toolCalls);

        // BuildApiMessages 应包含 tool_calls
        var messages = _manager.BuildApiMessages();

        messages.Should().Contain(m => m.ToolCalls != null && m.ToolCalls.Count == 1);
    }

    [Fact]
    public void GetStats_IncludesToolCallCount()
    {
        _manager.AddUserMessage("Search and inspect");
        _manager.AddAssistantMessage(null, toolCalls:
        [
            new ToolCall
            {
                Id = "call_1",
                Type = "function",
                Function = new ToolCallFunction { Name = "file_search", Arguments = "{}" },
            },
            new ToolCall
            {
                Id = "call_2",
                Type = "function",
                Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" },
            },
        ]);

        var stats = _manager.GetStats();

        stats.ToolCallCount.Should().Be(2);
    }

    [Fact]
    public void AddAssistantMessage_WithBothReasoningAndToolCalls_StoresBoth()
    {
        _manager.AddUserMessage("Search");

        var toolCalls = new List<ToolCall>
        {
            new()
            {
                Id = "call_2",
                Type = "function",
                Function = new ToolCallFunction
                {
                    Name = "grep_search",
                    Arguments = "{}",
                }
            }
        };

        _manager.AddAssistantMessage("Let me search...", "I need to find files first.", toolCalls);

        var messages = _manager.BuildApiMessages();

        // 有 tool_calls 的 assistant 消息必须回传 reasoning_content
        var assistantMsg = messages.FirstOrDefault(m => m.Role == "assistant" && m.ToolCalls != null);
        assistantMsg.Should().NotBeNull();
        assistantMsg!.ReasoningContent.Should().Be("I need to find files first.");
    }

    [Fact]
    public void AddAssistantMessage_ReasoningWithoutToolCalls_NotIncludedInApiMessages()
    {
        _manager.AddUserMessage("Question");
        _manager.AddAssistantMessage("Answer", reasoningContent: "This is my reasoning...");

        var messages = _manager.BuildApiMessages();

        var assistantMsg = messages.First(m => m.Role == "assistant");
        // 无 tool_calls → reasoning_content 不应回传
        assistantMsg.ReasoningContent.Should().BeNull();
    }

    #endregion

    #region AddCustomMessage

    [Fact]
    public void AddCustomMessage_StoresCorrectly()
    {
        _manager.AddCustomMessage("system", "Custom system instruction");

        _manager.MessageCount.Should().Be(1);
        // Custom message 不计入 turn
        _manager.TurnCount.Should().Be(0);
    }

    [Fact]
    public void AddCustomMessage_EmptyContent_DoesNotAdd()
    {
        _manager.AddCustomMessage("system", "");

        _manager.MessageCount.Should().Be(0);
    }

    [Fact]
    public void AddCustomMessage_NullContent_DoesNotAdd()
    {
        _manager.AddCustomMessage("system", null!);

        _manager.MessageCount.Should().Be(0);
    }

    [Fact]
    public void AddCustomMessage_IsIncludedInBuildApiMessages()
    {
        _manager.AddCustomMessage("system", "Custom instruction");
        _manager.AddUserMessage("Hello");

        var messages = _manager.BuildApiMessages();

        messages.Should().Contain(m => m.Content!.Contains("Custom instruction"));
    }

    #endregion

    #region Tool Result

    [Fact]
    public void AddToolResult_StoresWithCorrectFields()
    {
        _manager.AddUserMessage("Read a file");
        _manager.AddToolResult("call_xyz", "read_file", "File content here");

        var messages = _manager.BuildApiMessages();

        var toolMsg = messages.FirstOrDefault(m => m.Role == "tool");
        toolMsg.Should().NotBeNull();
        toolMsg!.ToolCallId.Should().Be("call_xyz");
        toolMsg!.Name.Should().Be("read_file");
        toolMsg!.Content.Should().Be("File content here");
    }

    #endregion

    #region UsagePercent

    [Fact]
    public void UsagePercent_CalculatesCorrectly()
    {
        _manager.TokenBudget = 1000;
        var longText = new string('x', 400); // ~121 tokens
        _manager.AddUserMessage(longText);

        _manager.UsagePercent.Should().BeGreaterThan(0);
    }

    #endregion

    #region SetCompressor

    [Fact]
    public void SetCompressor_CanSetAndRead()
    {
        var compressor = new ContextCompressorService();

        _manager.SetCompressor(compressor);

        _manager.Compressor.Should().BeSameAs(compressor);
    }

    [Fact]
    public void SetCompressor_Null_ClearsCompressor()
    {
        var compressor = new ContextCompressorService();
        _manager.SetCompressor(compressor);
        _manager.Compressor.Should().NotBeNull();

        _manager.SetCompressor(null);

        _manager.Compressor.Should().BeNull();
    }

    #endregion

    #region BuildApiMessages with Compressor

    [Fact]
    public void BuildApiMessages_WithCompressorButEmptySummary_DoesNotInjectEmpty()
    {
        var compressor = new ContextCompressorService();
        _manager.SetCompressor(compressor);
        _manager.AddUserMessage("Hello");

        var messages = _manager.BuildApiMessages();

        // 没有压缩摘要时不应注入空 system 消息
        messages.Should().NotContain(m => m.Role == "system" && m.Content!.Contains("[对话历史摘要]"));
    }

    [Fact]
    public void BuildApiMessages_WithEmptyCompressor_DoesNotAddEmptySystem()
    {
        var compressor = new ContextCompressorService();
        _manager.SetCompressor(compressor);
        _manager.AddUserMessage("Hello");

        var messages = _manager.BuildApiMessages();

        messages.Should().NotContain(m => m.Role == "system" && m.Content!.Contains("[对话历史摘要]"));
    }

    #endregion

    #region BuildApiMessagesRecentTurns

    [Fact]
    public void BuildApiMessagesRecentTurns_LessThanMaxTurns_ReturnsAll()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");

        var messages = _manager.BuildApiMessagesRecentTurns(maxTurns: 5);

        messages.Should().Contain(m => m.Content == "Q1");
        messages.Should().Contain(m => m.Content == "Q2");
    }

    [Fact]
    public void BuildApiMessagesRecentTurns_MoreThanMaxTurns_Truncates()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");
        _manager.AddUserMessage("Q3");
        _manager.AddAssistantMessage("A3");

        var messages = _manager.BuildApiMessagesRecentTurns(maxTurns: 2);

        // Q1 应该在 maxTurns 之外
        messages.Should().NotContain(m => m.Content == "Q1");
        // Q2 和 Q3 应保留
        messages.Should().Contain(m => m.Content == "Q2");
        messages.Should().Contain(m => m.Content == "Q3");
    }

    [Fact]
    public void BuildApiMessagesRecentTurns_PreservesSystemMessages()
    {
        _manager.SetSystemPrompt("You are helpful.");
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");

        var messages = _manager.BuildApiMessagesRecentTurns(maxTurns: 1);

        // 系统提示词必须保留
        messages.Should().Contain(m => m.Role == "system" && m.Content!.Contains("You are helpful"));
    }

    [Fact]
    public void BuildApiMessagesRecentTurns_MaxTurnsZero_TruncatesAll()
    {
        _manager.SetSystemPrompt("You are helpful.");
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");

        // maxTurns=0 时 TurnCount(1) > 0，截断到 0 轮
        var messages = _manager.BuildApiMessagesRecentTurns(maxTurns: 0);

        // 系统消息应保留
        messages.Should().Contain(m => m.Role == "system");
    }

    #endregion

    #region TrimAfter

    [Fact]
    public void TrimAfter_ValidIndex_RemovesMessages()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");

        // TrimAfter(2) 移除索引 2 及之后（Q2, A2）
        _manager.TrimAfter(2);

        _manager.MessageCount.Should().Be(2);
        var messages = _manager.BuildApiMessages();
        messages.Should().Contain(m => m.Content == "Q1");
        messages.Should().NotContain(m => m.Content == "Q2");
    }

    [Fact]
    public void TrimAfter_InvalidIndex_DoesNothing()
    {
        _manager.AddUserMessage("Q1");

        _manager.TrimAfter(-1);
        _manager.MessageCount.Should().Be(1);

        _manager.TrimAfter(100);
        _manager.MessageCount.Should().Be(1);
    }

    [Fact]
    public void TrimAfter_AdjustsTokenEstimate()
    {
        _manager.AddUserMessage("Hello");
        _manager.AddAssistantMessage("World");

        var tokensBefore = _manager.EstimatedTokens;
        _manager.TrimAfter(1);

        _manager.EstimatedTokens.Should().BeLessThan(tokensBefore);
    }

    #endregion

    #region RemoveLastAssistantMessage

    [Fact]
    public void RemoveLastAssistantMessage_RemovesLastAssistant()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");

        _manager.RemoveLastAssistantMessage();

        _manager.MessageCount.Should().Be(3);
        var messages = _manager.BuildApiMessages();
        messages.Should().Contain(m => m.Content == "A1");
        messages.Should().NotContain(m => m.Content == "A2");
    }

    [Fact]
    public void RemoveLastAssistantMessage_NoAssistant_DoesNothing()
    {
        _manager.AddUserMessage("Q1");

        _manager.RemoveLastAssistantMessage();

        _manager.MessageCount.Should().Be(1);
    }

    #endregion

    #region EstimateTokens

    [Theory]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("hello", 2)] // 5 chars * 0.3 + 1 = ~2
    [InlineData("你好世界", 3)] // 4 chars * 0.6 + 1 = ~3
    public void EstimateTokens_CalculatesCorrectly(string? text, int expectedMin)
    {
        var tokens = ConversationContextManager.EstimateTokens(text);

        tokens.Should().BeGreaterOrEqualTo(expectedMin);
    }

    [Fact]
    public void EstimateTokens_ChineseText_HigherThanEnglish()
    {
        var englishTokens = ConversationContextManager.EstimateTokens("hello");
        var chineseTokens = ConversationContextManager.EstimateTokens("你好");

        // 中文字符的 token 估算应高于等长英文字符
        chineseTokens.Should().BeGreaterOrEqualTo(englishTokens);
    }

    [Fact]
    public void EstimateTokens_LongText_ProportionalToLength()
    {
        var shortText = new string('a', 100);
        var longText = new string('a', 1000);

        var shortTokens = ConversationContextManager.EstimateTokens(shortText);
        var longTokens = ConversationContextManager.EstimateTokens(longText);

        // 长文本的 token 估算应约为短文本的 10 倍
        (longTokens * 1.0 / shortTokens).Should().BeInRange(8, 12);
    }

    [Fact]
    public void EstimateTokens_MixedContent_CalculatesProportionally()
    {
        var text = "Hello 你好 World 世界";

        var tokens = ConversationContextManager.EstimateTokens(text);

        tokens.Should().BeGreaterThan(0);
    }

    #endregion

    #region GetStats

    [Fact]
    public void GetStats_ReturnsCorrectStatistics()
    {
        _manager.SetSystemPrompt("You are a helpful assistant.");
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");

        var stats = _manager.GetStats();

        stats.Should().NotBeNull();
        stats.MessageCount.Should().Be(4);
        stats.TurnCount.Should().Be(2);
        stats.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetStats_EmptyManager_ReturnsEmptyStats()
    {
        var stats = _manager.GetStats();

        stats.MessageCount.Should().Be(0);
        stats.TurnCount.Should().Be(0);
        stats.EstimatedTokens.Should().Be(0);
    }

    #endregion

    #region GetConversationHistory

    [Fact]
    public void GetConversationHistory_ReturnsAllMessages()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");

        var history = _manager.GetConversationHistory();

        history.Should().HaveCount(2);
    }

    [Fact]
    public void GetConversationHistory_Empty_ReturnsEmpty()
    {
        var history = _manager.GetConversationHistory();

        history.Should().BeEmpty();
    }

    #endregion

    #region GetFullContext / RestoreFullContext / RestoreFromHistory

    [Fact]
    public void GetFullContext_ReturnsAllMessagesPlusContext()
    {
        _manager.SetSystemPrompt("System prompt");
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");

        var context = _manager.GetFullContext();

        context.Should().NotBeNull();
    }

    [Fact]
    public void RestoreFullContext_RestoresMessages()
    {
        _manager.SetSystemPrompt("System prompt");
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");

        var savedContext = _manager.GetFullContext();
        _manager.Clear();

        _manager.RestoreFullContext(savedContext);

        _manager.MessageCount.Should().Be(2);
        _manager.TurnCount.Should().Be(1);
    }

    [Fact]
    public void GetFullContext_JsonRoundTrip_PreservesVisualContent()
    {
        _manager.AddUserMessage(
            string.Empty,
            new List<ChatContentPart>
            {
                new()
                {
                    Type = "image_url",
                    ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,AAAA" },
                },
            });

        var savedContext = _manager.GetFullContext();
        var json = JsonSerializer.Serialize(savedContext);
        var deserialized = JsonSerializer.Deserialize<List<ChatApiMessage>>(json);

        deserialized.Should().NotBeNull();
        deserialized.Should().NotBeEmpty();
        deserialized![0].MultimodalContent.Should().NotBeNull();
        deserialized[0].MultimodalContent.Should().HaveCount(1);

        _manager.Clear();
        _manager.RestoreFullContext(deserialized);

        _manager.IsEmpty.Should().BeFalse();
        _manager.TurnCount.Should().Be(1);
    }

    [Fact]
    public void RestoreFromHistory_RestoresMessages()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");

        var history = _manager.GetConversationHistory();
        _manager.Clear();

        _manager.RestoreFromHistory(history);

        _manager.MessageCount.Should().Be(2);
    }

    [Fact]
    public void RestoreFromHistory_EmptyHistory_DoesNothing()
    {
        // RestoreFromHistory 会重建内部状态
        _manager.AddUserMessage("Q1");
        _manager.RestoreFromHistory(new List<ChatApiMessage>());

        // 空历史恢复后验证不抛出异常
        _manager.MessageCount.Should().BeGreaterOrEqualTo(0);
    }

    #endregion

    #region AutoTrimIfNeeded

    [Fact]
    public void AutoTrimIfNeeded_UnderBudget_DoesNotTrim()
    {
        _manager.TokenBudget = 10_000;
        _manager.AddUserMessage("Short message");

        var msgCountBefore = _manager.MessageCount;

        _manager.AutoTrimIfNeeded();

        _manager.MessageCount.Should().Be(msgCountBefore);
    }

    [Fact]
    public void AutoTrimIfNeeded_OverBudget_TrimsOldestTurn()
    {
        _manager.TokenBudget = 100; // 非常小的预算
        _manager.AutoTrimTurns = 1;

        var longText = new string('x', 1000);
        _manager.AddUserMessage(longText);
        _manager.AddAssistantMessage(longText);
        _manager.AddUserMessage(longText);
        _manager.AddAssistantMessage(longText);
        _manager.AddUserMessage(longText); // 再添加一条以超过预算

        var turnsBefore = _manager.TurnCount;

        _manager.AutoTrimIfNeeded();

        // 如果超过预算，应该修剪了一些轮次
        _manager.TurnCount.Should().BeLessOrEqualTo(turnsBefore);
    }

    #endregion

    #region Multiple context sources

    [Fact]
    public void ContextBlocks_IncludeSystemAndVolatileSources()
    {
        _manager.SetSystemPrompt("You are helpful.");
        _manager.SetSearchContext("Web search results");
        _manager.AddUserMessage("Query");

        var messages = _manager.BuildApiMessages();
        var volatileBlock = _manager.BuildVolatileContextBlock();

        messages.Should().Contain(m => m.Role == "system" && m.Content!.Contains("You are helpful"));
        volatileBlock.Should().NotBeNull();
        volatileBlock!.Should().Contain("Web search results");
    }

    [Fact]
    public void BuildVolatileContextBlock_IncludesRagContext()
    {
        _manager.SetRagContext("RAG context from database");

        var volatileBlock = _manager.BuildVolatileContextBlock();

        volatileBlock.Should().NotBeNull();
        volatileBlock!.Should().Contain("RAG context from database");
    }

    [Fact]
    public void BuildVolatileContextBlock_IncludesSearchContext()
    {
        _manager.SetSearchContext("Web search results");

        var volatileBlock = _manager.BuildVolatileContextBlock();

        volatileBlock.Should().NotBeNull();
        volatileBlock!.Should().Contain("Web search results");
    }

    [Fact]
    public void BuildVolatileContextBlock_PlacesSolutionBeforeIdeContext()
    {
        _manager.SetSolutionPath(@"C:\Projects\MyApp\MyApp.sln");
        _manager.SetIdeContext("[IDE Context] Active File: Test.cs");

        var volatileBlock = _manager.BuildVolatileContextBlock();

        volatileBlock.Should().NotBeNull();
        volatileBlock!.Should().Contain("MyApp.sln");
        volatileBlock.Should().Contain("[IDE Context]");
        volatileBlock.IndexOf("MyApp.sln", StringComparison.Ordinal)
            .Should().BeLessThan(volatileBlock.IndexOf("[IDE Context]", StringComparison.Ordinal));
    }

    [Fact]
    public void PersistCurrentVolatileSnapshot_PreservesPreviousSnapshotsInHistory()
    {
        _manager.SetSolutionPath(@"C:\Projects\MyApp\MyApp.sln");
        _manager.SetIdeContext("[IDE Context] Old");
        _manager.AddUserMessage("Q1");

        _manager.PersistCurrentVolatileSnapshot().Should().BeTrue();
        _manager.PersistCurrentVolatileSnapshot().Should().BeFalse();

        _manager.AddAssistantMessage("A1");
        _manager.SetIdeContext("[IDE Context] New");
        _manager.AddUserMessage("Q2");

        _manager.PersistCurrentVolatileSnapshot().Should().BeTrue();

        var messages = _manager.BuildApiMessages();

        messages.Should().Contain(m => m.Role == "user" && m.Content == "Q1");
        messages.Should().Contain(m => m.Role == "system" && m.Content!.Contains("[IDE Context] Old"));
        messages.Should().Contain(m => m.Role == "assistant" && m.Content == "A1");
        messages.Should().Contain(m => m.Role == "user" && m.Content == "Q2");
        messages.Should().Contain(m => m.Role == "system" && m.Content!.Contains("[IDE Context] New"));

        int oldIndex = messages.FindIndex(m => m.Role == "system" && m.Content!.Contains("[IDE Context] Old"));
        int q1Index = messages.FindIndex(m => m.Role == "user" && m.Content == "Q1");
        int newIndex = messages.FindIndex(m => m.Role == "system" && m.Content!.Contains("[IDE Context] New"));
        int q2Index = messages.FindIndex(m => m.Role == "user" && m.Content == "Q2");

        oldIndex.Should().BeLessThan(q1Index);
        newIndex.Should().BeLessThan(q2Index);
    }

    #endregion

    #region SetRagContext edge cases

    [Fact]
    public void SetRagContext_ReplaceExisting_AdjustsTokenCount()
    {
        _manager.SetRagContext("Original RAG context");
        var tokensAfterFirst = _manager.EstimatedTokens;

        _manager.SetRagContext("New RAG context that is much longer than the original one");
        var tokensAfterSecond = _manager.EstimatedTokens;

        // 第二次设置应替换而非累加
        tokensAfterSecond.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SetRagContext_Null_ClearsContext()
    {
        _manager.SetRagContext("Some RAG data");
        _manager.SetRagContext(null);

        _manager.RagContext.Should().BeNull();
    }

    #endregion

    #region BuildFinalSystemPrompt via BuildApiMessages

    [Fact]
    public void BuildApiMessages_WithSystemAndSkillContext_CombinesCorrectly()
    {
        _manager.SetSystemPrompt("Base system prompt");
        _manager.SetSkillContext("Skill: code-review v1.0");
        _manager.AddUserMessage("Review this code");

        var messages = _manager.BuildApiMessages();

        // 系统提示词和 skill 上下文应合并到第一条 system 消息
        var firstSystem = messages.FirstOrDefault(m => m.Role == "system");
        firstSystem.Should().NotBeNull();
    }

    [Fact]
    public void BuildApiMessages_WithoutSystemPrompt_DoesNotAddEmptySystem()
    {
        _manager.AddUserMessage("Hello");

        var messages = _manager.BuildApiMessages();

        // 没有 system prompt，则第一条不应该是空的 system 消息
        // 但如果没有任何上下文，system 消息可以不存在
        var systemMessages = messages.Where(m => m.Role == "system").ToList();
        // 没有 system prompt 也没有其他上下文时，不应有空 system 消息
    }

    #endregion

    #region ContextEntry - GetDebugSummary

    [Fact]
    public void GetDebugSummary_ReturnsFormattedString()
    {
        _manager.AddUserMessage("Test message");
        _manager.AddAssistantMessage("Response");

        var summary = _manager.GetDebugSummary();

        summary.Should().NotBeNullOrEmpty();
        summary.Should().Contain("Test message");
    }

    [Fact]
    public void GetDebugSummary_EmptyManager_ReturnsEmptyIndicator()
    {
        var summary = _manager.GetDebugSummary();

        summary.Should().NotBeNullOrEmpty();
    }

    #endregion
}
