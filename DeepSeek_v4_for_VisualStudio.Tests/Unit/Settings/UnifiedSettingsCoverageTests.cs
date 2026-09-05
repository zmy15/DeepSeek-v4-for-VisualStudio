using System.Reflection;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// Keeps Unified Settings declarations, the legacy DialogPage properties, and the
/// synchronization bridge aligned when non-sensitive options are added or removed.
/// </summary>
public class UnifiedSettingsCoverageTests
{
    private const string CategoryPrefix = "deepseekGeneral.";
    private const string ApiKeyGuideId = "deepseekApiKeyGuide";

    private static readonly (string OptionProperty, string SettingId)[] ExpectedCoverage =
    {
        (nameof(DeepSeekOptionsPage.SystemPrompt), "deepseekSystemPrompt"),
        (nameof(DeepSeekOptionsPage.SystemPromptEn), "deepseekSystemPromptEn"),
        (nameof(DeepSeekOptionsPage.SelectedModel), "deepseekModel"),
        (nameof(DeepSeekOptionsPage.IsThinkingEnabled), "deepseekThinking"),
        (nameof(DeepSeekOptionsPage.ReasoningEffort), "deepseekReasoningEffort"),
        (nameof(DeepSeekOptionsPage.EnableWebSearch), "deepseekWebSearch"),
        (nameof(DeepSeekOptionsPage.SearchProvider), "deepseekSearchProvider"),
        (nameof(DeepSeekOptionsPage.ShowDiffMarkersInEditor), "deepseekShowDiffMarkers"),
        (nameof(DeepSeekOptionsPage.OcrEngine), "deepseekOcrEngine"),
        (nameof(DeepSeekOptionsPage.AutoCompleteEnabled), "deepseekAutoCompleteEnabled"),
        (nameof(DeepSeekOptionsPage.AutoCompleteDelay), "deepseekAutoCompleteDelay"),
        (nameof(DeepSeekOptionsPage.AutoCompleteContinueAfterAccept), "deepseekAutoCompleteContinueAfterAccept"),
        (nameof(DeepSeekOptionsPage.TokenBudget), "deepseekTokenBudget"),
        (nameof(DeepSeekOptionsPage.EnableAutoCompression), "deepseekAutoCompression"),
        (nameof(DeepSeekOptionsPage.CompressionThreshold), "deepseekCompressionThreshold"),
        (nameof(DeepSeekOptionsPage.PreserveRecentTurns), "deepseekPreserveRecentTurns"),
        (nameof(DeepSeekOptionsPage.EnableRag), "deepseekEnableRag"),
        (nameof(DeepSeekOptionsPage.RagTopK), "deepseekRagTopK"),
        (nameof(DeepSeekOptionsPage.ShowContextStats), "deepseekContextStats"),
        (nameof(DeepSeekOptionsPage.EnableIdeContextInjection), "deepseekIdeContext"),
        (nameof(DeepSeekOptionsPage.EnableTelemetryExport), "deepseekTelemetryExport"),
        (nameof(DeepSeekOptionsPage.LlmTimeoutSeconds), "deepseekLlmTimeoutSeconds"),
        (nameof(DeepSeekOptionsPage.Language), "deepseekLanguage"),
        (nameof(DeepSeekOptionsPage.MaxToolCallRounds), "deepseekMaxToolCallRounds"),
        (nameof(DeepSeekOptionsPage.MaxRepeatedSameCall), "deepseekMaxRepeatedSameCall"),
        (nameof(DeepSeekOptionsPage.MaxConsecutiveErrors), "deepseekMaxConsecutiveErrors"),
        (nameof(DeepSeekOptionsPage.EnableAutoBuild), "deepseekEnableAutoBuild"),
        (nameof(DeepSeekOptionsPage.ApprovalMode), "deepseekApprovalMode"),
        (nameof(DeepSeekOptionsPage.ThemeModeString), "deepseekThemeMode"),
        (nameof(DeepSeekOptionsPage.InputBoxHeight), "deepseekInputBoxHeight"),
        (nameof(DeepSeekOptionsPage.BottomAreaScalePercent), "deepseekBottomAreaScalePercent"),
        (nameof(DeepSeekOptionsPage.WebView2ZoomPercent), "deepseekWebView2ZoomPercent"),
    };

    [Fact]
    public void UnifiedSettings_DeclarationsAndBindings_AreAligned()
    {
        var declaredIds = GetDeclaredSettingIds();
        var boundMonikers = GetBoundMonikers();

        declaredIds.Should().HaveCount(33);
        declaredIds.Should().Contain(ApiKeyGuideId);

        var synchronizedIds = declaredIds
            .Where(id => id != ApiKeyGuideId)
            .ToList();

        synchronizedIds.Should().HaveCount(32);
        boundMonikers.Should().HaveCount(32);
        declaredIds.GroupBy(id => id, StringComparer.Ordinal).Should().OnlyContain(group => group.Count() == 1);
        boundMonikers.GroupBy(id => id, StringComparer.Ordinal).Should().OnlyContain(group => group.Count() == 1);

        var declaredMonikers = synchronizedIds
            .Select(id => CategoryPrefix + id)
            .ToList();

        declaredMonikers.Should().BeEquivalentTo(boundMonikers);
    }

    [Fact]
    public void UnifiedSettings_CoverLegacyNonSensitiveOptions()
    {
        ExpectedCoverage.Should().HaveCount(32);

        var optionProperties = typeof(DeepSeekOptionsPage)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var declaredIds = GetDeclaredSettingIds().ToHashSet(StringComparer.Ordinal);

        foreach (var (optionProperty, settingId) in ExpectedCoverage)
        {
            optionProperties.Should().Contain(optionProperty);
            declaredIds.Should().Contain(settingId);
        }

        GetBoundMonikers().Should().NotContain(moniker =>
            moniker.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProvideOptionPage_RemainsVisible_ForSecureApiKeyEditing()
    {
        var attribute = typeof(DeepSeek_v4_for_VisualStudioPackage)
            .GetCustomAttributesData()
            .Single(data => data.AttributeType.FullName ==
                "Microsoft.VisualStudio.Shell.ProvideOptionPageAttribute");

        var isInUnifiedSettings = attribute.NamedArguments
            .Where(argument => argument.MemberName == "IsInUnifiedSettings")
            .Select(argument => argument.TypedValue.Value)
            .OfType<bool?>()
            .FirstOrDefault();

        // API keys intentionally stay out of Unified Settings, so the page that edits
        // Visual Studio Credential Storage entries must remain discoverable.
        isInUnifiedSettings.Should().NotBe(true);
    }

    private static IReadOnlyList<string> GetDeclaredSettingIds()
    {
        return typeof(DeepSeek_v4_for_VisualStudio.DeepSeekUnifiedSettings)
            .GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType.FullName?
                .StartsWith("Microsoft.VisualStudio.Extensibility.Settings.Setting+", StringComparison.Ordinal) == true)
            .Select(property => (string)property.GetValue(null)!.GetType()
                .GetProperty("Id")!
                .GetValue(property.GetValue(null))!)
            .ToList();
    }

    private static IReadOnlyList<string> GetBoundMonikers()
    {
        var field = typeof(UnifiedSettingsSync).GetField(
            "Bindings",
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull();

        var bindings = (Array)field!.GetValue(null)!;
        return Enumerable.Range(0, bindings.Length)
            .Select(index =>
            {
                var binding = bindings.GetValue(index)!;
                return (string)binding.GetType().GetField("Item1")!.GetValue(binding)!;
            })
            .ToList();
    }
}
