using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class LocalizedAttributesTests
{
    [Fact]
    public void DescriptionAttribute_UpdatesWhenLanguageChanges()
    {
        var attribute = new LocalizedDescriptionAttribute("settings.themeMode.description");

        LocalizationService.Instance.SetLanguage("en");
        LocalizationService.Instance.CurrentLanguage.Should().Be("en");
        LocalizationService.Instance["settings.themeMode.description"].Should().Contain("Auto");
        string english = attribute.Description;

        LocalizationService.Instance.SetLanguage("zh-CN");
        string chinese = attribute.Description;

        english.Should().NotBe(chinese);
        english.Should().Contain("Auto");
        chinese.Should().Contain("自动");
    }
}
