using System.Text.Json;
using Microsoft.Data.Sqlite;
using Phaket.Core.Catalog;
using Phaket.Services;

namespace Phaket.Tests;

// W1 (PAK-96 re-review): the Builds screen and an install preparation keep separate
// CatalogStore connections to the same file. A concurrent writer must wait on the
// busy handler, and a sync that still cannot write must fail safe to the cache —
// never as a raw "database is locked" from PrepareAsync.
public sealed class Pak96BuildLockTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("pak96-lock-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    private static CatalogStore Open(string path, int busyTimeoutSeconds = 5) => new(path, busyTimeoutSeconds);

    private static JsonElement Build(string id, string label) => JsonSerializer.SerializeToElement(new
    {
        id, gameId = "among-us", store = "steam", versionLabel = label, buildId = "24302054",
        depotIds = new[] { "945361" }, manifestIds = new[] { "1397756378225229500" }, branch = "public",
        availabilityStatus = "known", source = "poc", sourceReference = "ref", confidence = 0.9,
        provenance = "test",
    });

    // Holds the write lock on the database file from another connection.
    private static SqliteConnection Lock(string path)
    {
        var holder = new SqliteConnection($"Data Source={path};Pooling=False");
        holder.Open();
        var tx = holder.BeginTransaction();
        using var cmd = holder.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO catalog_meta(key,value) VALUES('holder','1')";
        cmd.ExecuteNonQuery();
        return holder;
    }

    [Fact]
    public void BusyHandlerWaitsButIsBoundedByTheTimeout()
    {
        var path = Path.Combine(root, "bounded.db");
        using var store = Open(path, busyTimeoutSeconds: 1);
        using var holder = Lock(path);
        var items = JsonSerializer.SerializeToElement(new[] { Build("b1", "2026.8.18") });

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        // Not an instant "database is locked": the write waits for the other connection.
        var error = Assert.Throws<SqliteException>(() => store.ReplaceAll(items, 1, null));
        elapsed.Stop();

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(elapsed.ElapsedMilliseconds, 500, 5_000);
    }

    [Fact]
    public async Task WriterWaitingOnALockProceedsOnceItIsReleased()
    {
        var path = Path.Combine(root, "released.db");
        using var store = Open(path);
        using var holder = Lock(path);
        var items = JsonSerializer.SerializeToElement(new[] { Build("b1", "2026.8.18") });

        var writing = Task.Run(() => store.ReplaceAll(items, 1, null));
        await Task.Delay(300);
        holder.Dispose();
        await writing;

        Assert.Single(store.GetBuilds("among-us"));
    }

    [Fact]
    public async Task LockedSyncKeepsTheCacheInsteadOfThrowing()
    {
        var path = Path.Combine(root, "catalog.db");
        using (var seed = Open(path))
            seed.ReplaceAll(JsonSerializer.SerializeToElement(new[] { Build("seed", "2025.6.10") }), 1, null);
        using var store = Open(path, busyTimeoutSeconds: 1);
        using var holder = Lock(path);
        var transport = new Snapshot(new[] { Build("other", "2026.9.29") });

        var synced = await ModInstallService.TrySyncAsync(transport, store, CancellationToken.None);

        Assert.False(synced);
        Assert.Equal(1, transport.Calls);
        holder.Dispose();
        Assert.Equal("2025.6.10", Assert.Single(store.GetBuilds("among-us")).VersionLabel);
    }

    private sealed class Snapshot(JsonElement[] builds) : ICatalogTransport
    {
        public int Calls { get; private set; }

        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(new
            {
                revision = 2, items = builds, nextCursor = (string?)null,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }
}
