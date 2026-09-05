using System;
using DeepSeek_v4_for_VisualStudio.Services.Tools;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Tools
{
    /// <summary>
    /// 工具超时分档策略单元测试（P2，序号 22）。
    /// </summary>
    public class ToolTimeoutPolicyTests
    {
        [Theory]
        [InlineData("memory", 10)]
        [InlineData("get_errors", 20)]
        [InlineData("symbol_search", 20)]
        [InlineData("fetch_webpage", 45)]
        public void GetTimeout_KnownTools_UseTieredValues(string tool, int seconds)
        {
            ToolTimeoutPolicy.GetTimeout(tool).Should().Be(TimeSpan.FromSeconds(seconds));
        }

        [Theory]
        [InlineData("mcpServer_someTool")]
        [InlineData("unknown_tool")]
        [InlineData("")]
        public void GetTimeout_UnknownOrMcp_FallsBackToDefault(string tool)
        {
            ToolTimeoutPolicy.GetTimeout(tool).Should().Be(TimeSpan.FromSeconds(60));
        }

        [Fact]
        public void InteractiveTools_AreDocumentedAsExempt_ByBaseAgent_NotByPolicy()
        {
            // 防回归说明：read/edit/build/terminal 等交互式工具的超时豁免逻辑在
            // BaseAgent.IsInteractiveTool 中（需要审批弹窗 / 构建内部控时），
            // 本策略不应试图对它们返回短超时 —— 若未来把豁免迁入本类，
            // 应同步迁移对应单测并删除此哨兵用例。
            ToolTimeoutPolicy.GetTimeout("build_solution").Should().Be(TimeSpan.FromSeconds(60),
                "build_solution 由 IsInteractiveTool 豁免超时，策略值不会被实际使用");
        }
    }
}
