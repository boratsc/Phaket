using System.Net;
using System.Net.Sockets;
using System.Text;
using Phaket.Core.Accounts;

public sealed class AccountsTests
{
    [Fact]
    public void PkceMatchesRfc7636() => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", DiscordPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void ProtectorRoundTrip()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-account-").FullName;
        try
        {
            var protector = new TokenProtector(Path.Combine(dir, "account.bin"));
            byte[] plain = Encoding.UTF8.GetBytes("test secret");
            byte[] encrypted = protector.Protect(plain);
            Assert.NotEqual(plain, encrypted);
            Assert.Equal(plain, protector.Unprotect(encrypted));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "key.bin")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LoginReloadRefreshAndLogout()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-account-").FullName;
        try
        {
            string store = Path.Combine(dir, "account.bin");
            string redirect = Redirect();
            var handler = new FakeHandler();
            var service = new DiscordAccountService("https://config.example/", store, redirect, handler, Browser(redirect, false));
            int changes = 0;
            service.AccountChanged += () => changes++;
            var account = await service.LoginAsync(TestContext.Current.CancellationToken);
            Assert.Equal("42", account.UserId);
            Assert.Equal(account, service.Current);
            Assert.Equal(1, changes);
            Assert.DoesNotContain("access-secret", await File.ReadAllTextAsync(store, TestContext.Current.CancellationToken));
            var reloaded = new DiscordAccountService("https://config.example/", store, redirect, handler, Browser(redirect, false));
            Assert.Equal(account, reloaded.Current);
            await reloaded.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, handler.Refreshes);
            await service.LogoutAsync(TestContext.Current.CancellationToken);
            Assert.Null(service.Current);
            Assert.False(File.Exists(store));
            Assert.Equal(1, handler.Revocations);
            Assert.Equal(2, changes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WrongStateIsRejected()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-account-").FullName;
        try
        {
            string redirect = Redirect();
            var handler = new FakeHandler();
            var service = new DiscordAccountService("https://config.example/", Path.Combine(dir, "account.bin"), redirect, handler, Browser(redirect, true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Null(service.Current);
            Assert.Equal(0, handler.Exchanges);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string Redirect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/callback";
    }

    private static Action<string> Browser(string redirect, bool wrong) => url =>
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(redirect + "?code=abc&state=" + (wrong ? "wrong" : query["state"]));
            }
            catch (HttpRequestException) { }
        });
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Exchanges, Refreshes, Revocations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string result;
            if (request.RequestUri!.Host == "config.example") result = "{\"discord_client_id\":\"1234\"}";
            else if (request.RequestUri.AbsolutePath == "/api/users/@me")
            {
                Assert.Equal("access-secret", request.Headers.Authorization?.Parameter);
                result = "{\"id\":\"42\",\"username\":\"test\",\"global_name\":\"Test\"}";
            }
            else
            {
                string form = await request.Content!.ReadAsStringAsync(ct);
                Assert.DoesNotContain("client_secret", form);
                if (request.RequestUri.AbsolutePath.EndsWith("/revoke"))
                {
                    Assert.Contains("token=refresh-secret", form);
                    Revocations++;
                    result = "{}";
                }
                else
                {
                    if (form.Contains("grant_type=refresh_token")) Refreshes++; else { Exchanges++; Assert.Contains("code_verifier=", form); }
                    result = "{\"access_token\":\"access-secret\",\"refresh_token\":\"refresh-secret\",\"expires_in\":3600}";
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
