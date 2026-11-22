using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phaket.Services;

public sealed record ModpackSearchPage(IReadOnlyList<ModpackDefinition> Packages, string? NextCursor, bool IsOffline = false);
public sealed record ModpackDetail(ModpackDefinition Package, bool IsOffline = false);
public sealed record ModpackPublishResult(string Slug, int Version);

public interface IModpackApiClient
{
    Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null, CancellationToken cancellationToken = default);
    Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default);
}

public sealed class ModpackApiClient : IModpackApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string cacheDirectory;
    private readonly ISignedRequestSigner? signer;

    public ModpackApiClient(string endpoint) : this(CreateHttpClient(endpoint),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "modpack-cache"), true,
        DeviceIdentityServices.Current?.Signer) { }

    public ModpackApiClient(string endpoint, string appDataRoot) : this(CreateHttpClient(endpoint),
        Path.Combine(appDataRoot, "modpack-cache"), true, DeviceIdentityServices.Current?.Signer) { }

    public ModpackApiClient(HttpClient http) : this(http,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "modpack-cache"), false,
        DeviceIdentityServices.Current?.Signer) { }

    internal ModpackApiClient(HttpClient http, string appDataRoot) : this(http, Path.Combine(appDataRoot, "modpack-cache"), false,
        DeviceIdentityServices.Current?.Signer) { }

    internal ModpackApiClient(HttpClient http, string appDataRoot, ISignedRequestSigner signer) : this(http,
        Path.Combine(appDataRoot, "modpack-cache"), false, signer) { }

    private ModpackApiClient(HttpClient http, string cacheDirectory, bool ownsHttp, ISignedRequestSigner? signer)
    { this.http = http ?? throw new ArgumentNullException(nameof(http)); this.cacheDirectory = cacheDirectory; this.ownsHttp = ownsHttp; this.signer = signer; }

    private static HttpClient CreateHttpClient(string endpoint) => DeviceIdentityServices.Current?.CreateHttpClient(endpoint) ?? new HttpClient
    {
        BaseAddress = EnsureEndpoint(endpoint),
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"/packages?game={Uri.EscapeDataString(gameId)}&q={Uri.EscapeDataString(query?.Trim() ?? "")}&limit=50";
        if (!string.IsNullOrEmpty(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var packages = root.GetProperty("packages").EnumerateArray().Select(ParseSearch).ToArray();
            var next = root.TryGetProperty("nextCursor", out var nextCursor) && nextCursor.ValueKind == JsonValueKind.String
                ? nextCursor.GetString() : null;
            try { SaveCache(gameId, query, cursor, new(packages, next)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new(packages, next);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            var cached = LoadCache(gameId, query, cursor);
            return new(cached?.Packages ?? [], cached?.NextCursor, true);
        }
    }

    public async Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 80 || slug.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid package slug.", nameof(slug));
        try
        {
            using var response = await http.GetAsync("/packages/" + Uri.EscapeDataString(slug), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var detail = ParseDetail(json.RootElement);
            try { SaveDetailCache(slug, detail); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return detail;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return LoadDetailCache(slug);
        }
    }

    public async Task<ModpackPublishResult> PublishAsync(ModpackDefinition package, string slug, bool createNew = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (signer is null) throw new InvalidOperationException("A registered-device signer is required to publish packages.");
        if (!ValidSlug(slug) || !ValidPackage(package)) throw new ArgumentException("Invalid package publication request.", nameof(package));

        var snapshot = new
        {
            schemaVersion = 1,
            items = package.Items.Select(item => new
            {
                modName = item.Name,
                modVersion = item.Version,
                source = item.Source,
                // SusModder items identify the mod by the server's external id; it is optional
                // because the server also matches the full name ("SusModder-<name>").
                modId = item.Source is "thunderstore" or "susmodder" ? item.ModId : null,
                repo = item.Source == "github" ? item.Repository : null,
                tag = item.Source == "github" ? item.Tag : null,
                assetId = item.Source == "github" ? item.AssetId : null,
                link = item.Source == "nexus" ? item.Link : null,
                manualPath = item.Source == "nexus" ? item.ManualPath : null,
                dependencies = item.Dependencies,
                rules = Array.Empty<object>()
            }).ToArray()
        };
        object payload = createNew
            ? new { slug, gameId = package.GameId, name = package.Name, description = package.Description, snapshot }
            : new { snapshot };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        string path = createNew ? "/packages" : $"/packages/{Uri.EscapeDataString(slug)}/versions";
        if (http.BaseAddress is null) throw new InvalidOperationException("The package API requires a base address.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.BaseAddress, path))
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        IReadOnlyDictionary<string, string> headers = await signer.SignAsync(request, cancellationToken);
        foreach (var (name, value) in headers)
            if (!request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = json.RootElement;
        if (!root.TryGetProperty("slug", out var resultSlug) || resultSlug.GetString() != slug ||
            !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int number) || number < 1)
            throw new InvalidDataException("The package service returned an invalid publication result.");
        return new ModpackPublishResult(slug, number);
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }

    private static bool ValidSlug(string value) => value is { Length: > 0 and <= 80 }
        && Regex.IsMatch(value, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static bool ValidPackage(ModpackDefinition package)
    {
        static bool Text(string? value, int max, bool allowEmpty = false) => value is not null && value.Length <= max
            && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
        if (!Text(package.GameId, 80) || !Regex.IsMatch(package.GameId, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant) ||
            !Text(package.Name, 100) || !Text(package.Description, 4000, true) || package.Items is not { Count: > 0 and <= 500 } ||
            package.Items.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != package.Items.Count)
            return false;

        foreach (var item in package.Items)
        {
            if (!Text(item.Name, 160) || !Text(item.Version, 100) || item.Dependencies is null || item.Dependencies.Count > 100 ||
                item.Dependencies.Any(dependency => !Text(dependency, 160))) return false;
            bool valid = item.Source switch
            {
                "thunderstore" => Guid.TryParse(item.ModId, out _) && item.Repository is null && item.Tag is null && item.AssetId is null && item.Link is null && item.ManualPath is null,
                // The server resolves the artifact from its own catalog; the item only pins the
                // name/version and optionally the external id (PAK-103 contract).
                "susmodder" => (item.ModId is null || Text(item.ModId, 100)) && item.Repository is null && item.Tag is null
                    && item.AssetId is null && item.Sha256 is null && item.Link is null && item.ManualPath is null,
                "github" => item.ModId is null && item.Repository is { Length: <= 200 } repo &&
                    Regex.IsMatch(repo, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) &&
                    Text(item.Tag, 100) && Text(item.AssetId, 200) && item.Link is null && item.ManualPath is null,
                "nexus" => item.ModId is null && item.Repository is null && item.Tag is null && item.AssetId is null &&
                    Uri.TryCreate(item.Link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && Text(item.ManualPath, 500),
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }

    private void SaveDetailCache(string slug, ModpackDefinition pack)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, "detail-" + slug + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(pack, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private ModpackDefinition? LoadDetailCache(string slug)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<ModpackDefinition>(File.ReadAllText(Path.Combine(cacheDirectory, "detail-" + slug + ".json")), JsonOptions);
            // Cached files written by older builds could still carry a server-side archive path.
            if (cached is { IsPublic: true }) foreach (var item in cached.Items ??= []) item.ManualPath = null;
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void SaveCache(string game, string? query, string? cursor, ModpackSearchPage page)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = CachePath(game, query, cursor);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(page, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private ModpackSearchPage? LoadCache(string game, string? query, string? cursor)
    {
        try { return JsonSerializer.Deserialize<ModpackSearchPage>(File.ReadAllText(CachePath(game, query, cursor)), JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private string CachePath(string game, string? query, string? cursor)
    {
        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{game}\\n{query?.Trim()}\\n{cursor}")));
        return Path.Combine(cacheDirectory, key + ".json");
    }

    private static ModpackDefinition ParseSearch(JsonElement element) => new()
    {
        Id = GetString(element, "slug") ?? Guid.NewGuid().ToString("N"),
        Slug = GetString(element, "slug"),
        Name = GetString(element, "name") ?? "",
        Author = GetString(element, "author") ?? "",
        Description = GetString(element, "description") ?? "",
        GameId = GetString(element, "gameId") ?? "",
        Version = GetInt(element, "latestVersion")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1",
        PublicVersion = GetInt(element, "latestVersion"), IsPublic = true
    };

    private static ModpackDefinition ParseDetail(JsonElement element)
    {
        var pack = ParseSearch(element);
        pack.Version = GetInt(element, "version")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? pack.Version;
        pack.PublicVersion = GetInt(element, "version");
        if (!element.TryGetProperty("snapshot", out var snapshot) || !snapshot.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return pack;
        var required = items.EnumerateArray().SelectMany(i => i.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array
            ? deps.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!) : [])
            .ToHashSet(StringComparer.Ordinal);
        pack.Items = items.EnumerateArray().Select(item => new ModpackMod
        {
            Name = GetString(item, "modName") ?? "",
            Version = GetString(item, "modVersion") ?? "",
            Source = GetString(item, "source") ?? "thunderstore",
            ModId = GetString(item, "modId"), Repository = GetString(item, "repo"),
            Tag = GetString(item, "tag"), AssetId = GetString(item, "assetId"),
            // Trust boundary (PAK review W1): manualPath is a path on the publishing user's disk.
            // It is never read from server data; the local user must point the archive again.
            Link = GetString(item, "link"),
            Required = required.Contains(GetString(item, "modName") ?? ""),
            Dependencies = item.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array
                ? dependencies.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!).ToList() : []
        }).ToList();
        return pack;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : null;

    private static Uri EnsureEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new UriFormatException("A valid HTTP or HTTPS catalog endpoint is required.");
        return uri;
    }
}
