using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 前缀契约回归测试（PR #47 三轮评审）：
/// 工具结果的成败判定依赖 "Error: "/"Timeout: "/"[BLOCKED] " 文本前缀。
/// 此前 deleteFile.notFound / applyPatch.noPatchBlock / applyPatch.noAction
/// 等失败结果不带前缀，被 GetResultSummary / MultiReplace / EditAgent 成功追踪
/// 误判为成功（如"文件不存在"被摘要为"文件已删除"）。本组测试锁定修复后的契约。
/// </summary>
public class ToolResultPrefixContractTests
{
    [Theory]
    [InlineData("Error: 文件不存在: foo.cs", ToolResultKind.ToolError)]
    [InlineData("Error: apply_patch: 未检测到 *** Begin Patch 块。", ToolResultKind.ToolError)]
    [InlineData("Error: apply_patch: no action performed", ToolResultKind.ToolError)]
    [InlineData("Timeout: 工具 read_file 执行超时（20s），已跳过。", ToolResultKind.Timeout)]
    [InlineData("[BLOCKED] 危险命令被拦截", ToolResultKind.Blocked)]
    [InlineData("已删除文件: foo.cs", ToolResultKind.Success)]
    [InlineData("补丁应用完成", ToolResultKind.Success)]
    [InlineData("", ToolResultKind.Success)]
    public void Classify_PrefixSemantics(string output, ToolResultKind expected)
    {
        ToolExecutionOutcome.Classify(output).Should().Be(expected);
    }

    [Theory]
    [InlineData("tool.deleteFile.notFound")]
    [InlineData("tool.applyPatch.noPatchBlock")]
    [InlineData("tool.applyPatch.noAction")]
    public void FailureLocaleValues_CarryErrorPrefix(string key)
    {
        // 失败语义的资源值必须以 "Error: " 开头，否则跨语言的成败判定会被破坏
        var value = LocalizationService.Instance[key];
        value.Should().StartWith("Error: ",
            $"资源键 {key} 表示失败，必须携带 Error: 前缀（en/zh 双语同规则）");
    }

    [Fact]
    public void GetToolResultSummary_TimeoutResult_IsPassedThrough()
    {
        // BuiltInToolService.GetToolResultSummary 此前缺 Timeout: 分支，
        // 超时结果会落入文件列表解析路径生成误导性摘要。
        var summary = BuiltInToolService.GetToolResultSummary(
            "read_file", "Timeout: 工具 read_file 执行超时（20s），已跳过。");
        summary.Should().StartWith("Timeout: ", "超时结果必须原样透出，不得进入成功摘要解析");
    }

    [Fact]
    public void GetToolResultSummary_BlockedResult_IsPassedThrough()
    {
        var summary = BuiltInToolService.GetToolResultSummary(
            "run_in_terminal", "[BLOCKED] 危险命令被拦截");
        summary.Should().StartWith("[BLOCKED] ");
    }

    [Fact]
    public void DeleteFile_GetResultSummary_NotFoundIsNotReportedAsDeleted()
    {
        // 修复前：GetResultSummary 只短路 "Error: "，"文件不存在: x"（无前缀）
        // 会被摘要为 "文件已删除" —— 确定性误判。
        var tool = new DeepSeek_v4_for_VisualStudio.Services.BuiltInTools.DeleteFileTool();
        var summary = tool.GetResultSummary("Error: 文件不存在: ghost.cs");
        summary.Should().NotBe(LocalizationService.Instance["tool.deleteFile.deleted"]);
    }
}
