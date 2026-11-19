using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Services;

public sealed class GitHubReleaseService : IDisposable
{
    private const int PageSize = 30;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public GitHubReleaseService(HttpClient? httpClient = null)
    {
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Phaket", "0.1"));
        if (!http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/vnd.github+json"))
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static string? ParseRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string candidate = value.Trim();
        string[] segments;
        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                return null;
            segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;
            if (segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                segments[1] = segments[1][..^4];
        }
        else
        {
            candidate = candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? candidate[..^4] : candidate;
            segments = candidate.Split('/');
            if (segments.Length != 2) return null;
        }

        if (!IsValidRepoSegment(segments[0], allowDots: false) || !IsValidRepoSegment(segments[1], allowDots: true) ||
            segments[1] is "." or "..") return null;
        return segments[0] + "/" + segments[1];
    }

    private static bool IsValidRepoSegment(string segment, bool allowDots) =>
        segment.Length is > 0 and <= 100 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || (allowDots && c == '.')) &&
        segment[0] is not ('-' or '_') && segment[^1] is not ('-' or '_') && (!allowDots || segment[0] != '.' && segment[^1] != '.');

    // Same exact-host allowlist the Core installer transport accepts (HttpPackageTransport.GitHubAssetHosts);
    // a wildcard *.githubusercontent.com would let any attacker-owned subdomain serve archive bytes.
    private static readonly HashSet<string> GitHubAssetHosts = new(StringComparer.OrdinalIgnoreCase)
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    internal static bool IsSafeDownloadUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && GitHubAssetHosts.Contains(uri.Host) && uri.UserInfo.Length == 0;

    public async Task<GitHubReleasePage> GetReleasesAsync(string repo, int page = 1, CancellationToken cancellationToken = default)
    {
        repo = ParseRepository(repo) ?? throw new ArgumentException("Invalid GitHub repository", nameof(repo));
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        string key = repo.ToLowerInvariant() + "#" + page.ToString(CultureInfo.InvariantCulture);
        if (Cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.StoredAt < CacheDuration)
            return cached.Page;

        using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page={PageSize}&page={page}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubReleaseException("Repository not found or private.", "Sources.GitHub.NotFound");
        if ((int)response.StatusCode is 403 or 429)
            throw new GitHubReleaseException("GitHub rate limit exceeded.", "Sources.GitHub.RateLimit");
        if (!response.IsSuccessStatusCode)
            throw new GitHubReleaseException("GitHub request failed.", "Sources.GitHub.RequestFailed");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<GitHubApiRelease[]>(stream, cancellationToken: cancellationToken)
            ?? throw new GitHubReleaseException("GitHub returned invalid release data.", "Sources.GitHub.InvalidResponse");
        var releases = payload.Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName))
            .Select(r => new GitHubRelease(r.Id, r.TagName!, string.IsNullOrWhiteSpace(r.Name) ? r.TagName! : r.Name!,
                r.PublishedAt, r.Prerelease,
                (r.Assets ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Name) && IsSafeDownloadUrl(a.DownloadUrl))
                    .Select(a => new GitHubReleaseAsset(a.Id, a.Name!, a.Size, a.DownloadUrl!)).ToArray()))
            .ToArray();
        bool hasMore = response.Headers.TryGetValues("Link", out var links) &&
            links.Any(link => link.Split(',').Any(part => part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)));
        if (!response.Headers.Contains("Link")) hasMore = releases.Length == PageSize;
        var result = new GitHubReleasePage(releases, hasMore);
        Cache[key] = new CacheEntry(DateTimeOffset.UtcNow, result);
        return result;
    }

    public async Task<bool> HasNewerReleaseAsync(string repo, string pinnedTag, CancellationToken cancellationToken = default)
    {
        var page = await GetReleasesAsync(repo, cancellationToken: cancellationToken);
        var latest = page.Releases.FirstOrDefault();
        return latest is not null && !string.Equals(latest.Tag, pinnedTag, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<GitHubRelease> DemoReleases { get; } =
    [
        new(1, "v2.3.0", "Better Networking 2.3.0", DateTimeOffset.Parse("2026-10-01T12:00:00Z", CultureInfo.InvariantCulture), false,
            [new(101, "BetterNetworking-2.3.0.zip", 262144, ""), new(102, "BetterNetworking-2.3.0.dll", 143360, "")]),
        new(2, "v2.2.1", "Better Networking 2.2.1", DateTimeOffset.Parse("2026-08-12T12:00:00Z", CultureInfo.InvariantCulture), false,
            [new(103, "BetterNetworking-2.2.1.zip", 245760, "")]),
        new(3, "v2.4.0-beta.1", "Better Networking 2.4 beta", DateTimeOffset.Parse("2026-11-01T12:00:00Z", CultureInfo.InvariantCulture), true,
            [new(104, "BetterNetworking-2.4.0-beta.1.zip", 278528, "")])
    ];

    public static void ClearCache() => Cache.Clear();
    public void Dispose() { if (ownsHttp) http.Dispose(); }

    private sealed record CacheEntry(DateTimeOffset StoredAt, GitHubReleasePage Page);
    private sealed class GitHubApiRelease
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public GitHubApiAsset[]? Assets { get; set; }
    }
    private sealed class GitHubApiAsset
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
    }
}

public sealed record GitHubReleasePage(IReadOnlyList<GitHubRelease> Releases, bool HasMore);

public sealed record GitHubRelease(long Id, string Tag, string Name, DateTimeOffset? PublishedAt, bool Prerelease, IReadOnlyList<GitHubReleaseAsset> Assets)
{
    public string DisplayName => Name == Tag ? Tag : $"{Tag} · {Name}";
}

public sealed record GitHubReleaseAsset(long Id, string Name, long Size, string DownloadUrl)
{
    public string DisplayName => $"{Name} · {Math.Max(1, (long)Math.Round(Size / 1024d, MidpointRounding.AwayFromZero))} KB";
}

public sealed class GitHubReleaseException(string message, string localizationKey) : Exception(message)
{
    public string LocalizationKey { get; } = localizationKey;
}
