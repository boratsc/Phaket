using System.Diagnostics;
using System.Net.Http;
using Phaket.Core.Nexus;

namespace Phaket.Services;

public sealed record NexusKeyStatus(NexusUser User, NexusRateLimits Limits);

// Stable API error codes to bilingual UI keys, shared by Settings and the browser.
public static class NexusMessages
{
    public static string KeyFor(string code) => code switch
    {
        "NEXUS_API_KEY_MISSING" => "Nexus.Error.KeyMissing",
        "NEXUS_API_KEY_INVALID" => "Nexus.Error.KeyInvalid",
        "NEXUS_RATE_LIMIT" => "Nexus.Error.RateLimit",
        "NEXUS_DOWNLOAD_BLOCKED" => "Nexus.Error.DownloadBlocked",
        "NEXUS_DOWNLOAD_TOO_LARGE" => "Nexus.Error.DownloadTooLarge",
        "NEXUS_PREMIUM_REQUIRED" => "Nexus.Error.NxmRequired",
        "NEXUS_NXM_EXPIRED" => "Nexus.Error.NxmExpired",
        "NEXUS_NOT_FOUND" => "Nexus.Error.NotFound",
        "NEXUS_INVALID_LINK" => "Nexus.Error.InvalidReference",
        "NEXUS_DOWNLOAD_FAILED" => "Nexus.Error.DownloadFailed",
        "NEXUS_OAUTH_FAILED" => "Nexus.Error.OAuthFailed",
        "NEXUS_OAUTH_TIMEOUT" => "Nexus.Error.OAuthTimeout",
        "NEXUS_OAUTH_REVOKED" => "Nexus.Error.OAuthRevoked",
        "NEXUS_OAUTH_STATE" => "Nexus.Error.OAuthState",
        "NEXUS_OAUTH_UNAVAILABLE" => "Nexus.Error.OAuthUnavailable",
        _ => "Nexus.Error.Unavailable"
    };
}
public sealed record NexusDownloadResult(string Path, long Size, string FileName);

// Desktop wiring for Nexus (PAK-117): secret key, TTL cache, API client and the
// download folder. Every call goes from this machine straight to api.nexusmods.com;
// nothing about the user's Nexus account leaves it for PhaketServer.
public interface INexusService
{
    Task<NexusKeyStatus> ValidateKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<NexusKeyStatus> ValidateStoredKeyAsync(CancellationToken cancellationToken = default);
    Task<bool> SaveKeyAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteKeyAsync(CancellationToken cancellationToken = default);
    void ConfigureClientId(string? clientId);
    Task<NexusKeyStatus> SignInAsync(string? clientId, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task<bool> HasOAuthSessionAsync(CancellationToken cancellationToken = default);
    Task<NexusModMetadata> GetModAsync(string domain, int modId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusFileInfo>> GetFilesAsync(string domain, int modId, CancellationToken cancellationToken = default);
    NexusModMetadata? CachedMod(string domain, int modId);
    IReadOnlyList<NexusFileInfo>? CachedFiles(string domain, int modId);
    Task<NexusDownloadResult> DownloadAsync(NxmLink link, CancellationToken cancellationToken = default);
    NexusRateLimits LastLimits { get; }
    bool LastReadFromStaleCache { get; }
}

public sealed class NexusService : INexusService
{
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly NexusApiClient api;
    private readonly NexusApiKeyStore keys;
    private readonly string downloadsRoot;
    private readonly TimeProvider timeProvider;
    private readonly Func<NexusOAuthOptions> oauthOptions;
    private readonly Func<Uri, Task> openBrowser;
    private readonly Func<string, TimeSpan, CancellationToken, Task<NexusOAuthCallback>> receiveCallback;
    private readonly long maxDownloadBytes;
    private readonly bool allowLoopbackDownloads;
    private string? configuredClientId;

    public NexusService(NexusApiKeyStore? keys = null, HttpClient? http = null, string? appDataRoot = null,
        TimeProvider? timeProvider = null, Func<Uri, Task>? openBrowser = null,
        Func<string, TimeSpan, CancellationToken, Task<NexusOAuthCallback>>? receiveCallback = null,
        Func<NexusOAuthOptions>? oauthOptions = null,
        // Test seams: the loopback stub is http on a random port and streams without Content-Length.
        bool allowLoopbackDownloads = false, long maxDownloadBytes = MaxDownloadBytes)
    {
        string root = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");
        this.keys = keys ?? new NexusApiKeyStore();
        this.http = http ?? CreateHttpClient();
        this.maxDownloadBytes = maxDownloadBytes > 0 ? maxDownloadBytes : MaxDownloadBytes;
        this.allowLoopbackDownloads = allowLoopbackDownloads;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.openBrowser = openBrowser ?? OpenBrowserAsync;
        this.receiveCallback = receiveCallback ?? NexusOAuthCallbackListener.WaitAsync;
        this.oauthOptions = oauthOptions ?? (() => NexusOAuthOptions.Default with
        {
            ClientId = NexusOAuthOptions.ResolveClientId(configuredClientId)
        });
        api = new NexusApiClient(this.http, new NexusCredentialChain(
            new NexusOAuthCredentialProvider(this.keys, this.http, this.oauthOptions, timeProvider),
            new NexusApiKeyCredentialProvider(this.keys)),
            new NexusCache(Path.Combine(root, "nexus-cache.json"), timeProvider));
        downloadsRoot = Path.Combine(root, "downloads", "nexus");
    }

    public NexusRateLimits LastLimits => api.RateLimits;
    public bool LastReadFromStaleCache => api.LastReadFromStaleCache;
    public NexusModMetadata? CachedMod(string domain, int modId) => api.CachedMod(domain, modId);
    public IReadOnlyList<NexusFileInfo>? CachedFiles(string domain, int modId) => api.CachedFiles(domain, modId);

    private static HttpClient CreateHttpClient()
    {
        // Redirects are followed by hand so every hop is checked against the download allowlist.
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("https://api.nexusmods.com"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Phaket/0.1 (Nexus integration; testing)");
        return client;
    }

    public async Task<NexusKeyStatus> ValidateKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        NexusUser user = await api.ValidateAsync(key.Trim(), cancellationToken).ConfigureAwait(false);
        return new(user, api.RateLimits);
    }

    public async Task<NexusKeyStatus> ValidateStoredKeyAsync(CancellationToken cancellationToken = default)
    {
        NexusUser user = await api.ValidateAsync(null, cancellationToken).ConfigureAwait(false);
        return new(user, api.RateLimits);
    }

    public Task<bool> SaveKeyAsync(string key, CancellationToken cancellationToken = default) =>
        keys.SaveAsync(key, cancellationToken);

    public Task DeleteKeyAsync(CancellationToken cancellationToken = default) => keys.DeleteAsync(cancellationToken);

    public void ConfigureClientId(string? clientId) => configuredClientId = clientId;

    public async Task<NexusKeyStatus> SignInAsync(string? clientId, CancellationToken cancellationToken = default)
    {
        ConfigureClientId(clientId);
        NexusOAuthOptions options = oauthOptions();
        (string verifier, string challenge) = NexusPkce.Create();
        string state = NexusPkce.NewState();
        await openBrowser(NexusOAuth.BuildAuthorizeUrl(options, state, challenge)).ConfigureAwait(false);
        NexusOAuthCallback callback = await receiveCallback(state, options.Timeout, cancellationToken).ConfigureAwait(false);
        if (!callback.Matches(state)) throw new NexusApiException("NEXUS_OAUTH_STATE");
        NexusOAuthTokens tokens = await NexusOAuth.ExchangeCodeAsync(http, options, callback.Code, verifier, timeProvider,
            cancellationToken).ConfigureAwait(false);
        await keys.SaveOAuthTokensAsync(tokens, cancellationToken).ConfigureAwait(false);
        try
        {
            NexusUser user = await api.ValidateAsync(null, cancellationToken).ConfigureAwait(false);
            return new(user, api.RateLimits);
        }
        catch
        {
            // Tokens that cannot identify the account must not stay behind.
            await keys.DeleteOAuthTokensAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => keys.DeleteOAuthTokensAsync(cancellationToken);

    public async Task<bool> HasOAuthSessionAsync(CancellationToken cancellationToken = default) =>
        await keys.ReadOAuthTokensAsync(cancellationToken).ConfigureAwait(false) is not null;

    private static Task OpenBrowserAsync(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new NexusApiException("NEXUS_OAUTH_UNAVAILABLE", ex);
        }
    }

    public Task<NexusModMetadata> GetModAsync(string domain, int modId, CancellationToken cancellationToken = default) =>
        api.GetModAsync(domain, modId, cancellationToken);

    public Task<IReadOnlyList<NexusFileInfo>> GetFilesAsync(string domain, int modId, CancellationToken cancellationToken = default) =>
        api.GetFilesAsync(domain, modId, cancellationToken);

    public async Task<NexusDownloadResult> DownloadAsync(NxmLink link, CancellationToken cancellationToken = default)
    {
        // download_links.json needs the nxm key/expires pair; the premium-only endpoint
        // is called only after validate.json confirmed is_premium for the stored key.
        if (!link.HasDownloadToken)
        {
            NexusUser user = await api.ValidateAsync(null, cancellationToken).ConfigureAwait(false);
            if (!user.IsPremium) throw new NexusApiException("NEXUS_PREMIUM_REQUIRED");
        }

        IReadOnlyList<NexusDownloadLink> links = await api.GetDownloadLinksAsync(link, cancellationToken).ConfigureAwait(false);
        NexusDownloadLink? first = links.FirstOrDefault();
        if (first is null || !Uri.TryCreate(first.Uri, UriKind.Absolute, out var source) ||
            !NexusDownloadPolicy.IsAllowed(source, allowLoopbackDownloads))
            throw new NexusApiException("NEXUS_DOWNLOAD_BLOCKED");

        string directory = Path.Combine(downloadsRoot, link.GameDomain);
        Directory.CreateDirectory(directory);
        string target = UniquePath(directory, SafeFileName(first.Name, link));
        if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new NexusApiException("NEXUS_DOWNLOAD_FAILED");

        string temp = target + ".part";
        try
        {
            using var response = await OpenDownloadAsync(source, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new NexusApiException("NEXUS_DOWNLOAD_FAILED");
            if (response.Content.Headers.ContentLength is { } declared && declared > maxDownloadBytes)
                throw new NexusApiException("NEXUS_DOWNLOAD_TOO_LARGE");
            // A missing or lying Content-Length must not fill the disk: count while copying and
            // stop on the first chunk past the limit, then remove the partial file.
            var buffer = new byte[131072];
            long received = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > maxDownloadBytes) throw new NexusApiException("NEXUS_DOWNLOAD_TOO_LARGE");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            if (received == 0) throw new NexusApiException("NEXUS_DOWNLOAD_FAILED");
            File.Move(temp, target);
            return new(target, received, Path.GetFileName(target));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            TryDelete(temp);
            throw new NexusApiException("NEXUS_DOWNLOAD_FAILED", ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Redirects are followed only when every hop stays on an approved https host (or the
    // explicitly enabled loopback stub); the handler must not follow them on its own.
    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri source, CancellationToken cancellationToken)
    {
        Uri current = source;
        for (var hop = 0; ; hop++)
        {
            if (!NexusDownloadPolicy.IsAllowed(current, allowLoopbackDownloads))
                throw new NexusApiException("NEXUS_DOWNLOAD_BLOCKED");
            HttpResponseMessage response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            // A caller-supplied HttpClient may follow redirects on its own; the final address
            // is validated too, so such a hop is refused before its body is read.
            if (response.RequestMessage?.RequestUri is { } final && !NexusDownloadPolicy.IsAllowed(final, allowLoopbackDownloads))
            {
                response.Dispose();
                throw new NexusApiException("NEXUS_DOWNLOAD_BLOCKED");
            }
            if ((int)response.StatusCode is not (>= 300 and < 400)) return response;
            Uri? next = hop < NexusDownloadPolicy.MaxRedirects && response.Headers.Location is { } location
                ? new Uri(current, location)
                : null;
            response.Dispose();
            if (next is null) throw new NexusApiException("NEXUS_DOWNLOAD_FAILED");
            current = next;
        }
    }

    // Never overwrite an archive an installed mod or an earlier download may point at.
    private static string UniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(fileName), extension = Path.GetExtension(fileName);
        for (int index = 2; ; index++)
        {
            path = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    private static string SafeFileName(string? name, NxmLink link)
    {
        string candidate = Path.GetFileName((name ?? "").Trim());
        char[] invalid = Path.GetInvalidFileNameChars();
        candidate = new string(candidate.Where(c => !invalid.Contains(c) && c is not (< ' ')).ToArray());
        if (candidate.Length > 120) candidate = candidate[^120..];
        if (candidate.Length == 0 || candidate is "." or "..") candidate = $"nexus-{link.ModId}-{link.FileId}.zip";
        if (Path.GetExtension(candidate).Length == 0) candidate += ".zip";
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}