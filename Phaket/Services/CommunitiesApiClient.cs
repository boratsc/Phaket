using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Services;

public static class CommunityAccessModes
{
    public const string Public = "public";
    public const string Password = "password";
    public const string InviteCode = "invite-code";
    public const string PasswordOrInviteCode = "password-or-invite-code";

    public static bool IsKnown(string? mode) => mode is Public or Password or InviteCode or PasswordOrInviteCode;
}

public sealed record CommunitySummary(
    string Slug,
    string Name,
    string Description,
    string AccessMode,
    string? PackageSlug,
    int MemberCount,
    bool IsMember,
    bool IsLocalDemo = false);

public sealed record CommunityChannel(string Id, string Name);
public sealed record CommunityDetails(CommunitySummary Community, IReadOnlyList<CommunityChannel> Channels);
public sealed record CommunityMessage(long Id, string ChannelId, string AuthorNickname, string AuthorKey,
    string Content, DateTimeOffset CreatedAt);
public sealed record CommunityPage(IReadOnlyList<CommunitySummary> Communities, string? NextCursor, bool IsOffline = false);
public sealed record CreateCommunityRequest(string Name, string Description, string AccessMode,
    string? Password = null, string? InviteCode = null, string? PackageSlug = null);

public sealed class CommunityApiException(HttpStatusCode statusCode, string? errorCode = null) : Exception
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    // Server error code from {"error":{"code":...,"message":{"pl-PL":...,"en":...}}}; null when the body had none.
    public string? ErrorCode { get; } = errorCode;
}

public interface ICommunitiesApiClient
{
    bool IsDemoMode { get; }
    Task<CommunityPage> ListAsync(string? query = null, string? cursor = null, CancellationToken cancellationToken = default);
    Task<CommunityDetails?> GetAsync(string slug, CancellationToken cancellationToken = default);
    Task<CommunitySummary> CreateAsync(CreateCommunityRequest request, CancellationToken cancellationToken = default);
    Task<CommunitySummary> JoinAsync(string slug, string accessMode, string? password, string? inviteCode,
        CancellationToken cancellationToken = default);
    Task LeaveAsync(string slug, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommunityMessage>> GetMessagesAsync(string slug, string channelId, long after = 0,
        int limit = 50, CancellationToken cancellationToken = default);
    Task<CommunityMessage?> SendMessageAsync(string slug, string channelId, string content,
        CancellationToken cancellationToken = default);
}

public sealed class CommunitiesApiClient : ICommunitiesApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient http;
    private readonly ISignedRequestSigner? signer;
    private readonly bool ownsHttp;
    private readonly bool signsThroughHttpHandler;
    private readonly object demoGate = new();
    private readonly Dictionary<string, CommunitySummary> demoOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Slug, string ChannelId), List<CommunityMessage>> demoMessages = [];

    public CommunitiesApiClient() : this(CatalogSettings.Load(), null) { }

    public CommunitiesApiClient(string endpoint, ISignedRequestSigner? signer = null)
        : this(CreateHttpClient(endpoint), signer ?? DeviceIdentityServices.Current?.CommunitySigner, true,
            DeviceIdentityServices.Current is not null) { }

    public CommunitiesApiClient(HttpClient http, ISignedRequestSigner? signer = null)
        : this(http, signer, false, false) { }

    private CommunitiesApiClient(HttpClient http, ISignedRequestSigner? signer, bool ownsHttp, bool signedByClientHandler)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        if (http.BaseAddress is null) throw new ArgumentException("The HTTP client needs a base address.", nameof(http));
        this.signer = signer;
        this.ownsHttp = ownsHttp;
        signsThroughHttpHandler = signedByClientHandler && DeviceIdentityServices.Current is { } identity
            && ReferenceEquals(signer, identity.CommunitySigner);
    }

    public bool IsDemoMode => signer is null;

    public async Task<CommunityPage> ListAsync(string? query = null, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        query = LimitQuery(query);
        var path = $"/communities?q={Uri.EscapeDataString(query ?? string.Empty)}&limit=50";
        if (!string.IsNullOrWhiteSpace(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, signed: false, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var document = await ReadJsonAsync(response, cancellationToken);
            if (document is null) throw new JsonException("Empty communities response.");
            var result = ParseCommunityPage(document.RootElement);
            if (IsDemoMode) return result with { Communities = OverlayDemoChanges(result.Communities) };
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsDemoMode && IsOfflineFailure(ex))
        {
            var communities = FilterDemoCommunities(query);
            return new(OverlayDemoChanges(communities), null, IsOffline: true);
        }
    }

    public async Task<CommunityDetails?> GetAsync(string slug, CancellationToken cancellationToken = default)
    {
        ValidateSlug(slug, nameof(slug));
        if (IsDemoMode)
        {
            var demoCommunity = FindDemoCommunity(slug) ?? new CommunitySummary(slug, slug,
                LocalizationService.Instance["Communities.DemoDescription"], CommunityAccessModes.InviteCode,
                null, 1, true, IsLocalDemo: true);
            return new(demoCommunity with { IsMember = true }, DemoChannels());
        }

        using var response = await SendAsync(HttpMethod.Get, "/communities/" + Uri.EscapeDataString(slug), null,
            signed: false, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        if (document is null) return null;
        var root = Unwrap(document.RootElement, "community");
        var community = ParseCommunity(root);
        if (community is null) return null;
        var channels = community.IsMember && TryProperty(root, "channels", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(ParseChannel).Where(channel => channel is not null).Cast<CommunityChannel>().ToArray()
            : [];
        return new(community, channels);
    }

    public async Task<CommunitySummary> CreateAsync(CreateCommunityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ValidateCreateRequest(request);
        if (IsDemoMode)
        {
            var slug = CreateSlug(request.Name);
            lock (demoGate)
            {
                if (demoOverrides.ContainsKey(slug) || DemoSeeds().Any(community => community.Slug == slug))
                    slug += "-" + Guid.NewGuid().ToString("N")[..6];
                var created = new CommunitySummary(slug, request.Name, request.Description, request.AccessMode,
                    request.PackageSlug, 1, true, IsLocalDemo: true);
                demoOverrides[slug] = created;
                return created;
            }
        }

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "/communities", content, signed: true, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var createdCommunity = document is null ? null : ParseCommunity(Unwrap(document.RootElement, "community"));
        if (createdCommunity is not null) return createdCommunity;
        // A server that only answers {id, slug} still gives a usable address; read the full record from it.
        var createdSlug = document is null ? null : ReadStringProperty(Unwrap(document.RootElement, "community"), "slug");
        createdCommunity = createdSlug is null ? null : await FetchSummaryAsync(createdSlug, cancellationToken);
        return createdCommunity ?? throw new InvalidDataException("The server returned no community record.");
    }

    // Reads the full record for create/join replies that only carry an address or a flag.
    private async Task<CommunitySummary?> FetchSummaryAsync(string slug, CancellationToken cancellationToken)
    {
        if (!IsValidSlug(slug)) return null;
        try { return (await GetAsync(slug, cancellationToken))?.Community; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException
            or CommunityApiException or UriFormatException or ArgumentException) { return null; }
    }

    public async Task<CommunitySummary> JoinAsync(string slug, string accessMode, string? password, string? inviteCode,
        CancellationToken cancellationToken = default)
    {
        ValidateSlug(slug, nameof(slug));
        ValidateJoinRequest(accessMode, password, inviteCode);
        if (IsDemoMode)
        {
            lock (demoGate)
            {
                var existing = FindDemoCommunity(slug) ?? new CommunitySummary(slug, slug,
                    LocalizationService.Instance["Communities.DemoDescription"], accessMode, null, 0, false, IsLocalDemo: true);
                var joined = existing with { IsMember = true, MemberCount = Math.Max(1, existing.MemberCount) };
                demoOverrides[slug] = joined;
                return joined;
            }
        }

        var body = new JoinPayload(
            accessMode is CommunityAccessModes.Password or CommunityAccessModes.PasswordOrInviteCode ? password : null,
            accessMode is CommunityAccessModes.InviteCode or CommunityAccessModes.PasswordOrInviteCode ? inviteCode : null);
        using var content = JsonContent(body);
        using var response = await SendAsync(HttpMethod.Post, $"/communities/{Uri.EscapeDataString(slug)}/join",
            content, signed: true, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var joinedCommunity = document is null ? null : ParseCommunity(Unwrap(document.RootElement, "community"));
        // A server that answers {joined: true} does not describe the community, so read its full record.
        joinedCommunity ??= await FetchSummaryAsync(slug, cancellationToken);
        return joinedCommunity ?? new(slug, slug, "", accessMode, null, 0, true);
    }

    public async Task LeaveAsync(string slug, CancellationToken cancellationToken = default)
    {
        ValidateSlug(slug, nameof(slug));
        if (IsDemoMode)
        {
            lock (demoGate)
            {
                var existing = FindDemoCommunity(slug);
                if (existing is not null) demoOverrides[slug] = existing with { IsMember = false, MemberCount = Math.Max(0, existing.MemberCount - 1) };
            }
            return;
        }

        using var response = await SendAsync(HttpMethod.Post, $"/communities/{Uri.EscapeDataString(slug)}/leave",
            null, signed: true, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<CommunityMessage>> GetMessagesAsync(string slug, string channelId, long after = 0,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        ValidateSlug(slug, nameof(slug));
        ValidateChannelId(channelId);
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (IsDemoMode) return DemoMessagePage(slug, channelId, after, limit);

        var path = $"/communities/{Uri.EscapeDataString(slug)}/channels/{Uri.EscapeDataString(channelId)}/messages?after={after.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        // Member-only read: the server rejects it without a registered, signed device (review Ś3).
        using var response = await SendAsync(HttpMethod.Get, path, null, signed: true, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        if (document is null) return [];
        var array = TryProperty(document.RootElement, "messages", out var messages) ? messages : document.RootElement;
        if (array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(ParseMessage).Where(message => message is not null)
            .Cast<CommunityMessage>().Where(message => message.Id > after).OrderBy(message => message.Id).Take(limit).ToArray();
    }

    public async Task<CommunityMessage?> SendMessageAsync(string slug, string channelId, string content,
        CancellationToken cancellationToken = default)
    {
        ValidateSlug(slug, nameof(slug));
        ValidateChannelId(channelId);
        ValidateMessage(content);
        if (IsDemoMode)
        {
            var community = FindDemoCommunity(slug);
            if (community is not { IsMember: true }) throw new CommunityApiException(HttpStatusCode.Forbidden);
            lock (demoGate)
            {
                var messages = GetDemoMessageList(slug, channelId);
                var message = new CommunityMessage(messages.Count == 0 ? 1 : messages.Max(item => item.Id) + 1,
                    channelId, LocalizationService.Instance["Chat.DemoAuthor"], "phaket-demo-key", content, DateTimeOffset.UtcNow);
                messages.Add(message);
                return message;
            }
        }

        using var requestContent = JsonContent(new MessagePayload(content));
        using var response = await SendAsync(HttpMethod.Post,
            $"/communities/{Uri.EscapeDataString(slug)}/channels/{Uri.EscapeDataString(channelId)}/messages",
            requestContent, signed: true, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        if (document is null) return null;
        var root = Unwrap(document.RootElement, "message");
        var sent = ParseMessage(root);
        // The create reply may carry no author; re-read the stored row so it shows its nick and fingerprint.
        if (sent is { AuthorNickname.Length: 0, AuthorKey.Length: 0 })
        {
            long id = sent.Id;
            var stored = (await GetMessagesAsync(slug, channelId, id - 1, 1, cancellationToken))
                .FirstOrDefault(message => message.Id == id);
            if (stored is not null) sent = stored;
        }
        return sent;
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }

    public static CreateCommunityRequest ValidateCreateRequest(CreateCommunityRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        var description = request.Description?.Trim() ?? "";
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new ArgumentException("Invalid community name.", nameof(request));
        if (description.Length > 2000 || description.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("Invalid community description.", nameof(request));
        if (!CommunityAccessModes.IsKnown(request.AccessMode)) throw new ArgumentException("Invalid access mode.", nameof(request));

        var password = request.AccessMode is CommunityAccessModes.Password or CommunityAccessModes.PasswordOrInviteCode ? request.Password : null;
        var code = request.AccessMode is CommunityAccessModes.InviteCode or CommunityAccessModes.PasswordOrInviteCode ? request.InviteCode : null;
        // An empty optional field comes from the UI as "", not null; only a real value has to be a slug.
        var packageSlug = string.IsNullOrWhiteSpace(request.PackageSlug) ? null : request.PackageSlug.Trim();
        if (packageSlug is not null && !IsValidSlug(packageSlug)) throw new ArgumentException("Invalid package slug.", nameof(request));
        ValidateSecret(password, nameof(request.Password), required: request.AccessMode == CommunityAccessModes.Password);
        ValidateSecret(code, nameof(request.InviteCode), required: request.AccessMode == CommunityAccessModes.InviteCode);
        if (request.AccessMode == CommunityAccessModes.PasswordOrInviteCode && password is null && code is null)
            throw new ArgumentException("A password or invite code is required.", nameof(request));
        return request with { Name = name, Description = description, Password = password, InviteCode = code,
            PackageSlug = packageSlug };
    }

    public static void ValidateJoinRequest(string accessMode, string? password, string? inviteCode)
    {
        if (!CommunityAccessModes.IsKnown(accessMode)) throw new ArgumentException("Invalid access mode.", nameof(accessMode));
        if (accessMode == CommunityAccessModes.Public) return;
        var passwordRequired = accessMode == CommunityAccessModes.Password;
        var codeRequired = accessMode == CommunityAccessModes.InviteCode;
        ValidateSecret(password, nameof(password), passwordRequired);
        ValidateSecret(inviteCode, nameof(inviteCode), codeRequired);
        if (accessMode == CommunityAccessModes.PasswordOrInviteCode && password is null && inviteCode is null)
            throw new ArgumentException("A password or invite code is required.");
    }

    private static void ValidateSecret(string? secret, string name, bool required)
    {
        if (string.IsNullOrEmpty(secret))
        {
            if (required) throw new ArgumentException("A password or invite code is required.", name);
            return;
        }
        if (secret.Length is < 8 or > 128 || secret.Any(char.IsControl))
            throw new ArgumentException("A password or invite code must be 8–128 characters.", name);
    }

    private static void ValidateMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000 ||
            content.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("A message must contain 1–2,000 plain-text characters.", nameof(content));
    }

    private static bool IsValidSlug(string value) => value.Length is > 0 and <= 80 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static void ValidateSlug(string value, string parameter)
    {
        if (value is null || !IsValidSlug(value)) throw new ArgumentException("Invalid community slug.", parameter);
    }

    private static void ValidateChannelId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid channel identifier.", nameof(value));
    }

    private static string? LimitQuery(string? query)
    {
        var trimmed = query?.Trim();
        if (trimmed is null) return null;
        return trimmed.Length <= 100 ? trimmed : trimmed[..100];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        bool signed, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(http.BaseAddress!, path)) { Content = content };
        if (!string.IsNullOrWhiteSpace(signer?.DeviceKey))
            request.Headers.TryAddWithoutValidation("X-Device-Key", signer.DeviceKey);
        if (signed && signer is null)
        {
            request.Dispose();
            throw new InvalidOperationException("Signed requests are unavailable in demo mode.");
        }
        if (signed && !signsThroughHttpHandler)
        {
            var headers = await signer!.SignAsync(request, cancellationToken);
            foreach (var (name, value) in headers)
                if (!request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
        }
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        finally { request.Dispose(); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return;
        string? code = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 65_536)
            {
                using var document = JsonDocument.Parse(text);
                if (TryProperty(document.RootElement, "error", out var error) && error.ValueKind == JsonValueKind.Object)
                    code = ReadStringProperty(error, "code");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or NotSupportedException) { }
        throw new CommunityApiException(response.StatusCode, code);
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0) return null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private static JsonElement Unwrap(JsonElement element, string property) =>
        TryProperty(element, property, out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : element;

    private static CommunityPage ParseCommunityPage(JsonElement root)
    {
        var array = TryProperty(root, "communities", out var communities) ? communities : root;
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid communities response.");
        var items = array.EnumerateArray().Select(ParseCommunity).Where(community => community is not null)
            .Cast<CommunitySummary>().Where(community => community.AccessMode != CommunityAccessModes.InviteCode || community.IsMember).ToArray();
        var next = TryProperty(root, "nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String ? cursor.GetString() : null;
        return new(items, next);
    }

    private static CommunitySummary? ParseCommunity(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !TryProperty(item, "slug", out var slugElement)) return null;
        var slug = ReadString(slugElement);
        if (slug is null || !IsValidSlug(slug)) return null;
        var name = ReadStringProperty(item, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) return null;
        var mode = ReadStringProperty(item, "accessMode")?.ToLowerInvariant() ?? "unknown";
        var description = ReadStringProperty(item, "description") ?? "";
        if (description.Length > 4000) description = description[..4000];
        var packageSlug = ReadStringProperty(item, "packageSlug");
        if (packageSlug is not null && !IsValidSlug(packageSlug)) packageSlug = null;
        var memberCount = ReadIntProperty(item, "memberCount") ?? ReadIntProperty(item, "membersCount") ?? 0;
        var isMember = ReadBoolProperty(item, "isMember") ?? false;
        return new(slug, name, description, mode, packageSlug, Math.Max(0, memberCount), isMember);
    }

    private static CommunityChannel? ParseChannel(JsonElement element)
    {
        var id = ReadStringProperty(element, "id");
        var name = ReadStringProperty(element, "name");
        return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ? null : new(id, name);
    }

    private static CommunityMessage? ParseMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = ReadLongProperty(element, "id");
        var content = ReadStringProperty(element, "content") ?? ReadStringProperty(element, "message") ?? ReadStringProperty(element, "body");
        if (id is null or < 1 || content is null || content.Length > 2000) return null;
        var channelId = ReadStringProperty(element, "channelId") ?? "";
        // The server sends nick + fingerprint (see Phaket.Api/Communities.cs); older builds sent authorNickname/authorKey.
        var nickname = ReadStringProperty(element, "authorNickname") ?? ReadStringProperty(element, "authorName") ??
            ReadStringProperty(element, "nickname") ?? ReadStringProperty(element, "nick") ?? "";
        var key = ReadStringProperty(element, "authorKey") ?? ReadStringProperty(element, "authorPublicKey") ??
            ReadStringProperty(element, "deviceKey") ?? ReadStringProperty(element, "publicKey") ??
            ReadStringProperty(element, "fingerprint") ?? "";
        var created = DateTimeOffset.TryParse(ReadStringProperty(element, "createdAt"), out var timestamp) ? timestamp : DateTimeOffset.UtcNow;
        return new(id.Value, channelId, nickname, key, content, created);
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? ReadStringProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) ? ReadString(value) : null;
    private static string? ReadString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };
    private static int? ReadIntProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static long? ReadLongProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && (value.TryGetInt64(out var number) || long.TryParse(ReadString(value), out number)) ? number : null;
    private static bool? ReadBoolProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static bool IsOfflineFailure(Exception exception) => exception is HttpRequestException or TaskCanceledException or IOException;

    private CommunitySummary[] FilterDemoCommunities(string? query)
    {
        var rows = DemoSeeds();
        if (string.IsNullOrWhiteSpace(query)) return rows;
        return rows.Where(community => community.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            community.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private CommunitySummary[] OverlayDemoChanges(IReadOnlyList<CommunitySummary> serverRows)
    {
        lock (demoGate)
        {
            var rows = serverRows.ToDictionary(community => community.Slug, StringComparer.Ordinal);
            foreach (var (slug, overrideValue) in demoOverrides) rows[slug] = overrideValue;
            return rows.Values.Where(community => community.AccessMode != CommunityAccessModes.InviteCode || community.IsMember)
                .OrderBy(community => community.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private CommunitySummary? FindDemoCommunity(string slug)
    {
        lock (demoGate)
        {
            if (demoOverrides.TryGetValue(slug, out var existing)) return existing;
            return DemoSeeds().FirstOrDefault(community => community.Slug == slug);
        }
    }

    private static CommunitySummary[] DemoSeeds()
    {
        var loc = LocalizationService.Instance;
        return
        [
            new("demo-pogranicze", loc["Communities.DemoCommunityName"], loc["Communities.DemoDescription"],
                CommunityAccessModes.Public, null, 8, true, IsLocalDemo: true),
            new("demo-wyprawa", loc["Communities.DemoOpenCommunityName"], loc["Communities.DemoDescription"],
                CommunityAccessModes.Public, null, 3, false, IsLocalDemo: true)
        ];
    }

    private static CommunityChannel[] DemoChannels()
    {
        var loc = LocalizationService.Instance;
        return [new("general", loc["Chat.General"]), new("help", loc["Chat.Help"])];
    }

    private IReadOnlyList<CommunityMessage> DemoMessagePage(string slug, string channelId, long after, int limit)
    {
        if (!IsDemoMember(slug)) throw new CommunityApiException(HttpStatusCode.Forbidden);
        lock (demoGate)
            return GetDemoMessageList(slug, channelId).Where(message => message.Id > after).OrderBy(message => message.Id).Take(limit).ToArray();
    }

    private bool IsDemoMember(string slug) => FindDemoCommunity(slug) is { IsMember: true };

    private List<CommunityMessage> GetDemoMessageList(string slug, string channelId)
    {
        var key = (slug, channelId);
        if (demoMessages.TryGetValue(key, out var messages)) return messages;
        var loc = LocalizationService.Instance;
        messages = slug == "demo-pogranicze" && channelId == "general"
            ? [
                new(1, channelId, loc["Chat.DemoAuthor"], "phaket-demo-key", loc["Chat.DemoMessageOne"], DateTimeOffset.UtcNow.AddMinutes(-6)),
                new(2, channelId, loc["Chat.DemoSecondAuthor"], "demo-public-key", loc["Chat.DemoMessageTwo"], DateTimeOffset.UtcNow.AddMinutes(-4))
            ]
            : [];
        demoMessages[key] = messages;
        return messages;
    }

    private static string CreateSlug(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var ascii = new string(decomposed.Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = new string(ascii.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "community" : slug[..Math.Min(80, slug.Length)].TrimEnd('-');
    }

    private static HttpContent JsonContent<T>(T value) => new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    private sealed record JoinPayload(string? Password, string? Code);
    private sealed record MessagePayload(string Content);

    private static HttpClient CreateHttpClient(string endpoint)
    {
        if (!CatalogSettings.IsValid(endpoint)) throw new UriFormatException("A valid HTTP or HTTPS community endpoint is required.");
        return DeviceIdentityServices.Current?.CreateHttpClient(endpoint) ?? new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }
}
