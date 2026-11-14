using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "settings.json");

    public ObservableCollection<string> Languages { get; } = [];

    [ObservableProperty]
    private string selectedLanguage = LocalizationService.Instance.CurrentLanguage;

    public SettingsViewModel()
    {
        foreach (var language in LocalizationService.Instance.AvailableLanguages)
            Languages.Add(language);
        Load();
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        LocalizationService.Instance.CurrentLanguage = value;
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("language", out var language)
                    && language.GetString() is { } value && Languages.Contains(value))
                {
                    SelectedLanguage = value;
                    LocalizationService.Instance.CurrentLanguage = value;
                }
            }
        }
        catch { /* corrupt settings never block startup */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new { language = SelectedLanguage }));
        }
        catch { /* settings persistence is best-effort */ }
    }
}
