namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class AgentSuggestionItemTests
{
    #region Defaults

    [Fact]
    public void AgentSuggestionItem_Defaults_AreSetCorrectly()
    {
        var item = new AgentSuggestionItem();

        item.Name.Should().BeEmpty();
        item.Icon.Should().BeEmpty();
        item.Description.Should().BeEmpty();
        item.ArgumentHint.Should().BeEmpty();
        item.AgentType.Should().Be(AgentType.Ask);
    }

    #endregion

    #region Properties

    [Fact]
    public void AgentSuggestionItem_CanSetAllProperties()
    {
        var item = new AgentSuggestionItem
        {
            Name = "edit",
            Icon = "✏️",
            Description = "修改和创建代码文件",
            ArgumentHint = "[指令]",
            AgentType = AgentType.Edit,
        };

        item.Name.Should().Be("edit");
        item.Icon.Should().Be("✏️");
        item.Description.Should().Contain("修改");
        item.ArgumentHint.Should().Be("[指令]");
        item.AgentType.Should().Be(AgentType.Edit);
    }

    #endregion

    #region DisplayPrefix

    [Fact]
    public void DisplayPrefix_FormatsWithAtSymbol()
    {
        var item = new AgentSuggestionItem { Name = "plan" };

        item.DisplayPrefix.Should().Be("@plan");
    }

    [Fact]
    public void DisplayPrefix_WithEmptyName_ReturnsAtOnly()
    {
        var item = new AgentSuggestionItem { Name = "" };

        item.DisplayPrefix.Should().Be("@");
    }

    #endregion

    #region TooltipText

    [Fact]
    public void TooltipText_WithDescription_IncludesAllParts()
    {
        var item = new AgentSuggestionItem
        {
            Name = "edit",
            Description = "修改代码文件",
            ArgumentHint = "输入修改指令",
        };

        item.TooltipText.Should().Contain("@edit");
        item.TooltipText.Should().Contain("修改代码文件");
        item.TooltipText.Should().Contain("输入修改指令");
    }

    [Fact]
    public void TooltipText_WithoutDescription_OnlyShowsAtName()
    {
        var item = new AgentSuggestionItem
        {
            Name = "ask",
            Description = "",
        };

        item.TooltipText.Should().Be("@ask");
    }

    #endregion

    #region Each Agent Type

    [Theory]
    [InlineData(AgentType.Ask, "ask")]
    [InlineData(AgentType.Edit, "edit")]
    [InlineData(AgentType.Plan, "plan")]
    [InlineData(AgentType.Explore, "explore")]
    [InlineData(AgentType.Build, "build")]
    public void AgentSuggestionItem_CanRepresentEachAgentType(AgentType type, string expectedName)
    {
        var item = new AgentSuggestionItem
        {
            Name = expectedName,
            AgentType = type,
        };

        item.AgentType.Should().Be(type);
        item.Name.Should().Be(expectedName);
    }

    #endregion
}
