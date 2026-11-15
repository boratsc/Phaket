using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

public sealed record OwnedSteamGame(string AppId, string Name, int? PlaytimeMinutes, GameDefinition? Definition, string Source, bool LowerConfidence);

public sealed class SteamLibraryService(Func<Task<string?>> apiKeyProvider, string steamId, HttpClient http, IReadOnlyList<GameDefinition> definitions, bool allowCommunityXml = false)
{
    public async Task<IReadOnlyList<OwnedSteamGame>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        if (steamId.Length != 17 || steamId.Any(c => c is < '0' or > '9')) throw new ArgumentException("Invalid Steam ID", nameof(steamId));
        string? key = await apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key) && !allowCommunityXml)
            throw new PhaketException("STEAM_API_KEY_MISSING", ("loc", "https://steamcommunity.com/dev/apikey"));
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return await CommunityAsync(ct);
            string url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key=" + Uri.EscapeDataString(key) + "&steamid=" + steamId + "&include_appinfo=1&include_played_free_games=1&format=json";
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new PhaketException("STEAM_API_KEY_INVALID");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var result = doc.RootElement.GetProperty("response");
            if (!result.TryGetProperty("games", out var games)) throw new PhaketException("STEAM_LIBRARY_PRIVATE");
            return games.EnumerateArray().Select(g => Make(g.GetProperty("appid").GetInt32().ToString(CultureInfo.InvariantCulture), g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", g.TryGetProperty("playtime_forever", out var p) ? p.GetInt32() : null, false)).ToArray();
        }
        catch (PhaketException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or XmlException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new PhaketException("STEAM_LIBRARY_UNAVAILABLE"); }
    }

    private async Task<IReadOnlyList<OwnedSteamGame>> CommunityAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://steamcommunity.com/profiles/{steamId}/games?tab=all&xml=1", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) == true)
            throw new PhaketException("STEAM_LIBRARY_PRIVATE");
        response.EnsureSuccessStatusCode();
        using var reader = XmlReader.Create(await response.Content.ReadAsStreamAsync(ct), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true });
        var doc = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var root = doc.Root;
        if (root?.Name.LocalName != "gamesList" || root.Element("games") is not { } games) throw new PhaketException("STEAM_LIBRARY_PRIVATE");
        return games.Elements("game").Select(g => Make(g.Element("appID")?.Value ?? "", g.Element("name")?.Value ?? "", null, true)).Where(g => g.AppId.Length > 0).ToArray();
    }

    private OwnedSteamGame Make(string id, string name, int? playtime, bool xml) => new(id, name, playtime,
        definitions.FirstOrDefault(d => d.StoreIds?.GetValueOrDefault("steam") == id), xml ? "community-xml" : "web-api", xml);
}
