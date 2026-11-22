using System.IO.Compression;
using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Core.Platforms;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Phaket.Tests;

public sealed class ModInstallServiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string RootName = "Test-Mod";
    private const string LoaderName = "Test-BepInExPack";
    private const string RootUrl = "https://thunderstore.io/package/download/Test/Mod/1.0.0/";
    private const string LoaderUrl = "https://thunderstore.io/package/download/Test/Loader/1.0.0/";

    private sealed class Catalog(ICatalogTransport inner, IReadOnlyList<ThunderstoreMod> mods) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(new { revision = 1, mods, nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/mods/", StringComparison.Ordinal)) return inner.GetAsync(path, ifNoneMatch, cancellationToken);
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    private sealed class Details(IReadOnlyDictionary<string, ThunderstoreModDetails> values) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            var id = Uri.UnescapeDataString(path[6..]);
            return Task.FromResult(values.TryGetValue(id, out var value)
                ? new CatalogResponse(200, null, JsonSerializer.Serialize(new { mod = value.Mod, versions = value.Versions }, Json))
                : new CatalogResponse(404, null, null));
        }
    }

    private sealed class Packages(IReadOnlyDictionary<string, byte[]> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(files[url == "https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/" ? LoaderUrl : url], writable: false));
    }

    private sealed class CountingPackage(byte[] bytes) : IPackageTransport
    {
        public int Count { get; private set; }
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class Receipts : IReceiptTransport
    {
        public List<ModReceipt> Items { get; } = [];
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken)
        { Items.Add(receipt); return Task.CompletedTask; }
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return buffer.ToArray();
    }

    private static ThunderstoreVersion Version(string name, string version, string url, byte[] zip, params string[] dependencies) =>
        new(version, $"{name}-{version}", url, zip.Length, DateTimeOffset.UnixEpoch, dependencies);

    private static ThunderstoreMod Mod(string name, ThunderstoreVersion latest) => new(name, "among-us",
        name.Split('-')[0], name[(name.IndexOf('-') + 1)..], name, "", "test", "https://example.test", null,
        false, DateTimeOffset.UnixEpoch, latest);

    // The client keeps its SQLite handle open for the whole test; Windows refuses to delete the
    // fixture directory until it is released, so every test disposes the lease before cleanup.
    private sealed class ServiceLease(ModCatalogStore store, ModInstallService service, Receipts receipts) : IDisposable
    {
        public void Deconstruct(out ModInstallService leaseService, out Receipts leaseReceipts)
        { leaseService = service; leaseReceipts = receipts; }
        public void Dispose() => store.Dispose();
    }

    private static Task<ServiceLease> CreateService(string root,
        IReadOnlyDictionary<string, byte[]> packages, params (string Name, ThunderstoreVersion[] Versions)[] entries)
    {
        var versions = entries.ToDictionary(e => e.Name, e => e.Versions, StringComparer.OrdinalIgnoreCase);
        var mods = entries.Select(e => Mod(e.Name, e.Versions[^1])).ToArray();
        var details = entries.ToDictionary(e => e.Name, e => new ThunderstoreModDetails(Mod(e.Name, e.Versions[^1]), e.Versions, DateTimeOffset.UnixEpoch), StringComparer.OrdinalIgnoreCase);
        var transport = new Catalog(new Details(details), mods);
        var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
        var receipts = new Receipts();
        return Task.FromResult(new ServiceLease(store, new ModInstallService(root,
            new ModCatalogClient(transport, store), new Packages(packages), receipts,
            new ProfileStore(Path.Combine(root, "profiles"))), receipts));
    }

// N3: the version gate belongs to the service, so every caller (catalog button,
    // profile update, direct service use) refuses an unavailable version before a plan.
    [Fact]
    public async Task PrepareRefusesAVersionTheSourceMarkedUnavailable()
    {
        var root = Directory.CreateTempSubdirectory("phaket-unavailable-version-").FullName;
        try
        {
            var version = new ThunderstoreVersion("2.0", $"{RootName}-2.0", "", null, DateTimeOffset.UnixEpoch, [],
                true, Availability: "unavailable");
            using var lease = await CreateService(root, new Dictionary<string, byte[]>(), (RootName, [version]));
            var (service, _) = lease;

            var error = await Assert.ThrowsAsync<PhaketException>(() => service.PrepareAsync("among-us", RootName,
                "2.0", "", new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCodes.ModArtifactUnavailable, error.Error.Code);
            Assert.Equal($"{RootName} 2.0", error.Error.Parameters["artifact"]);
            Assert.Equal("x86", error.Error.Parameters["arch"]);
            Assert.Equal("steam", error.Error.Parameters["store"]);
        }
        finally { Directory.Delete(root, true); }
    }

    // N9: a version is matched character for character (as the server publishes it); the
    // full name stays case-insensitive like a Thunderstore package name.
    [Fact]
    public async Task VersionMatchIsExactWhileTheFullNameIsCaseInsensitive()
    {
        var root = Directory.CreateTempSubdirectory("phaket-version-identity-").FullName;
        try
        {
            var zip = Zip(("plugins/Mod.dll", "plugin"));
            var version = Version(RootName, "1.0.0", RootUrl, zip);
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [RootUrl] = zip },
                (RootName, [version]));
            var (service, _) = lease;

            var preparation = await service.PrepareAsync("among-us", "test-mod", "1.0.0", "",
                new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken);
            Assert.Equal("1.0.0", preparation.Version);

            var error = await Assert.ThrowsAsync<PhaketException>(() => service.PrepareAsync("among-us", RootName,
                "1.0", "", new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken));
            Assert.Equal("DEPENDENCY_VERSION_MISSING", error.Error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

// CI has no research artifacts: a test that needs a real archive skips instead of failing.
    private static byte[] FixtureBytes(string name)
    {
        var path = Path.Combine("/tmp/opencode/phaket-research/artifacts", name);
        if (!File.Exists(path)) Assert.Skip($"Real artifact {name} is not available locally.");
        return File.ReadAllBytes(path);
    }

    private static (byte[] Root, byte[] Loader) TestPackages() =>
        (Zip(("plugins/Mod.dll", "plugin")), FixtureBytes("amongus-loader-6.0.700.zip"));

    [Fact]
    public async Task SandboxLoaderWithoutOverrideUsesTheDefinitionDefaultArchitecture()
    {
        var root = Directory.CreateTempSubdirectory("phaket-unknown-arch-").FullName;
        try
        {
            var loader = Version(LoaderName, "1.0.0", LoaderUrl, []);
            var x64 = new Dictionary<string, byte[]>
            {
                ["https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip"] =
                    FixtureBytes("bepinex-il2cpp-x64-be793.zip")
            };
            using var lease = await CreateService(root, x64, (LoaderName, [loader]));
            var (service, _) = lease;
            // PAK-65: a sandbox has nothing to probe, so the current retail architecture from the
            // game definition is the plan's fact — the x64 loader pack, not a refusal.
            var prep = await service.PrepareAsync("among-us", LoaderName, "1.0.0", "", new("", "steam", true),
                ct: TestContext.Current.CancellationToken);
            var loaderStep = Assert.Single(Assert.Single(prep.Plans).Operations);
            Assert.Equal("loader.ensure", loaderStep.Kind);
            Assert.Equal("6.0.0-be.793", loaderStep.Parameters!["version"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SandboxLoaderWithExplicitX86OverrideUsesTheX86Pack()
    {
        var root = Directory.CreateTempSubdirectory("phaket-sandbox-x86-").FullName;
        try
        {
            var (modZip, loaderZip) = TestPackages();
            var loader = Version(LoaderName, "1.0.0", LoaderUrl, loaderZip);
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [LoaderUrl] = loaderZip }, (LoaderName, [loader]));
            var (service, _) = lease;
            var prep = await service.PrepareAsync("among-us", LoaderName, "1.0.0", "", new("", "steam", true, "x86"),
                ct: TestContext.Current.CancellationToken);
            var loaderStep = Assert.Single(Assert.Single(prep.Plans).Operations);
            Assert.Equal("6.0.700", loaderStep.Parameters!["version"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealInstallWithUndetectableArchitectureIsRejected()
    {
        var root = Directory.CreateTempSubdirectory("phaket-real-unknown-arch-").FullName;
        try
        {
            // A real game directory whose executable is not a PE: the architecture is unknown and
            // must never be guessed (PAK-65).
            var gameRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(gameRoot);
            File.WriteAllText(Path.Combine(gameRoot, "Among Us.exe"), "not a PE");
            var loader = Version(LoaderName, "1.0.0", LoaderUrl, []);
            using var lease = await CreateService(root, new Dictionary<string, byte[]>(), (LoaderName, [loader]));
            var (service, _) = lease;
            var ex = await Assert.ThrowsAsync<PhaketException>(() => service.PrepareAsync("among-us", LoaderName,
                "1.0.0", "", new(gameRoot, "steam", false), ct: TestContext.Current.CancellationToken));
            Assert.Equal("LOADER_ARCH_UNAVAILABLE", ex.Error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrepareDownloadsModArchiveOnlyOnce()
    {
        var root = Directory.CreateTempSubdirectory("phaket-one-download-").FullName;
        try
        {
            var zip = Zip(("plugins/Mod.dll", "plugin"));
            var version = Version(RootName, "1.0.0", RootUrl, zip);
            var mod = Mod(RootName, version);
            var details = new Dictionary<string, ThunderstoreModDetails>
            {
                [RootName] = new(mod, [version], DateTimeOffset.UnixEpoch)
            };
            using var store = new ModCatalogStore(Path.Combine(root, "catalog.db"));
            var client = new ModCatalogClient(new Catalog(new Details(details), [mod]), store);
            var transport = new CountingPackage(zip);
            var service = new ModInstallService(root, client, transport);
            var prep = await service.PrepareAsync("among-us", RootName, "1.0.0", "", new("", "steam", true, "x86"),
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(1, transport.Count);
            Assert.True(File.Exists(Path.Combine(prep.PackageRoots[0], "package.zip")));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task DetectedRealTargetRequiresAcceptanceAndInstallsIntoFixture()
    {
        var root = Directory.CreateTempSubdirectory("phaket-real-target-").FullName;
        var gameRoot = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "Among Us_Data"));
        File.WriteAllText(Path.Combine(gameRoot, "Among Us.exe"), "fixture");
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "config"));
        var config = Path.Combine(gameRoot, "BepInEx", "config", "user.cfg");
        File.WriteAllText(config, "keep");
        try
        {
            var (modZip, loaderZip) = TestPackages();
            var loader = Version(LoaderName, "1.0.0", LoaderUrl, loaderZip);
            var mod = Version(RootName, "1.0.0", RootUrl, modZip, "Test-BepInExPack-1.0.0");
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [RootUrl] = modZip, [LoaderUrl] = loaderZip }, (LoaderName, [loader]), (RootName, [mod]));
            var (service, receipts) = lease;
            var definitions = GameDefinitions.LoadAll();
            var game = new GameCatalogItem(definitions.First(g => g.Slug == "among-us"), "", "");
            game.Detected = new SteamAdapter(definitions).FromManualFolder(gameRoot, game.SteamAppId);
            var modsVm = new ModsViewModel(game, () => { }, root);
            if (modsVm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
            var vm = modsVm.Catalog;
            Assert.True(vm.SelectedTarget!.Target.Sandbox);
            vm.SelectedTarget = Assert.Single(vm.Targets, t => !t.Target.Sandbox);
            InstallPreparation? plan = null;
            vm.PrepareOverride = async (name, version, endpoint, target, overrides) => plan = await service.PrepareAsync("among-us", name, version, endpoint, target with { Arch = "x86" }, overrides);
            vm.ExecuteOverride = (p, accepted, progress) => service.ExecuteAsync(p, accepted, progress);
            vm.SelectedMod = Mod(RootName, mod);
            await vm.InstallVersionCommand.ExecuteAsync(mod);
            Assert.True(vm.HasPreview, vm.InstallStatus);
            Assert.Contains(vm.Operations, op => op.TargetPath.StartsWith(gameRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            modsVm.SelectedTabIndex = 1;
            var window = new Window { Width = 1280, Height = 900, Content = new Phaket.Views.ModsView { DataContext = modsVm } };
            window.Show(); Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using (var frame = window.CaptureRenderedFrame()) { Assert.NotNull(frame); frame.Save("/tmp/opencode/phaket-render/realtarget-preview.png"); }
            window.Close();
            await vm.ExecuteInstallCommand.ExecuteAsync(null);
            Assert.False(File.Exists(Path.Combine(gameRoot, "winhttp.dll")));
            vm.PlanAccepted = true;
            await vm.ExecuteInstallCommand.ExecuteAsync(null);
            Assert.True(File.Exists(Path.Combine(gameRoot, "winhttp.dll")), vm.InstallStatus);
            Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", RootName, "Mod.dll")));
            var entries = new InstallRegistry(Path.Combine(root, "installs.json")).All();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Equal(gameRoot, e.GameRoot));
            Assert.Equal("installed", Assert.Single(receipts.Items).Result);
            foreach (var (p, index) in plan!.Plans.Select((p, i) => (p, i)).Reverse())
            {
                var ctx = new PlanExecutionContext(gameRoot, plan.ProfileRoot, plan.StagingRoot, plan.PackageRoots[index], game.Definition, p.RecipeRef.Id, "mod-profile", plan.StateRoot);
                new PlanExecutor(ctx).Uninstall(p.RecipeRef.Id);
            }
            Assert.False(File.Exists(Path.Combine(gameRoot, "winhttp.dll")));
            Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", RootName, "Mod.dll")));
            Assert.Equal("keep", File.ReadAllText(config));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RegistryReadsOldEntriesAndRoundTripsIdentity()
    {
        var root = Directory.CreateTempSubdirectory("registry-test-").FullName;
        try
        {
            var path = Path.Combine(root, "installs.json");
            var registry = new InstallRegistry(path);
            var entry = new InstalledRecipe("recipe", "1", "among-us", "steam", "/game", "/profile", "/stage", "/state", null, DateTimeOffset.UnixEpoch);
            registry.Add(entry);
            // System.Text.Json indents with the platform newline, so normalise CRLF before stripping the new fields.
            var json = File.ReadAllText(path).Replace("\r\n", "\n")
                .Replace(",\n    \"ModFullName\": \"\",\n    \"ModVersion\": \"\"", "");
            Assert.DoesNotContain("ModFullName", json);
            File.WriteAllText(path, json);
            var old = Assert.Single(registry.All());
            Assert.Equal("", old.ModFullName);
            Assert.Equal("", old.ModVersion);
            registry.Add(old with { ModFullName = "Owner-Mod", ModVersion = "2.1" });
            var saved = Assert.Single(registry.All());
            Assert.Equal("Owner-Mod", saved.ModFullName);
            Assert.Equal("2.1", saved.ModVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrepareDoesNotCreateRealGameDirectory()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-plan-").FullName;
        try
        {
            var zip = Zip(("plugins/Mod.dll", "plugin"));
            var version = Version(RootName, "1.0.0", RootUrl, zip);
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [RootUrl] = zip }, (RootName, [version]));
            var (service, _) = lease;
            var gameRoot = Path.Combine(root, "nonexistent-game");
            await Assert.ThrowsAsync<PhaketException>(() => service.PrepareAsync("among-us", RootName, "1.0.0", "", new(gameRoot, "steam", false)));
            Assert.False(Directory.Exists(gameRoot));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrepareAndExecuteInstallsDependencyFilesRegistryProfileAndReceipt()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-").FullName;
        var (modZip, loaderZip) = TestPackages();
        var loader = Version(LoaderName, "1.0.0", LoaderUrl, loaderZip);
        var mod = Version(RootName, "1.0.0", RootUrl, modZip, "Test-BepInExPack-1.0.0");
        try
        {
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [RootUrl] = modZip, [LoaderUrl] = loaderZip },
                (LoaderName, [loader]), (RootName, [mod]));
            var (service, receipts) = lease;
            var prep = await service.PrepareAsync("among-us", RootName, "1.0.0", "", new("", "steam", true, "x86"));
            var outcome = await service.ExecuteAsync(prep, true);
            Assert.True(outcome.Success, outcome.ErrorCode);
            Assert.Equal(2, outcome.Journals.Count);
            Assert.Equal(2, prep.PackageRoots.Count);
            Assert.NotEqual(prep.PackageRoots[0], prep.PackageRoots[1]);
            var game = Path.Combine(prep.SandboxRoot, "game");
            Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(game, "BepInEx", "plugins", RootName, "Mod.dll")));
            Assert.Contains("thunderstore.test.bepinexpack", File.ReadAllText(Path.Combine(root, "installs.json")));
            var entries = new InstallRegistry(Path.Combine(root, "installs.json")).All();
            Assert.Contains(entries, e => e.ModFullName == RootName && e.ModVersion == "1.0.0");
            Assert.Contains(entries, e => e.ModFullName == LoaderName && e.ModVersion == "1.0.0");
            var profile = new ProfileStore(Path.Combine(root, "profiles")).Active("among-us")!;
            Assert.Contains(profile.Mods, m => m.Name == "BepInEx IL2CPP");
            Assert.Contains(profile.Mods, m => m.Name == "Custom Roles");
            var receipt = Assert.Single(receipts.Items);
            Assert.Equal("installed", receipt.Result);
            Assert.Equal("among-us", receipt.GameId);
            Assert.Equal(RootName, receipt.ModFullName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExecuteRequiresExplicitPlanAcceptance()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-").FullName;
        try
        {
            using var lease = await CreateService(root, new Dictionary<string, byte[]>());
            var (service, _) = lease;
            var prep = new InstallPreparation("among-us", RootName, "1.0.0", "", new("", "steam", true), [], [], [], [], "", "", "", "", "");
            var error = await Assert.ThrowsAsync<PhaketException>(() => service.ExecuteAsync(prep, false));
            Assert.Equal("PLAN_NOT_ACCEPTED", error.Error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrepareReportsVersionConflictAndOverrideResolvesIt()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-").FullName;
        var (modZip, loaderZip) = TestPackages();
        var loader1 = Version(LoaderName, "1.0.0", LoaderUrl, loaderZip);
        var loader2 = Version(LoaderName, "2.0.0", "https://thunderstore.io/package/download/Test/Loader/2.0.0/", loaderZip);
        var mod = Version(RootName, "1.0.0", RootUrl, modZip, "Test-BepInExPack-1.0.0", "Test-Other-1.0.0");
        var otherUrl = "https://thunderstore.io/package/download/Test/Other/1.0.0/";
        var other = Version("Test-Other", "1.0.0", otherUrl, modZip, "Test-BepInExPack-2.0.0");
        try
        {
            var packages = new Dictionary<string, byte[]> { [RootUrl] = modZip, [LoaderUrl] = loaderZip, [otherUrl] = modZip };
            using var lease = await CreateService(root, packages, (LoaderName, [loader1, loader2]), (RootName, [mod]), ("Test-Other", [other]));
            var (service, _) = lease;
            var conflictPrep = await service.PrepareAsync("among-us", RootName, "1.0.0", "", new("", "steam", true));
            var conflict = Assert.Single(conflictPrep.Conflicts);
            Assert.Equal(LoaderName, conflict.Package);
            Assert.Equal("2.0.0", conflict.SuggestedVersion);
            packages["https://thunderstore.io/package/download/Test/Loader/2.0.0/"] = loaderZip;
            var prep = await service.PrepareAsync("among-us", RootName, "1.0.0", "", new("", "steam", true, "x86"),
                new Dictionary<string, string> { [LoaderName] = "2.0.0" });
            Assert.Empty(prep.Conflicts);
            Assert.NotEmpty(prep.Plans);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PrepareRejectsCorruptPackage()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-").FullName;
        var url = RootUrl;
        try
        {
            var mod = Version(RootName, "1.0.0", url, [1, 2, 3]);
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [url] = [1, 2, 3] }, (RootName, [mod]));
            var (service, _) = lease;
            await Assert.ThrowsAnyAsync<Exception>(() => service.PrepareAsync("among-us", RootName, "1.0.0", "", new("", "steam", true)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledExecutionLeavesNoGameFiles()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-").FullName;
        var (modZip, loaderZip) = TestPackages();
        var loader = Version(LoaderName, "1.0.0", LoaderUrl, loaderZip);
        var mod = Version(RootName, "1.0.0", RootUrl, modZip, "Test-BepInExPack-1.0.0");
        try
        {
            using var lease = await CreateService(root, new Dictionary<string, byte[]> { [RootUrl] = modZip, [LoaderUrl] = loaderZip }, (LoaderName, [loader]), (RootName, [mod]));
            var (service, _) = lease;
            var prep = await service.PrepareAsync("among-us", RootName, "1.0.0", "", new("", "steam", true, "x86"));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(prep, true, ct: cts.Token));
            var game = Path.Combine(prep.SandboxRoot, "game");
            Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
            var plugins = Path.Combine(game, "BepInEx", "plugins");
            Assert.False(Directory.Exists(plugins) && Directory.EnumerateFileSystemEntries(plugins).Any());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptSecondPackageRollsBackFirstPackageAndRecordsFailure()
    {
        var root = Directory.CreateTempSubdirectory("mod-install-rollback-").FullName;
        var (modZip, loaderZip) = TestPackages();
        var corruptUrl = "https://thunderstore.io/package/download/Test/Mod/2.0.0/";
        var loader = Version(LoaderName, "1.0.0", LoaderUrl, loaderZip);
        var mod = Version(RootName, "1.0.0", RootUrl, modZip, "Test-BepInExPack-1.0.0");
            var corruptMod = Version(RootName, "2.0.0", corruptUrl, modZip, "Test-BepInExPack-1.0.0");
        try
        {
            using var lease = await CreateService(root,
                new Dictionary<string, byte[]> { [RootUrl] = modZip, [LoaderUrl] = loaderZip, [corruptUrl] = modZip },
                (LoaderName, [loader]), (RootName, [mod, corruptMod]));
            var (service, receipts) = lease;
            var prep = await service.PrepareAsync("among-us", RootName, "2.0.0", "", new("", "steam", true, "x86"), ct: TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(prep.PackageRoots[1], "package.zip"), [1, 2, 3], TestContext.Current.CancellationToken);
            var outcome = await service.ExecuteAsync(prep, true, ct: TestContext.Current.CancellationToken);
            Assert.False(outcome.Success);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(prep.SandboxRoot, "game"), "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("Among Us.exe", StringComparison.Ordinal)));
            Assert.Empty(new InstallRegistry(Path.Combine(root, "installs.json")).All());
            Assert.DoesNotContain(new ProfileStore(Path.Combine(root, "empty-profile-store")).Active("among-us")?.Mods ?? [], m => m.Name == RootName);
            Assert.Equal("failed", Assert.Single(receipts.Items).Result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LiveAmongUsInstallUsesBackendCatalogAndReportsReceipt()
    {
        await LiveServer.RequireAsync();
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var root = Directory.CreateTempSubdirectory("mod-install-live-");
        try
        {
            using var db = new ModCatalogStore(Path.Combine(root.FullName, "catalog.db"));
            using var http = new HttpClient { BaseAddress = new Uri(LiveServer.Url), Timeout = TimeSpan.FromMinutes(10) };
            using var packageHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var client = new ModCatalogClient(new HttpCatalogTransport(http), db);
            var service = new ModInstallService(root.FullName, client, new HttpPackageTransport(packageHttp),
                profiles: new ProfileStore(Path.Combine(root.FullName, "profiles")));
            var prep = await service.PrepareAsync("among-us", "Cosmella-Outfit_Presets", "1.1.1", LiveServer.Url, new("", "steam", true, "x86"));
            var outcome = await service.ExecuteAsync(prep, true);
            Assert.True(outcome.Success, outcome.ErrorCode);
            var game = Path.Combine(prep.SandboxRoot, "game");
            Assert.True(Directory.Exists(Path.Combine(game, "BepInEx", "plugins", "Cosmella-Outfit_Presets")));
            Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
            using var response = await probe.GetAsync($"{LiveServer.Url}/receipts/summary?game=among-us&mod=Cosmella-Outfit_Presets");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("installed").GetInt32() >= 1, body);
            Console.WriteLine($"LIVE_RECEIPTS_SUMMARY {body}");
        }
        finally { root.Delete(recursive: true); }
    }
}
