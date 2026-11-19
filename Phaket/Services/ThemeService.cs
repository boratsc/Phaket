using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;

namespace Phaket.Services;

public sealed class ThemeService : INotifyPropertyChanged
{
    private readonly string settingsPath;
    private string currentTheme;

    public ThemeService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "settings.json");
        currentTheme = ReadTheme(this.settingsPath);
        ApplyVariant();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentTheme => currentTheme;
    public bool IsDark => currentTheme == "dark";

    public void Toggle() => SetTheme(IsDark ? "light" : "dark");

    public bool SetTheme(string theme)
    {
        if (theme is not ("dark" or "light")) throw new ArgumentOutOfRangeException(nameof(theme));
        if (currentTheme == theme) return Persist();

        currentTheme = theme;
        ApplyVariant();
        PropertyChanged?.Invoke(this, new(nameof(CurrentTheme)));
        PropertyChanged?.Invoke(this, new(nameof(IsDark)));
        return Persist();
    }

    private void ApplyVariant()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = currentTheme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private static string ReadTheme(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("theme", out var value)
                && value.GetString() == "light" ? "light" : "dark";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "dark";
        }
    }

    // Shares the settings.json gate with the device nick and the preferences page.
    private bool Persist() => SettingsFile.TryUpdate(settingsPath, settings => settings["theme"] = currentTheme);
}
