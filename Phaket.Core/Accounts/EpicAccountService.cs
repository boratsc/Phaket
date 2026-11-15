using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Phaket.Core.Accounts;

public sealed class EpicAccountService : IAccountService
{
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    // Public Epic launcher client credential, published in Legendary's legendary/api/egs.py.
    private const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string Api = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient http;
    private readonly Action<string> openBrowser;
    private readonly EncryptedStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object pendingLock = new();
    private TaskCompletionSource<string>? pending;
    private StoredAccount? stored;

    public string Provider => "epic";
    public AccountInfo? Current { get; private set; }
    public event Action? AccountChanged;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (stored is null) throw new InvalidOperationException("Nie zalogowano do Epic / Not signed in to Epic.");
            if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                var token = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = stored.RefreshToken, ["token_type"] = "eg1" }, ct);
                stored = stored with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) };
                await SaveAsync(stored, ct);
            }
            return stored.AccessToken;
        }
        finally { gate.Release(); }
    }

    internal async Task<Platforms.LegendaryTokens> GetLegendaryTokensAsync(CancellationToken ct = default)
    {
        await GetAccessTokenAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            if (stored is null) throw new InvalidOperationException("Nie zalogowano do Epic / Not signed in to Epic.");
            return new(stored.AccessToken, stored.RefreshToken, stored.ExpiresAt, stored.AccountId, stored.DisplayName);
        }
        finally { gate.Release(); }
    }

    public EpicAccountService(string? storePath = null)
        : this(storePath, null, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }

    // Hosts that embed their own sign-in UI (WebView dialog) supply the opener.
    public EpicAccountService(string? storePath, Action<string> openBrowser)
        : this(storePath, null, openBrowser) { }

    internal EpicAccountService(string? storePath, HttpMessageHandler? handler, Action<string> openBrowser, ISecretStore? secretStore = null)
    {
        http = handler is null ? SharedHttp : new HttpClient(handler);
        this.openBrowser = openBrowser;
        store = new EncryptedStore(Path.GetFullPath(storePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "epic.bin")), secretStore);
        try
        {
            byte[]? data = store.Load();
            stored = data is null ? null : JsonSerializer.Deserialize<StoredAccount>(data, Json);
            if (stored is null || string.IsNullOrWhiteSpace(stored.AccessToken) || string.IsNullOrWhiteSpace(stored.RefreshToken) || string.IsNullOrWhiteSpace(stored.AccountId) || string.IsNullOrWhiteSpace(stored.DisplayName)) stored = null;
            Current = stored?.Account();
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or ArgumentException) { stored = null; }
    }

    public void SubmitCode(string userInput)
    {
        string code = ExtractAuthorizationCode(userInput);
        lock (pendingLock)
        {
            if (pending is null || !pending.TrySetResult(code))
                throw new InvalidOperationException("Brak oczekującego logowania / No login is waiting.");
        }
    }

    public static string ExtractAuthorizationCode(string input)
    {
        string value = input?.Trim() ?? "";
        try
        {
            if (value.StartsWith('{') || value.StartsWith('"'))
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("authorizationCode", out var code) && code.ValueKind == JsonValueKind.String)
                    value = code.GetString() ?? "";
                else if (doc.RootElement.ValueKind == JsonValueKind.String)
                    value = doc.RootElement.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var codes = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).Where(pair => Uri.UnescapeDataString(pair[0]) == "code").ToArray();
            value = codes.Length == 1 && codes[0].Length == 2 ? Uri.UnescapeDataString(codes[0][1]) : "";
        }
        if (value.Length is < 20 or > 64 || !value.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("Nieprawidłowy kod autoryzacji / Invalid authorization code.");
        return value;
    }

    public async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        bool changed = false;
        try
        {
            if (stored is not null)
            {
                if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    var token = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = stored.RefreshToken, ["token_type"] = "eg1" }, ct);
                    var updated = stored with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) };
                    await SaveAsync(updated, ct);
                    stored = updated;
                }
            }
            else
            {
                var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pendingLock) pending = source;
                string code;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(300));
                    timeout.Token.ThrowIfCancellationRequested();
                    string redirect = Uri.EscapeDataString("https://www.epicgames.com/id/api/redirect?clientId=" + ClientId + "&responseType=code");
                    openBrowser("https://www.epicgames.com/id/login?redirectUrl=" + redirect);
                    code = await source.Task.WaitAsync(timeout.Token);
                }
                finally { lock (pendingLock) pending = null; }
                var token = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["token_type"] = "eg1" }, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, Api + "verify");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var profile = await response.Content.ReadFromJsonAsync<Profile>(Json, ct);
                if (string.IsNullOrWhiteSpace(profile?.AccountId) || string.IsNullOrWhiteSpace(profile.DisplayName))
                    throw new InvalidOperationException("Nieprawidłowy profil Epic / Invalid Epic profile.");
                var now = DateTimeOffset.UtcNow;
                var data = new StoredAccount(token.AccessToken, token.RefreshToken, now.AddSeconds(token.ExpiresIn), profile.AccountId, profile.DisplayName, now);
                await SaveAsync(data, ct);
                stored = data;
                Current = data.Account();
                changed = true;
            }
        }
        finally { gate.Release(); }
        if (changed) AccountChanged?.Invoke();
        return Current!;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        bool changed;
        try
        {
            if (stored is not null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, Api + "sessions/kill/" + Uri.EscapeDataString(stored.AccessToken));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stored.AccessToken);
                    using var response = await http.SendAsync(request, ct);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            }
            store.Delete();
            changed = Current is not null;
            stored = null;
            Current = null;
        }
        finally { gate.Release(); }
        if (changed) AccountChanged?.Invoke();
    }

    private async Task<TokenResponse> TokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + "token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(ClientId + ":" + ClientSecret)));
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken) || token.ExpiresIn <= 0)
            throw new InvalidOperationException("Nieprawidłowy token Epic / Invalid Epic token.");
        return token;
    }

    private Task SaveAsync(StoredAccount data, CancellationToken ct) => store.SaveAsync(JsonSerializer.SerializeToUtf8Bytes(data, Json), ct);
    private sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);
    private sealed record Profile(string AccountId, string DisplayName);
    private sealed record StoredAccount(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string AccountId, string DisplayName, DateTimeOffset ObtainedAt)
    {
        public AccountInfo Account() => new("epic", AccountId, DisplayName, null, ObtainedAt);
    }
}
