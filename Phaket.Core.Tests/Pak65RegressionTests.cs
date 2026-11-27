using System.Buffers.Binary;
using System.Text.Json;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-65 (epic PAK-53, review 2026-11-15): an unknown fact must be an explicit refusal, never a
// silently empty plan; install tools must refuse a game tree containing links before the process
// starts; session directories must be private and swept; the depot OS comes from the game, not
// the host.
public sealed class MissingFactsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak65-").FullName;

    private static Recipe Guarded(RecipeCondition when) => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.arch-guarded",
        Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
        When = when,
        Requires = new RecipeRequires { Capabilities = ["file.copy"] },
        Artifacts = [new RecipeArtifact { Id = "bundle", Source = "package://bundle.zip" }],
        Steps = [new RecipeStep { Id = "copy", Use = "file.copy", With = new Dictionary<string, JsonElement>
        {
            ["from"] = JsonSerializer.SerializeToElement("package://bundle"),
            ["to"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/Mod.dll"),
        } }],
    };

    private PlannerContext Context(PlanFacts facts)
    {
        var game = Path.Combine(root, "game");
        foreach (var dir in new[] { game, "profile", "staging", "packages" })
            Directory.CreateDirectory(Path.Combine(root, dir));
        File.WriteAllBytes(Path.Combine(root, "packages", "bundle.zip"), []);
        return new PlannerContext("i", "p", game, Path.Combine(root, "profile"),
            Path.Combine(root, "staging"), Path.Combine(root, "packages"),
            TestFixtures.AmongUs, facts);
    }

    private PlanFacts Facts(string? arch, long? build) => new("linux", "steam", "2026.3.5",
        OperationCatalog.Capabilities, _ => false, Arch: arch, BuildId: build);

    [Fact]
    public void ArchConditionWithoutArchFactIsRefused()
    {
        var when = new RecipeCondition { All = [new RecipeCondition { Arch = "x86" }] };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(Guarded(when), Context(Facts(null, null))));
        Assert.Equal(ErrorCodes.InstallFactsMissing, ex.Error.Code);
        Assert.Equal("arch", ex.Error.Parameters["fact"]);
    }

    [Fact]
    public void BuildIdConditionWithoutBuildFactIsRefused()
    {
        var when = new RecipeCondition { All = [new RecipeCondition { Arch = "x86" }, new RecipeCondition { BuildId = "<=24302054" }] };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(Guarded(when), Context(Facts("x86", null))));
        Assert.Equal(ErrorCodes.InstallFactsMissing, ex.Error.Code);
        Assert.Equal("buildId", ex.Error.Parameters["fact"]);
    }

    [Fact]
    public void ConditionWithItsFactPlansNormally()
    {
        var plan = OperationPlanner.Plan(Guarded(new RecipeCondition { Arch = "x86" }), Context(Facts("x86", null)));
        Assert.NotEmpty(plan.Operations);
    }

    [Fact]
    public void DefinitionDeclaresArtifactPlatformAndDefaultArchitecture()
    {
        var amongUs = GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));
        // Windows-only game: even on a Linux host the artifact is the Windows depot (Proton).
        Assert.Equal("windows", amongUs.ArtifactOs("linux"));
        Assert.Equal("windows", amongUs.ArtifactOs("windows"));
        Assert.Equal("x64", amongUs.DefaultArch);

        var valheim = GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions", "games", "valheim.yaml")));
        // Native Linux build exists, so the host OS is right.
        Assert.Equal("linux", valheim.ArtifactOs("linux"));
        Assert.Equal("windows", valheim.ArtifactOs("windows"));
        Assert.Equal("x64", valheim.DefaultArch);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class DepotArtifactOsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak65-depot-").FullName;
    private readonly string binary;

    public DepotArtifactOsTests()
    {
        Directory.CreateDirectory(root);
        binary = Path.Combine(root, "DepotDownloader");
        File.WriteAllText(binary, "fake");
    }

    [Theory]
    [InlineData("windows", "945360", "windows")] // Windows host: native
    [InlineData("linux", "945360", "windows")]   // Proton: the game is still a Windows artifact
    public void AmongUsAlwaysUsesTheWindowsDepot(string hostOs, string appId, string expected)
    {
        var adapter = new DepotDownloaderInstallAdapter(new NoRun(), Path.Combine(root, "config"), binary);
        var definition = new GameDefinition("among-us", [], MainExecutable: "Among Us.exe", Platforms: ["windows"]);
        var plan = adapter.BuildInstallPlan(new OwnedSteamGame(appId, "Among Us", null, null, "test", false),
            Path.Combine(root, "games"), definition, hostOs, "64");
        Assert.Equal(expected, plan.Os);
    }

    [Theory]
    [InlineData("linux", "linux")]
    [InlineData("windows", "windows")]
    public void ValheimUsesTheHostNativeDepot(string hostOs, string expected)
    {
        var adapter = new DepotDownloaderInstallAdapter(new NoRun(), Path.Combine(root, "config"), binary);
        var plan = adapter.BuildInstallPlan(new OwnedSteamGame("892970", "Valheim", null, null, "test", false),
            Path.Combine(root, "games"), new GameDefinition("valheim", [], MainExecutable: "valheim.exe"), hostOs, "64");
        Assert.Equal(expected, plan.Os);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class NoRun : IDepotProcessRunner
    {
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct) => Task.FromResult(0);
    }
}

public sealed class SessionPrivacyTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak65-session-").FullName;
    private readonly string binary;

    public SessionPrivacyTests()
    {
        Directory.CreateDirectory(root);
        binary = Path.Combine(root, OperatingSystem.IsWindows() ? "legendary.exe" : "legendary");
        File.WriteAllText(binary, "fake");
    }

    private LegendaryInstallAdapter Adapter(FakeLegendary runner, string configRoot) =>
        new(_ => Task.FromResult(new LegendaryTokens("secret-access", "secret-refresh",
            DateTimeOffset.UtcNow.AddHours(1), "account", "Player")), runner, configRoot, binaryPath: binary);

    [Fact]
    public async Task SessionAndTokenFileArePrivateBeforeTheToolRuns()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows ACLs need an NTFS machine to verify");
        var configRoot = Path.Combine(root, "legendary-config");
        var runner = new FakeLegendary();
        var adapter = Adapter(runner, configRoot);
        var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"),
            Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));
        runner.OnRun = _ =>
        {
            // The checks run inside the tool process callback: the permissions must already hold.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(runner.ConfigPath!));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(runner.ConfigPath!, "user.json")));
            }
            Directory.CreateDirectory(runner.Plan!.TargetPath);
            File.WriteAllText(Path.Combine(runner.Plan.TargetPath, "game.exe"), "exe");
            return 0;
        };
        runner.Plan = plan;
        await adapter.ExecuteInstallAsync(plan, consent: true, planAccepted: true, ct: TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(configRoot));
    }

    [Theory]
    [InlineData(true)]   // ours and older than the cut-off: removed
    [InlineData(false)]  // a running install's fresh session: kept
    public void SweepRemovesOnlyStalePhaketSessions(bool stale)
    {
        var configRoot = Path.Combine(root, "sweep-" + stale);
        Directory.CreateDirectory(configRoot);
        var session = Path.Combine(configRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "user.json"), "x");
        if (stale)
        {
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            // A user settings file carries the age of the session as well.
            File.SetLastWriteTimeUtc(Path.Combine(session, "user.json"), old.UtcDateTime);
            Directory.SetLastWriteTimeUtc(session, old.UtcDateTime);
            Directory.SetCreationTimeUtc(session, old.UtcDateTime);
        }

        SessionSweep.SweepOrphans(configRoot, TimeSpan.FromHours(24));
        Assert.Equal(!stale, Directory.Exists(session));
        Assert.True(Directory.Exists(configRoot));
    }

    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows symlinks may require elevated privileges")]
    public void SweepIgnoresFoldersThatAreNotOurs()
    {
        var configRoot = Path.Combine(root, "sweep-foreign");
        var foreign = Path.Combine(configRoot, "not-a-guid");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "user.json"), "keep");
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        Directory.SetLastWriteTimeUtc(foreign, old.UtcDateTime);
        Directory.SetCreationTimeUtc(foreign, old.UtcDateTime);

        SessionSweep.SweepOrphans(configRoot, TimeSpan.FromHours(24));
        Assert.True(Directory.Exists(foreign));

        // A root that is itself a link is never swept.
        var link = Path.Combine(root, "sweep-link");
        Directory.CreateSymbolicLink(link, foreign);
        SessionSweep.SweepOrphans(link, TimeSpan.FromHours(24));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void SweepKeepsActiveSessionAndForeignRoot()
    {
        var configRoot = Path.Combine(root, "sweep-live");
        Directory.CreateDirectory(configRoot);
        var fresh = Path.Combine(configRoot, Guid.NewGuid().ToString("N"));
        var stale = Path.Combine(configRoot, Guid.NewGuid().ToString("N"));
        var foreign = Path.Combine(configRoot, "user-own-legendary");
        foreach (var dir in new[] { fresh, stale, foreign }) Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(stale, DateTimeOffset.UtcNow.AddDays(-3).UtcDateTime);
        SessionSweep.SweepOrphans(configRoot, TimeSpan.FromHours(24));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(foreign));
        Assert.False(Directory.Exists(stale));
    }

    // PAK-60: a long download or a wait for the Steam Guard code writes its tool files without touching
    // the session directory itself, so an active session can look days old. Only a session whose whole
    // tree was last written long ago is an orphan.
    private sealed class FixedTimeSweepRoot : IDisposable
    {
        public string Root { get; }
        public FixedTimeSweepRoot(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), $"phaket-sweep-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DirTimes(Root, DateTimeOffset.UtcNow.AddDays(-3));
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(true)]   // tool file written a minute ago: the session is alive
    [InlineData(false)]  // nothing written for days: orphan
    public void SweepKeepsSessionWhoseToolFilesAreStillBeingWritten(bool active)
    {
        using var sweepRoot = new FixedTimeSweepRoot("active-" + active);
        var session = Path.Combine(sweepRoot.Root, Guid.NewGuid().ToString("N"));
        var toolData = Path.Combine(session, "data", "DepotDownloader");
        Directory.CreateDirectory(toolData);
        string toolFile = Path.Combine(toolData, "ticket.bin");
        File.WriteAllText(toolFile, "x");

        var old = DateTimeOffset.UtcNow.AddDays(-3);
        // Dated as if written long ago; the live session gets a recent ticket write.
        File.SetLastWriteTimeUtc(toolFile, (active ? DateTimeOffset.UtcNow.AddMinutes(-1) : old).UtcDateTime);
        DirTimes(toolData, old);
        DirTimes(session, old);

        SessionSweep.SweepOrphans(sweepRoot.Root, TimeSpan.FromHours(24));
        Assert.Equal(active, Directory.Exists(session));
    }

    // Created now, but every entry is dated as if written long ago.
    private static void DirTimes(string path, DateTimeOffset time)
    {
        Directory.SetLastWriteTimeUtc(path, time.UtcDateTime);
        Directory.SetCreationTimeUtc(path, time.UtcDateTime);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class FakeLegendary : IProcessRunner
    {
        public string? ConfigPath;
        public LegendaryInstallPlan? Plan;
        public Func<IReadOnlyList<string>, int>? OnRun;

        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Action<string> output, CancellationToken cancellationToken)
        {
            ConfigPath = environment["LEGENDARY_CONFIG_PATH"];
            return Task.FromResult(OnRun?.Invoke(arguments) ?? 0);
        }
    }
}

public sealed class NestedLinkGuardTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak65-links-").FullName;

    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows symlinks may require elevated privileges")]
    public async Task LegendaryRefusesEscapingLinkInsideTargetBeforeStartingProcess()
    {
        var binary = Path.Combine(root, "legendary"); File.WriteAllText(binary, "fake");
        var runner = new CountingRunner();
        var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r",
            DateTimeOffset.UtcNow, "id", "name")), runner, Path.Combine(root, "config"), binaryPath: binary);
        var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"),
            Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));

        // An install tree that looks normal but contains a nested link to outside.
        Directory.CreateDirectory(Path.Combine(plan.TargetPath, "BepInEx"));
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(plan.TargetPath, "BepInEx", "redirect"), outside);

        var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true,
            ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
        // Nothing was started and no token directory was created.
        Assert.Equal(0, runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, "config")));
    }

    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows symlinks may require elevated privileges")]
    public async Task DepotRefusesEscapingLinkInsideTargetBeforeStartingProcess()
    {
        var binary = Path.Combine(root, "DepotDownloader"); File.WriteAllText(binary, "fake");
        var runner = new CountingDepotRunner();
        var adapter = new DepotDownloaderInstallAdapter(runner, Path.Combine(root, "depot-config"), binary);
        var plan = adapter.BuildInstallPlan(new OwnedSteamGame("945360", "Game", null, null, "test", false),
            Path.Combine(root, "games"), new GameDefinition("among-us", [], MainExecutable: "Among Us.exe"), "windows", "64");
        Directory.CreateDirectory(Path.Combine(plan.TargetPath, "plugins"));
        Directory.CreateSymbolicLink(Path.Combine(plan.TargetPath, "plugins", "escape"), root);

        var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, "account",
            (_, _) => Task.FromResult("pw"), true, true, ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
        Assert.Equal(0, runner.Calls);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class CountingRunner : IProcessRunner
    {
        public int Calls;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Action<string> output, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(0); }
    }

    private sealed class CountingDepotRunner : IDepotProcessRunner
    {
        public int Calls;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
        { Calls++; return Task.FromResult(0); }
    }
}
