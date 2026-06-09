namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ConversationContextManagerTests
{
    private readonly ConversationContextManager _manager;

    public ConversationContextManagerTests()
    {
        _manager = new ConversationContextManager();
    }

    [Fact]
    public void NewManager_HasEmptyState()
    {
        _manager.IsEmpty.Should().BeTrue();
        _manager.TurnCount.Should().Be(0);
        _manager.MessageCount.Should().Be(0);
        _manager.EstimatedTokens.Should().Be(0);
    }

    [Fact]
    public void AddUserMessage_IncrementsTurnCount()
    {
        _manager.AddUserMessage("Hello");

        _manager.TurnCount.Should().Be(1);
        _manager.MessageCount.Should().Be(1);
        _manager.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void AddUserMessage_IncrementsTokenEstimate()
    {
        _manager.AddUserMessage("Hello, this is a test message");

        _manager.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AddAssistantMessage_WithContent_StoresCorrectly()
    {
        _manager.AddUserMessage("Question");
        _manager.AddAssistantMessage("Answer");

        _manager.MessageCount.Should().Be(2);
    }

    [Fact]
    public void SetSystemPrompt_StoresPrompt()
    {
        _manager.SetSystemPrompt("You are a helpful assistant.");

        // System prompt doesn't increase message count
        _manager.MessageCount.Should().Be(0);
    }

    [Fact]
    public void BuildApiMessages_WithSystemPrompt_IncludesItAsFirstMessage()
    {
        _manager.SetSystemPrompt("You are helpful.");
        _manager.AddUserMessage("Hi");

        var messages = _manager.BuildApiMessages();

        // messages[0] = SharedImmutablePrefix (含 tools description, 43ceb84 合并回退), messages[1] = user, messages[2] = custom system prompt
        messages.Should().HaveCount(3);
        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().Contain("你是 DeepSeek v4 for Visual Studio");
        messages[1].Role.Should().Be("user");
        messages[1].Content.Should().Be("Hi");
        messages[2].Role.Should().Be("system");
        messages[2].Content.Should().Be("You are helpful.");
    }

    [Fact]
    public void SetRagContext_UpdatesTokenCount()
    {
        var initialTokens = _manager.EstimatedTokens;

        _manager.SetRagContext("RAG context: relevant document content here.");

        _manager.EstimatedTokens.Should().BeGreaterThan(initialTokens);
        _manager.RagContext.Should().NotBeNull();
    }

    [Fact]
    public void Clear_ResetsAllState()
    {
        _manager.SetSystemPrompt("prompt");
        _manager.AddUserMessage("hello");
        _manager.AddAssistantMessage("hi");
        _manager.SetRagContext("rag");

        _manager.Clear();

        _manager.IsEmpty.Should().BeTrue();
        _manager.TurnCount.Should().Be(0);
        _manager.MessageCount.Should().Be(0);
        _manager.EstimatedTokens.Should().Be(0);
        _manager.RagContext.Should().BeNull();
    }

    [Fact]
    public void AddToolResult_StoresCorrectly()
    {
        _manager.AddUserMessage("search for something");
        _manager.AddToolResult("call_1", "grep_search", "Found 5 results");

        var messages = _manager.BuildApiMessages();

        messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
    }

    [Fact]
    public void TokenBudget_DefaultIs900K()
    {
        _manager.TokenBudget.Should().Be(900_000);
    }

    [Fact]
    public void UsageRatio_CalculatesCorrectly()
    {
        _manager.TokenBudget = 1000;

        // Add enough content to estimate ~100 tokens
        var longText = new string('x', 400);
        _manager.AddUserMessage(longText);

        _manager.UsageRatio.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MultipleTurns_CountsCorrectly()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");
        _manager.AddUserMessage("Q3");

        _manager.TurnCount.Should().Be(3);
        _manager.MessageCount.Should().Be(5);
    }

    [Fact]
    public void SetSearchContext_IsInjectedIntoMessages()
    {
        _manager.SetSearchContext("Search results: ...");
        _manager.AddUserMessage("query");

        var messages = _manager.BuildApiMessages();

        messages.Should().Contain(m => m.Role == "system" && m.Content!.Contains("Search results"));
    }
}
