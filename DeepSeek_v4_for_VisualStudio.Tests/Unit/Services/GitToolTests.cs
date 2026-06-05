using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// GitTool 单元测试 — 验证 git 工具的 Name、Definition、DisplayText、ResultSummary
/// 以及 ExecuteAsync 的参数校验、操作分类、阻塞规则。
/// </summary>
public class GitToolTests
{
    private static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
               ?? new Dictionary<string, JsonElement>();
    }

    #region Tool Identity

    [Fact]
    public void GitTool_HasCorrectName()
    {
        new GitTool().Name.Should().Be("git");
    }

    [Fact]
    public void GitTool_Definition_TypeIsFunction()
    {
        new GitTool().GetDefinition().Type.Should().Be("function");
    }

    [Fact]
    public void GitTool_Definition_HasNonEmptyName()
    {
        new GitTool().GetDefinition().Function.Name.Should().Be("git");
    }

    [Fact]
    public void GitTool_Definition_HasDescription()
    {
        new GitTool().GetDefinition().Function.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GitTool_Definition_HasOperationParameter()
    {
        // Verify that the 'operation' parameter is in the required list
        // The Parameters is a dynamic object; we serialize it to check
        var def = new GitTool().GetDefinition();
        var json = JsonSerializer.Serialize(def.Function.Parameters);
        json.Should().Contain("operation");
        json.Should().Contain("required");
    }

    [Fact]
    public void GitTool_IsGitAvailable_DoesNotThrow()
    {
        // Git detection should not throw even if git is not installed
        var act = () => GitTool.IsGitAvailable;
        act.Should().NotThrow();
    }

    [Fact]
    public void GitTool_GitVersion_IsEmptyWhenNotAvailable()
    {
        if (!GitTool.IsGitAvailable)
            GitTool.GitVersion.Should().BeEmpty();
    }

    #endregion

    #region DisplayText

    [Fact]
    public void GetDisplayText_Status_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"status\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
        text.Should().ContainAny("status", "Status", "状态");
    }

    [Fact]
    public void GetDisplayText_Diff_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"diff\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Log_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"log\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Commit_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"commit\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Push_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"push\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Stash_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"stash\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Reset_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"reset\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_AllOperations_ReturnNonEmpty()
    {
        var operations = new[] { "status", "diff", "log", "add", "commit", "branch", "checkout", "pull", "push", "stash", "reset" };
        var tool = new GitTool();
        foreach (var op in operations)
        {
            var args = ParseArgs($"{{\"operation\": \"{op}\"}}");
            var text = tool.GetDisplayText(args);
            text.Should().NotBeNullOrEmpty($"display text for '{op}' should not be empty");
        }
    }

    #endregion

    #region ResultSummary

    [Fact]
    public void GetResultSummary_Empty_ReturnsNoResult()
    {
        new GitTool().GetResultSummary("").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetResultSummary_Success_ReturnsSuccess()
    {
        var summary = new GitTool().GetResultSummary("exit code: 0");
        summary.Should().Contain("✅");
    }

    [Fact]
    public void GetResultSummary_Error_PreservesError()
    {
        var summary = new GitTool().GetResultSummary("❌ something failed");
        summary.Should().Contain("❌");
    }

    [Fact]
    public void GetResultSummary_Blocked_PreservesBlocked()
    {
        var summary = new GitTool().GetResultSummary("⛔ blocked operation");
        summary.Should().Contain("⛔");
    }

    #endregion

    #region ExecuteAsync — Parameter Validation (no git needed)

    [Fact]
    public async Task ExecuteAsync_MissingOperation_ReturnsError()
    {
        var args = ParseArgs("{}");
        var result = await new GitTool().ExecuteAsync(args, null);
        result.Should().Contain("❌");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_ReturnsError()
    {
        var args = ParseArgs("{\"operation\": \"invalid_op\"}");
        var result = await new GitTool().ExecuteAsync(args, null);
        result.Should().Contain("❌");
        result.Should().Contain("invalid_op");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyOperation_ReturnsError()
    {
        var args = ParseArgs("{\"operation\": \"\"}");
        var result = await new GitTool().ExecuteAsync(args, null);
        result.Should().Contain("❌");
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentWorkspace_ReturnsRepoError()
    {
        // Skip if git is not installed
        if (!GitTool.IsGitAvailable)
            return;

        var args = ParseArgs("{\"operation\": \"status\"}");
        var result = await new GitTool().ExecuteAsync(args, "Z:\\NonExistent\\Path");
        // Should fail with either no-repo or directory-not-found error
        result.Should().ContainAny("❌", "git");
    }

    #endregion

    #region ExecuteAsync — Blocked Operations

    [Fact]
    public async Task ExecuteAsync_ResetHard_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"reset\", \"mode\": \"hard\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
        result.Should().MatchRegex("(?i)reset.*hard|hard.*reset");
    }

    [Fact]
    public async Task ExecuteAsync_PushForceMain_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"push\", \"branch\": \"main\", \"force\": true}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
    }

    [Fact]
    public async Task ExecuteAsync_PushForceMaster_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"push\", \"branch\": \"master\", \"force\": true}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
    }

    [Fact]
    public async Task ExecuteAsync_PushForce_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        // Any force push should be blocked
        var args = ParseArgs("{\"operation\": \"push\", \"branch\": \"feature/test\", \"force\": true}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
    }

    [Fact]
    public async Task ExecuteAsync_BranchForceDelete_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"branch\", \"branch\": \"test\", \"delete\": true, \"force\": true}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
    }

    [Fact]
    public async Task ExecuteAsync_CommitWithoutMessage_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"commit\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
    }

    [Fact]
    public async Task ExecuteAsync_CheckoutWithoutBranch_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"checkout\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("⛔");
    }

    #endregion

    #region ExecuteAsync — Agent Permission Check

    [Fact]
    public async Task ExecuteAsync_ExploreAgent_WriteOperation_Blocked()
    {
        // Skip if git or repo unavailable — agent check runs after those checks
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Explore;
        try
        {
            var args = ParseArgs("{\"operation\": \"add\", \"files\": [\"test.cs\"]}");
            var result = await new GitTool().ExecuteAsync(args, root);
            // Explore agent should be blocked from write operations
            result.Should().Contain("⛔");
            result.Should().MatchRegex("(?i)explore|不允许|not permitted|Agent");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_EditAgent_WriteOperation_NotBlockedByAgent()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Edit;
        try
        {
            var args = ParseArgs("{\"operation\": \"commit\", \"message\": \"test\"}");
            var result = await new GitTool().ExecuteAsync(args, root);
            // Should NOT contain the agent-blocked message (agent permission error in Chinese)
            result.Should().NotContain("不允许");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_NullAgentType_WriteOperation_NotBlocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = null;
        // Use branch --list (safe read operation classified under "branch")
        var args = ParseArgs("{\"operation\": \"branch\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().NotContain("⛔");
        result.Should().NotContain("Agent");
        result.Should().Contain("退出码: 0");
    }

    #endregion

    #region ExecuteAsync — Read-Only Operations (Requires Git)

    [Fact]
    public async Task ExecuteAsync_Status_WithGitRepo_SucceedsOrGivesMeaningfulError()
    {
        if (!GitTool.IsGitAvailable)
            return; // Skip if git not available

        // Use the actual project directory which is a git repo
        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"status\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        // Should either return status output or an error about permissions/state
        result.Should().NotBeNullOrEmpty();
        // Should not be a "not installed" or "unknown operation" error
        result.Should().NotContain("git 未安装");
        result.Should().NotContain("not installed");
        result.Should().NotContain("未知操作");
    }

    [Fact]
    public async Task ExecuteAsync_Log_WithGitRepo_ReturnsCommitHistory()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"log\", \"count\": 3, \"oneline\": true}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    [Fact]
    public async Task ExecuteAsync_Diff_WithGitRepo_ReturnsOutput()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"diff\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    #endregion

    #region ExecuteAsync — Write Operations (Requires Git + Careful)

    [Fact]
    public async Task ExecuteAsync_BranchList_WithGitRepo_Succeeds()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"branch\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    [Fact]
    public async Task ExecuteAsync_StashList_WithGitRepo_Succeeds()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"stash\", \"mode\": \"list\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    [Fact]
    public async Task ExecuteAsync_AddWithSpecificFile_ReturnsExpectedOutput()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        // Stage a file that definitely exists
        var args = ParseArgs("{\"operation\": \"add\", \"files\": [\"README.md\"]}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        // If README.md is already tracked, it may have no output; that's OK
        // git add on an already-staged file returns exit code 0 with no stdout
    }

    #endregion

    #region Git Detection

    [Fact]
    public void IsGitAvailable_ReturnsBoolean()
    {
        // Should not throw when accessed
        var available = GitTool.IsGitAvailable;
        // available is a bool — just verify it doesn't throw
    }

    [Fact]
    public void GitVersion_IsNonEmpty_WhenGitAvailable()
    {
        if (GitTool.IsGitAvailable)
        {
            GitTool.GitVersion.Should().NotBeNullOrEmpty();
            GitTool.GitVersion.Should().Contain("git version");
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the project root directory (the git repo root).
    /// </summary>
    private static string? GetProjectRoot()
    {
        // Walk up from the test assembly location to find the repo root
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, ".git")))
                    return dir;
                var parent = System.IO.Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    #endregion
}
