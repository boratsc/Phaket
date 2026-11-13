using System.Text.Json;
using Phaket.Core.Catalog;

namespace Phaket.Core.Tests;

// PAK-23: client catalog sync — snapshot, deltas, tombstones, ETag, 410 resync,
// offline reads, download proposals. Fake transport; no network.
public class CatalogClientTests
{
    private sealed class FakeTransport : ICatalogTransport
    {
        public Queue<CatalogResponse> Responses { get; } = new();
        public List<string> Requests { get; } = [];
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            Requests.Add(path + (ifNoneMatch is null ? "" : $" etag={ifNoneMatch}"));
            return Task.FromResult(Responses.Dequeue());
        }
    }

    private static string BuildJson(string id, string status = "known") => $$"""
        {"id":"{{id}}","gameId":"among-us","store":"steam","versionLabel":"2026.3.5","buildId":"b1",
         "depotIds":["945361"],"manifestIds":["m1"],"branch":"public","availabilityStatus":"{{status}}",
         "source":"poc","sourceReference":"ref","observedAt":"2026-11-01T00:00:00Z","verifiedAt":null,
         "confidence":0.5,"provenance":"poc-manual-import"}
        """;

    private static (FakeTransport Transport, CatalogStore Store, CatalogClient Client, string Dir) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-catalog-").FullName;
        var transport = new FakeTransport();
        var store = new CatalogStore(Path.Combine(dir, "catalog.db"));
        return (transport, store, new CatalogClient(transport, store), dir);
    }

    [Fact]
    public void DeltaSyncAppliesUpsertsAndRevokes()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-1","payload":{{BuildJson("b-1")}}},
                  {"revision":2,"type":"upsert","buildId":"b-2","payload":{{BuildJson("b-2")}}}]}
                """));
            client.SyncAsync().Wait();
            Assert.Equal(2, store.GetBuilds("among-us").Count);
            Assert.Equal(2, store.Revision);

            transport.Responses.Enqueue(new CatalogResponse(200, null, """
                {"revision":3,"nextSince":3,"hasMore":false,"changes":[
                  {"revision":3,"type":"revoke","buildId":"b-1","payload":{}}]}
                """));
            client.SyncAsync().Wait();
            Assert.Single(store.GetBuilds("among-us"));
            Assert.Empty(store.GetBuilds("among-us").Where(b => b.Id == "b-1"));
            Assert.Equal(3, store.Revision);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void PaginationFollowsNextSince()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":1,"hasMore":true,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-1","payload":{{BuildJson("b-1")}}}]}
                """));
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":2,"type":"upsert","buildId":"b-2","payload":{{BuildJson("b-2")}}}]}
                """));
            client.SyncAsync().Wait();
            Assert.Equal(2, store.GetBuilds("among-us").Count);
            Assert.Contains(transport.Requests, r => r.Contains("since=1"));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void ContinuityLossTriggersFullSnapshot()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            store.SetMeta("revision", "5");
            transport.Responses.Enqueue(new CatalogResponse(410, null, """{"error":{"code":"CATALOG_REVISION_TOO_OLD"}}"""));
            transport.Responses.Enqueue(new CatalogResponse(200, "\"9:cursor:100\"", $$"""
                {"revision":9,"items":[{{BuildJson("b-9")}}],"nextCursor":null}
                """));
            client.SyncAsync().Wait();
            Assert.Single(store.GetBuilds("among-us"));
            Assert.Equal(9, store.Revision);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void ETag304KeepsCache()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            store.SetMeta("revision", "2");
            store.SetMeta("etag", "\"2::100\"");
            transport.Responses.Enqueue(new CatalogResponse(304, "\"2::100\"", null));
            client.SyncAsync().Wait();
            Assert.Contains(transport.Requests, r => r.Contains("etag="));
            Assert.NotNull(store.LastSync);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void OfflineReadsReturnCachedBuildsWithSyncDate()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":1,"nextSince":1,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-1","payload":{{BuildJson("b-1")}}}]}
                """));
            client.SyncAsync().Wait();
            var syncedAt = store.LastSync;
            Assert.NotNull(syncedAt);

            // Network gone: sync throws, but cached data remains readable.
            transport.Responses.Enqueue(new CatalogResponse(503, null, null));
            Assert.Throws<HttpRequestException>(() => client.SyncAsync().GetAwaiter().GetResult());
            var builds = store.GetBuilds("among-us");
            Assert.Single(builds);
            Assert.Equal(syncedAt, store.LastSync);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void DownloadProposalsExcludeUnavailableBuilds()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-ok","payload":{{BuildJson("b-ok", "verified")}}},
                  {"revision":2,"type":"upsert","buildId":"b-no","payload":{{BuildJson("b-no", "unavailable")}}}]}
                """));
            client.SyncAsync().Wait();
            var proposals = store.GetDownloadProposals("among-us");
            Assert.Single(proposals);
            Assert.Equal("b-ok", proposals[0].Id);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void StoreFilterWorks()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            var epic = BuildJson("b-epic").Replace("\"store\":\"steam\"", "\"store\":\"epic\"");
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-steam","payload":{{BuildJson("b-steam")}}},
                  {"revision":2,"type":"upsert","buildId":"b-epic","payload":{{epic}}}]}
                """));
            client.SyncAsync().Wait();
            Assert.Single(store.GetBuilds("among-us", "epic"));
            Assert.Equal("b-epic", store.GetBuilds("among-us", "epic")[0].Id);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }
}
