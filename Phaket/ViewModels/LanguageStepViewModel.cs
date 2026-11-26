using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-134: the first real wizard step. It owns the language settings.json node, like the
// settings page does, so the wizard and Settings never disagree about the saved choice.
public sealed partial class LanguageStepViewModel : ObservableObject, IOnboardingStep
{
    public const string SettingsKey = "language";

    private readonly string settingsPath;
    // Where a re-run starts: the saved choice, else the UI locale, else pl-PL.
    private readonly string preset;

    [ObservableProperty] private string selectedLanguage;

    public LanguageStepViewModel(string settingsPath)
    {
        this.settingsPath = settingsPath;
        preset = Saved(settingsPath) ?? Detect(CultureInfo.CurrentUICulture) ?? LocalizationService.DefaultLanguage;
        Options = LocalizationService.Instance.AvailableLanguages
            .Select(code => new LanguageOption(code, Preview))
            .ToArray();
        OnSelectedLanguageChanged(preset);
        selectedLanguage = preset;
    }

    public string StepId => "language";
    public string Title => LocalizationService.Instance["Onboarding.Language.Title"];
    public string Description => LocalizationService.Instance["Onboarding.Language.Description"];
    // Empty until the constructor fills it: option wiring may fire before that assignment.
    public IReadOnlyList<LanguageOption> Options { get; } = [];

    // Live preview: the whole wizard re-reads its keys as soon as the selection changes.
    // Programmatic selection (preset, Reset) just syncs the radios; only a click previews.
    partial void OnSelectedLanguageChanged(string value)
    {
        foreach (var option in Options) option.SetPreset(option.Code == value);
    }

    // A clicked option is a live preview of the whole UI language.
    internal void Preview(string code)
    {
        SelectedLanguage = code;
        LocalizationService.Instance.CurrentLanguage = code;
    }

    // Next/Finish: the choice becomes the saved one; Skip leaves settings.json alone.
    public void Save() => Services.SettingsFile.TryUpdate(settingsPath, node => node[SettingsKey] = SelectedLanguage);

    // Restarting the wizard forgets nothing: the saved language is the preset again.
    public void Reset() => SelectedLanguage = preset;

    public static string? Saved(string settingsPath)
    {
        try
        {
            if (System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement is { } root
                && root.TryGetProperty(SettingsKey, out var value) && value.GetString() is { } saved
                && LocalizationService.Instance.AvailableLanguages.Contains(saved))
                return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return null;
    }

    // Exact locale, then its neutral parent, then nothing: the caller falls back to pl-PL.
    public static string? Detect(CultureInfo culture)
    {
        var available = LocalizationService.Instance.AvailableLanguages;
        foreach (var candidate in new[] { culture.Name, culture.TwoLetterISOLanguageName })
            if (available.Contains(candidate)) return candidate;
        return null;
    }
}