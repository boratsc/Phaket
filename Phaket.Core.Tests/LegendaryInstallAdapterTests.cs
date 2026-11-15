using System.Text.Json;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public sealed class LegendaryInstallAdapterTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    // Windows symlink creation may require developer mode or elevated privileges.
    [Theory(SkipWhen = nameof(IsWindows), Skip = "Windows symlinks may require elevated privileges or developer mode")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteRejectsLinksInBaseOrTargetBeforeCreatingConfig(bool targetLink)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "legendary"), "fake");
        try
        {
            var runner = new FakeRunner();
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), runner, Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));
            string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            if (targetLink) Directory.CreateDirectory(plan.BasePath);
            Directory.CreateSymbolicLink(targetLink ? plan.TargetPath : plan.BasePath, outside);
            var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
            Assert.False(Directory.Exists(Path.Combine(root, "config")));
            Assert.Empty(runner.Arguments);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory(SkipWhen = nameof(IsWindows), Skip = "Windows symlinks may require elevated privileges or developer mode")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutableLinkEscapingTargetIsUnverified(bool directoryLink)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "legendary"), "fake");
        try
        {
            var runner = new FakeRunner();
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), runner, Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: directoryLink ? "bin/game.exe" : "game.exe"));
            runner.OnRun = (_, _) =>
            {
                string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "game.exe"), "exe");
                Directory.CreateDirectory(plan.TargetPath);
                if (directoryLink) Directory.CreateSymbolicLink(Path.Combine(plan.TargetPath, "bin"), outside);
                else File.CreateSymbolicLink(Path.Combine(plan.TargetPath, "game.exe"), Path.Combine(outside, "game.exe"));
                return 0;
            };
            var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal("LEGENDARY_INSTALL_UNVERIFIED", ex.Error.Code);
            Assert.Empty(adapter.GetRecordedInstallations());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstallWritesPrivateConfigUsesArgumentListRedactsTokensAndCleansUp()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var binaryDir = Path.Combine(root, "bin"); Directory.CreateDirectory(binaryDir);
        var binary = Path.Combine(binaryDir, OperatingSystem.IsWindows() ? "legendary.exe" : "legendary"); File.WriteAllText(binary, "fake");
        try
        {
            var runner = new FakeRunner();
            LegendaryTokens secrets = new("secret-access", "secret-refresh", DateTimeOffset.UtcNow.AddHours(1), "account", "Player");
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(secrets), runner, Path.Combine(root, "config"), binaryPath: binary);
            var target = Path.Combine(root, "games");
            var plan = adapter.BuildInstallPlan(new("my-app", "ns", "catalog", "Game", true, "assets"), target,
                new GameDefinition("game", [], MainExecutable: "Game.exe"));
            Assert.False(Directory.Exists(target));
            string? config = null;
            runner.OnRun = (args, env) =>
            {
                Assert.Equal(new[] { "install", "my-app", "--base-path", target, "--yes" }, args);
                config = env["LEGENDARY_CONFIG_PATH"];
                var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(config!, "user.json"))).RootElement;
                Assert.Equal(secrets.AccessToken, json.GetProperty("access_token").GetString());
                if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(config!, "user.json")));
                Directory.CreateDirectory(plan.TargetPath);
                File.WriteAllText(Path.Combine(plan.TargetPath, "Game.exe"), "exe");
                return 0;
            };
            var logs = new List<string>();
            string verified = await adapter.ExecuteInstallAsync(plan, consent: true, planAccepted: true, new Progress<string>(logs.Add), TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(plan.TargetPath, "Game.exe"), verified);
            Assert.False(Directory.Exists(config));
            Assert.Single(adapter.GetRecordedInstallations());
            Assert.DoesNotContain("secret-access", string.Join(" ", runner.Arguments.Concat(logs)));
            Assert.DoesNotContain("secret-refresh", string.Join(" ", runner.Arguments.Concat(logs)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TargetBecomingNonEmptyAfterPlanningRequiresOverwriteConsent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "legendary.exe" : "legendary"), "fake");
        try
        {
            var runner = new FakeRunner();
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), runner, Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));
            Assert.Empty(plan.Conflicts);
            Directory.CreateDirectory(plan.TargetPath);
            File.WriteAllText(Path.Combine(plan.TargetPath, "user-data"), "keep");
            var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal("OVERWRITE_CONSENT_REQUIRED", ex.Error.Code);
            Assert.False(Directory.Exists(Path.Combine(root, "config")));
            runner.OnRun = (_, _) => { File.WriteAllText(Path.Combine(plan.TargetPath, "game.exe"), "exe"); return 0; };
            await adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken, overwriteConsent: true);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(plan.TargetPath, "user-data")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ChangedPlanPathsAreRejectedBeforeCreatingDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "legendary.exe" : "legendary"), "fake");
        try
        {
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), new FakeRunner(), Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));
            foreach (var changed in new[] { plan with { Executable = "../outside.exe" }, plan with { TargetPath = Path.Combine(root, "outside") } })
            {
                var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(changed, true, true, ct: TestContext.Current.CancellationToken));
                Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
            }
            Assert.False(Directory.Exists(plan.BasePath));
            Assert.False(Directory.Exists(Path.Combine(root, "config")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingTargetRequiresOverwriteConsent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var bin = Path.Combine(root, "legendary"); File.WriteAllText(bin, "fake");
        try
        {
            string targetDir = Path.Combine(root, "games");
            string target = Path.Combine(targetDir, "app"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "user-data"), "keep");
            var runner = new FakeRunner { OnRun = (_, _) => { File.WriteAllText(Path.Combine(target, "game.exe"), "exe"); return 0; } };
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), runner, Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), targetDir, new GameDefinition("g", [], MainExecutable: "game.exe"));
            Assert.Contains("target exists", Assert.Single(plan.Conflicts));
            var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal("OVERWRITE_CONSENT_REQUIRED", ex.Error.Code);
            await adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken, overwriteConsent: true);
            Assert.True(File.Exists(Path.Combine(target, "user-data")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailureCleansConfigAndIncludesBoundedOutputTail()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var bin = Path.Combine(root, "legendary"); File.WriteAllText(bin, "fake");
        try
        {
            var runner = new FakeRunner { OnRun = (_, _) => { throw new InvalidOperationException("must supply exit code"); } };
            runner.OnRun = (_, _) => 7;
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), runner, Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));
            runner.Output?.Invoke("not configured");
            var ex = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal("LEGENDARY_INSTALL_FAILED", ex.Error.Code);
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "config")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConsentRequiredAndUnverifiedInstallAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var bin = Path.Combine(root, "legendary"); File.WriteAllText(bin, "fake");
        try
        {
            var adapter = new LegendaryInstallAdapter(_ => Task.FromResult(new LegendaryTokens("a", "r", DateTimeOffset.UtcNow, "id", "name")), new FakeRunner(), Path.Combine(root, "config"), binaryPath: Path.Combine(root, "legendary"));
            var plan = adapter.BuildInstallPlan(new("app", "ns", "cat", "title", true, "assets"), Path.Combine(root, "games"), new GameDefinition("g", [], MainExecutable: "game.exe"));
            var consent = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, false, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal("CONSENT_REQUIRED", consent.Error.Code);
            var unverified = await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(plan, true, true, ct: TestContext.Current.CancellationToken));
            Assert.Equal("LEGENDARY_INSTALL_UNVERIFIED", unverified.Error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public Func<IReadOnlyList<string>, IDictionary<string, string?>, int>? OnRun { get; set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public Action<string>? Output { get; private set; }
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Action<string> output, CancellationToken cancellationToken)
        {
            Arguments = arguments; Output = output;
            output("fake output with secret-access and secret-refresh");
            return Task.FromResult(OnRun?.Invoke(arguments, environment) ?? 0);
        }
    }
}
