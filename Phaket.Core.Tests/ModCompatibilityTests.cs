using Phaket.Core.Catalog;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-97: the upstream SusModder full×dll matrix rounds-trips through the catalog cache and
// the resolver turns NT into a warning, NW into a block, and everything else into silence.
public sealed class ModCompatibilityTests
{
    private sealed class FakeTransport : ICatalogTransport
    {
        public Queue<CatalogResponse> Responses { get; } = new();
        public List<string> Requests { get; } = [];
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        { Requests.Add(path); return Task.FromResult(Responses.Dequeue()); }
    }

    private static (FakeTransport Transport, ModCatalogStore Store, ModCatalogClient Client, string Dir) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("mod-compat-").FullName;
        var transport = new FakeTransport();
        var store = new ModCatalogStore(Path.Combine(dir, "db.sqlite"));
        return (transport, store, new ModCatalogClient(transport, store), dir);
    }

    private static string Page(string compatibility, long revision) =>
        "{\"revision\":" + revision + ",\"mods\":[],\"compatibility\":" + compatibility + ",\"nextCursor\":null}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Snapshot_persists_the_matrix_and_a_later_snapshot_replaces_it()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new(200, "e1", Page("""[{"fullId":"13","dllId":"5","status":"F"}]""", 1)));
            await client.SyncAsync("among-us", Ct);
            var pair = Assert.Single(store.GetCompatibility("among-us"));
            Assert.Equal(("13", "5", "F"), (pair.FullId, pair.DllId, pair.Status));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }

        // A later full snapshot replaces the matrix instead of merging into it.
        (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new(200, "e1", Page("""[{"fullId":"13","dllId":"5","status":"F"}]""", 1)));
            await client.SyncAsync("among-us", Ct);
            transport.Responses.Enqueue(new(410, null, null));
            transport.Responses.Enqueue(new(200, "e2", Page("""[{"fullId":"13","dllId":"6","status":"NW"}]""", 2)));
            await client.SyncAsync("among-us", Ct);
            var replaced = Assert.Single(store.GetCompatibility("among-us"));
            Assert.Equal(("13", "6", "NW"), (replaced.FullId, replaced.DllId, replaced.Status));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Snapshot_without_a_matrix_clears_the_game_pairs()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new(200, "e1", Page("""[{"fullId":"13","dllId":"5","status":"NT"}]""", 1)));
            await client.SyncAsync("among-us", Ct);
            transport.Responses.Enqueue(new(410, null, null));
            transport.Responses.Enqueue(new(200, "e2", Page("null", 2)));
            await client.SyncAsync("among-us", Ct);
            Assert.Empty(store.GetCompatibility("among-us"));
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Delta_refresh_keeps_the_snapshot_matrix()
    {
        var (transport, store, client, dir) = Setup();
        try
        {
            transport.Responses.Enqueue(new(200, "e1", Page("""[{"fullId":"13","dllId":"5","status":"NT"}]""", 1)));
            await client.SyncAsync("among-us", Ct);
            transport.Responses.Enqueue(new(200, null, "{\"reset\":false,\"revision\":2,\"changes\":[]}"));
            await client.SyncAsync("among-us", Ct);
            Assert.Equal("NT", Assert.Single(store.GetCompatibility("among-us")).Status);
        }
        finally { store.Dispose(); Directory.Delete(dir, true); }
    }

    private static IReadOnlyList<(string Id, string Name, string Packaging)> Plan(string full, string dll) =>
        [("13", full, "full"), ("5", dll, "dll")];

    private static IReadOnlyList<ModCompatibility> Matrix(string status) => [new("13", "5", status)];

    [Theory]
    [InlineData("F")]
    [InlineData("W")]
    public void Favourable_status_is_silent(string status) =>
        Assert.Empty(ModCompatibilityResolver.Check(Matrix(status), Plan("TouMira", "Submerged")));

    [Fact]
    public void Unknown_pair_is_silent_not_untested() =>
        Assert.Empty(ModCompatibilityResolver.Check([], Plan("TouMira", "Submerged")));

    [Fact]
    public void Pair_present_in_one_direction_only_is_silent() =>
        Assert.Empty(ModCompatibilityResolver.Check(Matrix("NW"), [("13", "TouMira", "full")]));

    [Fact]
    public void Untested_pair_warns()
    {
        var finding = Assert.Single(ModCompatibilityResolver.Check(Matrix("NT"), Plan("TouMira", "Submerged")));
        Assert.Equal(ModCompatibilitySignal.Warn, finding.Signal);
        Assert.Equal(("TouMira", "Submerged", "NT"), (finding.FullName, finding.DllName, finding.Status));
    }

    [Fact]
    public void Not_working_pair_blocks()
    {
        var finding = Assert.Single(ModCompatibilityResolver.Check(Matrix("NW"), Plan("TouMira", "Submerged")));
        Assert.Equal(ModCompatibilitySignal.Block, finding.Signal);
        Assert.Equal("Mod TouMira i dll Submerged są oznaczone w tabeli zgodności jako niedziałające razem. Usuń jedno z nich z planu.",
            new PhaketException(ErrorCodes.ModCompatibilityBlocked, ("full", "TouMira"), ("dll", "Submerged")).LocalizedMessage("pl-PL"));
    }
}