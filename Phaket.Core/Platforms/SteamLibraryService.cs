using Phaket.Core.Omrl;
using System.Text.Json;

namespace Phaket.Core.Platforms;

public sealed record OwnedSteamGame(string AppId, string Name, int? PlaytimeMinutes, GameDefinition? Definition, string Source, bool LowerConfidence);

public sealed class SteamLibraryService(string steamId, IReadOnlyList<GameDefinition> definitions, string? dataRoot = null, string? userProfile = null)
{
    public async Task<IReadOnlyList<OwnedSteamGame>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        if (steamId.Length != 17 || steamId.Any(c => c is < '0' or > '9')) throw new ArgumentException("Invalid Steam ID", nameof(steamId));
        ct.ThrowIfCancellationRequested();
        var adapter = new SteamAdapter(definitions);
        var local = adapter.DetectInstallations(OperatingSystem.IsWindows() ? "windows" : "linux", userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var games = local.Select(i => new OwnedSteamGame(i.Manifest.AppId, i.Manifest.Name, null,
            definitions.FirstOrDefault(d => d.StoreIds?.GetValueOrDefault("steam") == i.Manifest.AppId), "local", false)).ToArray();
        var registered = Path.Combine(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket"), "steam-installs.json");
        if (!File.Exists(registered)) return games;
        try
        {
            var records = JsonSerializer.Deserialize<List<DepotRecordedInstallation>>(await File.ReadAllTextAsync(registered, ct)) ?? [];
            return games.Concat(records.Where(r => !games.Any(g => g.AppId == r.AppId)).Select(r => new OwnedSteamGame(r.AppId, Path.GetFileName(r.TargetPath), null,
                definitions.FirstOrDefault(d => d.StoreIds?.GetValueOrDefault("steam") == r.AppId), "local", false)))
                .DistinctBy(g => g.AppId).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return games; }
    }
}
