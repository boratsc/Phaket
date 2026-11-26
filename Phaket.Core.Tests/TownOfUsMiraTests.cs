using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// Town of Us Mira: version-matrix recipes (mod release -> exact game version),
// modelled on how SUSModder pairs ModVersion with AmongVersion, but the vanilla
// comes from official tooling only (DepotDownloader / legendary), never from a
// private server and never from the user's Steam library (SUSModder rule).
public class TownOfUsMiraTests
{
    private static GameDefinition Definition() => GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private static Recipe Mira(string version) => RecipeParser.ParseYaml(
        File.ReadAllText(Path.Combine(Phaket.Core.Recipes.RecipeLibrary.Directory, version == "1.7.3" ? "among-us-tou-mira-1.7.3.yaml" : "among-us-tou-mira-1.6.3-beta2.yaml")));

    private sealed class FakeBaseGameSource : IBaseGameSource
    {
        public int Calls;
        public bool Fail;
        public string? LastStore;
        public string? LastVersion;

        public Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId, string targetDirectory, CancellationToken ct = default)
        {
            Calls++;
            LastStore = store;
            LastVersion = version;
            if (Fail) return Task.FromResult(BaseGamePrepareResult.Fail("BASE_GAME_TOOL_FAILED", ("detail", "tool unavailable")));
            Directory.CreateDirectory(Path.Combine(targetDirectory, "Among Us_Data"));
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us.exe"), $"vanilla-{store}-{version}");
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us_Data", "globalgamemanagers"), "ggm");
            File.WriteAllText(Path.Combine(targetDirectory, "UnityPlayer.dll"), "unity");
            return Task.FromResult(BaseGamePrepareResult.Ok);
        }
    }

    private static (string Game, string Profile, string Staging, string Packages, string State) Sandbox(string root)
    {
        var game = Path.Combine(root, "game");
        var profile = Path.Combine(root, "profile");
        var staging = Path.Combine(root, "staging");
        var packages = Path.Combine(root, "packages");
        var state = Path.Combine(root, "state");
        foreach (var d in new[] { game, profile, staging, packages, state }) Directory.CreateDirectory(d);
        return (game, profile, staging, packages, state);
    }

    // PAK-100: the vanilla cache is per game+version, outside every package directory.
    private static string Cache(string root) => Path.Combine(root, "vanilla-cache");

    private static void PutFakePacks(string packages)
    {
        ExecutorFixture.MakeZip(Path.Combine(packages, "loader-x86.zip"),
            ("BepInExPack_AmongUs/winhttp.dll", "doorstop-x86"u8.ToArray()),
            ("BepInExPack_AmongUs/doorstop_config.ini", "cfg"u8.ToArray()),
            ("BepInExPack_AmongUs/.doorstop_version", "4.3.0"u8.ToArray()),
            ("BepInExPack_AmongUs/changelog.txt", "notes"u8.ToArray()),
            ("BepInExPack_AmongUs/dotnet/coreclr.dll", "coreclr"u8.ToArray()),
            ("BepInExPack_AmongUs/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "il2cpp-core"u8.ToArray()));
        ExecutorFixture.MakeZip(Path.Combine(packages, "loader-x64.zip"),
            ("winhttp.dll", "doorstop-x64"u8.ToArray()),
            ("doorstop_config.ini", "cfg"u8.ToArray()),
            (".doorstop_version", "4.3.0"u8.ToArray()),
            ("changelog.txt", "notes"u8.ToArray()),
            ("dotnet/coreclr.dll", "coreclr"u8.ToArray()),
            ("BepInEx/core/BepInEx.Unity.IL2CPP.dll", "il2cpp-core"u8.ToArray()));
        // Full mod tree like the real packs: plugins + configs + its doorstop layer.
        foreach (var (name, doorstop) in new[] { ("tou-mod-steam.zip", "doorstop-x86"), ("tou-mod-epic.zip", "doorstop-x64") })
            ExecutorFixture.MakeZip(Path.Combine(packages, name),
                ("TouMira/winhttp.dll", doorstop == "doorstop-x86" ? "tou-doorstop-x86"u8.ToArray() : "tou-doorstop-x64"u8.ToArray()),
                ("TouMira/doorstop_config.ini", "tou-cfg"u8.ToArray()),
                ("TouMira/.doorstop_version", "4.3.0"u8.ToArray()),
                ("TouMira/changelog.txt", "tou-notes"u8.ToArray()),
                ("TouMira/dotnet/coreclr.dll", "tou-coreclr"u8.ToArray()),
                ("TouMira/BepInEx/plugins/TownOfUsMira.dll", "tou-plugin"u8.ToArray()),
                ("TouMira/BepInEx/plugins/MiraAPI.dll", "mira-api"u8.ToArray()),
                ("TouMira/BepInEx/plugins/Reactor.dll", "reactor"u8.ToArray()),
                ("TouMira/BepInEx/config/auavengers.tou.mira.cfg", "tou-cfg-file"u8.ToArray()));
    }

    // Local-package variant of the shipped recipe (same steps, package:// sources).
    // Digests are dropped: the fixture zips stand in for the real artifacts'
    // STRUCTURE, not their bytes; the shipped YAML keeps the real digests.
    private static Recipe LocalVariant(Recipe recipe) => recipe with
    {
        Artifacts = recipe.Artifacts
            .Select(a => a with { Source = $"package://{a.Id}.zip", Sha256 = null, SizeBytes = null })
            .ToArray(),
        Verify = recipe.Verify
            .Select(v => v with
            {
                With = v.With.Where(kv => kv.Key != "sha256")
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
            })
            .ToArray(),
    };

    [Fact]
    public void BothReleasesParseValidateAndUseAllowedSources()
    {
        var definition = Definition();
        foreach (var version in new[] { "1.7.3", "1.6.3-beta2" })
        {
            var recipe = Mira(version);
            var element = JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement;
            Assert.Empty(RecipeSchema.Validate(element));
            Assert.Empty(RecipeValidator.Validate(recipe, definition));
            Assert.All(recipe.Artifacts, a => Assert.True(SourcePolicy.IsAllowedHttpsSource(a.Source), a.Source));
        }
        // The version matrix is data: each release pins its own game build.
        Assert.Equal("=2026.8.18", Mira("1.7.3").Target.Constraints!.GameVersion);
        Assert.Equal("=2026.6.5", Mira("1.6.3-beta2").Target.Constraints!.GameVersion);
    }

    [Fact]
    public void PlanSelectsStoreSpecificProvisionAndLoaderSteps()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-plan-").FullName;
        try
        {
            var (game, profile, staging, packages, _) = Sandbox(root);
            var recipe = Mira("1.7.3");
            foreach (var (store, expectedProvision, expectedLoader, expectedMod) in new[]
            {
                ("steam", "provision-vanilla-steam", "ensure-bepinex-steam", "install-mod-tree-steam"),
                
            })
            {
                var facts = new PlanFacts("linux", store, "2026.8.18", OperationCatalog.Capabilities, _ => false,
                    new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86", BuildId: 24302054);
                var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, Definition(), facts));
                Assert.Contains(expectedProvision, plan.Operations.Select(o => o.OpId));
                Assert.Contains(expectedLoader, plan.Operations.Select(o => o.OpId));
                Assert.Contains(expectedMod, plan.Operations.Select(o => o.OpId));
                Assert.DoesNotContain(store == "steam" ? "provision-vanilla-epic" : "provision-vanilla-steam",
                    plan.Operations.Select(o => o.OpId));
                Assert.True(plan.RequiresDownload); // no cache yet
                // Provision, loader and mod extract all merge into the game root;
                // per-file sharing is ownership-layered at execute time, so the
                // plan reports no conflicts for a single-recipe install.
                Assert.Empty(plan.Conflicts);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GameVersionPinIsEnforcedPerRelease()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-pin-").FullName;
        try
        {
            var (game, profile, staging, packages, _) = Sandbox(root);
            foreach (var (recipe, okVersion, badVersion) in new[]
            {
                (Mira("1.7.3"), "2026.8.18", "2026.6.5"),
                (Mira("1.6.3-beta2"), "2026.6.5", "2026.8.18"),
            })
            {
                var ok = new PlanFacts("linux", "steam", okVersion, OperationCatalog.Capabilities, _ => false, Arch: "x86", BuildId: 24302054);
                OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, Definition(), ok));
                var bad = new PlanFacts("linux", "steam", badVersion, OperationCatalog.Capabilities, _ => false, Arch: "x86", BuildId: 24302054);
                var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe,
                    new PlannerContext("i", "p", game, profile, staging, packages, Definition(), bad)));
                Assert.Equal(ErrorCodes.UnsupportedGameVersion, ex.Error.Code);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProvisionUsesOfficialToolingArgumentsAndPerVersionCache()
    {
        Assert.Equal(["-app", "945360", "-depot", "945361", "-dir", "/t", "-manifest", "12345"],
            BaseGameToolArguments.DepotDownloader("/t", "12345"));
        Assert.Equal(["install", "963137e4c29d4c79a81323b8fab03a40", "-y", "--manifest",
            "963137e4c29d4c79a81323b8fab03a40_2026.8.18.manifest", "--base-path", "/t"],
            BaseGameToolArguments.Legendary("963137e4c29d4c79a81323b8fab03a40",
                "963137e4c29d4c79a81323b8fab03a40_2026.8.18.manifest", "/t"));
        Assert.Throws<PhaketException>(() => BaseGameToolArguments.Legendary("app", null!, "/t"));
        var dir = VanillaCache.DirectoryFor("/cache", "among-us", "steam", "2026.8.18");
        Assert.Equal(Path.Combine("/cache", "among-us", "steam", "2026.8.18", "2026.8.18"), dir);
        Assert.Throws<PhaketException>(() => VanillaCache.DirectoryFor("/cache", "among-us", "steam", "../escape"));
        // An empty root would put the cache in the working directory instead of app data.
        Assert.Throws<PhaketException>(() => VanillaCache.DirectoryFor(" ", "among-us", "steam", "2026.8.18"));
    }

    [Fact]
    public void FullSteamPipeline_VanillaProvisionedOnce_CacheReused_UninstallKeepsVanilla()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-e2e-").FullName;
        try
        {
            var (game, profile, staging, packages, state) = Sandbox(root);
            PutFakePacks(packages);
            var source = new FakeBaseGameSource();
            var definition = Definition();
            var recipe = LocalVariant(Mira("1.7.3"));

            OperationPlan Plan() => OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, definition,
                new PlanFacts("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false,
                    new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86", BuildId: 24302054),
                VanillaCacheRoot: Cache(root)));

            var plan1 = Plan();
            Assert.True(plan1.RequiresDownload);
            var executor = new PlanExecutor(new PlanExecutionContext(game, profile, staging, packages, definition, recipe.Id, "p", state, source,
                VanillaCacheRoot: Cache(root)));
            var journal = new InstallOrchestrator(executor).Run(plan1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(JournalState.Completed, journal.State);
            Assert.Equal(1, source.Calls);
            Assert.Equal("steam", source.LastStore);
            Assert.Equal("2026.8.18", source.LastVersion);

            // Vanilla + loader + FULL mod tree in place; marker stays out of the game tree.
            Assert.Equal("vanilla-steam-2026.8.18", File.ReadAllText(Path.Combine(game, "Among Us.exe")));
            Assert.True(File.Exists(Path.Combine(game, "Among Us_Data", "globalgamemanagers")));
            // The mod pack's doorstop layer wins over the loader's (last-wins conflict).
            Assert.Equal("tou-doorstop-x86", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
            Assert.Equal("tou-plugin", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "TownOfUsMira.dll")));
            Assert.Equal("mira-api", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "MiraAPI.dll")));
            Assert.Equal("reactor", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "Reactor.dll")));
            Assert.Equal("tou-cfg-file", File.ReadAllText(Path.Combine(game, "BepInEx", "config", "auavengers.tou.mira.cfg")));
            Assert.False(File.Exists(Path.Combine(game, VanillaCache.MarkerFileName)));
            Assert.True(executor.VerifyPlan(plan1).Succeeded);

            // Second install of the SAME game version: cache hit, no download.
            var plan2 = Plan();
            Assert.False(plan2.RequiresDownload);

            // Uninstalling the recipe removes everything IT owns — including the
            // loader it installed — while the provisioned base game stays.
            executor.Uninstall(recipe.Id);
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "plugins", "TownOfUsMira.dll")));
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "plugins", "MiraAPI.dll")));
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "config", "auavengers.tou.mira.cfg")));
            Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll")));
            Assert.Equal("vanilla-steam-2026.8.18", File.ReadAllText(Path.Combine(game, "Among Us.exe")));
            Assert.True(File.Exists(Path.Combine(game, "UnityPlayer.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingSourceFailsCleanlyAndRollbackKeepsGameTreeIntact()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-fail-").FullName;
        try
        {
            var (game, profile, staging, packages, state) = Sandbox(root);
            PutFakePacks(packages);
            var source = new FakeBaseGameSource { Fail = true };
            var definition = Definition();
            var recipe = LocalVariant(Mira("1.7.3"));
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, definition,
                new PlanFacts("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false,
                    new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86", BuildId: 24302054)));
            var executor = new PlanExecutor(new PlanExecutionContext(game, profile, staging, packages, definition, recipe.Id, "p", state, source));
            var ex = Assert.Throws<PhaketException>(() => new InstallOrchestrator(executor).Run(plan, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.BaseGameToolFailed, ex.Error.Code);
            Assert.Contains("tool unavailable", ex.Error.Parameters["detail"]);

            var journal = PlanExecutor.LoadJournal(profile)!;
            executor.Rollback(journal);
            Assert.Empty(Directory.GetFileSystemEntries(game));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HostPlacedRemoteArtifactsClearTheDownloadFlag()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-hosted-").FullName;
        try
        {
            var (game, profile, staging, packages, _) = Sandbox(root);
            PutFakePacks(packages); // files named exactly like the artifact ids
            // Shipped recipe keeps its https sources; digests are recomputed for
            // the stand-in files (a host downloader would place real bytes here).
            var recipe = Mira("1.7.3") with
            {
                Artifacts = Mira("1.7.3").Artifacts.Select(a => a with
                {
                    Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(Path.Combine(packages, a.Id + ".zip")))),
                }).ToArray(),
            };
            PlanFacts Facts() => new("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86", BuildId: 24302054);

            // No vanilla cache yet -> the plan still reports a pending download.
            Assert.True(OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, Definition(), Facts(),
                VanillaCacheRoot: Cache(root))).RequiresDownload);

            // Seed the per-version vanilla cache -> nothing left to download.
            var cache = VanillaCache.DirectoryFor(Cache(root), "among-us", "steam", "2026.8.18");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "Among Us.exe"), "vanilla");
            VanillaCache.WriteMarker(cache, new VanillaCacheMarker("steam", "2026.8.18", null, "test", DateTimeOffset.UtcNow));
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, Definition(), Facts(),
                VanillaCacheRoot: Cache(root)));
            Assert.False(plan.RequiresDownload);
            Assert.Contains("provision-vanilla-steam", plan.Operations.Select(o => o.OpId));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProvisionStoreMustMatchInstallationStore()
    {
        var root = Directory.CreateTempSubdirectory("phaket-tou-store-").FullName;
        try
        {
            var (game, profile, staging, packages, _) = Sandbox(root);
            var recipe = Mira("1.7.3"); // steam provision step is unconditional for steam edition
            var facts = new PlanFacts("linux", "itch", "2026.8.18", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86", BuildId: 24302054);
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, Definition(), facts));
            // epic facts select the epic provision step; the steam one is skipped.
            Assert.DoesNotContain("provision-vanilla-steam", plan.Operations.Select(o => o.OpId));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

// "Latest" selection: the UI offers it, the planner resolves it ONCE from the
// build catalog, and the executor only ever sees a concrete version.
public class LatestVersionSelectionTests
{
    private static GameDefinition Definition() => GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private static Recipe LatestRecipe() => new()
    {
        SchemaVersion = "omrl/v0alpha1", Id = "test.latest", Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"], Editions = [new EditionTarget { Store = "steam" }] },
        Requires = new RecipeRequires { Capabilities = ["game.provision"] },
        Steps = [new RecipeStep { Id = "provision", Use = "game.provision",
            With = ExecutorFixture.Params(("store", "steam"), ("version", "latest")) }],
    };

    [Fact]
    public void LatestResolvesFromCatalogIntoConcretePlan()
    {
        var root = Directory.CreateTempSubdirectory("phaket-latest-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            foreach (var d in new[] { game, Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages") })
                Directory.CreateDirectory(d);
            var facts = new PlanFacts("linux", "steam", "2026.9.29", OperationCatalog.Capabilities, _ => false);
            var plan = OperationPlanner.Plan(LatestRecipe(), new PlannerContext("i", "p", game,
                Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages"),
                Definition(), facts, LatestGameVersion: _ => "2026.9.29"));
            var provision = plan.Operations.Single(o => o.Kind == "game.provision");
            Assert.Equal("2026.9.29", provision.Parameters!["version"]); // resolved, not "latest"
            Assert.Equal("vanilla://steam/2026.9.29", provision.SourceRef);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LatestWithoutCatalogFailsWithClearError()
    {
        var root = Directory.CreateTempSubdirectory("phaket-latest-none-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            foreach (var d in new[] { game, Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages") })
                Directory.CreateDirectory(d);
            var facts = new PlanFacts("linux", "steam", "2026.9.29", OperationCatalog.Capabilities, _ => false);
            var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(LatestRecipe(),
                new PlannerContext("i", "p", game, Path.Combine(root, "profile"), Path.Combine(root, "staging"),
                    Path.Combine(root, "packages"), Definition(), facts)));
            Assert.Equal(ErrorCodes.GameBuildUnavailable, ex.Error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
