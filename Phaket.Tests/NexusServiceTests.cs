using System.Net;
using System.Net.Sockets;
using System.Text;
using Phaket.Core.Accounts;
using Phaket.Core.Tests;
using Phaket.Core.Nexus;
using Phaket.Services;

namespace Phaket.Tests;

// Minimal loopback HTTP/1.1 stub: every request is answered by one handler and the
// connection is closed, so HttpClient never reuses a socket between test cases.
internal sealed class StubServer : IAsyncDisposable
{
    internal sealed record Request(string Method, string PathAndQuery, Dictionary<string, string> Headers, string Body)
    {
        public string Path => PathAndQuery.Split('?')[0];
    }

    internal sealed record Response(int Status, string ContentType, byte[] Body, Dictionary<string, string>? Headers = null,
        bool OmitContentLength = false)
    {
        public static Response Json(string body, int status = 200, Dictionary<string, string>? headers = null) =>
            new(status, "application/json", Encoding.UTF8.GetBytes(body), headers);
        public static Response Bytes(string contentType, byte[] body) => new(200, contentType, body);
    }

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<Request, Response> handler;
    private readonly Task loop;
    private readonly object gate = new();
    private readonly List<Request> requests = [];

    public StubServer(Func<Request, Response> handler)
    {
        this.handler = handler;
        listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        loop = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }
    public IReadOnlyList<Request> Requests { get { lock (gate) return requests.ToArray(); } }

    private async Task LoopAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellation.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                Request? request = await ReadRequestAsync(stream);
                if (request is null) return;
                lock (gate) requests.Add(request);
                Response response = handler(request);
                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\n")
                    .Append($"Content-Type: {response.ContentType}\r\n");
                if (!response.OmitContentLength) head.Append($"Content-Length: {response.Body.Length}\r\n");
                head.Append("Connection: close\r\n");
                if (response.Headers is not null)
                    foreach ((string name, string value) in response.Headers) head.Append($"{name}: {value}\r\n");
                head.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation.Token);
                await stream.WriteAsync(response.Body, cancellation.Token);
                await stream.FlushAsync(cancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task<Request?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, cancellation.Token);
            if (read == 0) return null;
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n') break;
        }

        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("Content-Length", out var raw) && int.TryParse(raw, out int parsed) ? parsed : 0;
        byte[] body = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(body, cancellation.Token);
        return new(requestLine[0], requestLine[1], headers, Encoding.UTF8.GetString(body));
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        listener.Stop();
        try { await loop; } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        cancellation.Dispose();
    }
}

public sealed class NexusServiceTests
{
    private const string ModJson =
        """{ "mod_id": 1, "name": "Example", "author": "Tester", "version": "1.2.3", "summary": "S", "endorsement_count": 5 }""";

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required NexusService Service { get; init; }
        public required NexusApiKeyStore Keys { get; init; }
        public required string Root { get; init; }
        public required List<Uri> Opened { get; init; }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }

    private static HttpClient Client(StubServer server) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(10)
        };

    private static Harness Create(StubServer server, FakeTime? time = null,
        Func<string, TimeSpan, CancellationToken, Task<NexusOAuthCallback>>? receive = null,
        bool allowLoopback = true, long maxDownloadBytes = 2L * 1024 * 1024 * 1024)
    {
        var store = new InMemorySecretStore();
        var keys = new NexusApiKeyStore(store);
        string root = Directory.CreateTempSubdirectory("phaket-nexus-").FullName;
        var opened = new List<Uri>();
        var service = new NexusService(keys, Client(server), root, time ?? new FakeTime(DateTimeOffset.UtcNow),
            openBrowser: uri => { opened.Add(uri); return Task.CompletedTask; },
            receiveCallback: receive ?? ((state, _, _) => Task.FromResult(new NexusOAuthCallback("the-code", state))),
            oauthOptions: () => NexusOAuthOptions.Default with
            {
                ClientId = "test-client",
                AuthorizeEndpoint = new Uri(server.BaseUrl + "/oauth/authorize"),
                TokenEndpoint = new Uri(server.BaseUrl + "/oauth/token")
            },
            allowLoopbackDownloads: allowLoopback,
            maxDownloadBytes: maxDownloadBytes);
        return new Harness { Keys = keys, Root = root, Service = service, Opened = opened };
    }

    [Fact]
    public async Task Nxm_token_downloads_without_premium_and_never_calls_validate()
    {
        byte[] payload = Encoding.UTF8.GetBytes("archive-bytes");
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" when request.PathAndQuery.Contains("key=k&expires=1700000000", StringComparison.Ordinal) =>
                StubServer.Response.Json($$"""[{ "name": "Example-1.zip", "URI": "{{baseUrl}}/cdn/Example-1.zip" }]"""),
            "/cdn/Example-1.zip" => StubServer.Response.Bytes("application/octet-stream", payload),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", cancellationToken: TestContext.Current.CancellationToken);

        NexusDownloadResult result = await harness.Service.DownloadAsync(NxmLink.TryParse(
            "nxm://amongus/mods/1/files/2?key=k&expires=1700000000", out var link) ? link! : throw new InvalidOperationException(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path, TestContext.Current.CancellationToken));
        Assert.EndsWith("Example-1.zip", result.Path);
        Assert.DoesNotContain(server.Requests, request => request.Path.Contains("validate", StringComparison.Ordinal));
        Assert.Equal("api-key", server.Requests[0].Headers["apikey"]);
    }

    [Fact]
    public async Task Free_account_is_told_to_use_an_nxm_link_instead_of_the_premium_endpoint()
    {
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/user/validate.json" => StubServer.Response.Json("""{ "name": "Free", "is_premium": false }"""),
            _ => StubServer.Response.Json("{}", 404)
        });
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", cancellationToken: TestContext.Current.CancellationToken);

        NxmLink.TryParse("nxm://amongus/mods/1/files/2", out var link);
        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(link!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("NEXUS_PREMIUM_REQUIRED", ex.Code);
        Assert.DoesNotContain(server.Requests, request => request.Path.Contains("download_link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Premium_account_uses_the_singular_download_link_endpoint()
    {
        byte[] payload = Encoding.UTF8.GetBytes("premium-bytes");
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/user/validate.json" => StubServer.Response.Json("""{ "name": "Premium", "is_premium": true }"""),
            "/v1/games/amongus/mods/1/files/2/download_link.json" =>
                StubServer.Response.Json($$"""[{ "name": "Premium.zip", "URI": "{{baseUrl}}/cdn/Premium.zip" }]"""),
            "/cdn/Premium.zip" => StubServer.Response.Bytes("application/octet-stream", payload),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", cancellationToken: TestContext.Current.CancellationToken);

        NxmLink.TryParse("nxm://amongus/mods/1/files/2", out var link);
        NexusDownloadResult result = await harness.Service.DownloadAsync(link!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Expired_nxm_token_is_reported_as_expired()
    {
        await using var server = new StubServer(_ => StubServer.Response.Json("""{ "message": "expired" }""", 403));
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", cancellationToken: TestContext.Current.CancellationToken);
        NxmLink.TryParse("nxm://amongus/mods/1/files/2?key=k&expires=1", out var link);
        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(link!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("NEXUS_NXM_EXPIRED", ex.Code);
    }

    [Fact]
    public async Task Api_key_is_sent_in_the_apikey_header_and_metadata_is_cached()
    {
        await using var server = new StubServer(_ => StubServer.Response.Json(ModJson));
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("key-abc", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Example", (await harness.Service.GetModAsync("amongus", 1, cancellationToken: TestContext.Current.CancellationToken)).Name);
        Assert.Equal("Example", (await harness.Service.GetModAsync("amongus", 1, cancellationToken: TestContext.Current.CancellationToken)).Name);
        Assert.Single(server.Requests);
        Assert.Equal("key-abc", server.Requests[0].Headers["apikey"]);
        Assert.False(server.Requests[0].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Oauth_sign_in_opens_the_browser_with_pkce_and_stores_tokens()
    {
        string? form = null;
        string? bearer = null;
        await using var server = new StubServer(request =>
        {
            switch (request.Path)
            {
                case "/oauth/token":
                    form = request.Body;
                    return StubServer.Response.Json("""{ "access_token": "access-1", "refresh_token": "refresh-1", "expires_in": 3600 }""");
                case "/v1/user/validate.json":
                    bearer = request.Headers.GetValueOrDefault("Authorization");
                    return StubServer.Response.Json("""{ "name": "OAuth User", "is_premium": true }""");
                default: return StubServer.Response.Json("{}", 404);
            }
        });
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var harness = Create(server, time);

        NexusKeyStatus status = await harness.Service.SignInAsync("test-client", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("OAuth User", status.User.Name);
        Assert.True(status.User.IsPremium);
        Assert.Single(harness.Opened);
        string authorize = Uri.UnescapeDataString(harness.Opened[0].Query);
        Assert.Contains("client_id=test-client", authorize);
        Assert.Contains("code_challenge_method=S256", authorize);
        Assert.Contains("redirect_uri=http://127.0.0.1:8642/callback", authorize);
        Assert.Contains("grant_type=authorization_code", form);
        Assert.Contains("code_verifier=", form);
        Assert.Contains("Bearer access-1", bearer);

        NexusOAuthTokens? tokens = await harness.Keys.ReadOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("access-1", tokens!.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal(time.GetUtcNow().AddSeconds(3600), tokens.ExpiresAt);
    }

    [Fact]
    public async Task Oauth_sign_in_with_a_mismatched_state_saves_nothing()
    {
        await using var server = new StubServer(_ => StubServer.Response.Json("{}", 404));
        await using var harness = Create(server, receive: (_, _, _) =>
            Task.FromResult(new NexusOAuthCallback("the-code", "not-the-state")));

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.SignInAsync("test-client", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("NEXUS_OAUTH_STATE", ex.Code);
        Assert.Null(await harness.Keys.ReadOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_and_used_as_bearer()
    {
        string? refreshForm = null;
        string? bearer = null;
        await using var server = new StubServer(request =>
        {
            switch (request.Path)
            {
                case "/oauth/token":
                    refreshForm = request.Body;
                    return StubServer.Response.Json("""{ "access_token": "access-2", "refresh_token": "refresh-2", "expires_in": 3600 }""");
                case "/v1/games/amongus/mods/1.json":
                    bearer = request.Headers.GetValueOrDefault("Authorization");
                    return StubServer.Response.Json(ModJson);
                default: return StubServer.Response.Json("{}", 404);
            }
        });
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var harness = Create(server, time);
        await harness.Keys.SaveOAuthTokensAsync(new NexusOAuthTokens("stale", "refresh-1", time.GetUtcNow().AddMinutes(-5)), cancellationToken: TestContext.Current.CancellationToken);

        await harness.Service.GetModAsync("amongus", 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("grant_type=refresh_token", refreshForm);
        Assert.Contains("refresh_token=refresh-1", refreshForm);
        Assert.Equal("Bearer access-2", bearer);
        NexusOAuthTokens? stored = await harness.Keys.ReadOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("access-2", stored!.AccessToken);
        Assert.Equal("refresh-2", stored.RefreshToken);
    }

    [Fact]
    public async Task Rejected_refresh_logs_out_and_reports_revoked_access()
    {
        await using var server = new StubServer(request => request.Path == "/oauth/token"
            ? StubServer.Response.Json("""{ "error": "invalid_grant" }""", 400)
            : StubServer.Response.Json(ModJson));
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var harness = Create(server, time);
        await harness.Keys.SaveOAuthTokensAsync(new NexusOAuthTokens("stale", "refresh-1", time.GetUtcNow().AddMinutes(-5)), cancellationToken: TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.GetModAsync("amongus", 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("NEXUS_OAUTH_REVOKED", ex.Code);
        Assert.Null(await harness.Keys.ReadOAuthTokensAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nxm_download_uses_the_oauth_bearer_when_signed_in()
    {
        byte[] payload = Encoding.UTF8.GetBytes("archive-bytes");
        string? baseUrl = null;
        string? authorization = null;
        bool sawApiKey = false;
        await using var server = new StubServer(request =>
        {
            if (request.Path.Contains("download_links", StringComparison.Ordinal))
            {
                authorization = request.Headers.GetValueOrDefault("Authorization");
                sawApiKey = request.Headers.ContainsKey("apikey");
                return StubServer.Response.Json($$"""[{ "name": "Oauth.zip", "URI": "{{baseUrl}}/cdn/Oauth.zip" }]""");
            }
            return request.Path == "/cdn/Oauth.zip"
                ? StubServer.Response.Bytes("application/octet-stream", payload)
                : StubServer.Response.Json("{}", 404);
        });
        baseUrl = server.BaseUrl;
        var time = new FakeTime(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        await using var harness = Create(server, time);
        await harness.Keys.SaveAsync("api-key", cancellationToken: TestContext.Current.CancellationToken);
        await harness.Keys.SaveOAuthTokensAsync(new NexusOAuthTokens("oauth-access", "r", time.GetUtcNow().AddHours(1)), cancellationToken: TestContext.Current.CancellationToken);

        NxmLink.TryParse("nxm://amongus/mods/1/files/2?key=k&expires=1700000000", out var link);
        await harness.Service.DownloadAsync(link!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bearer oauth-access", authorization);
        Assert.False(sawApiKey);
    }

    [Fact]
    public async Task Sign_out_removes_only_the_oauth_tokens()
    {
        await using var server = new StubServer(_ => StubServer.Response.Json("{}"));
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("fallback-key", cancellationToken: TestContext.Current.CancellationToken);
        await harness.Keys.SaveOAuthTokensAsync(new NexusOAuthTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await harness.Service.HasOAuthSessionAsync(cancellationToken: TestContext.Current.CancellationToken));

        await harness.Service.SignOutAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await harness.Service.HasOAuthSessionAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("fallback-key", await harness.Keys.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static NxmLink Link() =>
        NxmLink.TryParse("nxm://amongus/mods/1/files/2?key=k&expires=1700000000", out var link)
            ? link!
            : throw new InvalidOperationException();

    // PAK-129: the download address is untrusted API data. A private host is refused before
    // any request leaves this machine, even with the loopback test seam switched off.
    [Fact]
    public async Task Download_link_on_a_private_address_is_refused_before_the_request()
    {
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" =>
                StubServer.Response.Json($$"""[{ "name": "Evil.zip", "URI": "{{baseUrl}}/cdn/Evil.zip" }]"""),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server, allowLoopback: false);
        await harness.Keys.SaveAsync("api-key", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(Link(), TestContext.Current.CancellationToken));

        Assert.Equal("NEXUS_DOWNLOAD_BLOCKED", ex.Code);
        Assert.DoesNotContain(server.Requests, request => request.Path.EndsWith("Evil.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Download_link_on_a_host_outside_the_allowlist_is_refused()
    {
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" =>
                StubServer.Response.Json("""[{ "name": "Evil.zip", "URI": "https://evil.example.test/x.zip" }]"""),
            _ => StubServer.Response.Json("{}", 404)
        });
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(Link(), TestContext.Current.CancellationToken));

        Assert.Equal("NEXUS_DOWNLOAD_BLOCKED", ex.Code);
    }

    [Fact]
    public async Task Redirect_to_a_private_host_is_refused()
    {
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" =>
                StubServer.Response.Json($$"""[{ "name": "Redirect.zip", "URI": "{{baseUrl}}/cdn/Start.zip" }]"""),
            "/cdn/Start.zip" => new StubServer.Response(302, "text/plain", [],
                new Dictionary<string, string> { ["Location"] = "http://192.168.1.10/secret.zip" }),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(Link(), TestContext.Current.CancellationToken));

        Assert.Equal("NEXUS_DOWNLOAD_BLOCKED", ex.Code);
        Assert.Contains(server.Requests, request => request.Path == "/cdn/Start.zip");
        Assert.DoesNotContain(server.Requests, request => request.Path == "/secret.zip");
    }

    [Fact]
    public async Task Redirect_to_an_allowed_host_is_followed_and_the_file_is_saved()
    {
        byte[] payload = Encoding.UTF8.GetBytes("redirected-bytes");
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" =>
                StubServer.Response.Json($$"""[{ "name": "Redirect.zip", "URI": "{{baseUrl}}/cdn/Start.zip" }]"""),
            "/cdn/Start.zip" => new StubServer.Response(302, "text/plain", [],
                new Dictionary<string, string> { ["Location"] = "/cdn/Final.zip" }),
            "/cdn/Final.zip" => StubServer.Response.Bytes("application/octet-stream", payload),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server);
        await harness.Keys.SaveAsync("api-key", TestContext.Current.CancellationToken);

        NexusDownloadResult result = await harness.Service.DownloadAsync(Link(), TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path, TestContext.Current.CancellationToken));
    }

    // PAK-129: without Content-Length the copy must stop at the limit instead of filling the disk.
    [Fact]
    public async Task Stream_without_content_length_stops_at_the_limit_and_cleans_up()
    {
        byte[] payload = new byte[8192];
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" =>
                StubServer.Response.Json($$"""[{ "name": "Big.zip", "URI": "{{baseUrl}}/cdn/Big.zip" }]"""),
            "/cdn/Big.zip" => new StubServer.Response(200, "application/octet-stream", payload, OmitContentLength: true),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server, maxDownloadBytes: 1024);
        await harness.Keys.SaveAsync("api-key", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(Link(), TestContext.Current.CancellationToken));

        Assert.Equal("NEXUS_DOWNLOAD_TOO_LARGE", ex.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(harness.Root, "downloads", "nexus", "amongus")));
    }

    [Fact]
    public async Task Declared_content_length_over_the_limit_is_refused_before_writing()
    {
        string? baseUrl = null;
        await using var server = new StubServer(request => request.Path switch
        {
            "/v1/games/amongus/mods/1/files/2/download_links.json" =>
                StubServer.Response.Json($$"""[{ "name": "Big.zip", "URI": "{{baseUrl}}/cdn/Big.zip" }]"""),
            "/cdn/Big.zip" => StubServer.Response.Bytes("application/octet-stream", new byte[4096]),
            _ => StubServer.Response.Json("{}", 404)
        });
        baseUrl = server.BaseUrl;
        await using var harness = Create(server, maxDownloadBytes: 1024);
        await harness.Keys.SaveAsync("api-key", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => harness.Service.DownloadAsync(Link(), TestContext.Current.CancellationToken));

        Assert.Equal("NEXUS_DOWNLOAD_TOO_LARGE", ex.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(harness.Root, "downloads", "nexus", "amongus")));
    }

}
// Real API calls happen only when explicitly opted in; both variables stay in the
// environment and are never printed by the test.
public sealed class NexusLiveTests
{
    private sealed class StaticProvider(string key) : INexusCredentialProvider
    {
        public Task<NexusCredential?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusCredential?>(NexusCredential.ApiKey(key));
    }

    [Fact]
    public async Task Validate_a_live_api_key_when_gated()
    {
        if (Environment.GetEnvironmentVariable("PHAKET_NEXUS_LIVE") != "1")
        {
            Assert.Skip("set PHAKET_NEXUS_LIVE=1 (and PHAKET_NEXUS_API_KEY) to call the real Nexus API");
            return;
        }
        string? key = Environment.GetEnvironmentVariable("PHAKET_NEXUS_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip("PHAKET_NEXUS_API_KEY is not set");
            return;
        }
        using var http = new HttpClient { BaseAddress = new Uri("https://api.nexusmods.com"), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Phaket/0.1 (Nexus integration; testing)");
        var client = new NexusApiClient(http, new StaticProvider(key));
        NexusUser user = await client.ValidateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(user.Name));
    }
}
