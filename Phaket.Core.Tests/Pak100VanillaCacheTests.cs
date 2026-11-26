using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-100: the vanilla cache is one per user, game, store and version instead of one per
// mod package, so two full packages no longer download the same base game twice. The
// trust rules stay: exact store+version+manifest is not enough — the content must still
// hash to what was fetched, or the entry is discarded and fetched again.
public sealed class Pak100VanillaCacheTests
{
    private static GameDefinition AmongUs => GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private sealed class CountedSource : IBaseGameSource
    {
        public int Calls;
        public string? LastStore;
        public string? FirstVersion;
        public string LastVersion = "";
        public string? LastManifest;

        public Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId,
            string targetDirectory, CancellationToken ct = default)
        {
            Calls++;
            LastStore = store;
            FirstVersion ??= version;
            LastVersion = version;
            LastManifest = manifestId;
            Directory.CreateDirectory(Path.Combine(targetDirectory, "Among Us_Data"));
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us.exe"), $"vanilla-{store}-{version}");
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us_Data", "sharedassets0.assets"), "assets");
            return Task.FromResult(BaseGamePrepareResult.Ok);
        }
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        return buffer.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("pak100-cache-").FullName;
        public string Cache => Path.Combine(Root, "cache");
        // The same loader/mod structures the Town of Us pipeline test uses.
        public byte[] Loader { get; } = Zip(
            ("BepInExPack_AmongUs/winhttp.dll", "doorstop"u8.ToArray()),
            ("BepInExPack_AmongUs/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "core"u8.ToArray()));
        public byte[] Tou { get; } = Zip(
            ("TouMira/BepInEx/plugins/TownOfUsMira.dll", "tou-plugin"u8.ToArray()),
            ("TouMira/BepInEx/plugins/MiraAPI.dll", "mira-api"u8.ToArray()));
        public CountedSource Source { get; } = new();

        public Fixture()
        {
            foreach (var name in new[] { "game-a", "game-b" })
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Root, name)).FullName, "Among Us.exe"), "sandbox");
        }

        // One "mod package": its own package/staging/profile/state roots, like two full mods.
        public ExecutionJournal Provision(string name, string version)
        {
            var recipe = Recipe(version);
            var package = PackageFor(recipe, name);
            var profile = Directory.CreateDirectory(Path.Combine(Root, "profiles", name)).FullName;
            var staging = Directory.CreateDirectory(Path.Combine(Root, "staging", name)).FullName;
            var game = Path.GetFullPath(Path.Combine(Root, name));
            var facts = Facts(version);
            var planner = new PlannerContext("i", "p", game, profile, staging, package, AmongUs, facts,
                VanillaCacheRoot: Cache);
            var plan = OperationPlanner.Plan(recipe, planner);
            var context = new PlanExecutionContext(game, profile, staging, package, AmongUs, recipe.Id, "p",
                Path.Combine(Root, "state", name), Source, VanillaCacheRoot: Cache);
            var executor = new PlanExecutor(context);
            return executor.Execute(plan, executor.Approve(plan));
        }

        // The shipped full-package recipe: provisions the pinned vanilla and merges its tree.
        // Package sources and digests are dropped: the fixture zips stand in for the real
        // artifacts' STRUCTURE, not their bytes (same as the Town of Us pipeline test).
        public static Recipe Recipe(string version)
        {
            var shipped = RecipeParser.ParseYaml(File.ReadAllText(
                Path.Combine(Phaket.Core.Recipes.RecipeLibrary.Directory, "among-us-tou-mira-1.7.3.yaml")));
            return shipped with
            {
                Target = shipped.Target with { Constraints = new TargetConstraints { GameVersion = "=" + version } },
                // The other game version has its own provision target.
                Steps = shipped.Steps.Select(step => step.Use != "game.provision" ? step : step with
                {
                    With = step.With.Select(kv => kv.Key == "version"
                        ? new KeyValuePair<string, System.Text.Json.JsonElement>(kv.Key,
                            System.Text.Json.JsonSerializer.SerializeToElement(version))
                        : kv).ToDictionary(kv => kv.Key, kv => kv.Value),
                }).ToArray(),
                Artifacts = shipped.Artifacts
                    .Select(a => a with { Source = $"package://{a.Id}.zip", Sha256 = null, SizeBytes = null }).ToArray(),
                Verify = shipped.Verify.Select(v => v with
                {
                    With = v.With.Where(kv => kv.Key != "sha256").ToDictionary(kv => kv.Key, kv => kv.Value),
                }).ToArray(),
            };
        }

        public static PlanFacts Facts(string version) => new("linux", "steam", version, OperationCatalog.Capabilities,
            _ => false, Arch: "x86", BuildId: 24302054);

        // The planner checks the archives at plan time: they stand in for the downloaded ones.
        public string PackageFor(Recipe recipe, string name)
        {
            var package = Package(name);
            foreach (var artifact in recipe.Artifacts)
                File.WriteAllBytes(Path.Combine(package, artifact.Id + ".zip"),
                    artifact.Id.StartsWith("loader", StringComparison.Ordinal) ? Loader : Tou);
            return package;
        }

        public string Package(string name) => Directory.CreateDirectory(Path.Combine(Root, "pkgs", name)).FullName;

        public string Entry(string version) => VanillaCache.DirectoryFor(Cache, AmongUs.Slug, "steam", version);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    // The cache decision on its own: a package with local artifacts never needs a network
    // download, so RequiresDownload here reflects only the shared vanilla entry.
    private static OperationPlan Plan(Fixture fixture, string version, string package) => OperationPlanner.Plan(
        Fixture.Recipe(version), new PlannerContext("i", "p", Path.Combine(fixture.Root, package),
            Path.Combine(fixture.Root, "profiles", package), Path.Combine(fixture.Root, "staging", package),
            fixture.Package(package), AmongUs, Fixture.Facts(version), VanillaCacheRoot: fixture.Cache));

    [Fact]
    public void SecondPackageOfTheSameGameAndVersionReusesTheSharedCache()
    {
        using var fixture = new Fixture();
        fixture.PackageFor(Fixture.Recipe("2026.8.18"), "game-b");
        Assert.True(Plan(fixture, "2026.8.18", "game-b").RequiresDownload);
        Assert.Equal(JournalState.Completed, fixture.Provision("game-a", "2026.8.18").State);
        Assert.False(Plan(fixture, "2026.8.18", "game-b").RequiresDownload);

        Assert.Equal(JournalState.Completed, fixture.Provision("game-b", "2026.8.18").State);
        Assert.Equal(1, fixture.Source.Calls); // the second package reached no source at all
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "pkgs", "game-b", "vanilla")));
        Assert.Equal("vanilla-steam-2026.8.18", File.ReadAllText(Path.Combine(fixture.Root, "game-b", "Among Us.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.Entry("2026.8.18"), VanillaCache.HashFileName))); // hash sidecar written
        Assert.False(File.Exists(Path.Combine(fixture.Root, "game-b", VanillaCache.MarkerFileName)));
    }

    [Fact]
    public void DifferentVersionsAreDifferentEntries()
    {
        using var fixture = new Fixture();
        fixture.PackageFor(Fixture.Recipe("2026.8.18"), "game-a");
        fixture.Provision("game-a", "2026.8.18");
        // The other version has its own entry: the cached one answers nothing for it. Both
        // packages hold the same bytes, so this is a cache decision, not a package one.
        fixture.PackageFor(Fixture.Recipe("2026.9.29"), "game-b");
        Assert.True(Plan(fixture, "2026.9.29", "game-b").RequiresDownload);

        Assert.Equal(JournalState.Completed, fixture.Provision("game-b", "2026.9.29").State);
        Assert.Equal(2, fixture.Source.Calls);
        Assert.Equal("2026.9.29", fixture.Source.LastVersion);
        Assert.True(Directory.Exists(fixture.Entry("2026.9.29")));
        Assert.Equal("vanilla-steam-2026.9.29", File.ReadAllText(Path.Combine(fixture.Root, "game-b", "Among Us.exe")));
    }

    [Fact]
    public void CorruptedEntryIsRefusedAndFetchedAgain()
    {
        using var fixture = new Fixture();
        fixture.Provision("game-a", "2026.8.18");
        File.WriteAllText(Path.Combine(fixture.Entry("2026.8.18"), "Among Us.exe"), "tampered");
        fixture.PackageFor(Fixture.Recipe("2026.8.18"), "game-b");

        // The cache exists and its marker matches, only the content changed: it must read as
        // "not cached" so the download replaces it instead of shipping the tampered vanilla.
        Assert.True(Plan(fixture, "2026.8.18", "game-b").RequiresDownload);
        Assert.Equal(JournalState.Completed, fixture.Provision("game-b", "2026.8.18").State);

        Assert.Equal(2, fixture.Source.Calls);
        Assert.Equal("vanilla-steam-2026.8.18", File.ReadAllText(Path.Combine(fixture.Root, "game-b", "Among Us.exe")));
        Assert.Equal("vanilla-steam-2026.8.18", File.ReadAllText(Path.Combine(fixture.Entry("2026.8.18"), "Among Us.exe")));
    }

    [Fact]
    public async Task ParallelPackagesFetchTheSharedEntryOnce()
    {
        using var fixture = new Fixture();
        var journals = await Task.WhenAll(
            Task.Run(() => fixture.Provision("game-a", "2026.8.18")),
            Task.Run(() => fixture.Provision("game-b", "2026.8.18")));

        Assert.All(journals, journal => Assert.Equal(JournalState.Completed, journal.State));
        Assert.Equal(1, fixture.Source.Calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Cache, "among-us", "steam", "2026.8.18"), "*.partial-*"));
    }
}
