using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.Tests;

// PAK-96: ModInstallService derives the installation's game version from the build
// registry (buildId -> canonical label). A dll addon with a numeric amongVersion then
// plans when the build matches, is refused as a version conflict when it does not,
// and stays unknown when the registry does not know the build. A full package hands
// the registry manifest to game.provision. Fixtures only: no network, no real game.
public sealed class Pak96InstallVersionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ModName = "Sus-Submerged";
    private const string ModUrl = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";
    private const string LoaderUrl = "https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/";
    private const string InstalledBuildId = "24302054";
    private const string ManifestId = "9223372036854775807";
    private const string LoaderPath = "/tmp/opencode/phaket-research/artifacts/amongus-loader-6.0.700.zip";

    // CI has no research artifacts: every test that builds a fixture skips instead
    // of failing on a missing file.
    private static void RequireLoader()
    {
        if (!File.Exists(LoaderPath)) Assert.Skip("Real loader archive is not available locally.");
    }

    private static byte[] LoaderBytes() => File.ReadAllBytes(LoaderPath);

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return buffer.ToArray();
    }

    private sealed class Catalog(ThunderstoreMod mod, ThunderstoreVersion version) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { revision = 1, mods = new[] { mod }, nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/mods/", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { mod, versions = new[] { version } }, Json)));
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    private sealed class Packages(IReadOnlyDictionary<string, byte[]> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) => files.TryGetValue(source, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new InvalidOperationException($"unexpected download: {source}");
    }

    private sealed class Receipts : IReceiptTransport
    {
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HostVanilla : IBaseGameSource
    {
        public int Calls { get; private set; }
        public Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId, string targetDirectory, CancellationToken ct = default)
        {
            Calls++;
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us.exe"), "provisioned");
            Directory.CreateDirectory(Path.Combine(targetDirectory, "Among Us_Data"));
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us_Data", "sharedassets0.assets"), "vanilla");
            return Task.FromResult(BaseGamePrepareResult.Ok);
        }
    }

    // W1: the build registry is synced by the Builds screen only. PrepareAsync must fetch
    // it itself (one call) when the cache cannot answer for this installation.
    private sealed class BuildsRegistry(JsonElement? build, bool offline = false) : ICatalogTransport
    {
        public int Calls { get; private set; }

        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            Calls++;
            if (offline) throw new HttpRequestException("catalog offline");
            if (path.StartsWith("/catalog/index", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(new
                {
                    revision = 1, items = build is { } value ? new[] { value } : [], nextCursor = (string?)null,
                }, Json)));
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("pak96-install-").FullName;
        public string GameRoot { get; }
        public ModInstallService Service { get; }
        private readonly ModCatalogStore store;

        public Fixture(byte[] zip, string? packaging, string? amongVersion, string? assetName,
            string[] manifestIds, string? installedBuildId = InstalledBuildId, IBaseGameSource? baseGameSource = null,
            string modUrl = ModUrl, bool seedRegistry = true, ICatalogTransport? buildsTransport = null,
            string? installedDepotManifest = null)
        {
            var steamapps = Path.Combine(Root, "steamapps");
            GameRoot = Path.Combine(steamapps, "common", "Among Us");
            Directory.CreateDirectory(Path.Combine(GameRoot, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(GameRoot, "Among Us.exe"), "fixture");
            var depots = installedDepotManifest is null ? "" : $$"""

                    "InstalledDepots"
                    {
                        "945361"
                        {
                            "manifest"		"{{installedDepotManifest}}"
                        }
                    }
                """;
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), $$"""
                "AppState"
                {
                    "appid"		"945360"
                    "name"		"Among Us"
                    "buildid"		"{{installedBuildId}}"
                    "branch"		"public"
                    "manifest"		"1"
                    "installdir"		"Among Us"
                    "SizeOnDisk"		"1000000"{{depots}}
                }
                """);
            if (seedRegistry) SeedRegistry(manifestIds);

            var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
            var version = new ThunderstoreVersion("1.0.0", $"{ModName}-1.0.0", modUrl, zip.Length, DateTimeOffset.UnixEpoch,
                [], true,
                packaging is null ? null : [new ModArtifact(modUrl, "x86", ["steam", "itch"], hash, zip.Length,
                    "susmodder-cdn", assetName ?? "package.zip")],
                packaging, amongVersion, packaging is null ? null : "x86", packaging is null ? null : "susmodder", null);
            var mod = new ThunderstoreMod(ModName, "among-us", "Sus", ModName, ModName, "desc", "sus",
                "https://example.test", null, false, DateTimeOffset.UnixEpoch, version);
            store = new ModCatalogStore(Path.Combine(Root, "catalog.db"));
            Service = new ModInstallService(Root, new ModCatalogClient(new Catalog(mod, version), store),
                new Packages(new Dictionary<string, byte[]> { [modUrl] = zip, [LoaderUrl] = LoaderBytes() }), new Receipts(),
                baseGameSource: baseGameSource, catalogTransport: buildsTransport);
        }

        // Seeds the build cache exactly as a catalog sync would, including aliases.
        private void SeedRegistry(string[] manifestIds)
        {
            using var catalog = new CatalogStore(Path.Combine(Root, "catalog.db"));
            catalog.ReplaceAll(JsonSerializer.SerializeToElement(new[] { Build(manifestIds) }), 1, null);
        }

        public static JsonElement Build(string[] manifestIds) =>
            JsonSerializer.SerializeToElement(new
            {
                id = "among-us-steam-2026.8.18", gameId = "among-us", store = "steam", versionLabel = "2026.8.18",
                buildId = InstalledBuildId, depotIds = new[] { "945361" }, manifestIds, branch = "public",
                availabilityStatus = manifestIds.Length == 0 ? "unknown" : "known",
                source = "susmodder", sourceReference = "api.susmodder-cdn.ovh/v2/versions",
                observedAt = "2026-11-20T00:00:00Z", verifiedAt = (string?)null, confidence = 0.9,
                provenance = "susmodder-versions+steamdb", releasedAt = "2026-08-18T00:00:00Z", arch = "x86",
                aliases = new[] { "2026-8-18" },
            });

        public void Dispose()
        {
            store.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private static byte[] DllZip() => Zip(("BepInEx/plugins/Submerged/Submerged.dll", "mod"),
        ("Among Us_Data/sharedassets0.assets", "vanilla"));

    [Fact]
    public async Task DllWithNumericAmongVersionAndMatchingBuildPlans()
    {
        RequireLoader();
        using var fixture = new Fixture(DllZip(), "dll", "2026-8-18", "submerged.zip", [ManifestId]);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var operation = Assert.Single(Assert.Single(preparation.Plans).Operations);
        Assert.Equal("archive.extract", operation.Kind);
        Assert.Equal("registry", preparation.GameVersionSource);
    }

    [Fact]
    public async Task DllWithDifferentAmongVersionIsRefusedAsVersionConflict()
    {
        RequireLoader();
        using var fixture = new Fixture(DllZip(), "dll", "2026.9.29", "submerged.zip", [ManifestId]);
        var error = await Assert.ThrowsAsync<PhaketException>(() => fixture.Service.PrepareAsync("among-us",
            ModName, "1.0.0", "", new(fixture.GameRoot, "steam", false, "x86"),
            ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.UnsupportedGameVersion, error.Error.Code);
        Assert.Equal("2026.8.18", error.Error.Parameters["gameVersion"]);
    }

    [Fact]
    public async Task DllWithBuildUnknownToTheRegistryDoesNotGuessTheVersion()
    {
        RequireLoader();
        using var fixture = new Fixture(DllZip(), "dll", "2026-8-18", "submerged.zip", [ManifestId],
            installedBuildId: "11111111");
        var error = await Assert.ThrowsAsync<PhaketException>(() => fixture.Service.PrepareAsync("among-us",
            ModName, "1.0.0", "", new(fixture.GameRoot, "steam", false, "x86"),
            ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.GameBuildUnavailable, error.Error.Code);
    }

    private static byte[] FullZip() => Zip(("BepInEx/plugins/TownOfUsMira.dll", "mod"),
        ("Among Us_Data/sharedassets0.assets", "vanilla"));

    [Fact]
    public async Task FullPackageProvisionsTheRegistryManifest()
    {
        RequireLoader();
        using var fixture = new Fixture(FullZip(), "full", "2026-8-18", "tou.zip", [ManifestId]);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var provision = Assert.Single(Assert.Single(preparation.Plans).Operations, op => op.Kind == "game.provision");
        Assert.Equal(ManifestId, provision.Parameters!["manifestId"]);
        Assert.Equal("2026.8.18", provision.Parameters!["version"]);
    }

    [Fact]
    public async Task FullPackageWithoutPublishedManifestProvisionsByVersion()
    {
        RequireLoader();
        using var fixture = new Fixture(FullZip(), "full", "2026-8-18", "tou.zip", []);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var provision = Assert.Single(Assert.Single(preparation.Plans).Operations, op => op.Kind == "game.provision");
        Assert.False(provision.Parameters!.ContainsKey("manifestId"));
        Assert.Equal("2026.8.18", provision.Parameters!["version"]);
    }

    [Fact]
    public async Task GitHubCatalogArtifactIsAllowedBeforeItsDownload()
    {
        RequireLoader();
        // PAK-96 gate regression: the GitHub release allowlist was registered by the
        // recipe generator, which runs after the artifact download. A catalog artifact
        // hosted on GitHub was therefore refused with MOD_SOURCE_HOST_NOT_ALLOWED.
        const string gitHubUrl = "https://github.com/Pak96-Allowlist/Test/releases/download/v1/mod.zip";
        using var fixture = new Fixture(FullZip(), "full", "2026-8-18", "mod.zip", [ManifestId], modUrl: gitHubUrl);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);
        Assert.Single(preparation.Plans);
    }

    [Fact]
    public async Task FullPackageWithoutSeededCacheProvisionsThroughTheHostSource()
    {
        RequireLoader();
        // PAK-96 gate regression: ExecuteAsync used to create the executor without any
        // base-game source, so a full package with an empty vanilla cache always failed
        // with ARTIFACT_MISSING. The host runner is the product path for DepotDownloader
        // and legendary; a fake proves the executor receives it and provisions.
        var host = new HostVanilla();
        using var fixture = new Fixture(FullZip(), "full", "2026-8-18", "tou.zip", [ManifestId], baseGameSource: host);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var outcome = await fixture.Service.ExecuteAsync(preparation, true, ct: TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, outcome.ErrorCode);
        Assert.Equal(1, host.Calls);
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "winhttp.dll")));
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "TownOfUsMira.dll")));
        Assert.Equal("provisioned", File.ReadAllText(Path.Combine(fixture.GameRoot, "Among Us.exe")));
    }

    [Fact]
    public async Task ThunderstorePackagingIsUnchangedByTheRegistryFacts()
    {
        RequireLoader();
        var zip = Zip(("plugins/Mod.dll", "mod"));
        using var fixture = new Fixture(zip, null, null, null, [ManifestId]);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);
        Assert.NotEmpty(Assert.Single(preparation.Plans).Operations);
    }

    [Fact]
    public async Task EmptyBuildRegistryIsSyncedBeforePlanning()
    {
        RequireLoader();
        var registry = new BuildsRegistry(Fixture.Build([ManifestId]));
        using var fixture = new Fixture(DllZip(), "dll", "2026-8-18", "submerged.zip", [ManifestId],
            seedRegistry: false, buildsTransport: registry);

        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var operation = Assert.Single(Assert.Single(preparation.Plans).Operations);
        Assert.Equal("archive.extract", operation.Kind);
        Assert.Equal(1, registry.Calls);
    }

    [Fact]
    public async Task OfflineWithoutBuildCacheIsRefusedAsGameBuildUnavailable()
    {
        RequireLoader();
        var registry = new BuildsRegistry(null, offline: true);
        using var fixture = new Fixture(DllZip(), "dll", "2026-8-18", "submerged.zip", [ManifestId],
            seedRegistry: false, buildsTransport: registry);

        var error = await Assert.ThrowsAsync<PhaketException>(() => fixture.Service.PrepareAsync("among-us",
            ModName, "1.0.0", "", new(fixture.GameRoot, "steam", false, "x86"),
            ct: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.GameBuildUnavailable, error.Error.Code);
        Assert.Equal(1, registry.Calls);
    }

    // The user accepts a preview, so it must list exactly what the plan extracts:
    // a full package is filtered by the recipe include list, not the whole archive.
    [Fact]
    public async Task FullPackagePreviewShowsOnlyIncludedEntries()
    {
        RequireLoader();
        using var fixture = new Fixture(FullZip(), "full", "2026-8-18", "tou.zip", [ManifestId]);

        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var preview = Assert.Single(preparation.Preview);
        Assert.Contains("BepInEx/plugins/TownOfUsMira.dll", preview.Files);
        Assert.DoesNotContain("Among Us_Data/sharedassets0.assets", preview.Files);
    }

    // W1 fallback: the live SusModder feed publishes no build id for older versions.
    // The installed depot manifest is the installation's own version evidence, so a
    // dll pinned to that label still plans instead of being permanently cut off.
    [Fact]
    public async Task DllVersionFallsBackToTheInstalledDepotManifest()
    {
        RequireLoader();
        const string depotManifest = "1298083356997541927";
        using var fixture = new Fixture(DllZip(), "dll", "2026-8-18", "submerged.zip", [depotManifest],
            installedBuildId: "11111111", installedDepotManifest: depotManifest);
        var preparation = await fixture.Service.PrepareAsync("among-us", ModName, "1.0.0", "",
            new(fixture.GameRoot, "steam", false, "x86"), ct: TestContext.Current.CancellationToken);

        var operation = Assert.Single(Assert.Single(preparation.Plans).Operations);
        Assert.Equal("archive.extract", operation.Kind);
        Assert.Equal("probe", preparation.GameVersionSource);
    }

    // W1 negative cache: a build id the registry cannot resolve must not force a full
    // sync on every preparation attempt.
    [Fact]
    public async Task UnknownBuildIdSyncsTheRegistryOnlyOnce()
    {
        RequireLoader();
        var registry = new BuildsRegistry(Fixture.Build([ManifestId]));
        using var fixture = new Fixture(DllZip(), "dll", "2026-8-18", "submerged.zip", [ManifestId],
            installedBuildId: "11111111", buildsTransport: registry);

        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<PhaketException>(() => fixture.Service.PrepareAsync("among-us", ModName,
                "1.0.0", "", new(fixture.GameRoot, "steam", false, "x86"),
                ct: TestContext.Current.CancellationToken));

        Assert.Equal(1, registry.Calls);
    }

    // A malformed dll recipe step without "to" must not crash the preview; valid
    // targets are still listed.
    [Fact]
    public void DllPreviewSkipsStepsWithoutTarget()
    {
        var recipe = RecipeParser.ParseYaml("""
            schemaVersion: omrl/v0alpha1
            id: test.preview
            version: 1.0.0
            target:
              game: among-us
              systems: [windows, linux]
            requires:
              capabilities: [file.copy]
            artifacts:
              - id: package
                source: package://package
            steps:
              - id: broken
                use: file.copy
                with:
                  from: package://package
              - id: ok
                use: file.copy
                with:
                  from: package://package
                  to: game://BepInEx/plugins/Mod.dll
            """);

        Assert.Equal(["BepInEx/plugins/Mod.dll"], ModInstallService.DllPreviewFiles(recipe));
    }
}
