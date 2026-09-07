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
            customModelName: "kimi-k3");

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
            customModelName: "kimi-k3");

        config.IsCustom.Should().BeTrue();
        config.ApiKey.Should().Be("sk-custom");
        config.Model.Should().Be("kimi-k3");
        config.BaseUrl.Should().Be("https://relay.example.com/v1");
    }

    [Fact]
    public void Resolve_CustomWithoutModelName_FallsBackToCatalogModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-pro",
            customModelName: "");

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
            customModelName: "kimi-k3");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-pro");
    }
}
