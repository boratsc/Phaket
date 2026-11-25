using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Phaket.Core.Nexus;

// OAuth2 authorization-code + PKCE (S256) for Nexus, with no client secret:
// the browser returns a code to a loopback listener, which is exchanged for
// access/refresh tokens stored in the platform secret store.
public sealed record NexusOAuthOptions(string ClientId, Uri AuthorizeEndpoint, Uri TokenEndpoint,
    string RedirectUri, TimeSpan Timeout)
{
    public const string DefaultClientId = "public_test";

    public static NexusOAuthOptions Default => new(DefaultClientId,
        new Uri("https://users.nexusmods.com/oauth/authorize"),
        new Uri("https://users.nexusmods.com/oauth/token"),
        "http://127.0.0.1:8642/callback", TimeSpan.FromSeconds(120));

    // Precedence: environment override, then the value from settings, then the example client.
    public static string ResolveClientId(string? configured = null) =>
        Environment.GetEnvironmentVariable("PHAKET_NEXUS_CLIENT_ID") is { Length: > 0 } fromEnvironment
            ? fromEnvironment.Trim()
            : string.IsNullOrWhiteSpace(configured) ? DefaultClientId : configured.Trim();
}

public static class NexusPkce
{
    public static (string Verifier, string Challenge) Create()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return (verifier, ChallengeFor(verifier));
    }

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    // RFC 7636 S256: base64url(SHA-256(ASCII(verifier))) without padding.
    public static string ChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class NexusOAuth
{
    public static Uri BuildAuthorizeUrl(NexusOAuthOptions options, string state, string challenge, string? scope = null)
    {
        string query = string.Join('&',
        [
            "client_id=" + Uri.EscapeDataString(options.ClientId),
            "response_type=code",
            "scope=" + Uri.EscapeDataString(scope ?? ""),
            "redirect_uri=" + Uri.EscapeDataString(options.RedirectUri),
            "state=" + Uri.EscapeDataString(state),
            "code_challenge_method=S256",
            "code_challenge=" + Uri.EscapeDataString(challenge)
        ]);
        return new Uri(options.AuthorizeEndpoint.AbsoluteUri + "?" + query);
    }

    public static Task<NexusOAuthTokens> ExchangeCodeAsync(HttpClient http, NexusOAuthOptions options, string code,
        string verifier, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default) =>
        RequestTokensAsync(http, options, new()
        {
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = options.RedirectUri,
            ["client_id"] = options.ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier
        }, refresh: false, timeProvider ?? TimeProvider.System, null, cancellationToken);

    public static Task<NexusOAuthTokens> RefreshAsync(HttpClient http, NexusOAuthOptions options, NexusOAuthTokens current,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default) =>
        RequestTokensAsync(http, options, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = options.ClientId
        }, refresh: true, timeProvider ?? TimeProvider.System, current.RefreshToken, cancellationToken);

    private static async Task<NexusOAuthTokens> RequestTokensAsync(HttpClient http, NexusOAuthOptions options,
        Dictionary<string, string> form, bool refresh, TimeProvider time, string? previousRefresh, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        { throw new NexusApiException("NEXUS_OAUTH_UNAVAILABLE", ex); }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A rejected refresh token means the user revoked access: log out and say so.
                if ((int)response.StatusCode is 400 or 401 or 403)
                    throw new NexusApiException(refresh ? "NEXUS_OAUTH_REVOKED" : "NEXUS_OAUTH_FAILED");
                throw new NexusApiException("NEXUS_OAUTH_UNAVAILABLE");
            }
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                string? access = String(root, "access_token");
                string? refreshToken = String(root, "refresh_token") ?? previousRefresh;
                if (string.IsNullOrEmpty(access)) throw new NexusApiException("NEXUS_OAUTH_UNAVAILABLE");
                double expiresIn = Number(root, "expires_in") ?? 3600;
                return new(access, refreshToken ?? "", time.GetUtcNow().AddSeconds(Math.Max(60, expiresIn)));
            }
            catch (JsonException ex) { throw new NexusApiException("NEXUS_OAUTH_UNAVAILABLE", ex); }
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }
}