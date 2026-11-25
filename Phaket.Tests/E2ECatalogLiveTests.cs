using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Nexus;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.Tests;

// Opt-in e2e against the real stage catalog and the real CDNs (Thunderstore, GitHub, SusModder):
//   PHAKET_E2E_LIVE=1 dotnet test Phaket.Tests --filter E2ECatalogLiveTests
// Every install goes to a sandbox fixture under %TEMP%\phaket-e2e-*; no real game installation
// is touched. GitHub source: BepInEx/BepInEx v5.4.23.6 win-x64 ZIP — an immutable release asset
// whose sha256 is pinned below, so a changed payload fails the run instead of passing it.
public sealed class E2ECatalogLiveTests
{
    private const string Stage = "https://stage.phaket.app";
    private const string GitHubAssetUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip";
    private const string GitHubAssetSha256 = "6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755";
    private const long GitHubAssetSize = 642076;

    private readonly ITestOutputHelper output;
    public E2ECatalogLiveTests(ITestOutputHelper output) => this.output = output;

    private static bool Live => Environment.GetEnvironmentVariable("PHAKET_E2E_LIVE") == "1";

    private sealed class Receipts : IReceiptTransport
    {
        public List<ModReceipt> Items { get; } = [];
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken)
        { Items.Add(receipt); return Task.CompletedTask; }
    }

    private static HttpClient Http() => new() { BaseAddress = new Uri(Stage), Timeout = TimeSpan.FromSeconds(90) };

    // Every file below the game root, with its hash: the uninstall must bring the tree back
    // to exactly this set (files only; stale empty directories are allowed).
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            result[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }

    private static void AssertSameTree(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (path, hash) in expected) Assert.Equal(hash, actual[path]);
    }

    [Fact]
    public async Task Thunderstore_rows_with_a_download_url_stay_installable_in_the_catalog()
    {
        if (!Live) { Assert.Skip("set PHAKET_E2E_LIVE=1 to read the real stage catalog"); return; }
        var ct = TestContext.Current.CancellationToken;
        using var http = Http();
        // One page is enough: the server writes the same artifact shape for every Thunderstore version.
        var body = await http.GetStringAsync("/mods/index?game=valheim&limit=500", ct);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var rows = document.RootElement.GetProperty("mods").EnumerateArray()
            .Select(row => System.Text.Json.JsonSerializer.Deserialize<ThunderstoreMod>(row.GetRawText(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!)
            .Where(mod => mod.Source == "thunderstore" && !string.IsNullOrWhiteSpace(mod.Latest.DownloadUrl))
            .ToArray();
        // Server writes artifacts[] for every Thunderstore version with "sha256": null (Thunderstore v1
        // publishes no hash). A hash-less artifact with a declared size is installable, so the catalog
        // must not report the Thunderstore catalog as unavailable. Rows the server itself marked
        // unavailable are excluded: their state is deliberate.
        var broken = rows.Where(mod => !string.Equals(mod.Latest.Availability, "unavailable", StringComparison.OrdinalIgnoreCase)
            && !mod.Latest.IsAvailable).ToArray();
        output.WriteLine($"thunderstore rows on one page: {rows.Length}, reporting unavailable: {broken.Length}");
        Assert.Empty(broken);
    }

    [Fact]
    public async Task Valheim_sync_is_valheim_only_and_a_real_thunderstore_mod_installs_then_uninstalls()
    {
        if (!Live) { Assert.Skip("set PHAKET_E2E_LIVE=1 to sync the stage catalog and download real mods"); return; }
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("phaket-e2e-valheim-").FullName;
        using var http = Http();
        using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
        var catalog = new ModCatalogClient(new HttpCatalogTransport(http), store);

        var clock = Stopwatch.StartNew();
        await catalog.SyncAsync("valheim", ct);
        clock.Stop();
        var mods = store.GetMods("valheim");
        output.WriteLine($"valheim sync: {mods.Count} mods in {clock.ElapsedMilliseconds} ms, revision {store.Revision}");
        Assert.True(mods.Count > 1000, $"expected the full valheim catalog, got {mods.Count}");
        Assert.All(mods, mod => Assert.Equal("valheim", mod.GameId));
        // The per-game key isolates the snapshot: a valheim sync must not publish among-us rows.
        Assert.Empty(store.GetMods("among-us"));

        // Small, dependency-free Thunderstore plugin: a few hundred KB at most.
        var chosen = mods
            .Where(mod => mod.Source == "thunderstore" && mod.Packaging is null && !mod.IsDeprecated
                && !string.IsNullOrWhiteSpace(mod.Latest.DownloadUrl)
                && mod.Latest.Dependencies.Count == 0 && mod.Latest.FileSize is >= 20_000 and < 2_000_000
                && !mod.FullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase))
            .OrderBy(mod => mod.Latest.FileSize).First();
        output.WriteLine($"chosen: {chosen.FullName} {chosen.Latest.VersionNumber} ({chosen.Latest.FileSize} B) IsAvailable={chosen.Latest.IsAvailable} {chosen.Latest.DownloadUrl}");

        var receipts = new Receipts();
        var service = new ModInstallService(root, catalog, new HttpPackageTransport(http), receipts,
            new ProfileStore(Path.Combine(root, "profiles")));
        var preparation = await service.PrepareAsync("valheim", chosen.FullName, chosen.Latest.VersionNumber,
            Stage, new InstallTarget("", "steam", true), ct: ct);

        Assert.Empty(preparation.Conflicts);
        var plan = Assert.Single(preparation.Plans);
        Assert.NotEmpty(plan.Operations);
        // Thunderstore v1 publishes no sha256: the plan must say so and the registry must not claim verification.
        Assert.Contains("THUNDERSTORE_HASH_UNVERIFIED", plan.Warnings);
        Assert.Contains(chosen.FullName, preparation.UnverifiedPackages ?? []);
        Assert.NotEmpty(preparation.Preview.SelectMany(preview => preview.Files));

        var gameRoot = Path.Combine(preparation.SandboxRoot, "game");
        var before = Snapshot(gameRoot);
        Assert.Single(before); // the sandbox fixture: only the stub game executable

        var outcome = await service.ExecuteAsync(preparation, true, ct: ct);
        Assert.True(outcome.Success, outcome.ErrorCode);
        // The plan's game:// targets are what the executor writes; the preview lists package entries.
        foreach (var target in plan.Operations.Select(operation => operation.TargetPath)
            .Where(path => path.StartsWith("game://", StringComparison.Ordinal))
            .Select(path => path["game://".Length..]))
            Assert.True(File.Exists(Path.Combine(gameRoot, target.Replace('/', Path.DirectorySeparatorChar))),
                $"missing installed file: {target}");
        Assert.Contains(receipts.Items, receipt => receipt.Result == "installed" && receipt.ModFullName == chosen.FullName);

        var registry = new InstallRegistry(Path.Combine(root, "installs.json"));
        var entry = Assert.Single(registry.ForGame("valheim"));
        Assert.Equal("unverified", entry.ArtifactIntegrity);
        Assert.Equal(gameRoot, entry.GameRoot);
        Assert.Contains("\"ArtifactIntegrity\": \"unverified\"", File.ReadAllText(Path.Combine(root, "installs.json")));
        output.WriteLine($"installs.json: {File.ReadAllText(Path.Combine(root, "installs.json")).ReplaceLineEndings(" ")}");
        output.WriteLine($"installed files: {string.Join(", ", Snapshot(gameRoot).Keys)}");

        // The uninstall the Mods screen performs: the ownership ledger restores the previous tree.
        var definition = GameDefinitions.LoadAll().First(game => game.Slug == "valheim");
        var context = new PlanExecutionContext(entry.GameRoot, entry.ProfileRoot, entry.StagingRoot,
            Path.Combine(root, "packages"), definition, entry.RecipeId, "install-profile", entry.StateRoot);
        new PlanExecutor(context).Uninstall(entry.RecipeId);
        registry.Remove(entry.RecipeId, entry.GameRoot);

        AssertSameTree(before, Snapshot(gameRoot));
        Assert.Empty(registry.ForGame("valheim"));
    }

    [Fact]
    public async Task Amongus_stage_publishes_susmodder_and_a_real_susmodder_dll_installs_then_uninstalls()
    {
        if (!Live) { Assert.Skip("set PHAKET_E2E_LIVE=1 to sync the stage catalog and download real mods"); return; }
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("phaket-e2e-amongus-").FullName;
        using var http = Http();
        using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
        var catalog = new ModCatalogClient(new HttpCatalogTransport(http), store);

        await catalog.SyncAsync("among-us", ct);
        var mods = store.GetMods("among-us");
        output.WriteLine($"among-us sync: {mods.Count} mods, susmodder {mods.Count(mod => mod.Source == "susmodder")}");
        Assert.All(mods, mod => Assert.Equal("among-us", mod.GameId));
        Assert.Contains(mods, mod => mod.Source == "susmodder");
        Assert.Contains(mods, mod => mod.Source == "thunderstore"); // Thunderstore rows as well

        // Smallest SusModder dll addon without a pinned game build: a sandbox has no appmanifest,
        // so a ToU addon plans against an unknown build instead of refusing.
        var chosen = mods
            .Where(mod => mod.Source == "susmodder" && mod.Packaging == "dll"
                && !string.IsNullOrWhiteSpace(mod.Latest.DownloadUrl) && mod.Latest.Sha256 is not null
                && mod.Latest.FileSize is > 0 and < 2_000_000
                && mod.Latest.AmongVersion is null or "ToU")
            .OrderBy(mod => mod.Latest.FileSize).First();
        output.WriteLine($"chosen: {chosen.FullName} {chosen.Latest.VersionNumber} ({chosen.Latest.FileSize} B) among={chosen.Latest.AmongVersion}");

        var receipts = new Receipts();
        var service = new ModInstallService(root, catalog, new HttpPackageTransport(http), receipts,
            new ProfileStore(Path.Combine(root, "profiles")));
        var preparation = await service.PrepareAsync("among-us", chosen.FullName, chosen.Latest.VersionNumber,
            Stage, new InstallTarget("", "steam", true, "x86"), ct: ct);

        Assert.Empty(preparation.Conflicts);
        var plan = Assert.Single(preparation.Plans);
        Assert.NotEmpty(plan.Operations);
        // SusModder rows carry a declared sha256, so nothing is unverified here.
        Assert.Null(preparation.UnverifiedPackages);
        Assert.DoesNotContain("THUNDERSTORE_HASH_UNVERIFIED", plan.Warnings);

        var gameRoot = Path.Combine(preparation.SandboxRoot, "game");
        var before = Snapshot(gameRoot);
        var outcome = await service.ExecuteAsync(preparation, true, ct: ct);
        Assert.True(outcome.Success, outcome.ErrorCode);
        foreach (var file in preparation.Preview.SelectMany(preview => preview.Files))
            Assert.True(File.Exists(Path.Combine(gameRoot, file.Replace('/', Path.DirectorySeparatorChar))),
                $"missing installed file: {file}");
        Assert.Contains(receipts.Items, receipt => receipt.Result == "installed" && receipt.ModFullName == chosen.FullName);

        var registry = new InstallRegistry(Path.Combine(root, "installs.json"));
        var entry = Assert.Single(registry.ForGame("among-us"));
        Assert.Null(entry.ArtifactIntegrity); // declared hash verified, not "unverified"
        output.WriteLine($"installed files: {string.Join(", ", Snapshot(gameRoot).Keys)}");

        var definition = GameDefinitions.LoadAll().First(game => game.Slug == "among-us");
        var context = new PlanExecutionContext(entry.GameRoot, entry.ProfileRoot, entry.StagingRoot,
            Path.Combine(root, "packages"), definition, entry.RecipeId, "install-profile", entry.StateRoot);
        new PlanExecutor(context).Uninstall(entry.RecipeId);
        registry.Remove(entry.RecipeId, entry.GameRoot);

        AssertSameTree(before, Snapshot(gameRoot));
        Assert.Empty(registry.ForGame("among-us"));
    }

    // The GitHub path of ModpackSourceInstaller: host allowlist, declared size, pinned sha256 and
    // the redirect to the asset CDN all run against the real github.com.
    [Fact]
    public async Task Github_release_asset_installs_with_pinned_hash_and_uninstalls()
    {
        if (!Live) { Assert.Skip("set PHAKET_E2E_LIVE=1 to download a real GitHub release asset"); return; }
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("phaket-e2e-github-").FullName;
        var gameRoot = Path.Combine(root, "game");
        var profileRoot = Path.Combine(root, "profile");
        var stagingRoot = Path.Combine(root, "staging");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
        Directory.CreateDirectory(profileRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(packageRoot);

        var definition = GameDefinitions.LoadAll().First(game => game.Slug == "valheim");
        var context = new ModpackSourceInstallContext("install-e2e", "profile-e2e", gameRoot, profileRoot,
            stagingRoot, packageRoot, Path.Combine(root, "installation-state"), definition,
            new PlanFacts("linux", "steam", null, OperationCatalog.Capabilities, _ => false, Arch: "x64"));
        var item = new ModpackSourceItem("github", "BepInEx_Pack_5.4.23.6", "v5.4.23.6",
            Repo: "BepInEx/BepInEx", Tag: "v5.4.23.6", AssetId: 670748471,
            AssetName: "BepInEx_win_x64_5.4.23.6.zip", DownloadUrl: GitHubAssetUrl,
            Size: GitHubAssetSize, Sha256: GitHubAssetSha256);

        var installer = new ModpackSourceInstaller(new HttpPackageTransport());
        var plan = await installer.PrepareAsync([item], context, ct);
        output.WriteLine($"plan: {plan.Items[0].Plan.Operations.Count} operations, warnings [{string.Join(", ", plan.Warnings)}]");
        Assert.True(plan.CanInstall);
        Assert.DoesNotContain("GITHUB_HASH_UNVERIFIED", plan.Warnings);
        Assert.All(plan.Items[0].Plan.Operations.Where(operation => operation.Sha256 is not null),
            operation => Assert.Equal(GitHubAssetSha256, operation.Sha256!.ToLowerInvariant()));

        var before = Snapshot(gameRoot);
        var outcome = installer.Execute(plan, true, cancellationToken: ct);
        Assert.True(outcome.Success, outcome.ErrorCode);
        var installed = Snapshot(gameRoot);
        output.WriteLine($"installed: {string.Join(", ", installed.Keys)}");
        // PAK-125: the loader ZIP is a game-relative tree, so it keeps its own layout.
        Assert.Contains("BepInEx/core/BepInEx.dll", installed.Keys);
        // Loader root files stay at the game root, not inside the BepInEx tree.
        Assert.Contains("winhttp.dll", installed.Keys);
        Assert.DoesNotContain(installed.Keys,
            path => path.StartsWith("BepInEx/plugins/BepInEx_Pack_5.4.23.6/BepInEx/", StringComparison.Ordinal));

        new PlanExecutor(plan.Items[0].ExecutionContext).Uninstall(plan.Items[0].Plan.RecipeRef.Id);
        AssertSameTree(before, Snapshot(gameRoot));
    }

    // Offline half of the Nexus flow (no user key needed): a manual archive picked in the UI is
    // staged, planned and installed, and nxm:// links parse for the browser hand-off.
    [Fact]
    public async Task Nexus_manual_archive_and_nxm_links_work_without_an_api_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("phaket-e2e-nexus-").FullName;
        var gameRoot = Path.Combine(root, "game");
        var profileRoot = Path.Combine(root, "profile");
        var stagingRoot = Path.Combine(root, "staging");
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
        Directory.CreateDirectory(profileRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(packageRoot);

        var archive = Path.Combine(root, "nexus-manual.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("plugins/NexusManual.dll").Open()))
            writer.Write("manual nexus dll");

        var definition = GameDefinitions.LoadAll().First(game => game.Slug == "valheim");
        var context = new ModpackSourceInstallContext("install-e2e", "profile-e2e", gameRoot, profileRoot,
            stagingRoot, packageRoot, Path.Combine(root, "installation-state"), definition,
            new PlanFacts("linux", "steam", null, OperationCatalog.Capabilities, _ => false, Arch: "x64"));
        var item = new ModpackSourceItem("nexus", "Nexus Manual Mod", "1.0.0",
            Url: "https://www.nexusmods.com/valheim/mods/3667", PinnedVersion: "1.0.0", LocalArchivePath: archive);

        Assert.True(NxmLink.TryParse("nxm://valheim/mods/3667/files/123456?key=abc&expires=1700000000", out var link));
        Assert.Equal(3667, link!.ModId);
        Assert.Equal(123456, link.FileId);
        Assert.Equal("abc", link.Key);
        Assert.True(NexusModReference.TryParse("https://www.nexusmods.com/valheim/mods/3667?tab=files", out var reference));
        Assert.Equal(3667, reference!.ModId);

        var installer = new ModpackSourceInstaller();
        var plan = await installer.PrepareAsync([item], context, ct);
        Assert.True(plan.CanInstall);

        var before = Snapshot(gameRoot);
        var outcome = installer.Execute(plan, true, cancellationToken: ct);
        Assert.True(outcome.Success, outcome.ErrorCode);
        Assert.Equal("manual nexus dll", File.ReadAllText(
            Path.Combine(gameRoot, "BepInEx", "plugins", "Nexus_Manual_Mod", "NexusManual.dll")));

        new PlanExecutor(plan.Items[0].ExecutionContext).Uninstall(plan.Items[0].Plan.RecipeRef.Id);
        AssertSameTree(before, Snapshot(gameRoot));
    }
}