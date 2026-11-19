using System.Text.Json;

namespace Phaket.Services;

// Catalog endpoint (mods + builds) lives in the shared settings file next to the
// language choice. The catalog needs a server; without one the UI shows an offline state.
public static class CatalogSettings
{
    public const string DefaultEndpoint = "http://127.0.0.1:18080";

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "settings.json");

    public static string SettingsFile(string appDataRoot) => Path.Combine(appDataRoot, "settings.json");

    public static bool IsValid(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // Never throws: a missing or broken settings file falls back to the local default.
    public static string Load(string? settingsPath = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath ?? DefaultSettingsPath));
            if (doc.RootElement.TryGetProperty("catalogEndpoint", out var value) &&
                value.GetString() is { } endpoint && IsValid(endpoint))
                return endpoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return DefaultEndpoint;
    }
}
