using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Settings;
using System.ComponentModel;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// P1-5b：LegacySettingsMigrated 一次性迁移标志必须被 DialogPage 序列化持久化。
/// 修复前缺少 DesignerSerializationVisibility(Visible)，每次启动复位 false → 迁移反复执行、
/// 反复用旧值覆盖用户新改设置。
/// </summary>
public class LegacySettingsMigratedPersistenceTests
{
    [Fact]
    public void LegacySettingsMigrated_HasDesignerSerializationVisibilityVisible()
    {
        var prop = typeof(DeepSeekOptionsPage).GetProperty(nameof(DeepSeekOptionsPage.LegacySettingsMigrated));

        prop.Should().NotBeNull();

        var attr = prop!.GetCustomAttributes(typeof(DesignerSerializationVisibilityAttribute), inherit: true)
            .Cast<DesignerSerializationVisibilityAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("必须声明 DesignerSerializationVisibility 才能被 DialogPage 序列化");
        attr!.Visibility.Should().Be(DesignerSerializationVisibility.Visible);
    }
}

/// <summary>
/// P1-7：RunInTerminalTool / GitTool 的 CurrentAgentType 必须是 AsyncLocal 线程/异步流隔离，
/// 避免并发 Agent 相互覆盖导致只读判定被静默绕过或反向误拦。
/// </summary>
public class CurrentAgentTypeAsyncLocalIsolationTests
{
    [Fact]
    public async Task ConcurrentFlows_DoNotSeeEachOthersValue()
    {
        // 基线：两个并发异步流各自设置不同 Agent 类型
        var askSees = string.Empty;
        var exploreSees = string.Empty;

        var t1 = Task.Run(async () =>
        {
            RunInTerminalTool.CurrentAgentType = AgentType.Ask;
            await Task.Yield();
            askSees = RunInTerminalTool.CurrentAgentType?.ToString() ?? "null";
            RunInTerminalTool.CurrentAgentType = null;
        });

        var t2 = Task.Run(async () =>
        {
            RunInTerminalTool.CurrentAgentType = AgentType.Explore;
            await Task.Yield();
            exploreSees = RunInTerminalTool.CurrentAgentType?.ToString() ?? "null";
            RunInTerminalTool.CurrentAgentType = null;
        });

        await Task.WhenAll(t1, t2);

        askSees.Should().Be("Ask", "Ask 流内应读到自己设置的 Agent 类型");
        exploreSees.Should().Be("Explore", "Explore 流内应读到自己设置的 Agent 类型");
    }

    [Fact]
    public async Task ChildFlowSetting_DoesNotLeakBackToParent()
    {
        RunInTerminalTool.CurrentAgentType = null;

        await Task.Run(async () =>
        {
            RunInTerminalTool.CurrentAgentType = AgentType.Edit;
            await Task.Yield();
        });

        // 子异步流内设置的值不会回传父流（AsyncLocal 沿执行上下文向下传播，不向上回写）
        RunInTerminalTool.CurrentAgentType.Should().BeNull("子流设置不应泄漏回父流");
    }

    [Fact]
    public void GitTool_CurrentAgentType_IsAsyncLocalBacked()
    {
        GitTool.CurrentAgentType = null;

        GitTool.CurrentAgentType = AgentType.Ask;
        GitTool.CurrentAgentType.Should().Be(AgentType.Ask);

        GitTool.CurrentAgentType = null;
        GitTool.CurrentAgentType.Should().BeNull();
    }
}
