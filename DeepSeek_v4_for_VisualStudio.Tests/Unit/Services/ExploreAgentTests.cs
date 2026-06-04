using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// ExploreAgent 单元测试 — 测试 Agent 定义、只读工具集、
/// BuildExplorePrompt 上下文构建、源文件扩展名集合。
/// </summary>
public class ExploreAgentTests
{
    private readonly DeepSeekApiService _apiService;

    public ExploreAgentTests()
    {
        _apiService = new DeepSeekApiService("test-api-key");
    }

    #region Constructor

    [Fact]
    public void Constructor_WithApiService_CreatesSuccessfully()
    {
        var agent = new ExploreAgent(_apiService);

        agent.Should().NotBeNull();
        agent.Definition.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Explore);
    }

    [Fact]
    public void Constructor_WithNullApiService_ThrowsArgumentNullException()
    {
        Action act = () => new ExploreAgent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Agent Definition

    [Fact]
    public void Definition_Name_IsExplore()
    {
        var agent = new ExploreAgent(_apiService);

        agent.Definition.Name.Should().Be("Explore");
    }

    [Fact]
    public void Definition_IsUserInvocable()
    {
        var agent = new ExploreAgent(_apiService);

        // ExploreAgent 现在是用户可调用的（可直接通过 @explore 使用）
        agent.Definition.UserInvocable.Should().BeTrue();
    }

    [Fact]
    public void Definition_HasNoSubAgents()
    {
        var agent = new ExploreAgent(_apiService);

        agent.Definition.SubAgents.Should().BeEmpty();
    }

    [Fact]
    public void Definition_HasNoHandoffs()
    {
        var agent = new ExploreAgent(_apiService);

        agent.Definition.Handoffs.Should().BeEmpty();
    }

    [Fact]
    public void Definition_AllowedTools_AreReadOnly()
    {
        var agent = new ExploreAgent(_apiService);

        agent.Definition.AllowedTools.Should().NotContain("replace_string_in_file");
        agent.Definition.AllowedTools.Should().NotContain("create_file");
        agent.Definition.AllowedTools.Should().NotContain("delete_file");
        agent.Definition.AllowedTools.Should().NotContain("run_in_terminal");
    }

    [Fact]
    public void Definition_SystemPrompt_ContainsExploreMode()
    {
        var agent = new ExploreAgent(_apiService);

        agent.Definition.SystemPrompt.Should().Contain("Explore");
        agent.Definition.SystemPrompt.Should().Contain("强制工具使用");
    }

    #endregion

    #region DefaultReadTools

    [Fact]
    public void DefaultReadTools_ContainsCoreExplorationTools()
    {
        ExploreAgent.DefaultReadTools.Should().Contain("list_dir");
        ExploreAgent.DefaultReadTools.Should().Contain("file_search");
        ExploreAgent.DefaultReadTools.Should().Contain("grep_search");
        ExploreAgent.DefaultReadTools.Should().Contain("read_file");
        ExploreAgent.DefaultReadTools.Should().Contain("search"); // semantic_search alias
    }

    [Fact]
    public void DefaultReadTools_DoesNotContainModifyTools()
    {
        ExploreAgent.DefaultReadTools.Should().NotContain("replace_string_in_file");
        ExploreAgent.DefaultReadTools.Should().NotContain("create_file");
        ExploreAgent.DefaultReadTools.Should().NotContain("create_directory");
        ExploreAgent.DefaultReadTools.Should().NotContain("delete_file");
        ExploreAgent.DefaultReadTools.Should().NotContain("apply_patch");
        ExploreAgent.DefaultReadTools.Should().NotContain("run_in_terminal");
    }

    #endregion

    #region BuildExplorePrompt

    [Fact]
    public void BuildExplorePrompt_IncludesUserMessage()
    {
        var context = new AgentContext();

        var result = BuildExplorePromptPublic("找到认证相关的代码", context);

        result.Should().Contain("找到认证相关的代码");
        result.Should().Contain("探索任务");
    }

    [Fact]
    public void BuildExplorePrompt_WithSolutionPath_IncludesWorkspaceInfo()
    {
        var context = new AgentContext
        {
            SolutionPath = @"C:\Projects\MyApp\MyApp.sln",
        };

        var result = BuildExplorePromptPublic("探索项目结构", context);

        result.Should().Contain("MyApp.sln");
        result.Should().Contain("工作区根目录");
        result.Should().Contain("Windows 绝对路径");
    }

    [Fact]
    public void BuildExplorePrompt_WithoutSolutionPath_ExcludesWorkspaceInfo()
    {
        var context = new AgentContext
        {
            SolutionPath = null,
        };

        var result = BuildExplorePromptPublic("探索任务", context);

        result.Should().NotContain("工作区根目录");
    }

    [Fact]
    public void BuildExplorePrompt_WithFileContext_IncludesContext()
    {
        var context = new AgentContext
        {
            FileContext = "// File: src/auth.ts\nexport class AuthService {}",
        };

        var result = BuildExplorePromptPublic("分析认证模块", context);

        result.Should().Contain("附加文件上下文");
        result.Should().Contain("AuthService");
    }

    [Fact]
    public void BuildExplorePrompt_WithActivePlan_IncludesPlanInfo()
    {
        var context = new AgentContext
        {
            ActivePlan = new AgentTaskPlan
            {
                Title = "实现登录功能",
                CurrentStepIndex = 2,
                Steps = { new(), new(), new() },
            },
        };

        var result = BuildExplorePromptPublic("找到用户模型", context);

        result.Should().Contain("实现登录功能");
        result.Should().Contain("2/3");
    }

    [Fact]
    public void BuildExplorePrompt_AlwaysIncludesSearchInstructions()
    {
        var context = new AgentContext();

        var result = BuildExplorePromptPublic("任意任务", context);

        result.Should().Contain("必须使用工具");
        result.Should().Contain("list_dir");
        result.Should().Contain("file_search");
    }

    #endregion

    #region GenerateSearchKeywordsViaAi

    [Fact]
    public async Task GenerateSearchKeywordsViaAi_WithoutBuiltInTools_ReturnsEmpty()
    {
        var agent = new ExploreAgent(_apiService);
        var cts = new CancellationTokenSource();

        // No BuiltInTools set, so CallAiShortAsync would fail — but it will try to call API
        // which will throw because no mock. We catch gracefully.
        // Since we can't easily mock the API stream, we test the fallback path:
        // When AI call fails, the method returns empty set.

        // This tests that the method handles errors gracefully
        var result = await GenerateSearchKeywordsViaAiPublic(agent, "test query", null, cts.Token);

        // Should return empty set when AI call fails (no mock)
        result.Should().BeEmpty();
    }

    #endregion

    #region SourceFileExtensions

    [Fact]
    public void SourceFileExtensions_ContainsCommonExtensions()
    {
        var exts = GetSourceFileExtensionsPublic();

        exts.Should().Contain(".cs");
        exts.Should().Contain(".py");
        exts.Should().Contain(".ts");
        exts.Should().Contain(".js");
        exts.Should().Contain(".json");
        exts.Should().Contain(".md");
        exts.Should().Contain(".xaml");
        exts.Should().Contain(".csproj");
    }

    [Fact]
    public void SourceFileExtensions_ContainsPopularLanguageExtensions()
    {
        var exts = GetSourceFileExtensionsPublic();

        exts.Should().Contain(".go");
        exts.Should().Contain(".rs");
        exts.Should().Contain(".java");
        exts.Should().Contain(".kt");
        exts.Should().Contain(".swift");
    }

    [Fact]
    public void SourceFileExtensions_CaseInsensitive()
    {
        var exts = GetSourceFileExtensionsPublic();

        exts.Contains(".CS").Should().BeTrue();
        exts.Contains(".Ts").Should().BeTrue();
    }

    #endregion

    // ──────────── Reflection helpers ────────────

    private static string BuildExplorePromptPublic(string userMessage, AgentContext context)
    {
        var method = typeof(ExploreAgent).GetMethod("BuildExplorePrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { userMessage, context })!;
    }

    private static async Task<HashSet<string>> GenerateSearchKeywordsViaAiPublic(
        ExploreAgent agent, string userQuery, string? context, CancellationToken ct)
    {
        var method = typeof(ExploreAgent).GetMethod("GenerateSearchKeywordsViaAiAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<HashSet<string>>)method!.Invoke(agent, new object?[] { userQuery, context, ct })!;
        return await task;
    }

    private static HashSet<string> GetSourceFileExtensionsPublic()
    {
        var field = typeof(ExploreAgent).GetField("SourceFileExtensions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (HashSet<string>)field!.GetValue(null)!;
    }
}
