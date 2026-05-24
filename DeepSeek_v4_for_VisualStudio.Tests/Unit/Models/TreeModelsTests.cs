using System.Runtime.Serialization.Json;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class TreeModelsTests
{
    #region TreePersistenceData

    [Fact]
    public void TreePersistenceData_Defaults_AreSetCorrectly()
    {
        var data = new TreePersistenceData();

        data.Version.Should().Be(2);
        data.ActiveLeafId.Should().BeNull();
        data.Nodes.Should().BeEmpty();
    }

    [Fact]
    public void TreePersistenceData_CanSetProperties()
    {
        var data = new TreePersistenceData
        {
            Version = 2,
            ActiveLeafId = "abc123",
            Nodes = new List<TreeNodeData>
            {
                new() { Id = "root" },
                new() { Id = "leaf", ParentId = "root" },
            },
        };

        data.Version.Should().Be(2);
        data.ActiveLeafId.Should().Be("abc123");
        data.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public void TreePersistenceData_SerializationRoundTrip()
    {
        var data = new TreePersistenceData
        {
            Version = 2,
            ActiveLeafId = "node_xyz",
            Nodes = new List<TreeNodeData>
            {
                new()
                {
                    Id = "root",
                    ChildrenIds = new List<string> { "node_xyz" },
                },
                new()
                {
                    Id = "node_xyz",
                    ParentId = "root",
                    Message = new ChatMessage { Role = "user", Content = "Hello" },
                },
            },
        };

        // Serialize
        var serializer = new DataContractJsonSerializer(typeof(TreePersistenceData),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var ms = new MemoryStream();
        serializer.WriteObject(ms, data);
        string json = Encoding.UTF8.GetString(ms.ToArray());

        // Deserialize
        ms.Position = 0;
        var deserialized = (TreePersistenceData)serializer.ReadObject(ms)!;

        deserialized.Version.Should().Be(2);
        deserialized.ActiveLeafId.Should().Be("node_xyz");
        deserialized.Nodes.Should().HaveCount(2);
        deserialized.Nodes[1].Message!.Content.Should().Be("Hello");
    }

    #endregion

    #region TreeNodeData

    [Fact]
    public void TreeNodeData_Defaults_AreSetCorrectly()
    {
        var node = new TreeNodeData();

        node.Id.Should().BeEmpty();
        node.ParentId.Should().BeNull();
        node.Message.Should().BeNull();
        node.ChildrenIds.Should().BeNull();
    }

    [Fact]
    public void TreeNodeData_CanRepresentLeaf()
    {
        var leaf = new TreeNodeData
        {
            Id = "leaf1",
            ParentId = "parent1",
            Message = new ChatMessage { Role = "assistant", Content = "回答文本" },
        };

        leaf.Id.Should().Be("leaf1");
        leaf.ParentId.Should().Be("parent1");
        leaf.Message!.Content.Should().Be("回答文本");
        leaf.ChildrenIds.Should().BeNull(); // no children
    }

    [Fact]
    public void TreeNodeData_CanRepresentBranch()
    {
        var branch = new TreeNodeData
        {
            Id = "branch1",
            ParentId = "root",
            Message = new ChatMessage { Role = "user", Content = "问题" },
            ChildrenIds = new List<string> { "answer1", "answer2" },
        };

        branch.ChildrenIds.Should().HaveCount(2);
        branch.ChildrenIds.Should().Contain("answer1");
        branch.ChildrenIds.Should().Contain("answer2");
    }

    [Fact]
    public void TreeNodeData_SerializationRoundTrip()
    {
        var node = new TreeNodeData
        {
            Id = "test_node",
            ParentId = "parent",
            Message = new ChatMessage
            {
                Role = "assistant",
                Content = "测试内容",
                ReasoningContent = "思考过程",
            },
            ChildrenIds = new List<string> { "child1" },
        };

        var serializer = new DataContractJsonSerializer(typeof(TreeNodeData),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var ms = new MemoryStream();
        serializer.WriteObject(ms, node);
        string json = Encoding.UTF8.GetString(ms.ToArray());

        ms.Position = 0;
        var deserialized = (TreeNodeData)serializer.ReadObject(ms)!;

        deserialized.Id.Should().Be("test_node");
        deserialized.ParentId.Should().Be("parent");
        deserialized.Message!.Content.Should().Be("测试内容");
        deserialized.Message.ReasoningContent.Should().Be("思考过程");
        deserialized.ChildrenIds!.Should().Contain("child1");
    }

    #endregion
}
