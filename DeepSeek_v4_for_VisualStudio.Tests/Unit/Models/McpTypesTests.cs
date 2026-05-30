namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class McpTypesTests
{
    #region JsonRpcRequest

    [Fact]
    public void JsonRpcRequest_Defaults_AreCorrect()
    {
        var request = new JsonRpcRequest();

        request.JsonRpc.Should().Be("2.0");
        request.Id.Should().Be(0);
        request.Method.Should().BeEmpty();
        request.Params.Should().BeNull();
    }

    [Fact]
    public void JsonRpcRequest_CanSetProperties()
    {
        var request = new JsonRpcRequest
        {
            Id = 42,
            Method = "tools/list",
        };

        request.JsonRpc.Should().Be("2.0");
        request.Id.Should().Be(42);
        request.Method.Should().Be("tools/list");
    }

    #endregion

    #region JsonRpcResponse

    [Fact]
    public void JsonRpcResponse_Defaults_AreCorrect()
    {
        var response = new JsonRpcResponse();

        response.JsonRpc.Should().Be("2.0");
        response.Id.Should().Be(0);
        response.Result.Should().BeNull();
        response.Error.Should().BeNull();
    }

    [Fact]
    public void JsonRpcResponse_WithError_HasErrorMessage()
    {
        var response = new JsonRpcResponse
        {
            Id = 1,
            Error = new JsonRpcError
            {
                Code = -32601,
                Message = "Method not found",
            },
        };

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-32601);
        response.Error.Message.Should().Be("Method not found");
    }

    #endregion

    #region JsonRpcError

    [Fact]
    public void JsonRpcError_Defaults_AreCorrect()
    {
        var error = new JsonRpcError();

        error.Code.Should().Be(0);
        error.Message.Should().BeEmpty();
        error.Data.Should().BeNull();
    }

    #endregion

    #region JsonRpcNotification

    [Fact]
    public void JsonRpcNotification_Defaults_HasNoId()
    {
        var notification = new JsonRpcNotification();

        notification.JsonRpc.Should().Be("2.0");
        notification.Method.Should().BeEmpty();
        // Notifications have no id field
    }

    #endregion

    #region InitializeParams

    [Fact]
    public void InitializeParams_Defaults_UseCorrectProtocolVersion()
    {
        var init = new InitializeParams();

        init.ProtocolVersion.Should().Be("2025-11-25");
        init.Capabilities.Should().NotBeNull();
        init.ClientInfo.Should().NotBeNull();
    }

    [Fact]
    public void InitializeParams_ClientInfo_HasDefaultValues()
    {
        var init = new InitializeParams();

        init.ClientInfo.Name.Should().Be("DeepSeek-v4-for-VisualStudio");
        init.ClientInfo.Version.Should().Be("1.1.0");
    }

    #endregion

    #region McpTool

    [Fact]
    public void McpTool_Defaults_AreCorrect()
    {
        var tool = new McpTool();

        tool.Name.Should().BeEmpty();
        tool.Description.Should().BeEmpty();
        tool.InputSchema.Should().NotBeNull();
    }

    [Fact]
    public void McpTool_CanDefineWithInputSchema()
    {
        var tool = new McpTool
        {
            Name = "read_file",
            Description = "读取文件内容",
            InputSchema = new McpInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, McpPropertySchema>
                {
                    ["filePath"] = new()
                    {
                        Type = "string",
                        Description = "文件路径",
                    },
                },
                Required = new List<string> { "filePath" },
            },
        };

        tool.Name.Should().Be("read_file");
        tool.Description.Should().Contain("读取");
        tool.InputSchema.Properties.Should().ContainKey("filePath");
        tool.InputSchema.Properties["filePath"].Type.Should().Be("string");
        tool.InputSchema.Required.Should().Contain("filePath");
    }

    #endregion

    #region McpPropertySchema

    [Fact]
    public void McpPropertySchema_Defaults_AreCorrect()
    {
        var prop = new McpPropertySchema();

        prop.Type.Should().Be("string");
        prop.Description.Should().BeEmpty();
        prop.Enum.Should().BeNull();
        prop.Default.Should().BeNull();
    }

    [Fact]
    public void McpPropertySchema_CanDefineEnumValues()
    {
        var prop = new McpPropertySchema
        {
            Type = "string",
            Description = "排序方向",
            Enum = new List<string> { "asc", "desc" },
        };

        prop.Enum.Should().HaveCount(2);
        prop.Enum.Should().Contain("asc");
        prop.Enum.Should().Contain("desc");
    }

    #endregion

    #region ToolCallParams

    [Fact]
    public void ToolCallParams_Defaults_AreCorrect()
    {
        var call = new ToolCallParams();

        call.Name.Should().BeEmpty();
        call.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void ToolCallParams_CanSetNameAndArgs()
    {
        var call = new ToolCallParams
        {
            Name = "read_file",
            Arguments = new Dictionary<string, object>
            {
                ["filePath"] = "/path/to/file.ts",
            },
        };

        call.Name.Should().Be("read_file");
        call.Arguments["filePath"].Should().Be("/path/to/file.ts");
    }

    #endregion

    #region ToolCallResult

    [Fact]
    public void ToolCallResult_Defaults_AreCorrect()
    {
        var result = new ToolCallResult();

        result.Content.Should().BeEmpty();
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public void ToolCallResult_CanIndicateError()
    {
        var result = new ToolCallResult
        {
            IsError = true,
            Content = new List<ToolContentItem>
            {
                new() { Type = "text", Text = "File not found" },
            },
        };

        result.IsError.Should().BeTrue();
        result.Content.Should().HaveCount(1);
        result.Content[0].Text.Should().Be("File not found");
    }

    #endregion

    #region ToolContentItem

    [Fact]
    public void ToolContentItem_Defaults_TypeIsText()
    {
        var item = new ToolContentItem();

        item.Type.Should().Be("text");
        item.Text.Should().BeEmpty();
        item.Data.Should().BeNull();
        item.MimeType.Should().BeNull();
    }

    [Fact]
    public void ToolContentItem_CanHoldImageData()
    {
        var item = new ToolContentItem
        {
            Type = "image",
            Data = "base64encoded...",
            MimeType = "image/png",
        };

        item.Type.Should().Be("image");
        item.Data.Should().NotBeNull();
        item.MimeType.Should().Be("image/png");
    }

    #endregion

    #region McpResource

    [Fact]
    public void McpResource_Defaults_AreCorrect()
    {
        var resource = new McpResource();

        resource.Uri.Should().BeEmpty();
    }

    [Fact]
    public void McpResource_CanSetUri()
    {
        var resource = new McpResource
        {
            Uri = "file:///workspace/README.md",
        };

        resource.Uri.Should().Be("file:///workspace/README.md");
    }

    #endregion

    #region Serialization

    [Fact]
    public void JsonRpcRequest_Serialization_ProducesCorrectJson()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(request);

        json.Should().Contain("\"jsonrpc\":\"2.0\"");
        json.Should().Contain("\"id\":1");
        json.Should().Contain("\"method\":\"initialize\"");
    }

    [Fact]
    public void McpTool_Serialization_IncludesInputSchema()
    {
        var tool = new McpTool
        {
            Name = "search",
            Description = "Search for files",
            InputSchema = new McpInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, McpPropertySchema>
                {
                    ["query"] = new() { Type = "string", Description = "Search query" },
                },
                Required = new List<string> { "query" },
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(tool);

        json.Should().Contain("\"name\":\"search\"");
        json.Should().Contain("\"inputSchema\"");
        json.Should().Contain("\"query\"");
    }

    #endregion
}
