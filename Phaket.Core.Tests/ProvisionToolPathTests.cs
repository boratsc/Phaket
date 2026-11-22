using System.Net;
using Phaket.Core.Accounts;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Tools;

namespace Phaket.Core.Tests;

// e2e follow-up: provision used to read only PHAKET_DEPOTDOWNLOADER / PHAKET_LEGENDARY,
// while the binaries installed through Settings live under tools/<tool>/<version>/.
// The environment stays the override; the managed install is the product default, and a
// missing binary is downloaded through ToolProvider instead of refusing (PAK-102).
public sealed class ProvisionToolPathTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-provision-tools-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    private async Task<SteamCredentialStore> SignedInAsync()
    {
        var store = new SteamCredentialStore(new InMemorySecretStore());
        await store.SaveAsync("tester", "stored-password");
        return store;
    }

    private static HttpClient Offline() =>
        new(new ThrowingHandler());

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("no network"));
    }

    private sealed class RecordingRunner : Phaket.Core.Platforms.IDepotProcessRunner
    {
        public string? Executable { get; private set; }

        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
        {
            Executable = executable;
            return Task.FromResult(0);
        }
    }

    [Fact]
    public async Task ManagedDepotDownloaderIsUsedWhenNoPathIsConfigured()
    {
        var tools = Path.Combine(root, "tools");
        var binary = Path.Combine(tools, "depotdownloader", "3.4.0", OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        // The file content does not matter: the injected runner proves which path ran.
        File.WriteAllText(binary, "not a real binary");
        var runner = new RecordingRunner();
        // "" stands for "no env override" without mutating the process environment.
        var source = new ToolBaseGameSource(depotDownloaderPath: "", legendaryPath: "",
            timeout: TimeSpan.FromSeconds(60), toolProvider: new ToolProvider(tools, Offline()),
            steamCredentials: await SignedInAsync(), steamPrompt: (_, _) => Task.FromResult("guard"),
            runner: runner, settingsPath: Path.Combine(root, "settings.json"));

        var prepared = await source.TryPrepareAsync("steam", "2026.8.18", "9223372036854775807",
            Path.Combine(root, "cache"));

        Assert.True(prepared.Success, prepared.ErrorCode);
        Assert.Equal(Path.GetFullPath(binary), runner.Executable);
    }

    // A GUI-launched app often has no dotnet on PATH; the usual Linux installer
    // locations are checked before refusing.
    [Fact]
    public void DotnetHostFallsBackToInstallerLocations()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var pathDir = Path.Combine(root, "path-bin");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(pathDir);
        Directory.CreateDirectory(home);
        var onPath = Path.Combine(pathDir, name);
        File.WriteAllText(onPath, "");

        // PATH first, then the Linux installer locations; a machine that happens to
        // have dotnet in /usr/share must not change the outcome.
        var candidates = ToolBaseGameSource.DotnetHostCandidates(name, pathDir, home).ToArray();
        Assert.Equal(onPath, candidates[0]);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("/usr/share/dotnet/" + name, candidates);
            Assert.Contains(Path.Combine(home, ".dotnet", name), candidates);
        }
        Assert.Equal(onPath, ToolBaseGameSource.FindDotnetHost(name, pathDir, home));
    }

    [Fact]
    public async Task MissingManagedToolReturnsTheToolErrorInsteadOfRefusing()
    {
        // No binary under the managed root: ToolProvider tries to download it. The
        // injected HTTP layer is offline, so the test never touches the network.
        var source = new ToolBaseGameSource(depotDownloaderPath: "", legendaryPath: "",
            toolProvider: new ToolProvider(Path.Combine(root, "empty-tools"), Offline()),
            steamCredentials: await SignedInAsync(), steamPrompt: (_, _) => Task.FromResult("guard"),
            settingsPath: Path.Combine(root, "settings.json"));

        var prepared = await source.TryPrepareAsync("steam", "2026.8.18", "9223372036854775807",
            Path.Combine(root, "cache"));

        Assert.False(prepared.Success);
        Assert.Equal("TOOL_OFFLINE", prepared.ErrorCode);
    }

    [Fact]
    public async Task MissingManifestExplainsTheBuildRegistry()
    {
        var source = new ToolBaseGameSource(depotDownloaderPath: "", legendaryPath: "",
            toolProvider: new ToolProvider(Path.Combine(root, "unused"), Offline()));

        var prepared = await source.TryPrepareAsync("steam", "2026.8.18", null, Path.Combine(root, "cache"));

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameManifestMissing, prepared.ErrorCode);
        Assert.Equal("2026.8.18", prepared.Parameters!["version"]);
        var message = ErrorMessages.Format(prepared.ErrorCode!, "pl-PL", prepared.Parameters);
        Assert.Contains("Builds", message);
        Assert.Contains("GITHUB_TOKEN", message);
    }

    [Fact]
    public async Task WithoutStoredSignInTheProvisionRefusesWithAnInstruction()
    {
        var source = new ToolBaseGameSource(depotDownloaderPath: "", legendaryPath: "",
            toolProvider: new ToolProvider(Path.Combine(root, "unused"), Offline()),
            steamCredentials: new SteamCredentialStore(new InMemorySecretStore()));

        var prepared = await source.TryPrepareAsync("steam", "2026.8.18", "9223372036854775807",
            Path.Combine(root, "cache"));

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameSignInRequired, prepared.ErrorCode);
        Assert.Contains("Bibliotece", ErrorMessages.Format(prepared.ErrorCode!, "pl-PL"));
    }
}