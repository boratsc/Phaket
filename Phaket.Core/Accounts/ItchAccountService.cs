using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Phaket.Core.Accounts;

public sealed class ItchAccountService : IAccountService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string? clientId;
    private readonly Uri redirect;
    private readonly HttpClient http;
    private readonly Action<string> openBrowser;
    private readonly EncryptedStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private StoredAccount? stored;

    public string Provider => "itch";
    public AccountInfo? Current { get; private set; }
    public event Action? AccountChanged;

    public ItchAccountService(string? clientId = null, string? storePath = null, string? redirectUri = null)
        : this(clientId, storePath, redirectUri, null, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }

    internal ItchAccountService(string? clientId, string? storePath, string? redirectUri, HttpMessageHandler? handler, Action<string> openBrowser)
    {
        this.clientId = clientId;
        if (!Uri.TryCreate(redirectUri ?? "http://127.0.0.1/callback", UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Host != "127.0.0.1" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Nieprawidłowy adres zwrotny / Invalid callback address.", nameof(redirectUri));
        redirect = uri;
        http = handler is null ? SharedHttp : new HttpClient(handler);
        this.openBrowser = openBrowser;
        store = new EncryptedStore(Path.GetFullPath(storePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "itch.bin")));
        try
        {
            byte[]? plaintext = store.Load();
            stored = plaintext is null ? null : JsonSerializer.Deserialize<StoredAccount>(plaintext, Json);
            if (stored is null || string.IsNullOrEmpty(stored.AccessToken) || string.IsNullOrEmpty(stored.UserId) || string.IsNullOrEmpty(stored.Username)) stored = null;
            Current = stored?.Account();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException or JsonException or ArgumentException) { stored = null; }
    }

    public async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (stored is not null) return Current!;
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Zarejestruj aplikację OAuth itch.io (client_id) / Register an itch.io OAuth app (client_id).");
            string verifier = AccountPkce.NewVerifier(), state = AccountPkce.NewState();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                string callback = new UriBuilder(redirect) { Port = ((IPEndPoint)listener.LocalEndpoint).Port }.Uri.AbsoluteUri;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                string url = "https://itch.io/user/oauth?client_id=" + Uri.EscapeDataString(clientId) +
                    "&scope=profile%3Ame+profile%3Aowned&redirect_uri=" + Uri.EscapeDataString(callback) +
                    "&response_type=code&code_challenge=" + AccountPkce.Challenge(verifier) + "&code_challenge_method=S256&state=" + state;
                openBrowser(url);
                var values = await LoopbackCallback.ReceiveQueryAsync(listener, redirect.AbsolutePath, timeout.Token);
                LoopbackCallback.ValidateState(values, state);
                if (!values.TryGetValue("code", out string? code) || string.IsNullOrEmpty(code))
                    throw new InvalidOperationException("Autoryzacja odrzucona / Authorization rejected.");
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = callback, ["client_id"] = clientId });
                using var tokenResponse = await http.PostAsync("https://api.itch.io/oauth/token", content, ct);
                tokenResponse.EnsureSuccessStatusCode();
                var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);
                if (string.IsNullOrEmpty(token?.AccessToken)) throw new InvalidOperationException("Nieprawidłowy token itch.io / Invalid itch.io token.");
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.itch.io/profile");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var user = (await response.Content.ReadFromJsonAsync<ProfileResponse>(Json, ct))?.User;
                if (user is null || user.Id <= 0 || string.IsNullOrEmpty(user.Username)) throw new InvalidOperationException("Nieprawidłowa odpowiedź itch.io / Invalid itch.io response.");
                var data = new StoredAccount(token.AccessToken, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), user.DisplayName ?? user.Username, user.AvatarUrl, DateTimeOffset.UtcNow);
                await store.SaveAsync(JsonSerializer.SerializeToUtf8Bytes(data, Json), ct);
                stored = data; Current = data.Account();
            }
            finally { listener.Stop(); }
        }
        finally { gate.Release(); }
        AccountChanged?.Invoke();
        return Current!;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        bool changed;
        try
        {
            if (stored is not null && !string.IsNullOrWhiteSpace(clientId))
            {
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = stored.AccessToken, ["client_id"] = clientId });
                    using var response = await http.PostAsync("https://api.itch.io/oauth/revoke", content, ct);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            }
            store.Delete();
            changed = Current is not null;
            stored = null; Current = null;
        }
        finally { gate.Release(); }
        if (changed) AccountChanged?.Invoke();
    }

    private sealed record TokenResponse(string AccessToken);
    private sealed record ProfileResponse(UserResponse? User);
    private sealed record UserResponse(long Id, string Username, string? DisplayName, string? AvatarUrl);
    private sealed record StoredAccount(string AccessToken, string UserId, string Username, string? AvatarUrl, DateTimeOffset ObtainedAt)
    {
        public AccountInfo Account() => new("itch", UserId, Username, AvatarUrl, ObtainedAt);
    }
}
