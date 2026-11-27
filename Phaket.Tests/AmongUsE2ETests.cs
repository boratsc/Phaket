using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Launch;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-96 automated gate: catalog -> recipe -> install -> direct launch -> stop -> uninstall.
// Every game and artifact lives under a fresh temp root; no network, Steam, Epic or real game.
public sealed class AmongUsE2ETests
{
    private const string ModName = "Sus-Submerged";
    private const string ModId = "sus-submerged";
    private const string PluginFolder = "sus-submerged";
    private const string LoaderName = "BepInEx-BepInExPack_AmongUs";
    private const string LoaderVersion = "6.0.700";
    private const string LoaderUrl = "https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/";
    private const string BuildId = "24302054";
    private const string GameVersion = "2026.8.18";
    private const string CatalogGameVersion = "2026-8-18";
    private const string RealLoaderHash = "88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ModUrl(string version) => $"https://susmodder-cdn.ovh/mods/submerged/{version}.zip";
    private static string Plugin(string root, string file) =>
        Path.Combine(root, "BepInEx", "plugins", PluginFolder, file);

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                var entry = archive.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        return buffer.ToArray();
    }

    private static byte[] LoaderZip() => Zip(
        ($"{LoaderName}/winhttp.dll", "fixture BepInEx proxy"),
        ($"{LoaderName}/doorstop_config.ini", "[Loader]\nfixture=true\n"),
        ($"{LoaderName}/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "fixture IL2CPP chainloader"),
        ($"{LoaderName}/BepInEx/core/BepInEx.Preload.dll", "fixture preload"));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // A minimal PE32 (x86) header: the service probes the fixture instead of trusting a passed fact.
    private static void WritePe(string path)
    {
        var bytes = new byte[512];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x14c); U16(0x94, 224); U16(0x98, 0x10b); U32(190, 16);
        File.WriteAllBytes(path, bytes);
    }

    private static Dictionary<string, string> Tree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), HashFile, StringComparer.Ordinal);

    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));

    private static void AssertNoTemps(string root) => Assert.DoesNotContain(
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
        path => path.EndsWith(".phaket-tmp", StringComparison.OrdinalIgnoreCase));

    private sealed class Catalog(IReadOnlyList<ThunderstoreMod> mods,
        IReadOnlyDictionary<string, ThunderstoreVersion[]> versions) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { revision = 1, mods, nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/mods/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/mods/".Length..]);
                var mod = mods.SingleOrDefault(item => item.Id == id);
                return Task.FromResult(mod is null
                    ? new CatalogResponse(404, null, null)
                    : new CatalogResponse(200, null,
                        JsonSerializer.Serialize(new { mod, versions = versions[id] }, Json)));
            }
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    private sealed class Packages(IReadOnlyDictionary<string, byte[]> files) : IPackageTransport
    {
        public List<string> Requested { get; } = [];
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken)
        {
            Requested.Add(source);
            return files.TryGetValue(source, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                : throw new InvalidOperationException($"unexpected network fixture request: {source}");
        }
    }

    private sealed class Receipts : IReceiptTransport
    {
        public List<ModReceipt> Items { get; } = [];
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken)
        { Items.Add(receipt); return Task.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ModCatalogStore store;
        public string Root { get; } = Directory.CreateTempSubdirectory("phaket-among-us-e2e-").FullName;
        public string GameRoot { get; }
        public GameDefinition Definition { get; }
        public ModInstallService Service { get; }
        public Packages Packages { get; }
        public Receipts Receipts { get; } = new();
        public byte[] ModV1Bytes { get; }
        public byte[] ModV2Bytes { get; }

        public Fixture(byte[]? loaderBytes = null, bool validPe = true)
        {
            Definition = GameDefinitions.LoadAll().Single(item => item.Slug == "among-us");
            GameRoot = Path.Combine(Root, "steamapps", "common", "Among Us");
            Directory.CreateDirectory(Path.Combine(GameRoot, "Among Us_Data"));
            Directory.CreateDirectory(Path.Combine(GameRoot, "BepInEx", "config"));
            if (validPe) WritePe(Path.Combine(GameRoot, "Among Us.exe"));
            else File.WriteAllText(Path.Combine(GameRoot, "Among Us.exe"), "not a PE fixture");
            File.WriteAllText(Path.Combine(GameRoot, "Among Us_Data", "sharedassets0.assets"), "vanilla assets");
            File.WriteAllText(Path.Combine(GameRoot, "BepInEx", "config", "user.cfg"), "user = keep\n");
            File.WriteAllText(Path.Combine(Root, "steamapps", "appmanifest_945360.acf"), $$"""
                "AppState"
                {
                    "appid"     "945360"
                    "name"      "Among Us"
                    "buildid"   "{{BuildId}}"
                    "branch"    "public"
                    "manifest"  "1"
                    "installdir" "Among Us"
                }
                """);

            using (var builds = new CatalogStore(Path.Combine(Root, "catalog.db")))
                builds.ReplaceAll(JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        id = "among-us-steam-2026.8.18", gameId = "among-us", store = "steam", versionLabel = GameVersion,
                        buildId = BuildId, depotIds = new[] { "945361" }, manifestIds = new[] { "9223372036854775807" }, branch = "public",
                        availabilityStatus = "known", source = "susmodder", sourceReference = "fixture",
                        observedAt = "2026-11-20T00:00:00Z", verifiedAt = (string?)null, confidence = 0.9,
                        provenance = "fixture", releasedAt = "2026-08-18T00:00:00Z", arch = "x86",
                        aliases = new[] { CatalogGameVersion },
                    }
                }), 1, null);

            loaderBytes ??= LoaderZip();
            ModV1Bytes = Zip(("plugins/Submerged.dll", "submerged v1"), ("plugins/Readme.txt", "v1 docs"));
            ModV2Bytes = Zip(("plugins/Submerged.dll", "submerged v2"), ("plugins/NewFeature.dll", "new v2 feature"));
            var v1 = ModVersion("1.0.0", ModV1Bytes);
            var v2 = ModVersion("2.0.0", ModV2Bytes);
            var mod = Mod([v1, v2]);
            var loader = new ThunderstoreVersion(LoaderVersion, $"{LoaderName}-{LoaderVersion}", LoaderUrl,
                loaderBytes.Length, DateTimeOffset.UnixEpoch, []);
            var loaderMod = new ThunderstoreMod(LoaderName, "among-us", "BepInEx", "BepInExPack_AmongUs", LoaderName,
                "fixture loader", "BepInEx", "https://example.test", null, false, DateTimeOffset.UnixEpoch, loader);

            // The internal override points at this package-local artifact. No transport call is made.
            var loaderDirectory = Path.Combine(Root, "packages", $"{LoaderName}-{LoaderVersion}");
            Directory.CreateDirectory(loaderDirectory);
            File.WriteAllBytes(Path.Combine(loaderDirectory, "loader.zip"), loaderBytes);
            // The planner resolves package://loader, while the service preview uses the recipe
            // artifact id (package) for its ZIP path; keep both names for this local fixture.
            File.WriteAllBytes(Path.Combine(loaderDirectory, "package.zip"), loaderBytes);
            Packages = new Packages(new Dictionary<string, byte[]>
            {
                [ModUrl("1.0.0")] = ModV1Bytes,
                [ModUrl("2.0.0")] = ModV2Bytes,
            });
            store = new ModCatalogStore(Path.Combine(Root, "catalog.db"));
            Service = new ModInstallService(Root, new ModCatalogClient(new Catalog([mod, loaderMod],
                new Dictionary<string, ThunderstoreVersion[]> { [ModId] = [v1, v2], [LoaderName] = [loader] }), store),
                Packages, Receipts, new ProfileStore(Path.Combine(Root, "profiles")))
            {
                LoaderArtifactOverride = (_, _) => new RecipeArtifact
                {
                    Id = "loader", Source = "package://loader", Sha256 = Hash(loaderBytes), Arch = "x86",
                    SizeBytes = loaderBytes.Length,
                },
            };
        }

        private ThunderstoreVersion ModVersion(string version, byte[] bytes)
        {
            var url = ModUrl(version);
            return new(version, $"{ModName}-{version}", url, bytes.Length, DateTimeOffset.UnixEpoch,
                [$"{LoaderName}-{LoaderVersion}"], true,
                [new ModArtifact(url, "x86", ["steam"], Hash(bytes), bytes.Length, "susmodder-cdn", $"submerged-{version}.zip")],
                "dll", CatalogGameVersion, "x86", "susmodder", ModId);
        }

        private static ThunderstoreMod Mod(ThunderstoreVersion[] versions) => new(
            ModId, "among-us", "SusModder", ModName, ModName, "fixture addon", "SusModder", "https://example.test",
            null, false, DateTimeOffset.UnixEpoch, versions[^1], Packaging: "dll", AmongVersion: CatalogGameVersion,
            Source: "susmodder", ExternalId: ModId);

        public Task<InstallPreparation> PrepareAsync(string version = "1.0.0") => Service.PrepareAsync(
            "among-us", ModName, version, "", new(GameRoot, "steam", false), ct: Ct);

        public PlanExecutionContext Context(InstallPreparation preparation, int index)
        {
            var plan = preparation.Plans[index];
            return new PlanExecutionContext(GameRoot,
                preparation.PlanProfileRoots is { Count: > 0 } roots ? roots[index] : preparation.ProfileRoot,
                preparation.StagingRoot, preparation.PackageRoots[index], Definition, plan.RecipeRef.Id,
                "mod-profile", preparation.StateRoot,
                VanillaCacheRoot: Path.Combine(Root, "vanilla-cache"), InstallationId: plan.InstallationRef);
        }

        public void Dispose()
        {
            store.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CancelAfterDone(int count, CancellationTokenSource cancellation) : IProgress<InstallProgress>
    {
        public int Done { get; private set; }
        public void Report(InstallProgress value)
        {
            if (value.Phase != "done") return;
            Done++;
            if (Done == count) cancellation.Cancel();
        }
    }

    private sealed class CountingCloner : IFileCloner
    {
        public int Clones { get; private set; }
        public bool Probe(string directory) => true;
        public bool TryClone(string source, string destination) { Clones++; return false; }
    }

    private static (string Executable, string Arguments) InstallLaunchHelper(string gameRoot)
    {
        var executable = Path.Combine(gameRoot, "Among Us.exe");
        if (OperatingSystem.IsWindows())
        {
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), executable, true);
            return (executable, "-n 300 127.0.0.1");
        }
        File.Copy("/bin/sleep", executable, true);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (executable, "300");
    }

    private static GameCatalogItem DetectedGame(Fixture fixture, GameDefinition? definition = null)
    {
        var game = new GameCatalogItem(definition ?? fixture.Definition, "", "");
        game.Detected = new SteamInstallation(
            new Installation("steam-945360", "among-us", fixture.GameRoot),
            new SteamAppManifest("945360", "Among Us", BuildId, "public", "1", "Among Us", 1),
            new SteamLibrary(Path.Combine(fixture.Root, "steam"), false), GameAvailability.Moddable);
        return game;
    }

    private static void AssertGone(ProcessRecord record)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var (found, identity) = ProcessIdentity.TryGetProcessIdentity(record.Pid);
            if (!found || identity?.StartValue != record.StartTime) return;
            Assert.True(DateTime.UtcNow < deadline, $"fixture process {record.Pid} is still alive");
            Thread.Sleep(10);
        }
    }

    private static void KillOwnedProcess(ProcessRecord? record)
    {
        if (record is null) return;
        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(record.Pid);
        if (!found || identity?.StartValue != record.StartTime) return;
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [AvaloniaFact]
    public async Task Catalog_to_recipe_to_install_to_launch_to_stop_to_uninstall_preserves_vanilla_and_shared_loader()
    {
        using var fixture = new Fixture();
        var beforePrepare = Tree(fixture.GameRoot);
        var preparation = await fixture.PrepareAsync();
        Console.WriteLine("E2E 1/6: katalog wybrał wersję i zbudował plan loader + moda");

        Assert.Equal(beforePrepare, Tree(fixture.GameRoot));
        Assert.Equal(2, preparation.Plans.Count);
        Assert.Equal("registry", preparation.GameVersionSource);
        Assert.Contains(preparation.Plans.SelectMany(plan => plan.Operations), op => op.Kind == "loader.ensure");
        Assert.Contains(preparation.Plans.SelectMany(plan => plan.Operations), op => op.Kind == "archive.extract");
        Assert.Contains(preparation.Plans.SelectMany(plan => plan.Operations), op => op.Kind == "file.copy");
        Assert.DoesNotContain(fixture.Packages.Requested, url => url == LoaderUrl);
        Assert.False(File.Exists(Plugin(fixture.GameRoot, "Submerged.dll")));

        await Assert.ThrowsAsync<PhaketException>(() => fixture.Service.ExecuteAsync(preparation, false, ct: Ct));
        Assert.Equal(beforePrepare, Tree(fixture.GameRoot));
        var outcome = await fixture.Service.ExecuteAsync(preparation, true, ct: Ct);
        Assert.True(outcome.Success, outcome.ErrorCode);
        Assert.All(outcome.Journals, journal => Assert.Equal(JournalState.Completed, journal.State));
        Assert.Equal("submerged v1", File.ReadAllText(Plugin(fixture.GameRoot, "Submerged.dll")));
        Assert.Equal("fixture IL2CPP chainloader", File.ReadAllText(
            Path.Combine(fixture.GameRoot, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll")));
        Assert.Equal("user = keep\n", File.ReadAllText(Path.Combine(fixture.GameRoot, "BepInEx", "config", "user.cfg")));
        AssertNoTemps(fixture.Root);
        Console.WriteLine("E2E 2/6: akceptacja zapisała loader, moda, ledger, baseline i dziennik Completed");

        var installedTree = Tree(fixture.GameRoot);
        var added = installedTree.Keys.Except(beforePrepare.Keys, StringComparer.Ordinal).ToArray();
        var ledger = OwnershipLedger.Load(Path.Combine(preparation.StateRoot, "ledger.json"));
        Assert.All(added, path =>
        {
            var entry = ledger.Get(path);
            Assert.True(entry is not null, $"ledger has no owner for {path}");
            Assert.Contains(entry!.Owners, owner => preparation.Plans.Any(plan => plan.RecipeRef.Id == owner.RecipeId));
            Assert.Equal(installedTree[path], entry.CurrentHash);
        });
        var baseline = InstallBaseline.Load(InstallBaseline.DefaultPath(preparation.StateRoot), preparation.Plans[^1].InstallationRef);
        Assert.All(added, path => Assert.Equal(installedTree[path], baseline.Files[path].Sha256));
        for (var i = 0; i < preparation.Plans.Count; i++)
            Assert.Equal(JournalState.Completed, PlanExecutor.LoadJournal(preparation.PlanProfileRoots![i])!.State);
        var registry = new InstallRegistry(Path.Combine(fixture.Root, "installs.json"));
        Assert.Equal(2, registry.ForGame("among-us").Count);
        Assert.Contains(registry.ForGame("among-us"), entry => entry.ModFullName == ModName && entry.ModVersion == "1.0.0");
        Assert.Equal("installed", Assert.Single(fixture.Receipts.Items).Result);

        var vanillaHash = HashFile(Path.Combine(fixture.GameRoot, "Among Us_Data", "sharedassets0.assets"));
        var helper = InstallLaunchHelper(fixture.GameRoot);
        var vanillaSaves = Path.Combine(fixture.Root, "vanilla-saves");
        Directory.CreateDirectory(vanillaSaves);
        File.WriteAllText(Path.Combine(vanillaSaves, "playerPrefs"), "fixture progress");
        var hostOs = OperatingSystem.IsWindows() ? "windows" : "linux";
        var launchDefinition = fixture.Definition with
        {
            SavePaths = null,
            SaveLocations = new SaveLocationsByOs(new Dictionary<string, IReadOnlyList<string>> { ["linux"] = [vanillaSaves] }),
        };
        var adapter = new DefinitionGameAdapter(launchDefinition, hostOs, "steam", fixture.GameRoot,
            Path.Combine(fixture.Root, "adapter-profile"), Path.Combine(fixture.Root, "adapter-staging"),
            Path.Combine(fixture.Root, "adapter-packages"),
            _ => throw new InvalidOperationException("BuildLaunchSpec must not resolve a recipe."), preparation.StateRoot);
        var launchSpec = adapter.BuildLaunchSpec(
            new Profile("e2e", "e2e", [new RecipeRef(preparation.Plans[^1].RecipeRef.Id, "1.0.0")]),
            new InstallationFacts(new Installation("e2e", "among-us", fixture.GameRoot), [GameVersion],
                OperationCatalog.Capabilities.ToArray(), "x86"));
        Assert.Equal("Among Us.exe", launchSpec.Executable);
        Assert.Equal(fixture.GameRoot, launchSpec.WorkingDirectory);

        var processStore = Path.Combine(fixture.Root, "processes.json");
        var launch = new LaunchService(processStore);
        var vm = new GameViewModel(DetectedGame(fixture, launchDefinition), () => { }, appDataRoot: fixture.Root)
        {
            Launch = launch,
            HostOs = hostOs,
            HelperArguments = helper.Arguments,
            SteamHome = fixture.Root,
            SteamRoots = [Path.Combine(fixture.Root, "steam")],
        };
        vm.RefreshSteamShortcut();
        Assert.Equal(fixture.GameRoot, vm.LaunchRoot);
        var steamCalls = SteamLaunchGuard.RequestedUris.Count;
        ProcessRecord? record = null;
        try
        {
            await vm.PlayCommand.ExecuteAsync(null);
            Assert.True(vm.LastLaunch is { Started: true, Tracked: true }, vm.LastLaunch?.Failure);
            Assert.Equal(steamCalls, SteamLaunchGuard.RequestedUris.Count);
            record = launch.Tracked("945360");
            Assert.NotNull(record);
            Assert.True(record.Pid > 0);
            Assert.NotNull(record.StartTime);
            Assert.NotNull(record.StartTimeUtc);
            Assert.Equal(record, Assert.Single(new ProcessTracker(processStore).Load()));
            Assert.True(ProcessIdentity.TryGetProcessIdentity(record.Pid).found);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal("fixture progress", File.ReadAllText(Path.Combine(fixture.GameRoot,
                    SavesIsolationPlanner.HomeDirectoryName, "config", "playerPrefs")));
                Assert.Equal("fixture progress", File.ReadAllText(Path.Combine(vanillaSaves, "playerPrefs")));
            }
            Console.WriteLine($"E2E 3/6: bezpośredni launch fixture PID={record.Pid}, bez steam://");

            var stop = new ProcessTracker(processStore).TryStop(record, force: false);
            Assert.True(stop.Status == ProcessStopStatus.Stopped, stop.ToString());
            AssertGone(record);
            Assert.Empty(new ProcessTracker(processStore).Load());
            Console.WriteLine("E2E 4/6: TryStop zatrzymał proces i usunął rekord PID");
        }
        finally { KillOwnedProcess(record); }

        var isolatedHome = Path.Combine(fixture.GameRoot, SavesIsolationPlanner.HomeDirectoryName);
        if (Directory.Exists(isolatedHome)) Directory.Delete(isolatedHome, recursive: true);
        var loaderFile = Path.Combine(fixture.GameRoot, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll");
        var loaderHash = HashFile(loaderFile);
        var helperHash = HashFile(Path.Combine(fixture.GameRoot, "Among Us.exe"));
        var modPlan = preparation.Plans[^1];
        new PlanExecutor(fixture.Context(preparation, 1)).Uninstall(modPlan.RecipeRef.Id);
        registry.Remove(modPlan.RecipeRef.Id, fixture.GameRoot);

        Assert.False(File.Exists(Plugin(fixture.GameRoot, "Submerged.dll")));
        Assert.False(File.Exists(Plugin(fixture.GameRoot, "NewFeature.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", PluginFolder)));
        Assert.Equal(loaderHash, HashFile(loaderFile));
        Assert.Equal(helperHash, HashFile(Path.Combine(fixture.GameRoot, "Among Us.exe")));
        Assert.Equal(vanillaHash, HashFile(Path.Combine(fixture.GameRoot, "Among Us_Data", "sharedassets0.assets")));
        Assert.Equal("user = keep\n", File.ReadAllText(Path.Combine(fixture.GameRoot, "BepInEx", "config", "user.cfg")));
        Assert.DoesNotContain(registry.ForGame("among-us"), entry => entry.ModFullName == ModName);
        Assert.Single(registry.ForGame("among-us"), entry => entry.ModFullName == LoaderName);
        Assert.Empty(OwnershipLedger.Load(Path.Combine(preparation.StateRoot, "ledger.json")).ForRecipe(modPlan.RecipeRef.Id));
        Assert.DoesNotContain(InstallBaseline.Load(InstallBaseline.DefaultPath(preparation.StateRoot),
            modPlan.InstallationRef).Files.Keys, path => path.StartsWith($"BepInEx/plugins/{PluginFolder}/", StringComparison.Ordinal));
        AssertNoTemps(fixture.Root);
        Console.WriteLine("E2E 5/6: uninstall usunął moda i jego katalog, loader/vanilla pozostały bit-w-bit");
    }

    [Fact]
    public async Task Interrupted_install_rolls_back_completed_loader_and_partial_mod_without_touching_the_game()
    {
        using var fixture = new Fixture();
        var before = Tree(fixture.GameRoot);
        var preparation = await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAfterDone(2, cancellation); // loader + staging extraction, before first plugin copy

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ExecuteAsync(
            preparation, true, progress, cancellation.Token));

        Assert.Equal(2, progress.Done);
        Assert.Equal(before, Tree(fixture.GameRoot));
        Assert.Empty(new InstallRegistry(Path.Combine(fixture.Root, "installs.json")).ForGame("among-us"));
        var ledger = OwnershipLedger.Load(Path.Combine(preparation.StateRoot, "ledger.json"));
        Assert.Empty(ledger.Entries);
        Assert.Empty(ledger.CreatedDirectories);
        Assert.Equal(JournalState.RolledBack, PlanExecutor.LoadJournal(preparation.PlanProfileRoots![^1])!.State);
        AssertNoTemps(fixture.Root);
    }

    [Fact]
    public async Task KeepLocal_conflict_preserves_the_user_file_and_installs_unrelated_new_files()
    {
        using var fixture = new Fixture();
        var first = await fixture.PrepareAsync("1.0.0");
        Assert.True((await fixture.Service.ExecuteAsync(first, true, ct: Ct)).Success);
        var local = Plugin(fixture.GameRoot, "Submerged.dll");
        File.WriteAllText(local, "user edit");
        var second = await fixture.PrepareAsync("2.0.0");
        var plan = Assert.Single(second.Plans);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal($"BepInEx/plugins/{PluginFolder}/Submerged.dll", conflict.Path);

        var refused = await fixture.Service.ExecuteAsync(second, true, ct: Ct);
        Assert.False(refused.Success);
        Assert.Equal(ErrorCodes.FileConflictRequiresDecision, refused.ErrorCode);
        Assert.Equal("user edit", File.ReadAllText(local));
        Assert.False(File.Exists(Plugin(fixture.GameRoot, "NewFeature.dll")));

        var kept = await fixture.Service.ExecuteAsync(second, true, ct: Ct,
            decisions: new Dictionary<string, ConflictDecision> { [conflict.Path] = ConflictDecision.KeepLocal });
        Assert.True(kept.Success, kept.ErrorCode);
        Assert.Equal(JournalState.Completed, Assert.Single(kept.Journals).State);
        Assert.Equal("user edit", File.ReadAllText(local));
        Assert.Equal("new v2 feature", File.ReadAllText(Plugin(fixture.GameRoot, "NewFeature.dll")));
        var baseline = InstallBaseline.Load(InstallBaseline.DefaultPath(second.StateRoot), plan.InstallationRef);
        Assert.Equal(Hash(File.ReadAllBytes(local)), baseline.Files[$"BepInEx/plugins/{PluginFolder}/Submerged.dll"].Sha256);
        AssertNoTemps(fixture.Root);
    }

    [Fact]
    public async Task Unknown_loader_architecture_is_refused_with_a_stable_code_and_zero_game_mutation()
    {
        using var fixture = new Fixture(validPe: false);
        var before = Tree(fixture.GameRoot);

        var error = await Assert.ThrowsAsync<PhaketException>(() => fixture.PrepareAsync());

        Assert.Equal("LOADER_ARCH_UNAVAILABLE", error.Error.Code);
        Assert.Equal("unknown", error.Error.Parameters["arch"]);
        Assert.Equal(before, Tree(fixture.GameRoot));
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "installs.json")));
        AssertNoTemps(fixture.Root);
    }

    [Fact]
    public async Task Disk_full_preflight_has_a_bilingual_code_and_leaves_no_partial_package()
    {
        var root = Directory.CreateTempSubdirectory("phaket-among-us-disk-full-").FullName;
        try
        {
            var source = Path.Combine(root, "vanilla-cache");
            var target = Path.Combine(root, "package");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Among Us.exe"), "vanilla");
            File.WriteAllText(Path.Combine(source, "asset.bin"), "asset");
            var before = Tree(source);
            var cloner = new CountingCloner();

            var error = await Assert.ThrowsAsync<PhaketException>(() => PackageMaterializer.MaterializeAsync(
                source, target, null, cloner, Ct, freeBytes: _ => 0));

            Assert.Equal(ErrorCodes.DiskFull, error.Error.Code);
            Assert.Contains("Za mało miejsca na dysku", error.LocalizedMessage("pl-PL"));
            Assert.Contains("Not enough disk space", error.LocalizedMessage("en"));
            Assert.Equal(0, cloner.Clones);
            Assert.Equal(before, Tree(source));
            Assert.False(Directory.Exists(target));
            Assert.False(Directory.Exists(target + PackageMaterializer.PartialSuffix));
            AssertNoTemps(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Real_BepInEx_artifact_when_available_uses_the_same_offline_catalog_install_path()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("PHAKET_TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(artifactRoot)) artifactRoot = "/tmp/opencode/phaket-research/artifacts";
        var artifact = Path.Combine(artifactRoot, "amongus-loader-6.0.700.zip");
        if (!File.Exists(artifact))
            Assert.Skip("Realny artefakt BepInEx nie jest dostępny; ustaw PHAKET_TEST_ARTIFACT_ROOT. " +
                "Real BepInEx artifact is unavailable; set PHAKET_TEST_ARTIFACT_ROOT to opt in.");
        var loaderBytes = File.ReadAllBytes(artifact);
        Assert.Equal(RealLoaderHash, Hash(loaderBytes));
        using var fixture = new Fixture(loaderBytes);
        var preparation = await fixture.PrepareAsync();

        var outcome = await fixture.Service.ExecuteAsync(preparation, true, ct: Ct);

        Assert.True(outcome.Success, outcome.ErrorCode);
        var core = Path.Combine(fixture.GameRoot, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll");
        Assert.True(File.Exists(core));
        using var archive = new ZipArchive(new MemoryStream(loaderBytes, writable: false), ZipArchiveMode.Read);
        var coreEntry = archive.Entries.Single(entry => entry.FullName.EndsWith(
            "/BepInEx/core/BepInEx.Unity.IL2CPP.dll", StringComparison.OrdinalIgnoreCase));
        using var entryStream = coreEntry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        Assert.Equal(Hash(buffer.ToArray()), HashFile(core));
        Assert.Equal("submerged v1", File.ReadAllText(Plugin(fixture.GameRoot, "Submerged.dll")));
        AssertNoTemps(fixture.Root);
    }
}
