using System.Text.Json;
using Microsoft.Data.Sqlite;
using Phaket.Core.Catalog;

namespace Phaket.Core.Tests;

// PAK-23: client catalog sync — snapshot, deltas, tombstones, ETag, 410 resync,
// offline reads, download proposals. Fake transport; no network.
public class CatalogClientTests
{
    public sealed class FakeTransport : ICatalogTransport
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
    public async Task SnapshotAndDeltaCacheArchitectureAndPreserveEtag()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            var x64 = BuildJson("one").Replace("\"store\":", "\"arch\":\"x64\",\"store\":");
            var unknown = BuildJson("two").Replace("\"store\":", "\"arch\":\"unknown\",\"store\":");
            var nullArch = BuildJson("three").Replace("\"store\":", "\"arch\":null,\"store\":");
            transport.Responses.Enqueue(new CatalogResponse(200, "\"snapshot\"", $$"""
                {"revision":1,"items":[{{x64}},{{unknown}},{{nullArch}},{{BuildJson("four")}}],"nextCursor":null}
                """));
            await client.FullSyncAsync(TestContext.Current.CancellationToken);
            var builds = store.GetBuilds("among-us");
            Assert.Equal("x64", builds.Single(b => b.Id == "one").Arch);
            Assert.All(builds.Where(b => b.Id != "one"), b => Assert.Null(b.Arch));
            Assert.Equal("public", builds.Single(b => b.Id == "one").Branch);
            Assert.Equal("b1", builds.Single(b => b.Id == "one").BuildId);

            var x86 = x64.Replace("x64", "x86");
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":2,"type":"upsert","buildId":"one","payload":{{x86}}}]}
                """));
            await client.SyncAsync(TestContext.Current.CancellationToken);
            Assert.Equal("x86", store.GetBuilds("among-us").Single(b => b.Id == "one").Arch);
            Assert.Equal("\"snapshot\"", store.ETag);
            Assert.Equal(2, store.Revision);
            Assert.DoesNotContain(transport.Requests, r => r.Contains("etag="));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void OldDatabaseMigratesWithoutLosingBuildsOrMetadata()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-catalog-old-").FullName;
        var path = Path.Combine(dir, "catalog.db");
        try
        {
            using (var store = new CatalogStore(path))
            {
                store.ReplaceAll(JsonSerializer.SerializeToElement(new[] { JsonSerializer.Deserialize<JsonElement>(BuildJson("old")) }), 7, "old-etag");
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_builds DROP COLUMN arch; ALTER TABLE catalog_builds DROP COLUMN released_at";
                cmd.ExecuteNonQuery();
            }
            using (var store = new CatalogStore(path))
            {
                Assert.Null(Assert.Single(store.GetBuilds("among-us")).Arch);
                Assert.Equal(7, store.Revision);
                Assert.Equal("old-etag", store.ETag);
            }
            using var reopened = new CatalogStore(path);
            Assert.Single(reopened.GetBuilds("among-us"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DeltaSyncAppliesUpsertsAndRevokes()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-1","payload":{{BuildJson("b-1")}}},
                  {"revision":2,"type":"upsert","buildId":"b-2","payload":{{BuildJson("b-2")}}}]}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, store.GetBuilds("among-us").Count);
            Assert.Equal(2, store.Revision);

            transport.Responses.Enqueue(new CatalogResponse(200, null, """
                {"revision":3,"nextSince":3,"hasMore":false,"changes":[
                  {"revision":3,"type":"revoke","buildId":"b-1","payload":{}}]}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(store.GetBuilds("among-us"));
            Assert.DoesNotContain(store.GetBuilds("among-us"), b => b.Id == "b-1");
            Assert.Equal(3, store.Revision);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task PaginationFollowsNextSince()
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
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, store.GetBuilds("among-us").Count);
            Assert.Contains(transport.Requests, r => r.Contains("since=1"));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ContinuityLossTriggersFullSnapshot()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            store.SetMeta("revision", "5");
            transport.Responses.Enqueue(new CatalogResponse(410, null, """{"error":{"code":"CATALOG_REVISION_TOO_OLD"}}"""));
            transport.Responses.Enqueue(new CatalogResponse(200, "\"9:cursor:100\"", $$"""
                {"revision":9,"items":[{{BuildJson("b-9")}}],"nextCursor":null}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(store.GetBuilds("among-us"));
            Assert.Equal(9, store.Revision);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FullSyncStoresEtagAndDeltaSyncIsRevisionBased()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, "\"9::100\"", $$"""
                {"revision":9,"items":[{{BuildJson("b-9")}}],"nextCursor":null}
                """));
            await client.FullSyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("\"9::100\"", store.ETag);
            Assert.Equal(9, store.Revision);

            // Delta sync must not send the snapshot ETag to the changes endpoint.
            transport.Responses.Enqueue(new CatalogResponse(200, null, """
                {"revision":9,"nextSince":9,"hasMore":false,"changes":[]}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain(transport.Requests, r => r.Contains("etag="));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InterruptedFullSyncKeepsPreviousCatalog()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            // Seed an existing catalog at revision 1.
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":1,"nextSince":1,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-old","payload":{{BuildJson("b-old")}}}]}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(store.GetBuilds("among-us"));

            // Full resync: first page ok, second page fails -> nothing committed.
            transport.Responses.Enqueue(new CatalogResponse(200, "\"2:p1\"", $$"""
                {"revision":2,"items":[{{BuildJson("b-new1")}}],"nextCursor":"b-new1"}
                """));
            transport.Responses.Enqueue(new CatalogResponse(503, null, null));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.FullSyncAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Single(store.GetBuilds("among-us"));
            Assert.Equal("b-old", store.GetBuilds("among-us")[0].Id);
            Assert.Equal(1, store.Revision);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OfflineReadsReturnCachedBuildsWithSyncDate()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":1,"nextSince":1,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-1","payload":{{BuildJson("b-1")}}}]}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            var syncedAt = store.LastSync;
            Assert.NotNull(syncedAt);

            // Network gone: sync throws, but cached data remains readable.
            transport.Responses.Enqueue(new CatalogResponse(503, null, null));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken));
            var builds = store.GetBuilds("among-us");
            Assert.Single(builds);
            Assert.Equal(syncedAt, store.LastSync);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DownloadProposalsExcludeUnavailableBuilds()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"b-ok","payload":{{BuildJson("b-ok", "verified")}}},
                  {"revision":2,"type":"upsert","buildId":"b-no","payload":{{BuildJson("b-no", "unavailable")}}}]}
                """));
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            var proposals = store.GetDownloadProposals("among-us");
            Assert.Single(proposals);
            Assert.Equal("b-ok", proposals[0].Id);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StoreFilterWorks()
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
            await client.SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(store.GetBuilds("among-us", "epic"));
            Assert.Equal("b-epic", store.GetBuilds("among-us", "epic")[0].Id);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }
}

// Backend discovery surfaces release dates; the local cache keeps them and the
// UI's "latest" choice resolves through GetLatestBuild.
public class CatalogReleaseDateTests
{
    private static string BuildJson(string id, string version, string? released) => $$"""
        {"id":"{{id}}","gameId":"among-us","store":"steam","versionLabel":"{{version}}","buildId":"b-{{id}}",
         "depotIds":["945361"],"manifestIds":[],"branch":"live","availabilityStatus":"known",
         "source":"auto-discovery","sourceReference":"ref","observedAt":"2026-11-14T00:00:00Z","verifiedAt":null,
         "confidence":0.6,"provenance":"auto-discovery/steam-news","releasedAt":{{(released is null ? "null" : $"\"{released}\"")}}}
        """;

    [Fact]
    public async Task ReleasedAtRoundTripsAndLatestFollowsReleaseDate()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-catalog-rel-").FullName;
        var transport = new CatalogClientTests.FakeTransport();
        var store = new CatalogStore(Path.Combine(dir, "catalog.db"));
        try
        {
            transport.Responses.Enqueue(new CatalogResponse(200, null, $$"""
                {"revision":1,"nextSince":1,"hasMore":false,"changes":[
                  {"revision":1,"type":"upsert","buildId":"old","payload":{{BuildJson("old", "2026.6.5", "2026-06-05T00:00:00Z")}}},
                  {"revision":1,"type":"upsert","buildId":"new","payload":{{BuildJson("new", "2026.9.29", "2026-09-29T00:00:00Z")}}},
                  {"revision":1,"type":"upsert","buildId":"nodate","payload":{{BuildJson("nodate", "2026.8.18", null)}}}]}
                """));
            await new CatalogClient(transport, store).SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            var builds = store.GetBuilds("among-us", "steam");
            Assert.Equal(3, builds.Count);
            Assert.Equal(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
                builds.Single(b => b.Id == "new").ReleasedAt);
            Assert.Null(builds.Single(b => b.Id == "nodate").ReleasedAt);
            // "Latest" = newest release date, not insertion order.
            Assert.Equal("new", store.GetLatestBuild("among-us", "steam")!.Id);
            // Unavailable builds never become "latest".
            transport.Responses.Enqueue(new CatalogResponse(200, null, """
                {"revision":2,"nextSince":2,"hasMore":false,"changes":[
                  {"revision":2,"type":"revoke","buildId":"new","payload":{}}]}
                """));
            await new CatalogClient(transport, store).SyncAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("old", store.GetLatestBuild("among-us", "steam")!.Id);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }
}
