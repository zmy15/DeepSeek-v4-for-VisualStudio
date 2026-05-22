namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class SkillDefinitionTests
{
    #region SkillDefinition

    [Fact]
    public void SkillDefinition_Defaults_AreSetCorrectly()
    {
        var skill = new SkillDefinition();

        skill.Name.Should().BeEmpty();
        skill.Description.Should().BeEmpty();
        skill.ArgumentHint.Should().BeNull();
        skill.UserInvocable.Should().BeTrue();
        skill.DisableModelInvocation.Should().BeFalse();
        skill.Body.Should().BeEmpty();
        skill.FilePath.Should().BeEmpty();
        skill.RootDirectory.Should().BeNull();
        skill.Source.Should().Be(SkillSource.Project);
        skill.ResourceFiles.Should().BeEmpty();
    }

    [Fact]
    public void SkillDefinition_GetDiscoveryText_ReturnsFormattedText()
    {
        var skill = new SkillDefinition
        {
            Name = "code-review",
            Description = "Review code quality and security.",
        };

        var text = skill.GetDiscoveryText();

        text.Should().Be("## code-review\nReview code quality and security.");
    }

    [Fact]
    public void SkillDefinition_GetFullInstructions_WithoutResources_ReturnsFormattedInstructions()
    {
        var skill = new SkillDefinition
        {
            Name = "test-skill",
            Description = "A test skill.",
            Body = "# Test Skill\n\nThis is the body.",
        };

        var instructions = skill.GetFullInstructions();

        instructions.Should().Contain("<skill name=\"test-skill\">");
        instructions.Should().Contain("<description>A test skill.</description>");
        instructions.Should().Contain("# Test Skill");
        instructions.Should().Contain("This is the body.");
        instructions.Should().Contain("</skill>");
        instructions.Should().NotContain("<resources>");
    }

    [Fact]
    public void SkillDefinition_GetFullInstructions_WithResources_IncludesResourceList()
    {
        var skill = new SkillDefinition
        {
            Name = "test-skill",
            Description = "Test",
            Body = "Body content",
            ResourceFiles = new List<string> { "scripts/build.ps1", "references/api.md" },
        };

        var instructions = skill.GetFullInstructions();

        instructions.Should().Contain("<resources>");
        instructions.Should().Contain("scripts/build.ps1");
        instructions.Should().Contain("references/api.md");
        instructions.Should().Contain("</resources>");
    }

    [Fact]
    public void SkillDefinition_GetCompactInstructions_ShortBody_ReturnsFullBody()
    {
        var skill = new SkillDefinition
        {
            Name = "test",
            Description = "Test desc",
            Body = "Short body content here.",
        };

        var compact = skill.GetCompactInstructions(2000);

        compact.Should().Contain("Short body content here.");
        compact.Should().NotContain("truncated");
    }

    [Fact]
    public void SkillDefinition_GetCompactInstructions_LongBody_NoLongerTruncates()
    {
        // RAG-MARK: no-truncate — 技能正文不再截断，完整传递
        var skill = new SkillDefinition
        {
            Name = "test",
            Description = "Test desc",
            Body = new string('A', 3000),
        };

        var compact = skill.GetCompactInstructions(100);

        // 不再截断，完整内容应包含在结果中
        compact.Should().Contain(new string('A', 3000));
        compact.Should().NotContain("truncated");
    }

    [Fact]
    public void SkillDefinition_GetCompactInstructions_WithEmojiAtCutPoint_NoLongerTruncates()
    {
        var skill = new SkillDefinition
        {
            Name = "test",
            Description = "Test",
            Body = new string('A', 98) + "😀" + new string('B', 200),
        };

        var compact = skill.GetCompactInstructions(100);

        // 不再截断，emoji 字符完整保留
        compact.Should().Contain("😀");
        compact.Should().NotContain("truncated");
    }

    #endregion

    #region SkillSuggestionItem

    [Fact]
    public void SkillSuggestionItem_Defaults_AreSetCorrectly()
    {
        var item = new SkillSuggestionItem();

        item.Name.Should().BeEmpty();
        item.Description.Should().BeEmpty();
        item.Source.Should().BeEmpty();
        item.IsMeta.Should().BeFalse();
        item.SkillDefinition.Should().BeNull();
    }

    [Fact]
    public void SkillSuggestionItem_DisplayText_FormatsCorrectly()
    {
        var item = new SkillSuggestionItem
        {
            Name = "code-review",
            Source = "📁 项目",
        };

        item.DisplayText.Should().Be("/code-review  📁 项目");
    }

    [Fact]
    public void SkillSuggestionItem_TooltipText_WithDescription_ContainsAllFields()
    {
        var item = new SkillSuggestionItem
        {
            Name = "test",
            Description = "A test command",
            Source = "BuiltIn",
        };

        item.TooltipText.Should().Contain("/test");
        item.TooltipText.Should().Contain("A test command");
        item.TooltipText.Should().Contain("BuiltIn");
    }

    [Fact]
    public void SkillSuggestionItem_TooltipText_WithoutDescription_ShowsOnlyName()
    {
        var item = new SkillSuggestionItem
        {
            Name = "test",
            Description = "",
        };

        item.TooltipText.Should().Be("/test");
    }

    [Fact]
    public void SkillSuggestionItem_IsMeta_True()
    {
        var item = new SkillSuggestionItem
        {
            Name = "help",
            IsMeta = true,
        };

        item.IsMeta.Should().BeTrue();
        item.DisplayText.Should().Contain("/help");
    }

    [Fact]
    public void SkillSuggestionItem_WithSkillDefinition_StoresReference()
    {
        var skillDef = new SkillDefinition { Name = "my-skill" };
        var item = new SkillSuggestionItem
        {
            Name = "my-skill",
            SkillDefinition = skillDef,
        };

        item.SkillDefinition.Should().BeSameAs(skillDef);
    }

    #endregion

    #region SkillDiscoveryResult

    [Fact]
    public void SkillDiscoveryResult_Defaults_AreSetCorrectly()
    {
        var result = new SkillDiscoveryResult();

        result.TotalCount.Should().Be(0);
        result.Skills.Should().BeEmpty();
        result.ProjectSkillCount.Should().Be(0);
        result.UserSkillCount.Should().Be(0);
        result.UserInvocableSkills.Should().BeEmpty();
        result.AutoLoadableSkills.Should().BeEmpty();
    }

    [Fact]
    public void SkillDiscoveryResult_CountsSkillsBySource()
    {
        var result = new SkillDiscoveryResult
        {
            Skills = new List<SkillDefinition>
            {
                new() { Name = "a", Source = SkillSource.Project },
                new() { Name = "b", Source = SkillSource.Project },
                new() { Name = "c", Source = SkillSource.User },
                new() { Name = "d", Source = SkillSource.BuiltIn },
            }
        };

        result.TotalCount.Should().Be(4);
        result.ProjectSkillCount.Should().Be(2);
        result.UserSkillCount.Should().Be(1);
    }

    [Fact]
    public void SkillDiscoveryResult_UserInvocableSkills_FiltersCorrectly()
    {
        var result = new SkillDiscoveryResult
        {
            Skills = new List<SkillDefinition>
            {
                new() { Name = "invocable1", UserInvocable = true },
                new() { Name = "invocable2", UserInvocable = true },
                new() { Name = "hidden", UserInvocable = false },
            }
        };

        result.UserInvocableSkills.Should().HaveCount(2);
        result.UserInvocableSkills.Should().OnlyContain(s => s.UserInvocable);
    }

    [Fact]
    public void SkillDiscoveryResult_AutoLoadableSkills_FiltersCorrectly()
    {
        var result = new SkillDiscoveryResult
        {
            Skills = new List<SkillDefinition>
            {
                new() { Name = "auto1", DisableModelInvocation = false },
                new() { Name = "auto2", DisableModelInvocation = false },
                new() { Name = "disabled", DisableModelInvocation = true },
            }
        };

        result.AutoLoadableSkills.Should().HaveCount(2);
        result.AutoLoadableSkills.Should().OnlyContain(s => !s.DisableModelInvocation);
    }

    #endregion

    #region SkillRoutingResult

    [Fact]
    public void SkillRoutingResult_Defaults_AreSetCorrectly()
    {
        var result = new SkillRoutingResult();

        result.Skill.Should().BeNull();
        result.Confidence.Should().BeNull();
        result.Reason.Should().BeNull();
        result.HasSkill.Should().BeFalse();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_WithValidName_ReturnsTrue()
    {
        var result = new SkillRoutingResult { Skill = "code-review" };

        result.HasSkill.Should().BeTrue();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_WithNone_ReturnsFalse()
    {
        var result = new SkillRoutingResult { Skill = "none" };

        result.HasSkill.Should().BeFalse();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_WithNull_ReturnsFalse()
    {
        var result = new SkillRoutingResult { Skill = "null" };

        result.HasSkill.Should().BeFalse();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_WithEmpty_ReturnsFalse()
    {
        var result = new SkillRoutingResult { Skill = "" };

        result.HasSkill.Should().BeFalse();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_WithWhitespace_ReturnsFalse()
    {
        var result = new SkillRoutingResult { Skill = "   " };

        result.HasSkill.Should().BeFalse();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_CaseInsensitive_None()
    {
        var result = new SkillRoutingResult { Skill = "NONE" };

        result.HasSkill.Should().BeFalse();
    }

    [Fact]
    public void SkillRoutingResult_HasSkill_CaseInsensitive_Null()
    {
        var result = new SkillRoutingResult { Skill = "NULL" };

        result.HasSkill.Should().BeFalse();
    }

    #endregion

    #region SkillSource Enum

    [Fact]
    public void SkillSource_Enum_HasThreeValues()
    {
        Enum.GetValues(typeof(SkillSource)).Length.Should().Be(3);
        Enum.IsDefined(typeof(SkillSource), SkillSource.Project).Should().BeTrue();
        Enum.IsDefined(typeof(SkillSource), SkillSource.User).Should().BeTrue();
        Enum.IsDefined(typeof(SkillSource), SkillSource.BuiltIn).Should().BeTrue();
    }

    #endregion
}
