using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phaket.Services;

public sealed record ProfilePublishRequest(string Slug, string GameId, string Name, string Description, JsonElement Snapshot, bool CreateNew = true);
public sealed record ProfilePublishResult(string Slug, int Version);

public interface IProfilePublishService
{
    Task<ProfilePublishResult> PublishAsync(ProfilePublishRequest request, CancellationToken cancellationToken = default);
}

public sealed class HttpProfilePublishService : IProfilePublishService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly ISignedRequestSigner? signer;

    public HttpProfilePublishService(HttpClient client, Uri endpoint, ISignedRequestSigner? signer = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.endpoint = DeviceIdentityServices.NormalizeEndpoint(endpoint);
        this.signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    public async Task<ProfilePublishResult> PublishAsync(ProfilePublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Slug is not { Length: <= 80 } slug || !Regex.IsMatch(slug, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant) ||
            request.GameId is not { } gameId || !Regex.IsMatch(gameId, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.Name.Any(char.IsControl) ||
            request.Description is null || request.Description.Length > 4000 || request.Description.Any(char.IsControl) ||
            request.Snapshot.ValueKind != JsonValueKind.Object ||
            !request.Snapshot.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1 ||
            !request.Snapshot.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is 0 or > 500)
            throw new ArgumentException("Invalid profile publication request.", nameof(request));

        object payload = request.CreateNew
            ? new { request.Slug, request.GameId, request.Name, request.Description, Snapshot = request.Snapshot }
            : new { Snapshot = request.Snapshot };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        string path = request.CreateNew ? "/packages" : $"/packages/{Uri.EscapeDataString(request.Slug)}/versions";
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signer is not null)
        {
            IReadOnlyDictionary<string, string> headers = await signer.SignAsync(message, cancellationToken);
            foreach (var (name, value) in headers)
                if (!message.Headers.Contains(name)) message.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = json.RootElement;
        if (!root.TryGetProperty("slug", out var resultSlug) || resultSlug.GetString() != request.Slug ||
            !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int number) || number < 1)
            throw new InvalidDataException("The package service returned an invalid publication result.");
        return new ProfilePublishResult(request.Slug, number);
    }
}

public interface IProfileSnapshotClient
{
    Task<JsonDocument> FetchAsync(ProfileLink link, CancellationToken cancellationToken = default);
}

public sealed class HttpProfileSnapshotClient : IProfileSnapshotClient
{
    public async Task<JsonDocument> FetchAsync(ProfileLink link, CancellationToken cancellationToken = default)
    {
        string endpoint = CatalogSettings.Load();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var apiEndpoint))
            throw new UriFormatException("A valid HTTP or HTTPS API endpoint is required.");
        using var client = DeviceIdentityServices.Current?.CreateHttpClient(endpoint) ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var address = DeviceIdentityServices.NormalizeEndpoint(apiEndpoint);
        using var response = await client.GetAsync(new Uri(address, $"packages/{Uri.EscapeDataString(link.Slug)}/versions/{link.Version}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (limited.Length + read > 1_048_576) throw new InvalidDataException("Snapshot too large.");
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return JsonDocument.Parse(limited.ToArray());
    }
}

public sealed record ProfileLink(string Slug, int Version)
{
    private static readonly Regex Format = new("^phaket://profile/([a-z0-9]+(?:-[a-z0-9]+)*)/([1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string ToUri() => $"phaket://profile/{Slug}/{Version}";

    public static bool TryParse(string? value, out ProfileLink? link)
    {
        link = null;
        if (value is null || value.Length > 256) return false;
        var match = Format.Match(value);
        if (!match.Success || match.Groups[1].Value.Length > 80 ||
            !int.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
            return false;
        link = new ProfileLink(match.Groups[1].Value, version);
        return true;
    }
}
