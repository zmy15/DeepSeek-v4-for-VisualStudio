using DeepSeek_v4_for_VisualStudio.ToolWindows;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.ToolWindows;

/// <summary>
/// TerminalWindowHelper 单元测试。
/// 注意：WriteCodeToFileAsync / ApplyCodeToActiveDocumentAsync / ShowFinalDiffAsync
/// 等公开方法深度依赖 VS SDK（ITextBuffer / IVsInvisibleEditor / IWpfTextView），
/// 属于集成测试范畴。此处仅测试可独立验证的纯逻辑。
/// </summary>
public class TerminalWindowHelperTests
{
    #region Type Structure

    [Fact]
    public void Class_IsStatic()
    {
        typeof(TerminalWindowHelper).IsAbstract.Should().BeTrue();
        typeof(TerminalWindowHelper).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void Class_HasShowFinalDiffMethod()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("ShowFinalDiffAsync");
        method.Should().NotBeNull();
        method!.IsPublic.Should().BeTrue();
        method.IsStatic.Should().BeTrue();
    }

    [Fact]
    public void Class_HasWriteCodeToFileMethod()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("WriteCodeToFileAsync");
        method.Should().NotBeNull();
        method!.IsPublic.Should().BeTrue();
        method.IsStatic.Should().BeTrue();
    }

    [Fact]
    public void Class_HasApplyCodeToActiveDocumentMethod()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("ApplyCodeToActiveDocumentAsync");
        method.Should().NotBeNull();
        method!.IsPublic.Should().BeTrue();
        method.IsStatic.Should().BeTrue();
    }

    #endregion

    #region Method Signatures

    [Fact]
    public void WriteCodeToFileAsync_ReturnsTaskOfNullableString()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("WriteCodeToFileAsync")!;
        var returnType = method.ReturnType;

        returnType.Should().Be(typeof(System.Threading.Tasks.Task<string?>));
    }

    [Fact]
    public void WriteCodeToFileAsync_HasTwoParameters()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("WriteCodeToFileAsync")!;
        var parameters = method.GetParameters();

        parameters.Should().HaveCount(2);
        parameters[0].Name.Should().Be("filePath");
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].Name.Should().Be("newContent");
        parameters[1].ParameterType.Should().Be(typeof(string));
    }

    [Fact]
    public void ShowFinalDiffAsync_HasThreeParameters()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("ShowFinalDiffAsync")!;
        var parameters = method.GetParameters();

        // 写穿模式新增了可选的 workspace 撤销追踪参数（第 4 个，带默认值）
        parameters.Should().HaveCount(4);
        parameters[0].ParameterType.Should().Be(typeof(string)); // oldContent
        parameters[1].ParameterType.Should().Be(typeof(string)); // newContent
        parameters[2].ParameterType.Should().Be(typeof(string)); // filePath
        parameters[3].ParameterType.Should().Be(
            typeof(DeepSeek_v4_for_VisualStudio.Services.Editing.StagedEditWorkspace)); // workspace
        parameters[3].HasDefaultValue.Should().BeTrue();
    }

    [Fact]
    public void ApplyCodeToActiveDocumentAsync_HasOneParameter()
    {
        var method = typeof(TerminalWindowHelper).GetMethod("ApplyCodeToActiveDocumentAsync")!;
        var parameters = method.GetParameters();

        parameters.Should().HaveCount(1);
        parameters[0].ParameterType.Should().Be(typeof(string)); // code
    }

    #endregion
}
