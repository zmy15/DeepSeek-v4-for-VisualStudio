using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 端点 reasoning 能力推断与 effort 档位映射（参考 CC Switch 设计）。
/// </summary>
public class ReasoningCapabilityConfigTests
{
    [Fact]
    public void Infer_DeepSeekOfficial_ReturnsDeepSeekConfig()
    {
        var config = ReasoningCapabilityConfig.Infer("https://api.deepseek.com", "deepseek-v4-pro");
        config.SupportsThinking.Should().BeTrue();
        config.SupportsEffort.Should().BeTrue();
        config.ThinkingParam.Should().Be("thinking");
        config.EffortParam.Should().Be("reasoning_effort");
        config.EffortValueMode.Should().Be("deepseek");
        config.NeedsStreamOptionsForUsage.Should().BeFalse();
    }

    [Fact]
    public void Infer_OpenAiOSeries_RequiresMaxCompletionTokensAndRejectsSampling()
    {
        var config = ReasoningCapabilityConfig.Infer("https://api.openai.com/v1", "o3-mini");
        config.UseMaxCompletionTokens.Should().BeTrue();
        config.RejectsSamplingParams.Should().BeTrue();
        config.SupportsEffort.Should().BeTrue();
        config.EffortParam.Should().Be("reasoning_effort");
    }

    [Fact]
    public void Infer_Gpt5_RequiresMaxCompletionTokensAndRejectsSampling()
    {
        var config = ReasoningCapabilityConfig.Infer("https://api.openai.com/v1", "gpt-5-codex");
        config.UseMaxCompletionTokens.Should().BeTrue();
        config.RejectsSamplingParams.Should().BeTrue();
    }

    [Fact]
    public void Infer_NonDeepSeekEndpoints_NeedStreamOptionsForUsage()
    {
        ReasoningCapabilityConfig.Infer("https://api.openai.com/v1", "gpt-4o").NeedsStreamOptionsForUsage.Should().BeTrue();
        ReasoningCapabilityConfig.Infer("https://openrouter.ai/api/v1", "x").NeedsStreamOptionsForUsage.Should().BeTrue();
        ReasoningCapabilityConfig.Infer("https://api.siliconflow.cn/v1", "x").NeedsStreamOptionsForUsage.Should().BeTrue();
        ReasoningCapabilityConfig.Infer("https://relay.example.com/v1", "deepseek-v4").NeedsStreamOptionsForUsage.Should().BeTrue();
    }

    [Fact]
    public void Infer_OpenRouter_UsesReasoningObjectWithXhighClamp()
    {
        var config = ReasoningCapabilityConfig.Infer("https://openrouter.ai/api/v1", "some-model");
        config.SupportsThinking.Should().BeFalse();
        config.SupportsEffort.Should().BeTrue();
        config.EffortParam.Should().Be("reasoning.effort");
        config.EffortValueMode.Should().Be("openrouter");
        config.MapEffort("max").Should().Be("xhigh");
        config.MapEffort("ultra").Should().Be("xhigh");
        config.MapEffort("high").Should().Be("high");
    }

    [Fact]
    public void Infer_SiliconFlow_UsesEnableThinkingWithoutEffort()
    {
        var config = ReasoningCapabilityConfig.Infer("https://api.siliconflow.cn/v1", "deepseek-v3");
        config.SupportsThinking.Should().BeTrue();
        config.SupportsEffort.Should().BeFalse();
        config.ThinkingParam.Should().Be("enable_thinking");
        config.MapEffort("max").Should().BeNull();
    }

    [Fact]
    public void Infer_KimiModel_UsesThinkingWithoutEffort()
    {
        var config = ReasoningCapabilityConfig.Infer("https://example.com/v1", "kimi-k3");
        config.SupportsThinking.Should().BeTrue();
        config.SupportsEffort.Should().BeFalse();
        config.ThinkingParam.Should().Be("thinking");
    }

    [Fact]
    public void Infer_GlmModel_UsesThinkingWithoutEffort()
    {
        var config = ReasoningCapabilityConfig.Infer("https://example.com/v1", "glm-5.3");
        config.ThinkingParam.Should().Be("thinking");
        config.SupportsEffort.Should().BeFalse();
    }

    [Fact]
    public void Infer_GlmFlashModel_IsAlwaysThinkingAndUsesEffort()
    {
        var config = ReasoningCapabilityConfig.Infer(
            "https://new-api.xiaoduoai.com/v1", "glm-5.3-flash-kingsoft");

        config.SupportsThinking.Should().BeFalse();
        config.AlwaysThinking.Should().BeTrue();
        config.SupportsEffort.Should().BeTrue();
        config.EffortParam.Should().Be("reasoning_effort");
        config.MapEffort("low").Should().Be("low");
        config.MapEffort("max").Should().Be("max");
    }

    [Fact]
    public void Infer_QwenModel_UsesEnableThinking()
    {
        var config = ReasoningCapabilityConfig.Infer("https://dashscope.aliyuncs.com/v1", "qwen3-max");
        config.ThinkingParam.Should().Be("enable_thinking");
        config.SupportsEffort.Should().BeFalse();
    }

    [Fact]
    public void Infer_MinimaxModel_UsesReasoningSplit()
    {
        var config = ReasoningCapabilityConfig.Infer("https://example.com/v1", "minimax-m2");
        config.ThinkingParam.Should().Be("reasoning_split");
    }

    [Fact]
    public void Infer_DeepSeekModelViaThirdParty_KeepsDeepSeekShape()
    {
        var config = ReasoningCapabilityConfig.Infer("https://relay.example.com/v1", "deepseek-v4-flash");
        config.EffortValueMode.Should().Be("deepseek");
        config.ThinkingParam.Should().Be("thinking");
    }

    [Fact]
    public void Infer_UnknownEndpoint_DefaultPassthroughEffort()
    {
        var config = ReasoningCapabilityConfig.Infer("https://my-llm.local/v1", "my-model");
        config.SupportsThinking.Should().BeFalse();
        config.SupportsEffort.Should().BeTrue();
        config.EffortParam.Should().Be("reasoning_effort");
        config.EffortValueMode.Should().Be("passthrough");
    }

    [Fact]
    public void MapEffort_DeepSeekMode_ClampsExtendedLevels()
    {
        var config = ReasoningCapabilityConfig.DeepSeek;
        config.MapEffort("max").Should().Be("max");
        config.MapEffort("xhigh").Should().Be("max");
        config.MapEffort("ultra").Should().Be("max");
        config.MapEffort("high").Should().Be("high");
        config.MapEffort("medium").Should().Be("high");
        config.MapEffort("disabled").Should().BeNull();
    }

    [Fact]
    public void MapEffort_PassthroughMode_KeepsOriginalValue()
    {
        var config = ReasoningCapabilityConfig.Default;
        config.MapEffort("low").Should().Be("low");
        config.MapEffort("medium").Should().Be("medium");
        config.MapEffort("high").Should().Be("high");
        config.MapEffort("max").Should().Be("max");
    }

    [Fact]
    public void MapEffort_ThinkingDisabled_ReturnsNull()
    {
        var config = ReasoningCapabilityConfig.DeepSeek;
        config.MapEffort(null).Should().BeNull();
        config.MapEffort("").Should().BeNull();
    }
}
