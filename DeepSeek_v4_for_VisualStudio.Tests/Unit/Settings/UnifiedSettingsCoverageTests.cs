using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// Keeps Unified Settings declarations, the legacy DialogPage properties, and the
/// synchronization bridge aligned when non-sensitive options are added or removed.
/// </summary>
public class UnifiedSettingsCoverageTests
{
    private const string CategoryPrefix = "deepseekGeneral.";
    private static readonly string[] GuideSettingIds =
    {
        "deepseekCustomApiKeyGuide",
        "deepseekApiKeyGuide",
        "deepseekTestConnectionGuide",
        "deepseekCustomModelPickerGuide",
    };

    private static readonly (string OptionProperty, string SettingId)[] ExpectedCoverage =
    {
        (nameof(DeepSeekOptionsPage.SystemPrompt), "deepseekSystemPrompt"),
        (nameof(DeepSeekOptionsPage.SystemPromptEn), "deepseekSystemPromptEn"),
        (nameof(DeepSeekOptionsPage.SelectedModel), "deepseekModel"),
        (nameof(DeepSeekOptionsPage.ApiBaseUrl), "deepseekApiBaseUrl"),
        (nameof(DeepSeekOptionsPage.CustomModelName), "deepseekCustomModelName"),
        (nameof(DeepSeekOptionsPage.CustomVisionModels), "deepseekCustomVisionModels"),
        (nameof(DeepSeekOptionsPage.ActiveModelSource), "deepseekModelSource"),
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

        declaredIds.Should().HaveCount(40);
        foreach (var guideSettingId in GuideSettingIds)
            declaredIds.Should().Contain(guideSettingId);

        var synchronizedIds = declaredIds
            .Where(id => !GuideSettingIds.Contains(id, StringComparer.Ordinal))
            .ToList();

        synchronizedIds.Should().HaveCount(36);
        boundMonikers.Should().HaveCount(36);
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
        ExpectedCoverage.Should().HaveCount(36);

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

    [Fact]
    public void UnifiedSettings_ResourceTokens_AreDefinedInEveryLocale()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot, "Settings", "DeepSeekUnifiedSettings.cs"));
        var resourceKeys = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in Regex.Matches(source, @"%([^%]+)%"))
        {
            var key = match.Groups[1].Value;
            if (!resourceKeys.Contains(key, StringComparer.Ordinal))
                resourceKeys.Add(key);
        }

        resourceKeys.Should().NotBeEmpty();

        var resourceFiles = new[]
        {
            Path.Combine(repositoryRoot, ".vsextension", "string-resources.json"),
            Path.Combine(repositoryRoot, ".vsextension", "zh-Hans", "string-resources.json"),
        };

        foreach (var resourceFile in resourceFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resourceFile));
            var missingKeys = resourceKeys
                .Where(key => !document.RootElement.TryGetProperty(key, out _))
                .ToList();

            missingKeys.Should().BeEmpty(
                $"{Path.GetFileName(resourceFile)} 缺少资源键: {string.Join(", ", missingKeys)}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var hasSettingsSource = File.Exists(Path.Combine(
                directory.FullName, "Settings", "DeepSeekUnifiedSettings.cs"));
            var hasExtensionResources = Directory.Exists(Path.Combine(
                directory.FullName, ".vsextension"));
            if (hasSettingsSource && hasExtensionResources)
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位测试仓库根目录");
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
