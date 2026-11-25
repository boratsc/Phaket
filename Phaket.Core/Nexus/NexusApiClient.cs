using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Phaket.Core.Nexus;

// REST v1 client. The key travels in the apikey header on every request and never
// appears in an exception message or a cache file. Metadata reads go through the
// TTL cache; when the hourly quota is exhausted the last copy is returned instead
// (LastReadFromStaleCache tells the UI to warn).
public sealed class NexusApiClient(HttpClient http, INexusCredentialProvider credentials, NexusCache? cache = null)
{
    private const string UserAgent = "Phaket/0.1 (Nexus integration; testing)";

    public NexusRateLimits RateLimits { get; private set; } = new(null, null);
    public bool LastReadFromStaleCache { get; private set; }

    public async Task<NexusUser> ValidateAsync(string? explicitKey = null, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("/v1/user/validate.json", explicitKey, cancellationToken);
        var root = document.RootElement;
        bool premium = TryGet(root, "is_premium", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new(TryString(root, "name") ?? "", premium, TryString(root, "profile_url"));
    }

    public Task<NexusModMetadata> GetModAsync(string domain, int modId, CancellationToken cancellationToken = default)
    {
        string clean = RequireDomain(domain);
        return CachedAsync($"mod:{clean}:{modId}", $"/v1/games/{clean}/mods/{modId}.json", ParseMod, cancellationToken);
    }

    public Task<IReadOnlyList<NexusFileInfo>> GetFilesAsync(string domain, int modId, CancellationToken cancellationToken = default)
    {
        string clean = RequireDomain(domain);
        return CachedAsync<IReadOnlyList<NexusFileInfo>>($"files:{clean}:{modId}",
            $"/v1/games/{clean}/mods/{modId}/files.json", ParseFiles, cancellationToken);
    }

    // Expired copies are still useful when the API says "too many requests".
    public NexusModMetadata? CachedMod(string domain, int modId) =>
        cache?.TryGet<NexusModMetadata>($"mod:{domain}:{modId}", out var value, allowExpired: true) == true ? value : null;

    public IReadOnlyList<NexusFileInfo>? CachedFiles(string domain, int modId) =>
        cache?.TryGet<IReadOnlyList<NexusFileInfo>>($"files:{domain}:{modId}", out var value, allowExpired: true) == true ? value : null;

    public async Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(NxmLink link, CancellationToken cancellationToken = default)
    {
        string domain = RequireDomain(link.GameDomain);
        string path = link.HasDownloadToken
            ? $"/v1/games/{domain}/mods/{link.ModId}/files/{link.FileId}/download_links.json?key={Uri.EscapeDataString(link.Key!)}&expires={Uri.EscapeDataString(link.Expires!)}"
            : $"/v1/games/{domain}/mods/{link.ModId}/files/{link.FileId}/download_link.json";
        try
        {
            using var document = await GetJsonAsync(path, null, cancellationToken);
            var links = new List<NexusDownloadLink>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    // Legacy v1 names the address URI; newer payloads also use uri/url.
                    string? uri = TryString(element, "URI") ?? TryString(element, "uri") ?? TryString(element, "url");
                    // The desktop download layer enforces https (loopback exempt for tests).
                    if (!string.IsNullOrWhiteSpace(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                        parsed.Scheme is "https" or "http")
                        links.Add(new(parsed.AbsoluteUri, TryString(element, "name") ?? TryString(element, "short_name"),
                            TryLong(element, "size")));
                }
            return links;
        }
        catch (NexusApiException ex) when (ex.Code == "NEXUS_FORBIDDEN")
        {
            throw new NexusApiException(link.HasDownloadToken ? "NEXUS_NXM_EXPIRED" : "NEXUS_PREMIUM_REQUIRED");
        }
    }

    private async Task<T> CachedAsync<T>(string key, string path, Func<JsonElement, T> parse, CancellationToken cancellationToken)
        where T : class
    {
        LastReadFromStaleCache = false;
        if (cache?.TryGet<T>(key, out var cached) == true) return cached!;
        try
        {
            using var document = await GetJsonAsync(path, null, cancellationToken);
            T value = parse(document.RootElement);
            cache?.Set(key, value);
            return value;
        }
        catch (NexusApiException ex) when (ex.Code == "NEXUS_RATE_LIMIT" && cache is not null &&
                                          cache.TryGet<T>(key, out var stale, allowExpired: true))
        {
            LastReadFromStaleCache = true;
            return stale!;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string? explicitKey, CancellationToken cancellationToken)
    {
        NexusCredential? credential = string.IsNullOrWhiteSpace(explicitKey)
            ? await credentials.GetAsync(cancellationToken).ConfigureAwait(false)
            : NexusCredential.ApiKey(explicitKey.Trim());
        if (credential is null) throw new NexusApiException("NEXUS_API_KEY_MISSING");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        credential.Apply(request);
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        { throw new NexusApiException("NEXUS_UNAVAILABLE", ex); }

        using (response)
        {
            ReadLimits(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new NexusApiException("NEXUS_RATE_LIMIT");
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new NexusApiException("NEXUS_API_KEY_INVALID");
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new NexusApiException("NEXUS_FORBIDDEN");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new NexusApiException("NEXUS_NOT_FOUND");
            if (!response.IsSuccessStatusCode) throw new NexusApiException("NEXUS_UNAVAILABLE");
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex) { throw new NexusApiException("NEXUS_INVALID_RESPONSE", ex); }
        }
    }

    private void ReadLimits(HttpResponseMessage response)
    {
        int? hourly = Header(response, "x-rl-hourly-remaining");
        int? daily = Header(response, "x-rl-daily-remaining");
        RateLimits = new(hourly ?? RateLimits.HourlyRemaining, daily ?? RateLimits.DailyRemaining);
    }

    private static int? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : null;

    private static string RequireDomain(string domain) =>
        NxmLink.IsDomain(domain) ? domain.ToLowerInvariant() : throw new NexusApiException("NEXUS_INVALID_LINK");

    private static NexusModMetadata ParseMod(JsonElement root) => new(
        (int)(TryLong(root, "mod_id") ?? 0),
        TryString(root, "name") ?? "",
        TryString(root, "author"),
        TryString(root, "version"),
        TryString(root, "summary"),
        TryLong(root, "endorsement_count"),
        !TryGet(root, "available", out var available) || available.ValueKind != JsonValueKind.False);

    private static IReadOnlyList<NexusFileInfo> ParseFiles(JsonElement root)
    {
        var files = new List<NexusFileInfo>();
        if (!TryGet(root, "files", out var array) || array.ValueKind != JsonValueKind.Array) return files;
        foreach (var file in array.EnumerateArray())
        {
            long? id = TryLong(file, "file_id");
            if (id is null or <= 0) continue;
            files.Add(new((int)id.Value,
                TryString(file, "name") ?? TryString(file, "file_name") ?? $"#{id}",
                TryString(file, "file_name"),
                TryString(file, "mod_version") ?? TryString(file, "version"),
                TryString(file, "category_name"),
                TryLong(file, "size_kb") ?? TryLong(file, "size")));
        }
        return files;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        }
        value = default;
        return false;
    }

    private static string? TryString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? TryLong(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.Number when value.TryGetDouble(out double approximate) => (long)approximate,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => null
        };
    }
}