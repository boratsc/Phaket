using System.Net;
using System.Text;
using Phaket.Core.Accounts;

public sealed class ItchAccountsTests
{
    [Fact]
    public async Task LoginReloadAndLogout()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-itch-").FullName;
        try
        {
            string path = Path.Combine(dir, "itch.bin");
            var handler = new FakeHandler();
            var service = new ItchAccountService("client", path, "http://127.0.0.1:12345/callback", handler, Browser(handler, false));
            int changes = 0;
            service.AccountChanged += () => changes++;
            var account = await service.LoginAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new AccountInfo("itch", "42", "Display", "https://example.test/avatar", account.ObtainedAt), account);
            Assert.Equal(account, service.Current);
            Assert.Equal(1, changes);
            Assert.DoesNotContain("secret-token", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var reloaded = new ItchAccountService("client", path, null, handler, _ => throw new Exception());
            Assert.Equal(account, reloaded.Current);
            Assert.Equal(account, await reloaded.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.Exchanges);
            await service.LogoutAsync(TestContext.Current.CancellationToken);
            Assert.Null(service.Current);
            Assert.False(File.Exists(path));
            Assert.Equal(1, handler.Revocations);
            Assert.Equal(2, changes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WrongStateIsRejected()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-itch-").FullName;
        try
        {
            var handler = new FakeHandler();
            var service = new ItchAccountService("client", Path.Combine(dir, "itch.bin"), null, handler, Browser(handler, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Null(service.Current);
            Assert.Equal(0, handler.Exchanges);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MissingClientIdHasBilingualError()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-itch-").FullName;
        try
        {
            var service = new ItchAccountService(null, Path.Combine(dir, "itch.bin"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Contains("Zarejestruj", error.Message);
            Assert.Contains("Register", error.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("https://127.0.0.1/callback")]
    [InlineData("http://localhost/callback")]
    [InlineData("http://127.0.0.1/callback?x=1")]
    [InlineData("http://127.0.0.1/callback#x")]
    public void InvalidRedirectIsRejected(string redirect) => Assert.Throws<ArgumentException>(() => new ItchAccountService("client", redirectUri: redirect));

    private static Dictionary<string, string> Parse(string text) => text.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));

    private static Action<string> Browser(FakeHandler handler, bool wrong) => url =>
    {
        var uri = new Uri(url);
        Assert.Equal("https://itch.io/user/oauth", uri.GetLeftPart(UriPartial.Path));
        var query = Parse(uri.Query);
        Assert.Equal("client", query["client_id"]);
        Assert.Equal("profile:me profile:owned", query["scope"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        handler.Challenge = query["code_challenge"];
        handler.Redirect = query["redirect_uri"];
        Assert.True(new Uri(handler.Redirect).Port > 0);
        Assert.Equal("/callback", new Uri(handler.Redirect).AbsolutePath);
        _ = Task.Run(async () =>
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(handler.Redirect + "?code=abc&state=" + (wrong ? "wrong" : query["state"]));
        });
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? Challenge, Redirect;
        public int Exchanges, Revocations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.itch.io", request.RequestUri!.Host);
            string result;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/oauth/token":
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Null(request.Headers.Authorization);
                    var token = Parse(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal(5, token.Count);
                    Assert.Equal("authorization_code", token["grant_type"]);
                    Assert.Equal("abc", token["code"]);
                    Assert.Equal("client", token["client_id"]);
                    Assert.Equal(Redirect, token["redirect_uri"]);
                    Assert.Equal(Challenge, AccountPkce.Challenge(token["code_verifier"]));
                    Exchanges++;
                    result = "{\"access_token\":\"secret-token\",\"token_type\":\"bearer\",\"scope\":\"profile:me profile:owned\"}";
                    break;
                case "/profile":
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
                    result = "{\"user\":{\"id\":42,\"username\":\"test\",\"display_name\":\"Display\",\"avatar_url\":\"https://example.test/avatar\"}}";
                    break;
                case "/oauth/revoke":
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Null(request.Headers.Authorization);
                    var revoke = Parse(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal(2, revoke.Count);
                    Assert.Equal("secret-token", revoke["token"]);
                    Assert.Equal("client", revoke["client_id"]);
                    Revocations++;
                    result = "{}";
                    break;
                default: throw new InvalidOperationException("Unexpected request: " + request.RequestUri);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
