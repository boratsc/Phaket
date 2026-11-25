using System.Net;
using System.Net.Http;
using System.Text;
using Phaket.Core.Accounts;
using Phaket.Core.Nexus;

namespace Phaket.Core.Tests;

// PAK-117: nxm parsing, credential abstraction, secret key slot and metadata cache.
public sealed class NexusCoreTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<(bool ApiKey, string? AuthScheme, string? UserAgent)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            bool apiKey = request.Headers.TryGetValues("apikey", out var values) && !string.IsNullOrEmpty(values.FirstOrDefault());
            Seen.Add((apiKey, request.Headers.Authorization?.Scheme,
                request.Headers.UserAgent.Count > 0 ? request.Headers.UserAgent.ToString() : null));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK,
        Action<HttpResponseMessage>? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        headers?.Invoke(response);
        return response;
    }

    private sealed class ThrowingStore : ISecretStore
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "unavailable");
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default) =>
            throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "unavailable");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "unavailable");
    }

    [Theory]
    [InlineData("nxm://amongus/mods/123/files/456?key=abc&expires=1700000000", true, true, "amongus", 123, 456)]
    [InlineData("NXM://amongus/mods/1/files/2", true, false, "amongus", 1, 2)]
    [InlineData("nxm://amongus/mods/1/files/2?key=abc", true, false, "amongus", 1, 2)]
    [InlineData("nxm://amongus/mods/1/files/2?key=abc&expires=soon", true, false, "amongus", 1, 2)]
    [InlineData("https://amongus/mods/1/files/2", false, false, null, 0, 0)]
    [InlineData("nxm://amongus/mods/1", false, false, null, 0, 0)]
    [InlineData("nxm://amongus/mods/0/files/2", false, false, null, 0, 0)]
    [InlineData("nxm://user@amongus/mods/1/files/2", false, false, null, 0, 0)]
    [InlineData("nxm://among_us/mods/1/files/2", false, false, null, 0, 0)]
    [InlineData("", false, false, null, 0, 0)]
    public void Nxm_link_is_parsed(string value, bool ok, bool token, string? domain, int modId, int fileId)
    {
        Assert.Equal(ok, NxmLink.TryParse(value, out var link));
        if (!ok) { Assert.Null(link); return; }
        Assert.Equal(domain, link!.GameDomain);
        Assert.Equal(modId, link.ModId);
        Assert.Equal(fileId, link.FileId);
        Assert.Equal(token, link.HasDownloadToken);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/amongus/mods/123?tab=files", "amongus", 123)]
    [InlineData("https://nexusmods.com/games/1/mods/123", null, 123)]
    [InlineData("123", null, 123)]
    [InlineData("https://www.nexusmods.com/amongus/mods/0", null, 0)]
    [InlineData("https://nexusmods.com.evil.test/amongus/mods/1", null, 0)]
    [InlineData("http://www.nexusmods.com/amongus/mods/1", null, 0)]
    public void Mod_reference_accepts_site_links_and_bare_ids(string value, string? domain, int modId)
    {
        Assert.Equal(modId > 0, NexusModReference.TryParse(value, out var reference));
        if (modId > 0) { Assert.Equal(domain, reference!.Domain); Assert.Equal(modId, reference.ModId); }
    }

    [Theory]
    [InlineData("https://cf-files.nexusmods.com/cdn/1/2/x.zip", true)]
    [InlineData("https://supporter-files.nexus-cdn.com/1/2/x.zip", true)]
    [InlineData("https://nexusmods.com/x.zip", true)]
    [InlineData("https://nexusmods.com.evil.test/x.zip", false)]
    [InlineData("https://evilnexusmods.com/x.zip", false)]
    [InlineData("https://127.0.0.1/x.zip", false)]
    [InlineData("https://192.168.0.10/x.zip", false)]
    [InlineData("https://[::1]/x.zip", false)]
    [InlineData("https://cf-files.nexusmods.com:8443/x.zip", false)]
    [InlineData("http://cf-files.nexusmods.com/x.zip", false)]
    [InlineData("https://user@cf-files.nexusmods.com/x.zip", false)]
    public void Download_policy_accepts_only_nexus_https_hosts(string url, bool allowed) =>
        Assert.Equal(allowed, NexusDownloadPolicy.IsAllowed(new Uri(url)));

    // PAK-129: the loopback exception exists for the offline stub only and must stay opt-in.
    [Fact]
    public void Download_policy_allows_loopback_only_for_the_test_stub()
    {
        Assert.False(NexusDownloadPolicy.IsAllowed(new Uri("http://127.0.0.1:1234/x.zip")));
        Assert.True(NexusDownloadPolicy.IsAllowed(new Uri("http://127.0.0.1:1234/x.zip"), allowLoopback: true));
        Assert.True(NexusDownloadPolicy.IsAllowed(new Uri("http://localhost:1234/x.zip"), allowLoopback: true));
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/games/3667/mods/123", 3667, null)]
    [InlineData("https://www.nexusmods.com/games/valheim/mods/123", 0, "valheim")]
    [InlineData("https://www.nexusmods.com/valheim/mods/123", 0, "valheim")]
    [InlineData("https://www.nexusmods.com/games/not_a_domain/mods/123", 0, null)]
    public void Mod_reference_keeps_the_numeric_game_id_or_domain(string value, int gameId, string? domain)
    {
        bool ok = NexusModReference.TryParse(value, out var reference);
        Assert.Equal(gameId > 0 || domain is not null, ok);
        if (!ok) return;
        Assert.Equal(gameId > 0 ? gameId : (int?)null, reference!.GameId);
        Assert.Equal(domain, reference.Domain);
        Assert.Equal(123, reference.ModId);
    }

    [Fact]
    public async Task Key_is_saved_and_read_through_the_secret_store()
    {
        var store = new InMemorySecretStore();
        var keys = new NexusApiKeyStore(store);
        Assert.Null(await keys.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await keys.SaveAsync("  abcdef123456  ", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("abcdef123456", await keys.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("abcdef123456", Encoding.UTF8.GetString((await store.GetAsync(NexusApiKeyStore.SecretKey, cancellationToken: TestContext.Current.CancellationToken))!));
        await keys.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await keys.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Key_falls_back_to_session_memory_when_the_store_is_unavailable()
    {
        var keys = new NexusApiKeyStore(new ThrowingStore());
        Assert.False(await keys.SaveAsync("session-only-key", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(keys.IsMemoryOnly);
        Assert.Equal("session-only-key", await keys.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
        await keys.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await keys.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oauth_token_slot_round_trips_in_the_same_store()
    {
        var keys = new NexusApiKeyStore(new InMemorySecretStore());
        var tokens = new NexusOAuthTokens("access", "refresh", new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await keys.SaveOAuthTokensAsync(tokens, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(tokens, await keys.ReadOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken));
        await keys.DeleteOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await keys.ReadOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Credential_maps_to_apikey_or_bearer_header()
    {
        using var api = new HttpRequestMessage();
        NexusCredential.ApiKey("k1").Apply(api);
        Assert.Equal("k1", api.Headers.GetValues("apikey").Single());

        using var bearer = new HttpRequestMessage();
        NexusCredential.Bearer("token").Apply(bearer);
        Assert.Equal("Bearer", bearer.Headers.Authorization?.Scheme);
        Assert.Equal("token", bearer.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Cache_serves_metadata_until_the_ttl_passes_then_refetches()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler(_ => Json(
            """{ "mod_id": 123, "name": "Example", "author": "A", "version": "1.0", "endorsement_count": 7 }"""));
        var keys = new NexusApiKeyStore(new InMemorySecretStore());
        await keys.SaveAsync("key", cancellationToken: TestContext.Current.CancellationToken);
        var client = new NexusApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.nexusmods.com") }, new NexusApiKeyCredentialProvider(keys),
            new NexusCache(null, time, TimeSpan.FromMinutes(5)));

        Assert.Equal("Example", (await client.GetModAsync("amongus", 123, cancellationToken: TestContext.Current.CancellationToken)).Name);
        Assert.Equal("Example", (await client.GetModAsync("amongus", 123, cancellationToken: TestContext.Current.CancellationToken)).Name);
        Assert.Equal(1, handler.Calls);
        Assert.True(Assert.Single(handler.Seen).ApiKey);
        Assert.False(string.IsNullOrEmpty(Assert.Single(handler.Seen).UserAgent));

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("Example", (await client.GetModAsync("amongus", 123, cancellationToken: TestContext.Current.CancellationToken)).Name);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Exhausted_limit_returns_the_last_cached_metadata_with_a_flag()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        int call = 0;
        var handler = new RecordingHandler(_ => ++call == 1
            ? Json("""{ "mod_id": 5, "name": "Cached" }""")
            : Json("""{ "message": "rate limited" }""", HttpStatusCode.TooManyRequests,
                response => response.Headers.TryAddWithoutValidation("x-rl-hourly-remaining", "0")));
        var keys = new NexusApiKeyStore(new InMemorySecretStore());
        await keys.SaveAsync("key", cancellationToken: TestContext.Current.CancellationToken);
        var client = new NexusApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.nexusmods.com") }, new NexusApiKeyCredentialProvider(keys),
            new NexusCache(null, time, TimeSpan.FromMinutes(5)));

        await client.GetModAsync("amongus", 5, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(6));
        var stale = await client.GetModAsync("amongus", 5, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Cached", stale.Name);
        Assert.True(client.LastReadFromStaleCache);
        Assert.Equal(0, client.RateLimits.HourlyRemaining);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Bearer_credential_is_sent_as_authorization_header()
    {
        var handler = new RecordingHandler(_ => Json("""{ "name": "Someone", "is_premium": true }"""));
        var client = new NexusApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.nexusmods.com") }, new BearerProvider());
        var user = await client.ValidateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(user.IsPremium);
        Assert.False(Assert.Single(handler.Seen).ApiKey);
        Assert.Equal("Bearer", Assert.Single(handler.Seen).AuthScheme);
    }

    private sealed class BearerProvider : INexusCredentialProvider
    {
        public Task<NexusCredential?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusCredential?>(NexusCredential.Bearer("oauth-token"));
    }

    // RFC 7636 Appendix B, verbatim.
    [Fact]
    public void Pkce_challenge_matches_the_rfc7636_example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            NexusPkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Pkce_verifier_is_url_safe_and_authorize_url_carries_every_parameter()
    {
        var options = NexusOAuthOptions.Default with { ClientId = "client-1" };
        var (verifier, challenge) = NexusPkce.Create();
        Assert.True(verifier.Length is >= 43 and <= 128);
        Assert.DoesNotContain(verifier, c => c is '+' or '/' or '=');
        Assert.DoesNotContain(challenge, c => c is '+' or '/' or '=');
        Assert.NotEqual(NexusPkce.NewState(), NexusPkce.NewState());

        var url = NexusOAuth.BuildAuthorizeUrl(options, "state-1", challenge);
        Assert.StartsWith("https://users.nexusmods.com/oauth/authorize?", url.AbsoluteUri);
        string query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("client_id=client-1", query);
        Assert.Contains("response_type=code", query);
        Assert.Contains("redirect_uri=http://127.0.0.1:8642/callback", query);
        Assert.Contains("state=state-1", query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("code_challenge=" + challenge, query);
        Assert.Contains("scope=", query);
    }

    [Fact]
    public async Task Code_exchange_posts_the_verifier_and_stores_the_expiry()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        string? form = null;
        var handler = new RecordingHandler(request =>
        {
            form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{ "access_token": "at", "refresh_token": "rt", "expires_in": 3600, "token_type": "Bearer" }""");
        });
        var tokens = await NexusOAuth.ExchangeCodeAsync(new HttpClient(handler), NexusOAuthOptions.Default,
            "the-code", "the-verifier", time, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);
        Assert.Equal(time.GetUtcNow().AddSeconds(3600), tokens.ExpiresAt);
        Assert.Contains("grant_type=authorization_code", form);
        Assert.Contains("code_verifier=the-verifier", form);
        Assert.Contains("code=the-code", form);
        Assert.Contains("client_id=public_test", form);
    }

    [Fact]
    public async Task Refresh_keeps_the_previous_refresh_token_when_the_response_omits_it()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler(_ => Json("""{ "access_token": "new", "expires_in": 60 }"""));
        var previous = new NexusOAuthTokens("old", "keep-me", time.GetUtcNow().AddSeconds(-1));
        var tokens = await NexusOAuth.RefreshAsync(new HttpClient(handler), NexusOAuthOptions.Default, previous, time, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new", tokens.AccessToken);
        Assert.Equal("keep-me", tokens.RefreshToken);
    }

    [Fact]
    public async Task Rejected_refresh_is_reported_as_revoked()
    {
        var handler = new RecordingHandler(_ => Json("""{ "error": "invalid_grant" }""", HttpStatusCode.BadRequest));
        var current = new NexusOAuthTokens("a", "r", DateTimeOffset.UtcNow.AddSeconds(-1));
        var ex = await Assert.ThrowsAsync<NexusApiException>(() =>
            NexusOAuth.RefreshAsync(new HttpClient(handler), NexusOAuthOptions.Default, current, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("NEXUS_OAUTH_REVOKED", ex.Code);
    }
}