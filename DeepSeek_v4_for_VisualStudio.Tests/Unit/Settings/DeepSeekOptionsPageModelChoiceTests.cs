using System.ComponentModel;
using System.Reflection;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class DeepSeekOptionsPageModelChoiceTests
{
    private static readonly string[] ModelSettingsProperties =
    {
        nameof(DeepSeekOptionsPage.ApiKey),
        nameof(DeepSeekOptionsPage.CustomApiKey),
        nameof(DeepSeekOptionsPage.ApiBaseUrl),
        nameof(DeepSeekOptionsPage.CustomModelName),
        nameof(DeepSeekOptionsPage.CustomVisionModels),
        nameof(DeepSeekOptionsPage.CustomModelPicker),
        nameof(DeepSeekOptionsPage.TestConnection),
        nameof(DeepSeekOptionsPage.SelectedModelChoice),
        nameof(DeepSeekOptionsPage.IsThinkingEnabled),
        nameof(DeepSeekOptionsPage.ReasoningEffort),
    };

    [Fact]
    public void ModelSettings_AreMergedIntoOneVisibleCategory()
    {
        string expectedCategory = LocalizationService.Instance["settings.category.model"];

        foreach (var propertyName in ModelSettingsProperties)
        {
            var property = typeof(DeepSeekOptionsPage).GetProperty(propertyName)!;
            property.GetCustomAttribute<BrowsableAttribute>()?.Browsable.Should().NotBe(false);
            property.GetCustomAttribute<CategoryAttribute>()?.Category.Should().Be(expectedCategory);
        }

        typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.ActiveModelSource))!
            .GetCustomAttribute<BrowsableAttribute>()!
            .Browsable
            .Should()
            .BeFalse();
    }
}
