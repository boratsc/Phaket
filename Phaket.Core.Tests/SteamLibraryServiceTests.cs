using System.Net;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public class SteamLibraryServiceTests
{
    private static readonly GameDefinition[] Definitions = [new("among-us", [], new Dictionary<string, string> { ["steam"] = "945360" }), new("valheim", [], new Dictionary<string, string> { ["steam"] = "892970" })];
    private static SteamLibraryService Service(Func<HttpRequestMessage, HttpResponseMessage> response, string? key = "secret", bool xml = false) =>
        new(() => Task.FromResult(key), "76561198000000000", new HttpClient(new Handler(response)), Definitions, xml);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact] public async Task MapsApiGamesAndRequestsFreeGames()
    {
        var result = await Service(r => { Assert.Contains("include_played_free_games=1", r.RequestUri!.Query); return Ok("{\"response\":{\"games\":[{\"appid\":945360,\"name\":\"Among Us\",\"playtime_forever\":42},{\"appid\":892970,\"name\":\"Valheim\"}]}}"); }).GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("among-us", result[0].Definition!.Slug); Assert.Equal(42, result[0].PlaytimeMinutes); Assert.Equal("valheim", result[1].Definition!.Slug);
    }
    [Fact] public async Task MissingKeyIsExplicit()
    {
        var ex = await Assert.ThrowsAsync<PhaketException>(() => Service(_ => throw new Exception(), null).GetOwnedGamesAsync(TestContext.Current.CancellationToken)); Assert.Equal("STEAM_API_KEY_MISSING", ex.Error.Code);
    }
    [Fact] public async Task ForbiddenAndNetworkAreMapped()
    {
        var forbidden = await Assert.ThrowsAsync<PhaketException>(() => Service(_ => new(HttpStatusCode.Forbidden)).GetOwnedGamesAsync(TestContext.Current.CancellationToken)); Assert.Equal("STEAM_API_KEY_INVALID", forbidden.Error.Code);
        var network = await Assert.ThrowsAsync<PhaketException>(() => Service(_ => throw new HttpRequestException()).GetOwnedGamesAsync(TestContext.Current.CancellationToken)); Assert.Equal("STEAM_LIBRARY_UNAVAILABLE", network.Error.Code);
    }
    [Fact] public async Task PublicXmlIsExplicitlyLowerConfidence()
    {
        var games = await Service(_ => Ok("<gamesList><games><game><appID>945360</appID><name>Among Us</name></game></games></gamesList>"), null, true).GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        var game = Assert.Single(games); Assert.Equal("community-xml", game.Source); Assert.True(game.LowerConfidence); Assert.Equal("among-us", game.Definition!.Slug);
    }
    [Fact] public async Task PrivateXmlIsReported()
    {
        var ex = await Assert.ThrowsAsync<PhaketException>(() => Service(_ => Ok("<gamesList><error>private</error></gamesList>"), null, true).GetOwnedGamesAsync(TestContext.Current.CancellationToken)); Assert.Equal("STEAM_LIBRARY_PRIVATE", ex.Error.Code);
    }
}
