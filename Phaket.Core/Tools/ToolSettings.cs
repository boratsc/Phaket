using System.Text.Json;

namespace Phaket.Core.Tools;

/// <summary>Reads tool pins from the shared Phaket settings file without depending on the UI.</summary>
public static class ToolSettings
{
    public static string? GetPinnedVersion(string tool, string? settingsPath = null)
    {
        if (tool is not ("legendary" or "depotdownloader")) throw new ArgumentException("Unknown tool", nameof(tool));
        settingsPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "settings.json");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var version = json.RootElement.GetProperty("tools").GetProperty(tool).GetProperty("version");
            return version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return null; }
    }
}
