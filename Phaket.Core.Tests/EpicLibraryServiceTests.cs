using System.Net;
using System.Text;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public sealed class EpicLibraryServiceTests
{
    [Fact]
    public async Task EmptyAppNamesAreNotReturnedEvenWhenAssetsMarkThemInstallable()
    {
        var service = Service(new Handler(request => Ok(request.RequestUri!.Host.StartsWith("library-", StringComparison.Ordinal)
            ? "{\"records\":[{\"catalogItemId\":\"one\"},{\"appName\":\"  \",\"catalogItemId\":\"two\"}],\"responseMetadata\":{}}"
            : "[{\"catalogItemId\":\"one\"},{\"appName\":\"  \",\"catalogItemId\":\"two\"}]")));
        Assert.Empty(await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken));
    }

    private static readonly string[] Empty = [];
    private static readonly IReadOnlyList<GameDefinition> Definitions =
    [
        new GameDefinition("among-us", Empty, new Dictionary<string, string> { ["epic"] = "963137e4c29d4c79a81323b8fab03a40" }, Title: new Dictionary<string, string> { ["en"] = "Among Us" })
    ];

    [Fact]
    public async Task PagesAndMergesSourcesTitlesAndInstallability()
    {
        var urls = new List<string>();
        var handler = new Handler(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            string body = request.RequestUri.Host.StartsWith("library-", StringComparison.Ordinal)
                ? request.RequestUri.Query.Contains("cursor=")
                    ? "{\"records\":[{\"appName\":\"fallback\",\"catalogItemId\":\"second\"}],\"responseMetadata\":{}}"
                    : "{\"records\":[{\"appName\":\"963137e4c29d4c79a81323b8fab03a40\",\"catalogItemId\":\"729a86a5146640a2ace9e8c595414c56\",\"metadata\":{\"title\":\"metadata title\"}}],\"responseMetadata\":{\"nextCursor\":\"next\"}}"
                : request.RequestUri.Host.StartsWith("launcher-", StringComparison.Ordinal)
                    ? "[{\"appName\":\"963137e4c29d4c79a81323b8fab03a40\",\"catalogItemId\":\"729a86a5146640a2ace9e8c595414c56\"}]"
                    : "{\"records\":[{\"appName\":\"fallback\",\"catalogItemId\":\"second\",\"metadata\":{\"title\":\"entitled title\"}}]}";
            return Ok(body);
        });
        var games = await Service(handler).GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        var among = Assert.Single(games, g => g.AppName.StartsWith("963"));
        Assert.Equal("metadata title", among.Title);
        Assert.True(among.Installable);
        Assert.Contains("assets", among.Source);
        Assert.Equal(2, urls.Count(u => u.Contains("library/api")));
        Assert.Contains(games, g => g.CatalogItemId == "second" && g.Source.Contains("entitlements"));
    }

    [Fact]
    public async Task UsesDefinitionTitleFallbackForAmongUsAndAppNameForUnknown()
    {
        var service = Service(new Handler(request => Ok(request.RequestUri!.Host.StartsWith("library-", StringComparison.Ordinal)
            ? "{\"records\":[{\"appName\":\"963137e4c29d4c79a81323b8fab03a40\",\"catalogItemId\":\"729a86a5146640a2ace9e8c595414c56\"},{\"appName\":\"Fallback Name\"}],\"responseMetadata\":{}}"
            : "[]")));
        var games = await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        Assert.Contains(games, g => g.Title == "Among Us");
        Assert.Contains(games, g => g.Title == "Fallback Name");
    }

    [Fact]
    public async Task MapsUnauthorizedAndNetworkErrors()
    {
        var unauthorized = await Assert.ThrowsAsync<PhaketException>(() => Service(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))).GetOwnedGamesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("EPIC_TOKEN_INVALID", unauthorized.Error.Code);
        var unavailable = await Assert.ThrowsAsync<PhaketException>(() => Service(new Handler(_ => throw new HttpRequestException())).GetOwnedGamesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("EPIC_LIBRARY_UNAVAILABLE", unavailable.Error.Code);
    }

    [Fact]
    public async Task OptionalAssetsFailureRetainsLibraryAndEmptyLibraryIsValid()
    {
        var service = Service(new Handler(request => request.RequestUri!.Host.StartsWith("library-", StringComparison.Ordinal)
            ? Ok("{\"records\":[{\"appName\":\"keep\"}],\"responseMetadata\":{}}")
            : throw new HttpRequestException()));
        Assert.Single(await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken));
        var empty = Service(new Handler(_ => Ok("{\"records\":[],\"responseMetadata\":{}}")));
        Assert.Empty(await empty.GetOwnedGamesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopsCursorLoop()
    {
        int pages = 0;
        var service = Service(new Handler(request =>
        {
            if (request.RequestUri!.Host.StartsWith("library-", StringComparison.Ordinal))
            {
                pages++;
                return Ok("{\"records\":[],\"responseMetadata\":{\"nextCursor\":\"same\"}}");
            }
            return Ok("[]");
        }));
        await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, pages);
    }

    private static EpicLibraryService Service(HttpMessageHandler handler) => new(() => Task.FromResult("secret"), new HttpClient(handler), "account", Definitions);
    private static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
