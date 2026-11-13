using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Phaket.Core.Tests")]
namespace Phaket.Core.Accounts;

public sealed class DiscordAccountService : IAccountService
{
    private const string TokenUrl = "https://discord.com/api/oauth2/token";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient http;
    private readonly Action<string> openBrowser;
    private readonly string configEndpoint, storePath, redirectUri;
    private readonly Uri redirect;
    private readonly EncryptedStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? clientId;
    private StoredAccount? stored;

    public string Provider => "discord";
    public AccountInfo? Current { get; private set; }
    public event Action? AccountChanged;

    public DiscordAccountService(string? configEndpoint = null, string? storePath = null, string? redirectUri = null)
        : this(configEndpoint, storePath, redirectUri, null, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }

    internal DiscordAccountService(string? configEndpoint, string? storePath, string? redirectUri, HttpMessageHandler? handler, Action<string> openBrowser)
    {
        this.configEndpoint = configEndpoint ?? "https://clairbot.app/api/susmodder/config";
        this.storePath = Path.GetFullPath(storePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "account.bin"));
        this.redirectUri = redirectUri ?? "http://127.0.0.1:53124/susmodder/callback";
        redirect = new Uri(this.redirectUri, UriKind.Absolute);
        if (redirect.Scheme != "http" || redirect.Host != "127.0.0.1" || redirect.Port <= 0 || !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment) || !string.IsNullOrEmpty(redirect.UserInfo))
            throw new ArgumentException("Nieprawidłowy adres zwrotny / Invalid callback address.", nameof(redirectUri));
        http = handler is null ? SharedHttp : new HttpClient(handler);
        this.openBrowser = openBrowser;
        store = new EncryptedStore(this.storePath);
        try
        {
            var plaintext = store.Load();
            stored = plaintext is null ? null : JsonSerializer.Deserialize<StoredAccount>(plaintext, Json);
            if (stored is null || string.IsNullOrEmpty(stored.UserId) || string.IsNullOrEmpty(stored.Username) || string.IsNullOrEmpty(stored.RefreshToken) || string.IsNullOrEmpty(stored.AccessToken)) stored = null;
            Current = stored?.Account();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException or JsonException or ArgumentException) { stored = null; }
    }

    public async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (stored is not null)
            {
                if (stored.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) await RefreshCoreAsync(ct);
                return Current!;
            }
            string id = await ClientIdAsync(ct);
            string verifier = AccountPkce.NewVerifier();
            string state = AccountPkce.NewState();
            var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
            listener.Start();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                string url = "https://discord.com/api/oauth2/authorize?response_type=code&client_id=" + Uri.EscapeDataString(id) +
                    "&redirect_uri=" + Uri.EscapeDataString(redirectUri) + "&scope=identify+guilds&state=" + state +
                    "&code_challenge=" + AccountPkce.Challenge(verifier) + "&code_challenge_method=S256";
                openBrowser(url);
                string code = await ReceiveCallbackAsync(listener, state, timeout.Token);
                TokenResponse token = await TokenAsync(new() { ["client_id"] = id, ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier }, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var user = await response.Content.ReadFromJsonAsync<UserResponse>(Json, ct);
                if (user is null || string.IsNullOrEmpty(user.Id) || string.IsNullOrEmpty(user.Username)) throw new InvalidOperationException("Nieprawidłowa odpowiedź Discord / Invalid Discord response.");
                var now = DateTimeOffset.UtcNow;
                var data = new StoredAccount(token.AccessToken, token.RefreshToken, now.AddSeconds(token.ExpiresIn), user.Id, user.Username, user.GlobalName, now);
                await SaveAsync(data, ct);
                stored = data; Current = data.Account();
            }
            finally { listener.Stop(); }
        }
        finally { gate.Release(); }
        AccountChanged?.Invoke();
        return Current!;
    }

    internal async Task RefreshAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await RefreshCoreAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var old = stored ?? throw new InvalidOperationException("Brak konta / No account.");
        var token = await TokenAsync(new() { ["client_id"] = await ClientIdAsync(ct), ["grant_type"] = "refresh_token", ["refresh_token"] = old.RefreshToken }, ct);
        var updated = old with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) };
        await SaveAsync(updated, ct);
        stored = updated;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        Exception? error = null;
        bool changed = false;
        try
        {
            if (stored is not null)
            {
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = stored.RefreshToken, ["client_id"] = await ClientIdAsync(ct) });
                    using var response = await http.PostAsync(TokenUrl + "/revoke", content, ct);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException) { error = ex; }
            }
            store.Delete();
            changed = Current is not null;
            stored = null; Current = null;
        }
        finally { gate.Release(); }
        if (changed) AccountChanged?.Invoke();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private async Task<string> ClientIdAsync(CancellationToken ct)
    {
        if (clientId is not null) return clientId;
        var config = await http.GetFromJsonAsync<Config>(configEndpoint, Json, ct);
        if (string.IsNullOrWhiteSpace(config?.DiscordClientId)) throw new InvalidOperationException("Brak identyfikatora klienta / Missing client ID.");
        return clientId = config.DiscordClientId;
    }

    private async Task<TokenResponse> TokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(TokenUrl, content, ct);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);
        if (token is null || string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.RefreshToken) || token.ExpiresIn <= 0)
            throw new InvalidOperationException("Nieprawidłowy token Discord / Invalid Discord token.");
        return token;
    }

    private Task SaveAsync(StoredAccount data, CancellationToken ct) =>
        store.SaveAsync(JsonSerializer.SerializeToUtf8Bytes(data, Json), ct);

    private async Task<string> ReceiveCallbackAsync(TcpListener listener, string state, CancellationToken ct)
    {
        var values = await LoopbackCallback.ReceiveQueryAsync(listener, redirect.AbsolutePath, ct);
        LoopbackCallback.ValidateState(values, state);
        if (!values.TryGetValue("code", out string? code) || string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Autoryzacja odrzucona / Authorization rejected.");
        return code;
    }

    private sealed record Config(string DiscordClientId);
    private sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);
    private sealed record UserResponse(string Id, string Username, string? GlobalName);
    private sealed record StoredAccount(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId, string Username, string? GlobalName, DateTimeOffset ObtainedAt)
    {
        public AccountInfo Account() => new("discord", UserId, Username, GlobalName, ObtainedAt);
    }
}
