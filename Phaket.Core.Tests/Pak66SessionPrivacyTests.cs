using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-66 (review of the secrets wave): a chmod/ACL the filesystem refuses or silently ignores must
// stop the operation. A token is never written where another account could read it, and the tool
// is never started.
public sealed class Pak66SessionPrivacyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak66-privacy-").FullName;
    private readonly string binary;

    public Pak66SessionPrivacyTests()
    {
        Directory.CreateDirectory(root);
        binary = Path.Combine(root, OperatingSystem.IsWindows() ? "legendary.exe" : "legendary");
        File.WriteAllText(binary, "fake");
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    private sealed class FakeTool : IProcessRunner
    {
        public int Calls;
        public string? SeenConfig;
        public Func<IReadOnlyList<string>, int>? OnRun;

        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Action<string> output, CancellationToken cancellationToken)
        {
            Calls++;
            SeenConfig = environment["LEGENDARY_CONFIG_PATH"];
            return Task.FromResult(OnRun?.Invoke(arguments) ?? 0);
        }
    }

    private sealed class FakeDepot : IDepotProcessRunner
    {
        public int Calls;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(0);
        }
    }

    private LegendaryInstallAdapter Adapter(FakeTool runner, string configRoot) =>
        new(_ => Task.FromResult(new LegendaryTokens("secret-access", "secret-refresh",
            DateTimeOffset.UtcNow.AddHours(1), "account", "Player")), runner, configRoot, binaryPath: binary);

    private LegendaryInstallPlan Plan(LegendaryInstallAdapter adapter) =>
        adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"),
            Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));

    // A mount without permission support accepts chmod and keeps a group/other-readable mode.
    private const UnixFileMode PublicDirectory = (UnixFileMode)(0b111_101_101);
    private const UnixFileMode PublicFile = (UnixFileMode)0b110_100_100;

    // Scoped to one test's own tree, so a leaked override can never disturb a parallel test.
    private static IDisposable FakeChmod(string scoped, Action<string, UnixFileMode> behavior)
    {
        var previous = SessionSecurity.SetModeOverride.Value;
        SessionSecurity.SetModeOverride.Value = (path, mode) =>
        {
            if (path.StartsWith(scoped, StringComparison.Ordinal)) behavior(path, mode);
            else File.SetUnixFileMode(path, mode);
        };
        return new ResetOverride(previous);
    }

    private sealed class ResetOverride(Action<string, UnixFileMode>? previous) : IDisposable
    {
        public void Dispose() => SessionSecurity.SetModeOverride.Value = previous;
    }

    private bool AnySecretOnDisk() => Directory.Exists(root)
        && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path).Contains("secret-access", StringComparison.Ordinal));

    [Theory(SkipWhen = nameof(IsWindows), Skip = "Unix mode simulation; the Windows ACL path is verified on an NTFS machine")]
    // chmod reports an error (EIO / EPERM) ...
    [InlineData(true)]
    // ... or it is accepted and ignored, as on a filesystem without permission support.
    [InlineData(false)]
    public async Task UnprivateSessionRootRefusesBeforeAnyTokenIsWritten(bool chmodThrows)
    {
        var configRoot = Path.Combine(root, chmodThrows ? "legendary-throws" : "legendary-noop");
        var runner = new FakeTool();
        var adapter = Adapter(runner, configRoot);
        var plan = Plan(adapter);

        using var _ = FakeChmod(configRoot, chmodThrows
            ? (_, _) => throw new IOException("Operation not permitted")
            : (path, _) => File.SetUnixFileMode(path, Directory.Exists(path) ? PublicDirectory : PublicFile));

        var thrown = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan,
            consent: true, planAccepted: true, ct: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SessionPrivacyFailed, thrown.Error.Code);
        Assert.Equal(configRoot, thrown.Error.Parameters["path"]);
        // legendary never started, no token written anywhere, nothing installed.
        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(Path.Combine(configRoot, "user.json")));
        Assert.False(AnySecretOnDisk());
        Assert.False(Directory.Exists(Path.Combine(root, "games", "app")));
    }

    [Fact(SkipWhen = nameof(IsWindows), Skip = "Unix mode simulation; the token file is created by the same code path on Windows")]
    public async Task UnprivateTokenFileIsRefusedAndNeverLeftBehind()
    {
        var configRoot = Path.Combine(root, "legendary-file");
        var runner = new FakeTool();
        var adapter = Adapter(runner, configRoot);
        var plan = Plan(adapter);
        string? tokenPath = null;
        // The session directory is made private properly; only the token file stays public, so the
        // refusal must come from the file check — before a single token byte is serialized.
        using var _ = FakeChmod(configRoot, (path, mode) =>
        {
            if (Path.GetFileName(path) != "user.json") { File.SetUnixFileMode(path, mode); return; }
            tokenPath = path;
            File.SetUnixFileMode(path, PublicFile);
        });

        var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan,
            consent: true, planAccepted: true, ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SessionPrivacyFailed, ex.Error.Code);
        Assert.EndsWith("user.json", ex.Error.Parameters["path"]);

        Assert.NotNull(tokenPath);
        // A file the umask may have left world-readable must not survive the refusal, empty or not.
        Assert.False(File.Exists(tokenPath));
        Assert.Equal(0, runner.Calls);
        Assert.False(AnySecretOnDisk());
    }

    [Fact(SkipWhen = nameof(IsWindows), Skip = "Unix mode simulation")]
    public async Task DepotSessionPrivacyFailureStopsBeforeTheToolRuns()
    {
        var depotBinary = Path.Combine(root, "DepotDownloader");
        File.WriteAllText(depotBinary, "fake");
        var runner = new FakeDepot();
        var configRoot = Path.Combine(root, "depot-config");
        var adapter = new DepotDownloaderInstallAdapter(runner, configRoot, depotBinary);
        var plan = adapter.BuildInstallPlan(new OwnedSteamGame("945360", "Game", null, null, "test", false),
            Path.Combine(root, "depot-games"), new GameDefinition("among-us", [], MainExecutable: "Among Us.exe"), "windows", "64");

        using var _ = FakeChmod(configRoot, (path, _) =>
            File.SetUnixFileMode(path, Directory.Exists(path) ? PublicDirectory : PublicFile));

        var thrown = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, "account",
            (_, _) => Task.FromResult("pw"), consent: true, planAccepted: true, ct: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SessionPrivacyFailed, thrown.Error.Code);
        Assert.Equal(0, runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, "depot-games", "945360")));
    }

    // The real chmod still works on a normal filesystem: failing closed must not break working
    // installs, and the order directory -> file -> content is unchanged.
    [Fact(SkipWhen = nameof(IsWindows), Skip = "Unix mode assertions")]
    public async Task WorkingFilesystemStillInstallsAndKeepsEverythingPrivate()
    {
        var configRoot = Path.Combine(root, "legendary-ok");
        var runner = new FakeTool();
        var adapter = Adapter(runner, configRoot);
        var plan = Plan(adapter);
        UnixFileMode directoryMode = 0, fileMode = 0;
        runner.OnRun = _ =>
        {
            directoryMode = File.GetUnixFileMode(runner.SeenConfig!);
            fileMode = File.GetUnixFileMode(Path.Combine(runner.SeenConfig!, "user.json"));
            Directory.CreateDirectory(plan.TargetPath);
            File.WriteAllText(Path.Combine(plan.TargetPath, "game.exe"), "exe");
            return 0;
        };
        await adapter.ExecuteInstallAsync(plan, consent: true, planAccepted: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, directoryMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
