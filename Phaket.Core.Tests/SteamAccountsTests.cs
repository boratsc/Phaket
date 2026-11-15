using System.Net;
using System.Net.Sockets;
using System.Text;
using Phaket.Core.Accounts;
using Phaket.Core.Tests;

public sealed class SteamAccountsTests
{
    private readonly InMemorySecretStore memory = new();
    private const string Id = "76561198000000001";

    [Fact]
    public async Task LoginReloadAndLogout()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            string path = Path.Combine(dir, "steam.bin"), redirect = Redirect();
            var handler = new FakeHandler();
            var service = new SteamAccountService(path, redirect, handler, Browser(), memory);
            int changes = 0;
            service.AccountChanged += () => changes++;
            var account = await service.LoginAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new AccountInfo("steam", Id, "Player", null, account.ObtainedAt), account);
            Assert.Equal(1, changes);
            Assert.False(File.Exists(path));
            var reload = new SteamAccountService(path, redirect, handler, _ => throw new Exception("Unexpected browser"), memory);
            Assert.Equal(account, await reload.LoginAsync(TestContext.Current.CancellationToken));
            await service.LogoutAsync(TestContext.Current.CancellationToken);
            Assert.Null(service.Current);
            Assert.False(File.Exists(path));
            Assert.Equal(2, changes);
            Assert.Null(new SteamAccountService(path, redirect, handler, Browser(), memory).Current);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RejectsInvalidAssertions(bool valid, bool foreign)
    {
        string dir = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            var handler = new FakeHandler { Valid = valid };
            var service = new SteamAccountService(Path.Combine(dir, "steam.bin"), Redirect(), handler, Browser(foreign), memory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Null(service.Current);
            Assert.Equal(foreign ? 0 : 1, handler.Checks);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ProfileFailureFallsBackToId()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            var service = new SteamAccountService(Path.Combine(dir, "steam.bin"), Redirect(), new FakeHandler { FailProfile = true }, Browser(), memory);
            Assert.Equal(Id, (await service.LoginAsync(TestContext.Current.CancellationToken)).Username);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DetectsLatestLocalUser()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            string path = Path.Combine(dir, "loginusers.vdf");
            Assert.Null(SteamAccountService.DetectLocal(path));
            await File.WriteAllTextAsync(path, "\"users\" { \"76561198000000002\" { \"PersonaName\" \"Older\" \"Timestamp\" \"12\" } \"76561198000000001\" { \"PersonaName\" \"Latest\" \"Timestamp\" \"100\" } }", TestContext.Current.CancellationToken);
            var account = SteamAccountService.DetectLocal(path);
            Assert.NotNull(account);
            Assert.Equal(Id, account.UserId);
            Assert.Equal("Latest", account.Username);
            Assert.Equal("steam", account.Provider);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string Redirect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/steam/callback";
    }

    private static Dictionary<string, string> Parse(string query) => query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static Action<string> Browser(bool foreign = false) => url =>
    {
        var query = Parse(new Uri(url).Query);
        string returnTo = query["openid.return_to"];
        var fields = new Dictionary<string, string> {
            ["openid.ns"] = "http://specs.openid.net/auth/2.0", ["openid.mode"] = "id_res",
            ["openid.claimed_id"] = $"https://{(foreign ? "evil.example" : "steamcommunity.com")}/openid/id/{Id}",
            ["openid.return_to"] = returnTo, ["openid.signed"] = "claimed_id,return_to", ["openid.sig"] = "signature" };
        _ = Task.Run(async () =>
        {
            using var content = new FormUrlEncodedContent(fields);
            using var client = new HttpClient();
            using var response = await client.GetAsync(returnTo + "&" + await content.ReadAsStringAsync());
        });
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public bool Valid = true, FailProfile;
        public int Checks;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("steamcommunity.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Post)
            {
                Checks++;
                Assert.Equal("/openid/login", request.RequestUri.AbsolutePath);
                var fields = Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("check_authentication", fields["openid.mode"]);
                Assert.Equal($"https://steamcommunity.com/openid/id/{Id}", fields["openid.claimed_id"]);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("is_valid:" + (Valid ? "true" : "false") + "\n") };
            }
            Assert.Equal($"/profiles/{Id}?xml=1", request.RequestUri.PathAndQuery);
            if (FailProfile) throw new HttpRequestException("Offline");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<profile><steamID><![CDATA[Player]]></steamID></profile>", Encoding.UTF8, "application/xml") };
        }
    }
}

public class SteamAdoptTests
{
    private readonly InMemorySecretStore memory = new();
    [Fact]
    public async Task AdoptLocalSavesDetectedAccount()
    {
        string dir = Directory.CreateTempSubdirectory("phaket-steam-adopt-").FullName;
        try
        {
            string vdf = Path.Combine(dir, "loginusers.vdf");
            File.WriteAllText(vdf, "\"users\"\n{\n\t\"76561198000000000\"\n\t{\n\t\t\"PersonaName\"\t\"TestUser\"\n\t\t\"Timestamp\"\t\"1700000000\"\n\t}\n}\n");
            var service = new SteamAccountService(Path.Combine(dir, "steam.bin"), "http://127.0.0.1:53125/steam/callback", null, _ => { }, memory);
            var account = await service.AdoptLocalAsync(vdf, TestContext.Current.CancellationToken);
            Assert.NotNull(account);
            Assert.Equal("76561198000000000", account!.UserId);
            Assert.Equal("TestUser", account.Username);
            Assert.Equal(account, service.Current);
            var reloaded = new SteamAccountService(Path.Combine(dir, "steam.bin"), "http://127.0.0.1:53125/steam/callback", null, _ => { }, memory);
            Assert.Equal(account.UserId, reloaded.Current?.UserId);
        }
        finally { Directory.Delete(dir, true); }
    }
}
