using Phaket.Core.Catalog;

namespace Phaket.Core.Tests;

public class ModCatalogClientTests
{
    private sealed class FakeTransport : ICatalogTransport
    {
        public Queue<CatalogResponse> Responses { get; } = new();
        public List<(string Path,string? ETag)> Requests { get; } = [];
        public Task<CatalogResponse> GetAsync(string path,string? ifNoneMatch,CancellationToken cancellationToken)
        { Requests.Add((path,ifNoneMatch)); return Task.FromResult(Responses.Dequeue()); }
    }
    private static string Mod(string id,string game="among-us") => System.Text.Json.JsonSerializer.Serialize(new ThunderstoreMod(id,game,"ns",id,$"ns-{id}","","owner","https://example.test",null,false,DateTimeOffset.Parse("2026-01-01T00:00:00Z"),new ThunderstoreVersion("1.0.0",$"ns-{id}-1.0.0","https://example.test/mod",1,DateTimeOffset.Parse("2026-01-01T00:00:00Z"),[])));
    private static string Page(string mod,string? cursor=null,long revision=1) => "{\"revision\":"+revision+",\"mods\":["+mod+"],\"nextCursor\":"+(cursor is null?"null":System.Text.Json.JsonSerializer.Serialize(cursor))+"}";
    private static (FakeTransport T,ModCatalogStore S,ModCatalogClient C,string Dir) Setup()
    { var dir=Directory.CreateTempSubdirectory("mod-catalog-").FullName; var t=new FakeTransport(); var s=new ModCatalogStore(Path.Combine(dir,"db.sqlite")); return(t,s,new(t,s),dir); }
    private static void Clean(ModCatalogStore s,string d) { s.Dispose(); Directory.Delete(d,true); }

    [Fact] public async Task SnapshotPaginatesAndRetainsOtherGames()
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,"e1",Page(Mod("a"),"next"))); t.Responses.Enqueue(new(200,"e2",Page(Mod("b"),null,2))); await c.SyncAsync("among-us"); Assert.Equal(2,s.GetMods("among-us").Count); Assert.Null(t.Requests[1].ETag); t.Responses.Enqueue(new(200,null,Page(Mod("v","valheim")))); await c.SyncAsync("valheim"); Assert.Equal(2,s.GetMods("among-us").Count); Assert.Single(s.GetMods("valheim")); } finally { Clean(s,d); } }
    [Fact] public async Task NotModifiedMarksSyncAndDeltaErrorsAreNotSilent()
    { var(t,s,c,d)=Setup(); try { var before=DateTimeOffset.UtcNow; t.Responses.Enqueue(new(200,"e",Page(Mod("a")))); await c.SyncAsync("among-us"); t.Responses.Enqueue(new(304,null,null)); await Assert.ThrowsAsync<HttpRequestException>(()=>c.SyncAsync("among-us")); Assert.True(s.LastSync>=before); t.Responses.Enqueue(new(500,null,null)); await Assert.ThrowsAsync<HttpRequestException>(()=>c.SyncAsync("among-us")); } finally { Clean(s,d); } }
    [Fact] public async Task DeltaAppliesUpsertsAndTombstones()
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,null,Page(Mod("a")))); await c.SyncAsync("among-us"); t.Responses.Enqueue(new(200,null,$$"""{"reset":false,"revision":2,"changes":[{"op":"upsert","mod":{{Mod("b")}}},{"op":"tombstone","id":"a"}]}""")); await c.SyncAsync("among-us"); Assert.Equal("b",Assert.Single(s.GetMods("among-us")).Id); } finally { Clean(s,d); } }
    [Theory] [InlineData(410)] [InlineData(200)] public async Task ContinuityLossOrResetUsesSnapshot(int status)
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,null,Page(Mod("a")))); await c.SyncAsync("among-us"); Assert.Single(s.GetMods("among-us")); } finally { Clean(s,d); } }
    [Fact] public async Task NullDeltaBodyFailsAndUnknownGameIsRejected()
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,null,null)); await Assert.ThrowsAsync<HttpRequestException>(()=>c.SyncAsync("among-us")); await Assert.ThrowsAsync<ArgumentException>(()=>c.SyncAsync("unknown")); } finally { Clean(s,d); } }
    [Fact] public async Task DetailsAreCachedOfflineAnd404HasCode()
    { var(t,s,c,d)=Setup(); try { var json=$$"""{"mod":{{Mod("a")}},"versions":[{"versionNumber":"1.0.0","fullName":"ns-a-1.0.0","downloadUrl":"https://example.test/mod","fileSize":1,"dateCreated":"2026-01-01T00:00:00Z","dependencies":[],"isActive":true}]}"""; t.Responses.Enqueue(new(200,null,json)); Assert.Single((await c.GetModAsync("a")).Versions); t.Responses.Enqueue(new(503,null,null)); Assert.NotNull(await c.GetModAsync("a")); t.Responses.Enqueue(new(404,null,null)); var error=await Assert.ThrowsAsync<HttpRequestException>(()=>c.GetModAsync("missing")); Assert.Equal("MOD_NOT_FOUND",error.Message); t.Responses.Enqueue(new(503,null,null)); var absent=await Assert.ThrowsAsync<HttpRequestException>(()=>c.GetModAsync("absent")); Assert.Equal("MOD_DETAILS_UNAVAILABLE",absent.Message); } finally { Clean(s,d); } }
    private static string CamelMod(string id,string game="among-us") => System.Text.Json.JsonSerializer.Serialize(new { id, gameId=game, @namespace="ns", name=id, fullName=$"ns-{id}", description="", owner="owner", packageUrl="https://example.test", iconUrl=(string?)null, isDeprecated=false, dateUpdated="2026-01-01T00:00:00Z", latest=new { versionNumber="1.0.0", fullName=$"ns-{id}-1.0.0", downloadUrl="https://example.test/mod", fileSize=1, dateCreated="2026-01-01T00:00:00Z", dependencies=Array.Empty<string>() } });

    [Fact] public async Task CamelCaseSnapshotAndDetailsDeserialize()
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,"au",Page(CamelMod("a")))); await c.SyncAsync("among-us"); Assert.Equal("a",Assert.Single(s.GetMods("among-us")).Id); var details="{\"mod\":"+CamelMod("a")+",\"versions\":[{\"versionNumber\":\"1.0.0\",\"fullName\":\"ns-a-1.0.0\",\"downloadUrl\":\"https://example.test/mod\",\"fileSize\":1,\"dateCreated\":\"2026-01-01T00:00:00Z\",\"dependencies\":[]}]}"; t.Responses.Enqueue(new(200,null,details)); Assert.Equal("1.0.0",(await c.GetModAsync("a")).Versions[0].VersionNumber); } finally { Clean(s,d); } }

    [Fact] public async Task EachGameGetsSnapshotAndUsesItsOwnEtag()
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,"au",Page(CamelMod("a")))); await c.SyncAsync("among-us"); t.Responses.Enqueue(new(200,null,"{\"reset\":false,\"revision\":1,\"changes\":[]}")); await c.SyncAsync("among-us"); t.Responses.Enqueue(new(200,"vh",Page(CamelMod("v","valheim"),revision:2))); await c.SyncAsync("valheim"); Assert.Single(s.GetMods("among-us")); Assert.Single(s.GetMods("valheim")); Assert.Null(t.Requests[2].ETag); } finally { Clean(s,d); } }

    [Fact] public async Task GlobalTombstoneRemovesModAcrossGames()
    { var(t,s,c,d)=Setup(); try { t.Responses.Enqueue(new(200,"au",Page(CamelMod("a")))); await c.SyncAsync("among-us"); t.Responses.Enqueue(new(200,"vh",Page(CamelMod("v","valheim"),revision:2))); await c.SyncAsync("valheim"); t.Responses.Enqueue(new(200,null,"{\"reset\":false,\"revision\":3,\"changes\":[{\"op\":\"tombstone\",\"id\":\"a\"}]}")); await c.SyncAsync("valheim"); Assert.Empty(s.GetMods("among-us")); Assert.Single(s.GetMods("valheim")); } finally { Clean(s,d); } }

    [Fact] public async Task DeltaCursorUsesMinimumGameRevisionAfterOtherGameSnapshot()
    {
        var (t, s, c, d) = Setup();
        try
        {
            t.Responses.Enqueue(new(200, "au", Page(CamelMod("a"), revision: 1)));
            await c.SyncAsync("among-us");
            t.Responses.Enqueue(new(200, "vh", Page(CamelMod("v", "valheim"), revision: 2)));
            await c.SyncAsync("valheim");
            t.Responses.Enqueue(new(200, null, $$"""{"reset":false,"revision":2,"changes":[{"op":"upsert","mod":{{CamelMod("b")}}}]}"""));
            await c.SyncAsync("among-us");
            Assert.Contains("since=1", t.Requests[^1].Path);
            Assert.Contains(s.GetMods("among-us"), m => m.Id == "b");
        }
        finally { Clean(s, d); }
    }
}
