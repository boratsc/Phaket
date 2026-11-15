using System.Net;
using System.Text;
using System.Text.Json;
using Phaket.Core.Accounts;

namespace Phaket.Core.Tests;

public sealed class EpicAccountsTests
{
    private const string Code = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Code)]
    [InlineData("{\"authorizationCode\":\"" + Code + "\"}")]
    [InlineData("\"" + Code + "\"")]
    [InlineData("https://localhost/launcher/authorized?x=1&code=" + Code + "&other=2")]
    public void ExtractsCode(string input) => Assert.Equal(Code, EpicAccountService.ExtractAuthorizationCode(input));

    [Theory]
    [InlineData("garbage")]
    [InlineData("0123456789abcdef")]
    [InlineData("ż" + Code)]
    [InlineData("https://localhost/launcher/authorized?state=x")]
    [InlineData("{\"authorizationCode\":123}")]
    [InlineData("https://localhost/?code=0123456789abcdef0123456789abcdef&code=0123456789abcdef0123456789abcdef")]
    public void RejectsInvalidCode(string input) => Assert.Throws<InvalidOperationException>(() => EpicAccountService.ExtractAuthorizationCode(input));

    [Fact]
    public async Task LoginRefreshAndLogout()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phaket-epic-test-" + Guid.NewGuid());
        string path = Path.Combine(dir, "epic.bin");
        var memory = new InMemorySecretStore();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var requests = new List<(string Method, string Url, string? Auth, string Body)>();
            var handler = new FakeHandler(async request =>
            {
                string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                string result = request.Method == HttpMethod.Get ? "{\"account_id\":\"id1\",\"display_name\":\"Player\"}" : "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
            });
            string? browser = null;
            var service = new EpicAccountService(path, handler, url => browser = url, memory);
            int changes = 0;
            service.AccountChanged += () => changes++;
            Task<AccountInfo> login = service.LoginAsync(ct);
            Assert.StartsWith("https://www.epicgames.com/id/login?redirectUrl=", browser);
            service.SubmitCode(Code);
            var account = await login;
            Assert.Equal("epic", account.Provider);
            Assert.Equal("id1", account.UserId);
            Assert.Equal("Player", account.Username);
            Assert.Equal(1, changes);
            Assert.Equal("POST", requests[0].Method);
            Assert.EndsWith("/oauth/token", requests[0].Url);
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("34a02cf8f4414e29b15921876da36f9a:daafbccc737745039dffe53d94fc76cf")), requests[0].Auth);
            Assert.Contains("grant_type=authorization_code", requests[0].Body);
            Assert.Contains("code=" + Code, requests[0].Body);
            Assert.Contains("token_type=eg1", requests[0].Body);
            Assert.EndsWith("/oauth/verify", requests[1].Url);
            Assert.Equal("Bearer access", requests[1].Auth);

            var encrypted = new EncryptedStore(path, memory);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(encrypted.Load()!)!;
            var updated = data.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            updated["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-1);
            await encrypted.SaveAsync(JsonSerializer.SerializeToUtf8Bytes(updated), ct);
            var restored = new EpicAccountService(path, handler, _ => throw new Exception("Unexpected browser"), memory);
            Assert.Equal("Player", restored.Current!.Username);
            await restored.LoginAsync(ct);
            Assert.Contains("grant_type=refresh_token", requests[2].Body);
            Assert.Contains("refresh_token=refresh", requests[2].Body);
            await restored.LogoutAsync(ct);
            Assert.Equal("DELETE", requests[3].Method);
            Assert.EndsWith("/oauth/sessions/kill/access", requests[3].Url);
            Assert.Equal("Bearer access", requests[3].Auth);
            Assert.Null(restored.Current);
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task VerifyRequiresSnakeCaseProfile()
    {
        string path = Path.Combine(Path.GetTempPath(), "phaket-epic-" + Guid.NewGuid(), "epic.bin");
        try
        {
            var handler = new FakeHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get
                    ? "{\"AccountId\":\"id1\",\"DisplayName\":\"Player\"}"
                    : "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
            }));
            var service = new EpicAccountService(path, handler, _ => { }, new InMemorySecretStore());
            Task<AccountInfo> login = service.LoginAsync(TestContext.Current.CancellationToken);
            service.SubmitCode(Code);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => login);
            Assert.Contains("Nieprawidłowy profil Epic", error.Message);
        }
        finally
        {
            string dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CancellationStopsWaiting()
    {
        string path = Path.Combine(Path.GetTempPath(), "phaket-epic-" + Guid.NewGuid(), "epic.bin");
        var service = new EpicAccountService(path, new FakeHandler(_ => throw new Exception("Unexpected HTTP")), _ => { }, new InMemorySecretStore());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var login = service.LoginAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await login);
        Assert.Throws<InvalidOperationException>(() => service.SubmitCode(Code));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
