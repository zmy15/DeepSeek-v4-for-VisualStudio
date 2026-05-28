using DeepSeek_v4_for_VisualStudio.Commands;
using Microsoft.VisualStudio.Shell;
using System.ComponentModel.Design;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Commands;

/// <summary>
/// ShowChatWindowCommand 单元测试 — 测试命令常量、CommandSet GUID、命令 ID 定义。
/// 注意：InitializeAsync 和 Execute 依赖 VS SDK 运行时（AsyncPackage/JoinableTaskFactory），
/// 属于集成测试范畴。
/// </summary>
public class ShowChatWindowCommandTests
{
    #region Command IDs

    [Fact]
    public void CommandId_Is0x0100()
    {
        ShowChatWindowCommand.CommandId.Should().Be(0x0100);
    }

    [Fact]
    public void ToolbarCommandId_Is0x0101()
    {
        ShowChatWindowCommand.ToolbarCommandId.Should().Be(0x0101);
    }

    [Fact]
    public void CommandId_And_ToolbarCommandId_AreDifferent()
    {
        ShowChatWindowCommand.CommandId.Should().NotBe(ShowChatWindowCommand.ToolbarCommandId);
    }

    #endregion

    #region CommandSet GUID

    [Fact]
    public void CommandSet_IsValidGuid()
    {
        ShowChatWindowCommand.CommandSet.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void CommandSet_MatchesExpectedValue()
    {
        var expected = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        ShowChatWindowCommand.CommandSet.Should().Be(expected);
    }

    [Fact]
    public void CommandSet_ToString_ReturnsExpectedFormat()
    {
        var guidStr = ShowChatWindowCommand.CommandSet.ToString("D");

        guidStr.Should().Be("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    }

    [Fact]
    public void CommandSet_CanCreateCommandID()
    {
        var cmdId = new CommandID(ShowChatWindowCommand.CommandSet, ShowChatWindowCommand.CommandId);

        cmdId.Should().NotBeNull();
        cmdId.Guid.Should().Be(ShowChatWindowCommand.CommandSet);
        cmdId.ID.Should().Be(ShowChatWindowCommand.CommandId);
    }

    [Fact]
    public void Toolbar_CanCreateCommandID()
    {
        var cmdId = new CommandID(ShowChatWindowCommand.CommandSet, ShowChatWindowCommand.ToolbarCommandId);

        cmdId.Should().NotBeNull();
        cmdId.Guid.Should().Be(ShowChatWindowCommand.CommandSet);
        cmdId.ID.Should().Be(ShowChatWindowCommand.ToolbarCommandId);
    }

    #endregion

    #region Instance

    [Fact]
    public void Instance_InitiallyNull()
    {
        ShowChatWindowCommand.Instance.Should().BeNull();
    }

    #endregion

    #region CommandID Uniqueness

    [Fact]
    public void MenuAndToolbar_HaveUniqueCommandIDs()
    {
        // Both menu and toolbar command IDs should produce different CommandIDs
        var menuCmdId = new CommandID(ShowChatWindowCommand.CommandSet, ShowChatWindowCommand.CommandId);
        var toolbarCmdId = new CommandID(ShowChatWindowCommand.CommandSet, ShowChatWindowCommand.ToolbarCommandId);

        menuCmdId.Should().NotBe(toolbarCmdId);
    }

    #endregion
}
