using System.Diagnostics;
using Phaket.Core.Execution;
using Phaket.Core.Launch;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-146 part B: a package's saves/config are isolated per OS capability, the redirect only
// reaches the package tree (never vanilla, never a launcher-handled steam:// launch), and the
// first isolated launch copies the user's saves once without ever writing the source.
public sealed class Pak146SavesIsolationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak146b-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static SavesIsolationMode Env(params string[] keys) => new(SavesIsolationMode.EnvRedirect, keys);
    private static SavesIsolationMode Shared(string reason) => new(SavesIsolationMode.Shared, Reason: reason);

    private static GameDefinition Definition(SavesIsolation? isolation, params string[] savePaths) => new(
        "among-us", ["BepInEx/**"], MainExecutable: "Among Us.exe",
        SavePaths: savePaths.Length == 0 ? null : savePaths, SavesIsolation: isolation);

    // Definition YAML as shipped and as the server would seed it; version stays v0alpha1 and
    // existing definitions without the field still load (unknown, never a promise).
    [Fact]
    public void DefinitionCarriesIsolationMatrixAndDefaultsToUnknown()
    {
        var yaml = """
            schemaVersion: game-definition/v0alpha1
            slug: among-us
            writableAreas: [ "BepInEx/**" ]
            savesIsolation:
              os:
                linux: { mode: env, env: [ HOME, XDG_CONFIG_HOME, XDG_DATA_HOME ] }
                windows: { mode: shared, reason: unity-locallow-known-folder }
            """;
        var loaded = GameDefinitionLoader.LoadYaml(yaml);
        Assert.Equal(SavesIsolationMode.EnvRedirect, loaded.SavesIsolationFor("linux").Mode);
        Assert.Equal(["HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME"], loaded.SavesIsolationFor("linux").Env!);
        Assert.Equal(SavesIsolationMode.Shared, loaded.SavesIsolationFor("windows").Mode);
        Assert.Equal(SavesIsolationMode.Unknown, loaded.SavesIsolationFor("macos").Mode);

        var legacy = GameDefinitionLoader.LoadYaml("""
            schemaVersion: game-definition/v0alpha1
            slug: valheim
            writableAreas: [ "BepInEx/**" ]
            """);
        Assert.Equal(SavesIsolationMode.Unknown, legacy.SavesIsolationFor("linux").Mode);
    }

    // A catalog entry cannot redirect PATH-like variables or claim an unknown mode.
    [Theory]
    [InlineData("mode: env, env: [ PATH ]")]
    [InlineData("mode: env")]
    [InlineData("mode: magic")]
    public void UnsafeOrIncompleteIsolationIsRefused(string linuxMode)
    {
        var yaml = $$"""
            schemaVersion: game-definition/v0alpha1
            slug: among-us
            writableAreas: [ "BepInEx/**" ]
            savesIsolation:
              os:
                linux: { {{linuxMode}} }
            """;
        Assert.Throws<PhaketException>(() => GameDefinitionLoader.LoadYaml(yaml));
    }

    // Shipped definitions: both Unity titles redirect on Linux, both share on Windows.
    [Theory]
    [InlineData("among-us.yaml")]
    [InlineData("valheim.yaml")]
    public void ShippedUnityDefinitionsMatchTheVerifiedMatrix(string file)
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", file));
        var definition = GameDefinitionLoader.LoadYaml(yaml);
        Assert.Equal(SavesIsolationMode.EnvRedirect, definition.SavesIsolationFor("linux").Mode);
        Assert.Equal(SavesIsolationMode.Shared, definition.SavesIsolationFor("windows").Mode);
        Assert.NotEmpty(definition.SavePaths!);
        // Where the game really keeps saves: Linux only (Windows LocalLow cannot be redirected).
        Assert.NotEmpty(definition.SaveSourcesFor("linux")!);
        Assert.Empty(definition.SaveSourcesFor("windows")!);
        Assert.All(definition.SaveSourcesFor("linux")!, path => Assert.StartsWith("~/", path));
    }

    [Fact]
    public void PackageLaunchOnLinuxRedirectsIntoThePackageTree()
    {
        var package = Path.Combine(root, "package");
        var mode = Env("HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME");
        var plan = SavesIsolationPlanner.Plan("linux", mode, package);

        Assert.Equal(SavesIsolationMode.EnvRedirect, plan.Mode);
        Assert.False(plan.SavesShared);
        // Linux saves are per-file under the config dir (Unity: .config/unity3d), the data dir only
        // holds caches; seeding writes where the game reads, so those keys share "config".
        var home = Path.Combine(Path.GetFullPath(package), SavesIsolationPlanner.HomeDirectoryName);
        Assert.Equal(Path.Combine(home, "config"), plan.Environment["HOME"]);
        Assert.Equal(Path.Combine(home, "config"), plan.Environment["XDG_CONFIG_HOME"]);
        Assert.Equal(Path.Combine(home, "config"), plan.Environment["XDG_DATA_HOME"]);
    }

    // Vanilla (no package tree) and every launcher-handled launch keep the user's profile.
    [Fact]
    public void VanillaLaunchSharesTheProfile()
    {
        var plan = SavesIsolationPlanner.Plan("linux", Env("HOME"), packageRoot: null);
        Assert.True(plan.SavesShared);
        Assert.Empty(plan.Environment);
    }

    [Fact]
    public void WindowsUnityPackageReportsSavesShared()
    {
        var plan = SavesIsolationPlanner.Plan("windows", Shared("unity-locallow-known-folder"), Path.Combine(root, "package"));
        Assert.True(plan.SavesShared);
        Assert.Equal("unity-locallow-known-folder", plan.Reason);
        Assert.Empty(plan.Environment);
    }

    // Launch of a package with EnvRedirect really hands the child the overrides: a real child
    // process on the current OS prints the redirected variable, so nothing is merely mocked.
    [Fact]
    public void PackageLaunchCarriesTheOverridesIntoTheChildEnvironment()
    {
        var packageDir = Path.Combine(root, "package");
        var definition = Definition(new SavesIsolation(new Dictionary<string, SavesIsolationMode>
        {
            ["linux"] = Env("HOME"),
            ["windows"] = Env("HOME"),
        }));
        var isolation = SavesIsolationPlanner.Plan(OperatingSystem.IsWindows() ? "windows" : "linux",
            definition.SavesIsolationFor(OperatingSystem.IsWindows() ? "windows" : "linux"), packageDir);
        Assert.False(isolation.SavesShared);

        // One variable of the plan, pointed at a probe path the child prints back.
        var probeDir = Path.Combine(root, "probe value");
        var key = isolation.Environment.Keys.First();
        var environment = new Dictionary<string, string> { [key] = probeDir };

        var (executable, arguments) = Probe(key);
        var info = new ProcessStartInfo
        {
            FileName = executable, Arguments = arguments, UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (var (k, v) in environment) info.Environment[k] = v;
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEnd();
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.Contains(probeDir, output, StringComparison.Ordinal);
    }

    // A child that prints one environment variable; both platform shells, no extra binary.
    private static (string Executable, string Arguments) Probe(string key) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c echo %{key}%")
            : ("/bin/sh", $"-c \"echo ${key}\"");

    private string SaveTree(string name, params (string Relative, string Content)[] files)
    {
        var save = Path.Combine(root, name);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(save, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return save;
    }

    [Fact]
    public void FirstLaunchSeedsSavesOnceAndNeverWritesTheSource()
    {
        var source = SaveTree("saves", ("Among Us/playerPrefs", "vanilla"), ("Among Us/sub/gameData", "data"));
        var home = Path.Combine(root, "package", SavesIsolationPlanner.HomeDirectoryName, "data");

        var first = PackageSavesSeed.Seed([home], [source], "linux");
        Assert.Equal(PackageSavesSeed.SeedStatus.Seeded, first.Status);
        Assert.Equal(2, first.FilesCopied);
        Assert.Equal("vanilla", File.ReadAllText(Path.Combine(home, "Among Us/playerPrefs")));

        // The game then advances its own copy; a second launch must not overwrite it.
        File.WriteAllText(Path.Combine(home, "Among Us/playerPrefs"), "package-progress");
        var second = PackageSavesSeed.Seed([home], [source], "linux");
        Assert.Equal(PackageSavesSeed.SeedStatus.AlreadySeeded, second.Status);
        Assert.Equal("package-progress", File.ReadAllText(Path.Combine(home, "Among Us/playerPrefs")));
        Assert.Equal("vanilla", File.ReadAllText(Path.Combine(source, "Among Us/playerPrefs")));
    }

    // Copy-if-absent also applies to a file the package wrote before seeding ran.
    [Fact]
    public void SeedingDoesNotReplaceAFileThePackageAlreadyHas()
    {
        var source = SaveTree("saves", ("playerPrefs", "vanilla"));
        var home = Path.Combine(root, "package", ".phaket-home", "data");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "playerPrefs"), "package-progress");

        var result = PackageSavesSeed.Seed([home], [source], "linux");
        Assert.Equal(PackageSavesSeed.SeedStatus.Seeded, result.Status);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal("package-progress", File.ReadAllText(Path.Combine(home, "playerPrefs")));
    }

    // R3d #5: a file that cannot be copied means no marker (the package would look seeded for
    // good); the next launch fills the gap before marking. One source file makes the failure
    // deterministic regardless of the order in which the file system enumerates entries.
    [Fact]
    public void PartialSeedingLeavesNoMarkerAndTheNextLaunchCompletesIt()
    {
        var source = SaveTree("saves", ("a.dat", "one"));
        var home = Path.Combine(root, "package", ".phaket-home", "data");
        // A directory where the file must land: copying over it fails on every OS.
        var blocker = Path.Combine(home, "a.dat", "inside");
        Directory.CreateDirectory(blocker);

        var failed = PackageSavesSeed.Seed([home], [source], "linux");
        Assert.Equal(PackageSavesSeed.SeedStatus.Failed, failed.Status);
        Assert.False(File.Exists(Path.Combine(home, PackageSavesSeed.MarkerFileName)));
        Assert.False(File.Exists(Path.Combine(home, "a.dat")));

        Directory.Delete(Path.Combine(home, "a.dat"), recursive: true);
        var retry = PackageSavesSeed.Seed([home], [source], "linux");
        Assert.Equal(PackageSavesSeed.SeedStatus.Seeded, retry.Status);
        Assert.Equal(1, retry.FilesCopied);
        Assert.Equal("one", File.ReadAllText(Path.Combine(home, "a.dat")));
        Assert.True(File.Exists(Path.Combine(home, PackageSavesSeed.MarkerFileName)));
    }

    [Fact]
    public void SeedingSkipsOverTheLimitAndWritesNothing()
    {
        var source = SaveTree("saves", ("big.bin", new string('x', 2048)));
        var home = Path.Combine(root, "package", ".phaket-home", "data");

        var result = PackageSavesSeed.Seed([home], [source], "linux", limitBytes: 1024);
        Assert.Equal(PackageSavesSeed.SeedStatus.TooLarge, result.Status);
        Assert.False(Directory.Exists(home));

        // Free space below the size is the same answer: an empty destination must not look seeded.
        var noSpace = PackageSavesSeed.Seed([home], [source], "linux", limitBytes: 1 << 20, freeBytes: _ => 10);
        Assert.Equal(PackageSavesSeed.SeedStatus.TooLarge, noSpace.Status);
    }

    // A directory link is not save data: following it would copy (and later write) outside.
    [Fact]
    public void SeedingDoesNotFollowDirectoryLinks()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges on Windows
        var source = SaveTree("saves", ("real/save.dat", "inside"));
        var outside = SaveTree("outside", ("secret.dat", "outside"));
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
        var home = Path.Combine(root, "package", ".phaket-home", "data");

        var result = PackageSavesSeed.Seed([home], [source], "linux");
        Assert.Equal(PackageSavesSeed.SeedStatus.Seeded, result.Status);
        Assert.Equal(1, result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(home, "real/save.dat")));
        Assert.False(Directory.Exists(Path.Combine(home, "linked")));
        Assert.False(File.Exists(Path.Combine(home, "linked/secret.dat")));
    }

    // The whole flow the app calls: the definition's real save folder is the source, the profile
// copy Phaket keeps is not, and a package on Windows shares the user's saves (no seed at all).
    [Fact]
    public void PackageFlowSeedsFromTheGamesRealSaveFolderAndVanillaKeepsThemShared()
    {
        var profileRoot = Path.Combine(root, "profile");
        var profileCopy = SaveTree("profile/steam/945360", ("playerPrefs", "stale-copy"));
        var real = SaveTree("home/.config/unity3d/Innersloth/Among Us", ("playerPrefs", "vanilla"), ("gameData", "data"));
        var definition = Definition(new SavesIsolation(new Dictionary<string, SavesIsolationMode>
        {
            ["linux"] = Env("HOME", "XDG_DATA_HOME"),
            ["windows"] = Shared("unity-locallow-known-folder"),
        }), "profile://steam/945360");

        var linuxSources = (definition with
        {
            SaveLocations = new SaveLocationsByOs(new Dictionary<string, IReadOnlyList<string>>
            {
                ["linux"] = [Path.Combine(root, "home/.config/unity3d/Innersloth/Among Us")],
            }),
        }).SaveSourcesFor("linux");
        var package = Path.Combine(root, "package");
        var isolated = PackageSavesIsolation.Prepare(new PackageSavesIsolation.Request(
            "linux", definition.SavesIsolationFor("linux"), package, linuxSources, profileRoot));
        Assert.False(isolated.Isolation.SavesShared);
        Assert.Equal(PackageSavesSeed.SeedStatus.Seeded, isolated.Seed!.Status);
        // Two files of the real folder, none of the profile copy: the copy is never the source.
        Assert.Equal(2, isolated.Seed.FilesCopied);
        Assert.Equal("vanilla", File.ReadAllText(Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName, "config", "playerPrefs")));
        Assert.True(File.Exists(Path.Combine(real, "playerPrefs")));
        // A Phaket profile copy ("profile://...") is not where the game reads its progress.
        Assert.Empty(PackageSavesIsolation.SaveSources(["profile://steam/945360"]));
        Assert.True(File.Exists(Path.Combine(profileCopy, "playerPrefs")));

        var vanilla = PackageSavesIsolation.Prepare(new PackageSavesIsolation.Request(
            "linux", definition.SavesIsolationFor("linux"), null, linuxSources, profileRoot));
        Assert.True(vanilla.Isolation.SavesShared);
        Assert.Null(vanilla.Seed);

        var windows = PackageSavesIsolation.Prepare(new PackageSavesIsolation.Request(
            "windows", definition.SavesIsolationFor("windows"), package, definition.SaveSourcesFor("windows"), profileRoot));
        Assert.True(windows.Isolation.SavesShared);
        Assert.Null(windows.Seed);
    }

    // "~/..." is the only way a definition names a home-relative folder; nothing else qualifies.
    [Fact]
    public void SaveLocationsResolveFromHomeAndRefuseRelativePaths()
    {
        var home = Directory.CreateTempSubdirectory("phaket-home-").FullName;
        try
        {
            Assert.Equal(Path.Combine(home, ".config", "unity3d"),
                GameDefinition.ResolveLocation("~/.config/unity3d", home));
            Assert.Equal(Path.Combine(home, "saves"), GameDefinition.ResolveLocation("~/saves", home));
            Assert.Null(GameDefinition.ResolveLocation("profile://steam/945360", home));
            Assert.Null(GameDefinition.ResolveLocation("relative/saves", home));
        }
        finally { Directory.Delete(home, true); }
    }

    // No SavePaths: nothing to seed, the launch still runs with the redirect.
    [Fact]
    public void EmptySavePathsMeansNoSeedingButTheLaunchStillRedirects()
    {
        var definition = Definition(new SavesIsolation(new Dictionary<string, SavesIsolationMode>
        {
            ["linux"] = Env("HOME"),
        }));
        var package = Path.Combine(root, "package");
        var result = PackageSavesIsolation.Prepare(new PackageSavesIsolation.Request(
            "linux", definition.SavesIsolationFor("linux"), package, definition.SavePaths, Path.Combine(root, "profile")));
        Assert.Equal(SavesIsolationMode.EnvRedirect, result.Isolation.Mode);
        Assert.Equal(PackageSavesSeed.SeedStatus.NotApplicable, result.Seed!.Status);
        Assert.False(Directory.Exists(Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName)));
    }

    // Declared paths that do not exist yet are not a seed source either.
    [Fact]
    public void MissingSaveLocationsAreSkippedInsteadOfFailing()
    {
        var definition = Definition(new SavesIsolation(new Dictionary<string, SavesIsolationMode>
        {
            ["linux"] = Env("HOME"),
        }), "profile://steam/945360");
        var result = PackageSavesIsolation.Prepare(new PackageSavesIsolation.Request(
            "linux", definition.SavesIsolationFor("linux"), Path.Combine(root, "package"),
            definition.SavePaths, Path.Combine(root, "profile")));
        Assert.Equal(PackageSavesSeed.SeedStatus.NotApplicable, result.Seed!.Status);
    }
}