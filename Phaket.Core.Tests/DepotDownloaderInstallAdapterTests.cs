using System.Diagnostics;
using Phaket.Core;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Tools;

namespace Phaket.Core.Tests;

public sealed class DepotDownloaderInstallAdapterTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-depot-test-" + Guid.NewGuid().ToString("N"));
    private readonly string binary;
    private readonly FakeRunner runner = new();
    private readonly DepotDownloaderInstallAdapter adapter;
    private readonly GameDefinition definition = new("game", [], MainExecutable: "game.exe");
    private readonly OwnedSteamGame game = new("945360", "Game", null, null, "test", false);
    public DepotDownloaderInstallAdapterTests()
    {
        Directory.CreateDirectory(root);
        binary = Path.Combine(root, "DepotDownloader"); File.WriteAllText(binary, "fake");
        adapter = new(runner, Path.Combine(root, "config"), binary);
    }
    private DepotInstallPlan Plan(string? depot = null, string? manifest = null) => adapter.BuildInstallPlan(game, Path.Combine(root, "games"), definition, "windows", "64", depot, manifest);
    private static Task<string> Credentials(DepotPrompt p, CancellationToken _) => Task.FromResult(p == DepotPrompt.Password ? "secret-password" : "secret-guard");
    private Task<string> Execute(DepotInstallPlan p, bool consent = true, bool accepted = true, bool overwrite = false) => adapter.ExecuteInstallAsync(p, "account", Credentials, consent, accepted, overwriteConsent: overwrite);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows token isolation is not established")]
    public async Task InstallPassesArgumentsAndIsolatesCredentialsAndRecords()
    {
        var plan = Plan("10", "123");
        Assert.False(Directory.Exists(plan.BasePath));
        runner.Action = async (args, env, output, ct) =>
        {
            Assert.Equal(["-app", "945360", "-username", "account", "-os", "windows", "-osarch", "64", "-dir", plan.TargetPath, "-depot", "10", "-manifest", "123"], args);
            Assert.DoesNotContain("-remember-password", args);
            var session = Directory.GetParent(env["HOME"]!)!.FullName;
            foreach (var path in env.Values) Assert.StartsWith(session + Path.DirectorySeparatorChar, path!);
            await output(false, "Enter account pass", runner.Send);
            await output(false, "word for \"account\": ", runner.Send);
            await output(true, "STEAM GUARD! Please enter your 2-factor auth code from your authenticator app:", runner.Send);
            await output(true, " ", runner.Send);
            await output(false, "secret-password secret-guard\n", runner.Send);
            Directory.CreateDirectory(plan.TargetPath);
            File.WriteAllText(Path.Combine(plan.TargetPath, plan.Executable), "game");
            return 0;
        };
        var progress = new CaptureProgress();
        Assert.Equal(Path.Combine(plan.TargetPath, plan.Executable), await adapter.ExecuteInstallAsync(plan, "account", Credentials, true, true, progress, TestContext.Current.CancellationToken));
        Assert.Equal(["secret-password", "secret-guard"], runner.Inputs);
        Assert.DoesNotContain("secret-password", string.Join("", progress.Lines));
        Assert.DoesNotContain("secret-guard", string.Join("", progress.Lines));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "config", "depot-session")));
        var record = Assert.Single(adapter.GetRecordedInstallations());
        Assert.Equal(plan.AppId, record.AppId);
        Assert.DoesNotContain("secret-", File.ReadAllText(Path.Combine(root, "config", "steam-installs.json")));
    }
    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows token isolation is not established")]
    public async Task FailureRedactsTailAndCleansSession()
    {
        runner.Action = async (_, _, output, _) => { await output(false, "Enter account password for \"account\": ", runner.Send); await output(true, "error secret-password\n", runner.Send); return 1; };
        var e = await Assert.ThrowsAsync<PhaketException>(() => Execute(Plan()));
        Assert.Equal("DEPOTDOWNLOADER_INSTALL_FAILED", e.Error.Code);
        Assert.DoesNotContain("secret-password", e.ToString());
        Assert.DoesNotContain("secret-password", string.Join(" ", e.Error.Parameters.Values));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "config", "depot-session")));
    }
    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows token isolation is not established")]
    public async Task ChecksConsentConflictAndRechecksLinks()
    {
        var plan = Plan();
        Assert.Equal("CONSENT_REQUIRED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan, consent: false))).Error.Code);
        Assert.Equal("PLAN_NOT_ACCEPTED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan, accepted: false))).Error.Code);
        Directory.CreateDirectory(plan.TargetPath); File.WriteAllText(Path.Combine(plan.TargetPath, "existing"), "x");
        Assert.Equal("OVERWRITE_CONSENT_REQUIRED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan))).Error.Code);
        runner.Action = (_, _, _, _) => Task.FromResult(1);
        Assert.Equal("DEPOTDOWNLOADER_INSTALL_FAILED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan, overwrite: true))).Error.Code);
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateSymbolicLink(Path.Combine(plan.TargetPath, "escape"), root);
            Assert.Equal(ErrorCodes.PathOutOfBounds, (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan, overwrite: true))).Error.Code);
            Directory.Delete(Path.Combine(plan.TargetPath, "escape"));
            Directory.Delete(plan.TargetPath, true);
            Directory.CreateSymbolicLink(plan.TargetPath, root);
            Assert.Equal(ErrorCodes.PathOutOfBounds, (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan, overwrite: true))).Error.Code);
            Directory.Delete(plan.TargetPath);
        }
    }
    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows token isolation is not established")]
    public async Task UnverifiedExecutableAndMissingBinary()
    {
        runner.Action = (_, _, _, _) => Task.FromResult(0);
        Assert.Equal("DEPOTDOWNLOADER_INSTALL_UNVERIFIED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(Plan()))).Error.Code);
        File.Delete(binary);
        var plan = Plan();
        var offlineRoot = Path.Combine(root, "tools");
        using var client = new HttpClient(new OfflineHandler());
        var provider = new Phaket.Core.Tools.ToolProvider(offlineRoot, client);
        var offlineAdapter = new DepotDownloaderInstallAdapter(runner, Path.Combine(root, "config2"), toolProvider: provider);
        plan = offlineAdapter.BuildInstallPlan(game, Path.Combine(root, "games"), definition, "windows", "64");
        Assert.Equal("TOOL_OFFLINE", (await Assert.ThrowsAsync<PhaketException>(() => offlineAdapter.ExecuteInstallAsync(plan, "account", Credentials, true, true, ct: TestContext.Current.CancellationToken))).Error.Code);
    }

    [Fact]
    public async Task PinnedToolVersionTakesPrecedenceOverExistingLocalBinary()
    {
        const string pin = "1.2.3";
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"tools\":{\"depotdownloader\":{\"version\":\"1.2.3\"}}}");
        var installed = Path.Combine(root, "tools", "depotdownloader", pin, OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!); File.WriteAllText(installed, "pinned");
        using var client = new HttpClient(new OfflineHandler());
        var provider = new ToolProvider(Path.Combine(root, "tools"), client);
        var pinnedAdapter = new DepotDownloaderInstallAdapter(runner, Path.Combine(root, "pinned-config"), binary, toolProvider: provider, settingsPath: settings);
        var plan = pinnedAdapter.BuildInstallPlan(game, Path.Combine(root, "games"), definition, "windows", "64");
        runner.Action = (args, _, _, _) =>
        {
            Assert.Contains("-app", args);
            Directory.CreateDirectory(plan.TargetPath); File.WriteAllText(Path.Combine(plan.TargetPath, plan.Executable), "game");
            return Task.FromResult(0);
        };
        // Windows keeps the session store only under explicit persistence consent.
        await pinnedAdapter.ExecuteInstallAsync(plan, "account", Credentials, true, true,
            rememberSessionConsent: OperatingSystem.IsWindows(), ct: TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task RejectsExecutableLinkEscapingTarget()
    {
        if (!OperatingSystem.IsLinux()) return;
        var plan = Plan();
        runner.Action = (args, _, _, _) =>
        {
            Assert.DoesNotContain("-manifest", args);
            Directory.CreateDirectory(plan.TargetPath);
            var outside = Path.Combine(root, "outside"); File.WriteAllText(outside, "x");
            File.CreateSymbolicLink(Path.Combine(plan.TargetPath, "game.exe"), outside);
            return Task.FromResult(0);
        };
        Assert.Equal("DEPOTDOWNLOADER_INSTALL_UNVERIFIED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(plan))).Error.Code);
    }
    [Fact(SkipWhen = nameof(IsWindows), Skip = "Windows token isolation is not established")]
    public async Task CancelCleansSession()
    {
        runner.Action = (_, _, _, _) => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Execute(Plan()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "config", "depot-session")));
    }
    [Fact]
    public void RejectsInvalidPlanAndManifestWithoutDepot()
    {
        Assert.Throws<PhaketException>(() => Plan(manifest: "1"));
        Assert.Throws<PhaketException>(() => adapter.BuildInstallPlan(game, root, definition, "windows", "x86"));
    }
    [Fact]
    public async Task WindowsFailsClosedBeforeRunningTool()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows-only isolation gate");
        Assert.Equal("DEPOTDOWNLOADER_SESSION_CONSENT_REQUIRED", (await Assert.ThrowsAsync<PhaketException>(() => Execute(Plan()))).Error.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "config", "depot-session")));
    }
    [Fact]
    public async Task WindowsAllowsRunnerOnlyWithPersistentSessionConsent()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows-only consent gate");
        var plan = Plan();
        runner.Action = (_, _, _, _) => { Directory.CreateDirectory(plan.TargetPath); File.WriteAllText(Path.Combine(plan.TargetPath, plan.Executable), "game"); return Task.FromResult(0); };
        Assert.Equal(Path.Combine(plan.TargetPath, plan.Executable), await adapter.ExecuteInstallAsync(plan, "account", Credentials, true, true, ct: TestContext.Current.CancellationToken, rememberSessionConsent: true));
    }
    [Fact]
    public void UnattributedStoresAreNeverDeletedOrOfferedForConfirmation()
    {
        var fakeStore = Path.Combine(root, "Url.fake", "AssemFiles");
        Directory.CreateDirectory(fakeStore);
        File.WriteAllText(Path.Combine(fakeStore, "identity.dat"), "DepotDownloader");
        var listing = adapter.ListSessionStores();
        Assert.Equal("attribution unavailable", listing.Status);
        Assert.NotEmpty(listing.Root);
        Assert.Empty(listing.Paths);
        bool confirmed = false;
        var results = adapter.CleanupSessionStores([fakeStore, Path.Combine(root, "other")], _ => { confirmed = true; return true; });
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("attribution unavailable", r.Error));
        Assert.False(confirmed);
        Assert.True(File.Exists(Path.Combine(fakeStore, "identity.dat")));
    }
    [Fact(SkipWhen = nameof(IsWindows), Skip = "Linux session isolation test")]
    public async Task RememberConsentPreservesIsolatedSessionAfterFailure()
    {
        runner.Action = (_, _, _, _) => Task.FromResult(1);
        await Assert.ThrowsAsync<PhaketException>(() => adapter.ExecuteInstallAsync(Plan(), "account", Credentials, true, true, ct: TestContext.Current.CancellationToken, rememberSessionConsent: true));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "config", "depot-session")));
    }
    [Fact]
    public async Task RealRunnerHandlesPromptsWithoutNewlines()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux-only process probe");
        await File.WriteAllTextAsync(binary, """
            #!/bin/sh
            while [ "$#" -gt 0 ]; do
              if [ "$1" = -dir ]; then target="$2"; shift; fi
              shift
            done
            printf 'Enter account password for "account": '
            read -r password
            [ "$password" = secret-password ] || { printf 'password handshake failed\n' >&2; exit 21; }
            printf 'STEAM GUARD! Please enter your 2-factor auth code from your authenticator app: ' >&2
            read -r code
            [ "$code" = secret-guard ] || { printf 'guard handshake failed\n' >&2; exit 22; }
            : > "$target/game.exe"
            """, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var real = new DepotDownloaderInstallAdapter(configRoot: Path.Combine(root, "real-config"), binaryPath: binary);
        var plan = real.BuildInstallPlan(game, Path.Combine(root, "real-games"), definition, "linux", "32");
        // Other tests temporarily replace process-wide PATH; the probe uses shell builtins only.
        Directory.CreateDirectory(plan.TargetPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prompts = new List<DepotPrompt>();
        async Task<string> CapturePrompt(DepotPrompt prompt, CancellationToken ct)
        {
            prompts.Add(prompt);
            return await Credentials(prompt, ct);
        }
        try
        {
            Assert.Equal(Path.Combine(plan.TargetPath, "game.exe"), await real.ExecuteInstallAsync(plan, "account", CapturePrompt, true, true, ct: timeout.Token));
        }
        catch (PhaketException ex) when (ex.Error.Parameters.TryGetValue("tail", out var tail))
        {
            Assert.Fail($"Probe failed ({ex.Error.Code}): {tail}");
        }
        Assert.Equal([DepotPrompt.Password, DepotPrompt.SteamGuard], prompts);
    }
    private sealed class CaptureProgress : IProgress<string> { public List<string> Lines { get; } = []; public void Report(string value) => Lines.Add(value); }
    private sealed class FakeRunner : IDepotProcessRunner
    {
        public List<string> Inputs { get; } = [];
        public Func<IReadOnlyList<string>, IDictionary<string, string?>, Func<bool, string, Func<string, Task>, Task>, CancellationToken, Task<int>> Action { get; set; } = (_, _, _, _) => Task.FromResult(0);
        public Task Send(string s) { Inputs.Add(s); return Task.CompletedTask; }
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct) => Action(arguments, environment, output, ct);
    }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException();
    }

    [Fact]
    public async Task LinuxIsolatedStorageProbeStaysInSession()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux-only isolated storage probe");
        var project = Path.Combine(root, "probe"); Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), "using System.IO.IsolatedStorage; using var s = IsolatedStorageFile.GetUserStoreForAssembly(); using var f = new IsolatedStorageFileStream(\"probe-marker\", System.IO.FileMode.Create, s); f.WriteByte(42);", TestContext.Current.CancellationToken);
        var session = Path.Combine(root, "session"); Directory.CreateDirectory(session);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "--project", Path.Combine(project, "probe.csproj") }) start.ArgumentList.Add(arg);
        start.Environment["HOME"] = Path.Combine(session, "home"); start.Environment["XDG_DATA_HOME"] = Path.Combine(session, "data");
        Directory.CreateDirectory(start.Environment["HOME"]!); Directory.CreateDirectory(start.Environment["XDG_DATA_HOME"]!);
        start.Environment["DOTNET_CLI_HOME"] = start.Environment["HOME"];
        using var process = Process.Start(start)!;
        string stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); string stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, stdout + stderr);
        Assert.Single(Directory.EnumerateFiles(session, "probe-marker", SearchOption.AllDirectories));
    }
}
