using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-65: DepotDownloader's -os is the artifact OS the game ships, never the host OS, so an
// Among Us install on Linux (Proton) fetches the Windows depot.
public sealed class Pak65ArtifactOsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak65-os-").FullName;
    private readonly string binary;

    public Pak65ArtifactOsTests()
    {
        Directory.CreateDirectory(root);
        binary = Path.Combine(root, "depot");
        File.WriteAllText(binary, "fake");
    }

    private SteamInstallFlowViewModel Flow(string slug)
    {
        var definition = GameDefinitions.LoadAll().First(g => g.Slug == slug);
        var adapter = new DepotDownloaderInstallAdapter(new NoRun(), Path.Combine(root, "config"), binary);
        var appId = definition.StoreIds!["steam"];
        var vm = new SteamInstallFlowViewModel(new OwnedSteamGame(appId, slug, null, definition, "test", false),
            definition, adapter, () => Task.FromResult<string?>(null), loc: new LocalizationService())
        { TargetDir = Path.Combine(root, slug) };
        vm.OsArch = "64";
        vm.PrepareCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void AmongUsAlwaysPlansTheWindowsArtifact()
    {
        var vm = Flow("among-us");
        Assert.NotNull(vm.Preview);
        Assert.Equal("windows", vm.Preview!.Plan.Os);
    }

    [Fact]
    public void ValheimPlansTheNativeHostArtifact()
    {
        var expected = OperatingSystem.IsWindows() ? "windows" : "linux";
        var vm = Flow("valheim");
        Assert.NotNull(vm.Preview);
        Assert.Equal(expected, vm.Preview!.Plan.Os);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class NoRun : IDepotProcessRunner
    {
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct) => Task.FromResult(0);
    }
}
