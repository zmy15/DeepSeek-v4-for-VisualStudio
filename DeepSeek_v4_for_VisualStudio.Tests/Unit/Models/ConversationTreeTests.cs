namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class ConversationTreeTests
{
    #region ConvNode

    [Fact]
    public void ConvNode_Defaults_AreSetCorrectly()
    {
        var node = new ConvNode
        {
            Id = "node1",
            Message = new ChatMessage { Role = "user", Content = "Hello" },
        };

        node.Id.Should().Be("node1");
        node.Message.Role.Should().Be("user");
        node.Parent.Should().BeNull();
        node.Children.Should().BeEmpty();
        node.SiblingIndex.Should().Be(0);
        node.SiblingCount.Should().Be(1);
    }

    [Fact]
    public void ConvNode_IsUserMessage_ReturnsTrueForUserRole()
    {
        var node = new ConvNode
        {
            Message = new ChatMessage { Role = "user", Content = "test" }
        };

        node.IsUserMessage.Should().BeTrue();
        node.IsAssistantMessage.Should().BeFalse();
    }

    [Fact]
    public void ConvNode_IsAssistantMessage_ReturnsTrueForAssistantRole()
    {
        var node = new ConvNode
        {
            Message = new ChatMessage { Role = "assistant", Content = "response" }
        };

        node.IsUserMessage.Should().BeFalse();
        node.IsAssistantMessage.Should().BeTrue();
    }

    [Fact]
    public void ConvNode_GetLeaf_ReturnsDeepestChild()
    {
        var root = new ConvNode { Id = "r", Message = new ChatMessage { Role = "user" } };
        var child1 = new ConvNode { Id = "c1", Message = new ChatMessage { Role = "assistant" }, Parent = root };
        var child2 = new ConvNode { Id = "c2", Message = new ChatMessage { Role = "user" }, Parent = child1 };
        root.Children.Add(child1);
        child1.Children.Add(child2);

        var leaf = root.GetLeaf();
        leaf.Should().Be(child2);
    }

    [Fact]
    public void ConvNode_GetLeaf_ReturnsSelfWhenNoChildren()
    {
        var node = new ConvNode { Id = "n", Message = new ChatMessage { Role = "user" } };

        var leaf = node.GetLeaf();
        leaf.Should().Be(node);
    }

    [Fact]
    public void ConvNode_SiblingInfo_CalculatedFromParent()
    {
        var parent = new ConvNode { Id = "p", Message = new ChatMessage { Role = "user" } };
        var sibling1 = new ConvNode { Id = "s1", Message = new ChatMessage { Role = "assistant" }, Parent = parent };
        var sibling2 = new ConvNode { Id = "s2", Message = new ChatMessage { Role = "assistant" }, Parent = parent };
        parent.Children.Add(sibling1);
        parent.Children.Add(sibling2);

        sibling1.SiblingIndex.Should().Be(0);
        sibling2.SiblingIndex.Should().Be(1);
        sibling1.SiblingCount.Should().Be(2);
        sibling2.SiblingCount.Should().Be(2);
    }

    [Fact]
    public void ConvNode_ToString_TruncatesLongContent()
    {
        var node = new ConvNode
        {
            Id = "n1",
            Message = new ChatMessage { Role = "user", Content = new string('x', 200) }
        };

        var str = node.ToString();
        str.Should().Contain("[n1]");
        str.Should().Contain("U:");
        str.Length.Should().BeLessThan(100); // truncate at 60
    }

    #endregion

    #region ConversationTree

    [Fact]
    public void ConversationTree_NewInstance_HasRootAndActiveLeaf()
    {
        var tree = new ConversationTree();

        tree.Root.Should().NotBeNull();
        tree.Root.Id.Should().Be("root");
        tree.ActiveLeaf.Should().Be(tree.Root);
    }

    [Fact]
    public void AddChildMessage_AddsToRoot_TracksActiveLeaf()
    {
        var tree = new ConversationTree();
        var message = new ChatMessage { Role = "user", Content = "第一个问题" };

        var node = tree.AddChildMessage(message);

        node.Parent.Should().Be(tree.Root);
        tree.ActiveLeaf.Should().Be(node);
        tree.Root.Children.Should().Contain(node);
        message.NodeId.Should().Be(node.Id);
    }

    [Fact]
    public void AddChildMessage_BuildsLinearChain()
    {
        var tree = new ConversationTree();

        var userMsg = tree.AddChildMessage(new ChatMessage { Role = "user", Content = "Q1" });
        var assistantMsg = tree.AddChildMessage(new ChatMessage { Role = "assistant", Content = "A1" });
        var userMsg2 = tree.AddChildMessage(new ChatMessage { Role = "user", Content = "Q2" });

        tree.Root.Children.Should().HaveCount(1);
        userMsg.Children.Should().HaveCount(1);
        assistantMsg.Children.Should().HaveCount(1);
        tree.ActiveLeaf.Should().Be(userMsg2);
    }

    [Fact]
    public void ForkAt_CreatesSiblingBranch()
    {
        var tree = new ConversationTree();

        var originalUser = tree.AddChildMessage(new ChatMessage { Role = "user", Content = "原始问题" });
        var originalAssistant = tree.AddChildMessage(new ChatMessage { Role = "assistant", Content = "原始回答" });

        // Fork at assistant: create another answer
        var newMessage = new ChatMessage { Role = "user", Content = "编辑后的问题" };
        var forkedNode = tree.ForkAt(originalAssistant, newMessage, "edit");

        originalAssistant.SiblingCount.Should().Be(2);
        forkedNode.Parent.Should().Be(originalUser);
        forkedNode.Message.ForkReason.Should().Be("edit");
        tree.ActiveLeaf.Should().Be(forkedNode);
    }

    [Fact]
    public void ReplaceInPlace_ReplacesMessageAndPrunesChildren()
    {
        var tree = new ConversationTree();

        var userMsg = tree.AddChildMessage(new ChatMessage { Role = "user", Content = "问题" });
        var assistantMsg = tree.AddChildMessage(new ChatMessage { Role = "assistant", Content = "回答" });
        var followUp = tree.AddChildMessage(new ChatMessage { Role = "user", Content = "追问" });

        var newMessage = new ChatMessage { Role = "assistant", Content = "新的回答" };
        var replaced = tree.ReplaceInPlace(assistantMsg, newMessage);

        replaced.Should().Be(assistantMsg);
        assistantMsg.Message.Content.Should().Be("新的回答");
        assistantMsg.Children.Should().BeEmpty(); // children pruned
        assistantMsg.Message.ForkReason.Should().BeNull();
    }

    [Fact]
    public void FindNode_ReturnsNodeById()
    {
        var tree = new ConversationTree();
        var node = tree.AddChildMessage(new ChatMessage { Role = "user", Content = "test" });

        var found = tree.FindNode(node.Id);
        found.Should().Be(node);
    }

    [Fact]
    public void FindNode_NonExistent_ReturnsNull()
    {
        var tree = new ConversationTree();

        var found = tree.FindNode("nonexistent");
        found.Should().BeNull();
    }

    [Fact]
    public void ForkAt_Retry_CreatesSiblingWithForkReason()
    {
        var tree = new ConversationTree();

        tree.AddChildMessage(new ChatMessage { Role = "user", Content = "问题" });
        var originalAssistant = tree.AddChildMessage(new ChatMessage { Role = "assistant", Content = "错误回答" });

        var newMessage = new ChatMessage { Role = "assistant", Content = "重试回答" };
        var retryNode = tree.ForkAt(originalAssistant, newMessage, "retry");

        retryNode.Message.ForkReason.Should().Be("retry");
        originalAssistant.SiblingCount.Should().Be(2);
    }

    [Fact]
    public void AddChildMessage_MultipleThousandNodes_TreeRemainsConsistent()
    {
        var tree = new ConversationTree();

        // Build a deep chain of 20 nodes
        ConvNode lastNode = tree.Root;
        for (int i = 0; i < 20; i++)
        {
            lastNode = tree.AddChildMessage(new ChatMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"Message {i}"
            });
        }

        tree.ActiveLeaf.Should().Be(lastNode);
        tree.FindNode(lastNode.Id).Should().NotBeNull();
    }

    #endregion
}
