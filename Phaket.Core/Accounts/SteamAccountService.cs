using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Phaket.Core.Platforms;

namespace Phaket.Core.Accounts;

public sealed class SteamAccountService : IAccountService
{
    private const string OpenIdUrl = "https://steamcommunity.com/openid/login";
    private const string OpenIdNamespace = "http://specs.openid.net/auth/2.0";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient http;
    private readonly Action<string> openBrowser;
    private readonly Uri redirect;
    private readonly EncryptedStore store;
    private readonly SemaphoreSlim gate = new(1, 1);

    public string Provider => "steam";
    public AccountInfo? Current { get; private set; }
    public event Action? AccountChanged;

    public SteamAccountService(string? storePath = null, string? redirectUri = null)
        : this(storePath, redirectUri, null, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }

    internal SteamAccountService(string? storePath, string? redirectUri, HttpMessageHandler? handler, Action<string> openBrowser)
    {
        redirect = new Uri(redirectUri ?? "http://127.0.0.1:53125/steam/callback", UriKind.Absolute);
        if (redirect.Scheme != "http" || redirect.Host != "127.0.0.1" || redirect.Port <= 0 || !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment) || !string.IsNullOrEmpty(redirect.UserInfo))
            throw new ArgumentException("Nieprawidłowy adres zwrotny / Invalid callback address.", nameof(redirectUri));
        http = handler is null ? SharedHttp : new HttpClient(handler);
        this.openBrowser = openBrowser;
        store = new EncryptedStore(Path.GetFullPath(storePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "steam.bin")));
        try
        {
            byte[]? bytes = store.Load();
            var saved = bytes is null ? null : JsonSerializer.Deserialize<StoredAccount>(bytes, Json);
            if (saved is not null && IsSteamId(saved.Steamid) && !string.IsNullOrWhiteSpace(saved.Persona)) Current = saved.Account();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or CryptographicException) { }
    }

    public async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Current is not null) return Current;
            string state = AccountPkce.NewState();
            string returnTo = redirect.AbsoluteUri + "?state=" + state;
            string realm = redirect.GetLeftPart(UriPartial.Authority) + "/";
            var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
            listener.Start();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(180));
                string select = OpenIdNamespace + "/identifier_select";
                using var query = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["openid.ns"] = OpenIdNamespace, ["openid.mode"] = "checkid_setup", ["openid.return_to"] = returnTo,
                    ["openid.realm"] = realm, ["openid.identity"] = select, ["openid.claimed_id"] = select });
                openBrowser(OpenIdUrl + "?" + await query.ReadAsStringAsync(timeout.Token));
                var values = await LoopbackCallback.ReceiveQueryAsync(listener, redirect.AbsolutePath, timeout.Token);
                LoopbackCallback.ValidateState(values, state);
                if (!values.TryGetValue("openid.mode", out var mode) || mode != "id_res" ||
                    !values.TryGetValue("openid.claimed_id", out var claimed) ||
                    !Regex.IsMatch(claimed, @"\Ahttps://steamcommunity\.com/openid/id/[0-9]{17}\z", RegexOptions.CultureInvariant) ||
                    !values.TryGetValue("openid.return_to", out var returned) || returned != returnTo ||
                    !values.TryGetValue("openid.signed", out var signed) ||
                    !signed.Split(',').Contains("claimed_id", StringComparer.Ordinal) || !signed.Split(',').Contains("return_to", StringComparer.Ordinal))
                    throw new InvalidOperationException("Autoryzacja odrzucona / Authorization rejected.");
                var fields = values.Where(v => v.Key.StartsWith("openid.", StringComparison.Ordinal)).ToDictionary();
                fields["openid.mode"] = "check_authentication";
                using var content = new FormUrlEncodedContent(fields);
                using var response = await http.PostAsync(OpenIdUrl, content, timeout.Token);
                response.EnsureSuccessStatusCode();
                string verification = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!verification.Split('\n').Any(line => line.TrimEnd('\r') == "is_valid:true"))
                    throw new InvalidOperationException("Autoryzacja odrzucona / Authorization rejected.");
                string id = claimed[(claimed.LastIndexOf('/') + 1)..];
                string persona = await PersonaAsync(id, timeout.Token);
                var saved = new StoredAccount(id, persona, DateTimeOffset.UtcNow);
                await store.SaveAsync(JsonSerializer.SerializeToUtf8Bytes(saved, Json), timeout.Token);
                Current = saved.Account();
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
        try { store.Delete(); changed = Current is not null; Current = null; }
        finally { gate.Release(); }
        if (changed) AccountChanged?.Invoke();
    }

    public static AccountInfo? DetectLocal() => DetectLocal(DefaultLoginUsersPath());

    private static string DefaultLoginUsersPath()
    {
        string root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam");
        return Path.Combine(root, "config", "loginusers.vdf");
    }

    // One-click connect: adopt the locally detected Steam account without a browser round-trip.
    public Task<AccountInfo?> AdoptLocalAsync(CancellationToken ct = default) => AdoptLocalAsync(DefaultLoginUsersPath(), ct);

    internal async Task<AccountInfo?> AdoptLocalAsync(string loginUsersPath, CancellationToken ct)
    {
        var local = DetectLocal(loginUsersPath);
        if (local is null) return null;
        await gate.WaitAsync(ct);
        try
        {
            var saved = new StoredAccount(local.UserId, local.Username, DateTimeOffset.UtcNow);
            await store.SaveAsync(JsonSerializer.SerializeToUtf8Bytes(saved, Json), ct);
            Current = saved.Account();
        }
        finally { gate.Release(); }
        AccountChanged?.Invoke();
        return Current;
    }

    internal static AccountInfo? DetectLocal(string path)
    {
        try
        {
            var users = VdfParser.Parse(File.ReadAllText(path)).Child("users");
            var latest = users?.Children.Where(u => IsSteamId(u.Key) && !string.IsNullOrWhiteSpace(u.Value.Get("PersonaName")) && long.TryParse(u.Value.Get("Timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .OrderByDescending(u => long.Parse(u.Value.Get("Timestamp")!, CultureInfo.InvariantCulture)).FirstOrDefault();
            return latest is { } user && user.Key is not null ? new AccountInfo("steam", user.Key, user.Value.Get("PersonaName")!, null, DateTimeOffset.UtcNow) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    private async Task<string> PersonaAsync(string id, CancellationToken ct)
    {
        try
        {
            string xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{id}?xml=1", ct);
            return XDocument.Parse(xml).Root?.Element("steamID")?.Value is { Length: > 0 } name ? name : id;
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or InvalidOperationException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return id; }
    }

    private static bool IsSteamId(string? value) => value is { Length: 17 } && value.All(c => c is >= '0' and <= '9');
    private sealed record StoredAccount(string Steamid, string Persona, DateTimeOffset ObtainedAt)
    {
        public AccountInfo Account() => new("steam", Steamid, Persona, null, ObtainedAt);
    }
}
