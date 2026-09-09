using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// DeepSeek 官方与自定义端点配置分离的来源解析。
/// </summary>
public class DeepSeekEndpointResolverTests
{
    [Fact]
    public void Resolve_NullOptions_ReturnsEmptyOfficial()
    {
        var config = DeepSeekEndpointResolver.Resolve(null);

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().BeEmpty();
        config.Model.Should().Be("deepseek-v4-pro");
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_NoBaseUrl_UsesOfficialKeyAndCatalogModel()
    {
        // 自定义配置已填但端点为空 → 不生效
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-flash");
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_WithBaseUrl_SwitchesToCustomKeyAndModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3");

        config.IsCustom.Should().BeTrue();
        config.ApiKey.Should().Be("sk-custom");
        config.Model.Should().Be("kimi-k3");
        config.BaseUrl.Should().Be("https://relay.example.com/v1");
    }

    [Fact]
    public void Resolve_CustomWithEmptyList_UsesDefaultModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "");

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("deepseek-v4-pro");
    }

    [Fact]
    public void Resolve_WhitespaceBaseUrl_TreatedAsOfficial()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "   ",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "",
            customModels: "kimi-k3");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-pro");
    }

    [Fact]
    public void Resolve_ExplicitOfficial_OverridesCustomEndpoint()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3",
            activeModelSource: "official");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-flash");
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_ExplicitCustomWithoutBaseUrl_FallsBackToOfficial()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3",
            activeModelSource: "custom");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public void Resolve_ExplicitCustomWithBaseUrl_UsesCustomConfig()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3",
            activeModelSource: "custom");

        config.IsCustom.Should().BeTrue();
        config.ApiKey.Should().Be("sk-custom");
        config.Model.Should().Be("kimi-k3");
        config.BaseUrl.Should().Be("https://relay.example.com/v1");
    }

    [Fact]
    public void Resolve_CustomModelList_UsesActiveModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3; glm-5.3-flash; qwen3.8-max",
            activeCustomModel: "glm-5.3-flash");

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("glm-5.3-flash");
    }

    [Fact]
    public void Resolve_InactiveCustomModel_FallsBackToFirstListItem()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3, glm-5.3-flash",
            activeCustomModel: "removed-model");

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("kimi-k3");
    }

    // ── IsVision 视觉能力权威判定（方案 A：用户手动标记自定义多模态模型）──

    [Fact]
    public void Resolve_CustomVisionList_MarksActiveModelAsVision()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "gpt-4o",
            customVisionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeTrue();
        config.IsVision.Should().BeTrue();
    }

    [Fact]
    public void Resolve_CustomVisionList_ActiveNotMarked_NotVision()
    {
        // 名单不含当前激活模型 → 按纯文本模型处理
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "kimi-k3",
            customVisionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("kimi-k3");
        config.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_CustomVisionListEmpty_NotVision()
    {
        // customVisionModels 为 null（未配置）→ 自定义模型默认按纯文本处理
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "gpt-4o",
            customVisionModels: null);

        config.IsCustom.Should().BeTrue();
        config.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_CustomVisionList_IgnoreCase()
    {
        // 名单大小写与激活模型不一致时仍应命中（与 ParseCustomModels 的 OrdinalIgnoreCase 语义一致）
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "gpt-4o",
            customVisionModels: new[] { "GPT-4O" });

        config.IsCustom.Should().BeTrue();
        config.IsVision.Should().BeTrue();
    }

    [Fact]
    public void Resolve_OfficialVisionModel_IsVision()
    {
        // 官方模式：视觉判定沿用官方模型目录（deepseek-v4-flash-vision-exp）
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash-vision-exp",
            customModels: "gpt-4o");

        config.IsCustom.Should().BeFalse();
        config.IsVision.Should().BeTrue();
    }

    [Fact]
    public void Resolve_OfficialNonVisionModel_NotVision()
    {
        // 官方模式不受自定义视觉名单影响（即使名单包含同名模型）
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o",
            customVisionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeFalse();
        config.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_CustomVisionFallbackFirstItem_MarksFallbackAsVision()
    {
        // 激活模型失效回退到列表第一项时，视觉判定应跟随回退后的生效模型
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o, kimi-k3",
            activeCustomModel: "removed-model",
            customVisionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("gpt-4o");
        config.IsVision.Should().BeTrue();
    }
}
