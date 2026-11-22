using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-95: catalog contract extras — artifact variants, tolerant sync and cache.
public sealed class Pak95CatalogArtifactTests
{
    private static ThunderstoreVersion Version(params ModArtifact[] artifacts) =>
        new("1.7.3", "Sus-TouMira-1.7.3", "https://example.invalid/legacy.zip", 1024,
            DateTimeOffset.UnixEpoch, [], true, artifacts, "full", "2026-8-18", "x86", "susmodder", null);

    private static ModArtifact Artifact(string arch, string[] stores, string url) =>
        new(url, arch, stores, new string('a', 64), 100, "github", "Tou.zip");

    [Fact]
    public void SelectsTheVariantMatchingArchAndStore()
    {
        var version = Version(
            Artifact("x86", ["steam", "itch"], "https://example.invalid/x86.zip"),
            Artifact("x64", ["epic", "msstore", "steam"], "https://example.invalid/x64.zip"));

        Assert.Equal("https://example.invalid/x86.zip", ModArtifactSelector.Select(version, "Tou", "x86", "steam").Url);
        Assert.Equal("https://example.invalid/x86.zip", ModArtifactSelector.Select(version, "Tou", "x86", "itch").Url);
        Assert.Equal("https://example.invalid/x64.zip", ModArtifactSelector.Select(version, "Tou", "x64", "epic").Url);
        Assert.Equal("https://example.invalid/x64.zip", ModArtifactSelector.Select(version, "Tou", "x64", "steam").Url);
    }

    // W7: the selected artifact's own store list is what the recipe target must follow.
    [Fact]
    public void SelectedArtifactCarriesItsStoreEditions()
    {
        var version = Version(Artifact("x64", ["steam", "itch"], "https://example.invalid/x64.zip"));

        var selected = ModArtifactSelector.Select(version, "Tou", "x64", "itch");

        Assert.Equal(["steam", "itch"], selected.Stores);
    }

    [Theory]
    [InlineData("x86", "epic")]   // x86 artifact only declares steam/itch
    [InlineData("x64", "itch")]   // x64 is not offered on itch
    [InlineData(null, "steam")]   // unknown arch never guesses between x86/x64
    public void RefusesWhenNoVariantMatchesFacts(string? arch, string store)
    {
        var version = Version(
            Artifact("x86", ["steam", "itch"], "https://example.invalid/x86.zip"),
            Artifact("x64", ["epic", "msstore"], "https://example.invalid/x64.zip"));

        var error = Assert.Throws<PhaketException>(() => ModArtifactSelector.Select(version, "Tou", arch, store));
        Assert.Equal(ErrorCodes.ModArtifactUnavailable, error.Error.Code);
        Assert.Equal(arch ?? "unknown", error.Error.Parameters["arch"]);
        Assert.Equal(store, error.Error.Parameters["store"]);
    }

    [Fact]
    public void ThunderstoreRecordsFallBackToTheLegacyDownloadUrl()
    {
        var legacy = new ThunderstoreVersion("1.0.0", "Owner-Mod-1.0.0",
            "https://thunderstore.io/package/download/Owner/Mod/1.0.0/", 42, DateTimeOffset.UnixEpoch, []);
        var selected = ModArtifactSelector.Select(legacy, "Owner-Mod", "x86", "steam");
        Assert.Equal(legacy.DownloadUrl, selected.Url);
        Assert.Equal(42, selected.SizeBytes);
        Assert.Null(selected.Sha256);

        var mismatched = legacy with { Arch = "x64" };
        Assert.Equal(ErrorCodes.ModArtifactUnavailable,
            Assert.Throws<PhaketException>(() => ModArtifactSelector.Select(mismatched, "Owner-Mod", "x86", "steam")).Error.Code);
        Assert.NotNull(ModArtifactSelector.Select(mismatched, "Owner-Mod", null, "steam"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.invalid/x86.zip")]
    public void ArtifactWithoutANonEmptyHttpsUrlIsRefused(string url)
    {
        var version = Version(new ModArtifact(url, "x86", ["steam"], new string('a', 64), 100, "github", "Tou.zip"));

        var error = Assert.Throws<PhaketException>(() => ModArtifactSelector.Select(version, "Tou", "x86", "steam"));

        Assert.Equal(ErrorCodes.ModArtifactUnavailable, error.Error.Code);
        Assert.Equal("Tou 1.7.3", error.Error.Parameters["artifact"]);
        Assert.Equal("x86", error.Error.Parameters["arch"]);
        Assert.Equal("steam", error.Error.Parameters["store"]);
    }

    [Fact]
    public void LegacyDownloadUrlMustAlsoBeHttps()
    {
        var legacy = new ThunderstoreVersion("1.0.0", "Owner-Mod-1.0.0",
            "http://example.invalid/mod.zip", 42, DateTimeOffset.UnixEpoch, []);
        Assert.Equal(ErrorCodes.ModArtifactUnavailable,
            Assert.Throws<PhaketException>(() => ModArtifactSelector.Select(legacy, "Owner-Mod", "x86", "steam")).Error.Code);
    }

    private static string CamelMod(string id, string versionExtra = "", string modExtra = "") =>
        "{\"id\":\"" + id + "\",\"gameId\":\"among-us\",\"namespace\":\"ns\",\"name\":\"" + id
        + "\",\"fullName\":\"ns-" + id + "\",\"description\":\"\",\"owner\":\"owner\","
        + "\"packageUrl\":\"https://example.test\",\"iconUrl\":null,\"isDeprecated\":false,"
        + "\"dateUpdated\":\"2026-01-01T00:00:00Z\"" + modExtra
        + ",\"latest\":{\"versionNumber\":\"1.0.0\",\"fullName\":\"ns-" + id
        + "-1.0.0\",\"downloadUrl\":\"https://example.test/mod\",\"fileSize\":1,"
        + "\"dateCreated\":\"2026-01-01T00:00:00Z\",\"dependencies\":[]" + versionExtra + "}}";

    private static string Page(string mod, long revision = 1) =>
        "{\"revision\":" + revision + ",\"mods\":[" + mod + "],\"nextCursor\":null}";

    private sealed class Snapshot(params string[] pages) : ICatalogTransport
    {
        private int index;
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogResponse(200, null, pages[Math.Min(index++, pages.Length - 1)]));
    }

    [Theory]
    [InlineData("https://susmodder-cdn.ovh/mods/x.zip")]
    [InlineData("https://api.susmodder-cdn.ovh/v2/mods/x.zip")]
    [InlineData("https://susmodder.app/mods/x.zip")]
    [InlineData("https://cdn.susmodder.app/mods/x.zip")]
    public void SusModderCdnHostsAreAllowedForTransport(string url)
    {
        Assert.True(SourcePolicy.IsAllowedTransportSource(url));
        Assert.False(SourcePolicy.IsAllowedHttpsSource(url)); // an artifact needs a declared sha256
        Assert.True(SourcePolicy.IsAllowedHttpsSource(url, new string('a', 64)));
    }

    [Fact]
    public async Task LegacyCacheRowsStillDeserializeAndNewFieldsSurviveResync()
    {
        var dir = Directory.CreateTempSubdirectory("pak95-catalog-").FullName;
        try
        {
            var path = Path.Combine(dir, "catalog.db");
            using (var store = new ModCatalogStore(path))
            {
                // A cache populated before PAK-95: no packaging/artifacts/amongVersion fields.
                await new ModCatalogClient(new Snapshot(Page(CamelMod("old"))), store)
                    .SyncAsync("among-us", TestContext.Current.CancellationToken);
                var old = Assert.Single(store.GetMods("among-us"));
                Assert.Null(old.Latest.Packaging);
                Assert.Null(old.Latest.Artifacts);

                // The extended contract, including fields the client does not consume yet.
                var extended = CamelMod("tou", """
                    ,"packaging":"full","amongVersion":"2026-8-18","arch":"x86","source":"susmodder","license":"MIT",
                    "artifacts":[{"url":"https://example.invalid/x86.zip","arch":"x86","stores":["steam","itch"],
                                  "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                                  "sizeBytes":100,"source":"github","assetName":"Tou.zip"}]
                    """);
                // A reset makes the client take a fresh snapshot through the delta path.
                await new ModCatalogClient(new Snapshot(
                        "{\"reset\":true,\"revision\":2,\"changes\":[]}", Page(extended, 2)), store)
                    .SyncAsync("among-us", TestContext.Current.CancellationToken);
                var tou = Assert.Single(store.GetMods("among-us"), m => m.Id == "tou").Latest;
                Assert.Equal("full", tou.Packaging);
                Assert.Equal("2026-8-18", tou.AmongVersion);
                Assert.Equal("x86", tou.Arch);
                Assert.Equal("x86", Assert.Single(tou.Artifacts!).Arch);
                Assert.Equal(new[] { "steam", "itch" }, tou.Artifacts![0].Stores?.ToArray());
            }

            // Reopening the cache keeps the new fields: the JSON payload is the schema,
            // so no ALTER TABLE migration can lose rows.
            using (var reopened = new ModCatalogStore(path))
            {
                var tou = Assert.Single(reopened.GetMods("among-us"), m => m.Id == "tou").Latest;
                Assert.Equal("full", tou.Packaging);
                Assert.Equal("2026-8-18", tou.AmongVersion);
                Assert.Equal(new string('a', 64), tou.Artifacts![0].Sha256);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SyncToleratesUnknownExtraFieldsAndNullExtras()
    {
        var dir = Directory.CreateTempSubdirectory("pak95-catalog-null-").FullName;
        try
        {
            using var store = new ModCatalogStore(Path.Combine(dir, "catalog.db"));
            var payload = CamelMod("plain",
                """
                ,"packaging":null,"amongVersion":null,"arch":null,"source":null,"license":null,
                "someFutureField":{"nested":true},"artifacts":null
                """,
                ",\"repoAvailable\":false");
            await new ModCatalogClient(new Snapshot(Page(payload)), store)
                .SyncAsync("among-us", TestContext.Current.CancellationToken);
            var mod = Assert.Single(store.GetMods("among-us"));
            Assert.Null(mod.Latest.Packaging);
            Assert.Null(mod.Latest.Artifacts);
            Assert.False(mod.RepoAvailable);
        }
        finally { Directory.Delete(dir, true); }
    }
}