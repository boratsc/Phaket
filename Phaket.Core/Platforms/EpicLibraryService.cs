using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

public sealed record OwnedEpicGame(string AppName, string Namespace, string CatalogItemId, string Title, bool Installable, string Source);

public sealed class EpicLibraryService
{
    private const string Library = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true";
    private const string Assets = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live";
    private const string Entitlements = "https://entitlement-public-service-prod08.ol.epicgames.com/entitlement/api/account/{0}/entitlements?start=0&count=1000";
    private readonly Func<Task<string>> tokenProvider;
    private readonly HttpClient http;
    private readonly string accountId;
    private readonly IReadOnlyList<GameDefinition> definitions;

    public EpicLibraryService(Func<Task<string>> tokenProvider, HttpClient http, string accountId, IReadOnlyList<GameDefinition> definitions)
    { this.tokenProvider = tokenProvider; this.http = http; this.accountId = accountId; this.definitions = definitions; }

    public async Task<IReadOnlyList<OwnedEpicGame>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        string token = await tokenProvider();
        var byId = new Dictionary<string, OwnedEpicGame>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; page < 20; page++)
            {
                var url = Library + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                using var pageRequest = new HttpRequestMessage(HttpMethod.Get, url);
                pageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(pageRequest, ct);
                Ensure(response);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in doc.RootElement.GetProperty("records").EnumerateArray())
                {
                    var game = Parse(item, false, "library");
                if (game is not null) byId[Key(game)] = game;
                }
                cursor = doc.RootElement.TryGetProperty("responseMetadata", out var meta) && meta.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                if (string.IsNullOrEmpty(cursor) || !seenCursors.Add(cursor)) break;
            }
        }
        catch (PhaketException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new PhaketException("EPIC_LIBRARY_UNAVAILABLE", ("source", "library")); }

        await MergeAsync(Assets, "assets", byId, true, token, ct);
        await MergeAsync(string.Format(Entitlements, Uri.EscapeDataString(accountId)), "entitlements", byId, false, token, ct);
        return byId.Values.ToArray();
    }

    private async Task MergeAsync(string url, string source, Dictionary<string, OwnedEpicGame> games, bool installable, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            Ensure(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var records = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("records");
            foreach (var item in records.EnumerateArray())
            {
                var game = Parse(item, installable, source);
                if (game is null) continue;
                string key = Key(game);
                if (games.TryGetValue(key, out var old)) games[key] = old with { Installable = old.Installable || installable, Source = old.Source + "+" + source, Title = old.Title.Length > 0 ? old.Title : game.Title };
                else games[key] = game;
            }
        }
        catch (PhaketException ex) when (ex.Error.Code == "EPIC_TOKEN_INVALID") { throw; }
        catch (PhaketException) { /* optional source unavailable; retain other sources */ }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* optional source unavailable; retain other sources */ }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PhaketException("EPIC_TOKEN_INVALID", ("loc", "relogin"));
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Epic API unavailable");
    }

    private static string Key(OwnedEpicGame game) => game.CatalogItemId.Length > 0 ? game.CatalogItemId : game.AppName;

    private OwnedEpicGame? Parse(JsonElement item, bool installable, string source)
    {
        string Read(string key) => item.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        string app = Read("appName"), ns = Read("namespace"), catalog = Read("catalogItemId");
        if (string.IsNullOrWhiteSpace(app))
        {
            System.Diagnostics.Trace.TraceWarning("Ignoring Epic library item with empty appName from {0}", source);
            return null;
        }
        if (catalog.Length == 0 && item.TryGetProperty("entitlementName", out var entitlement)) catalog = entitlement.GetString() ?? "";
        if (app.Length == 0 && catalog.Length == 0) return null;
        string title = Read("title");
        if (title.Length == 0 && item.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("title", out var metaTitle)) title = metaTitle.GetString() ?? "";
        var definition = definitions.FirstOrDefault(d => d.StoreIds?.TryGetValue("epic", out var id) == true && (id == app || id == catalog || id == ns));
        if (definition is null && (app.Equals("963137e4c29d4c79a81323b8fab03a40", StringComparison.OrdinalIgnoreCase) || catalog.Equals("729a86a5146640a2ace9e8c595414c56", StringComparison.OrdinalIgnoreCase)))
            definition = definitions.FirstOrDefault(d => d.Slug.Equals("among-us", StringComparison.OrdinalIgnoreCase));
        title = title.Length > 0 ? title : definition?.Title?.GetValueOrDefault("en") ?? app;
        return new OwnedEpicGame(app, ns, catalog, title, installable, source);
    }
}
