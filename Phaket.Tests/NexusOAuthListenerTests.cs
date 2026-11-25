using System.Net;
using System.Net.Http;
using Phaket.Core.Nexus;

namespace Phaket.Tests;

// The loopback OAuth receiver is exercised against a real HttpListener on
// 127.0.0.1:8642, but the test skips when another process holds the port.
public sealed class NexusOAuthListenerTests
{
    private static async Task<HttpResponseMessage> SendWhenReadyAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (int attempt = 0; ; attempt++)
        {
            try { return await http.GetAsync(url); }
            catch (HttpRequestException) when (attempt < 60) { await Task.Delay(50); }
        }
    }

    [Fact]
    public async Task Callback_code_is_returned_when_state_matches()
    {
        Task<NexusOAuthCallback> waiting;
        try { waiting = NexusOAuthCallbackListener.WaitAsync("state-ok", TimeSpan.FromSeconds(10)); }
        catch (NexusApiException unavailable) when (unavailable.Code == "NEXUS_OAUTH_UNAVAILABLE")
        {
            Assert.Skip("127.0.0.1:8642 is not available in this environment");
            return;
        }

        using var response = await SendWhenReadyAsync("http://127.0.0.1:8642/callback?code=the-code&state=state-ok");
        NexusOAuthCallback callback = await waiting;
        Assert.Equal("the-code", callback.Code);
        Assert.Equal("state-ok", callback.State);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Mismatched_state_is_rejected()
    {
        Task<NexusOAuthCallback> waiting;
        try { waiting = NexusOAuthCallbackListener.WaitAsync("state-ok", TimeSpan.FromSeconds(10)); }
        catch (NexusApiException unavailable) when (unavailable.Code == "NEXUS_OAUTH_UNAVAILABLE")
        {
            Assert.Skip("127.0.0.1:8642 is not available in this environment");
            return;
        }

        using var response = await SendWhenReadyAsync("http://127.0.0.1:8642/callback?code=the-code&state=evil");
        var ex = await Assert.ThrowsAsync<NexusApiException>(() => waiting);
        Assert.Equal("NEXUS_OAUTH_STATE", ex.Code);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Waiting_past_the_timeout_reports_a_timeout()
    {
        var ex = await Assert.ThrowsAsync<NexusApiException>(() =>
            NexusOAuthCallbackListener.WaitAsync("state-ok", TimeSpan.FromMilliseconds(200)));
        Assert.True(ex.Code is "NEXUS_OAUTH_TIMEOUT" or "NEXUS_OAUTH_UNAVAILABLE", ex.Code);
    }
}