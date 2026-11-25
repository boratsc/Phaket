using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Accounts;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// PAK-105: a modpack composition with source=susmodder installs through the same
// catalog pipeline as a single catalog install (artifact selection, full/dll recipes,
// plan -> execute, ledger). The Steam prompt reaches the catalog installer and the
// mobile-confirmation state is part of the shared dialog. Fixtures only: no network,
// no real Steam, no real game installs.
public sealed class Pak105ModpackSusModderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string LoaderUrl = "https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/";
    private const string FullUrl = "https://susmodder-cdn.ovh/mods/mira/1.7.3.zip";
    private const string DllUrl = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";
    private const string ThunderstoreUrl = "https://thunderstore.io/package/download/Test/Mod/1.0.0/";
    private const string GitHubUrl = "https://github.com/example/mod/releases/download/v1.0.0/mod.zip";
    private const string LoaderPath = "/tmp/opencode/phaket-research/artifacts/amongus-loader-6.0.700.zip";

    private static void RequireLoader()
    {
        if (!File.Exists(LoaderPath)) Assert.Skip("Real loader archive is not available locally.");
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // A minimal PE32 (x86) header so the install's own arch probe is a fact, not a guess.
    private static void WritePe(string path, bool x64)
    {
        var bytes = new byte[512];
        void U16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o), v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o), v);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c);
        U16(0x94, x64 ? (ushort)240 : (ushort)224);
        U16(0x98, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(0x98 + (x64 ? 108 : 92), 16);
        File.WriteAllBytes(path, bytes);
    }

    private static GameCatalogItem Game(string root)
    {
        var gameRoot = Path.Combine(root, "steamapps", "common", "Among Us");
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
        WritePe(Path.Combine(gameRoot, "Among Us.exe"), x64: false);
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_945360.acf"), """
            "AppState"
            {
                "appid"		"945360"
                "name"		"Among Us"
                "buildid"		"24302054"
                "branch"		"public"
                "manifest"		"1"
                "installdir"		"Among Us"
            }
            """);
        var game = new GameCatalogItem(GameDefinitions.LoadAll().Single(item => item.Slug == "among-us"), "", "");
        game.Installations.Add(new SteamInstallation(new Installation("test", "steam-edition", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "24302054", "Among Us", 1),
            new SteamLibrary(root, false), GameAvailability.Installed));
        return game;
    }

    private static ThunderstoreVersion SusVersion(string version, string url, byte[] bytes, string packaging,
        string? amongVersion, string assetName) =>
        new(version, $"SusModder-{version}", url, bytes.Length, DateTimeOffset.UnixEpoch, [], true,
            [new ModArtifact(url, "x86", ["steam", "itch"], Hash(bytes), bytes.Length, "susmodder-cdn", assetName)],
            packaging, amongVersion, "x86", "susmodder", null);

    private static ThunderstoreMod SusMod(string id, string externalId, string name, ThunderstoreVersion version) =>
        new(id, "among-us", "SusModder", name, "SusModder-" + name, "desc", "SusModder", "https://example.test",
            null, false, DateTimeOffset.UnixEpoch, version, Packaging: version.Packaging,
            AmongVersion: version.AmongVersion, Source: "susmodder", ExternalId: externalId);

    private static ThunderstoreMod ThunderstoreMod(string version, byte[] bytes) =>
        new("ts-id", "among-us", "Test", "Mod", "Test-Mod", "desc", "Test",
            ThunderstoreUrl, null, false, DateTimeOffset.UnixEpoch,
            new ThunderstoreVersion(version, "Test-Mod-" + version, ThunderstoreUrl, bytes.Length, DateTimeOffset.UnixEpoch, []));

    private sealed class Catalog(IReadOnlyList<ThunderstoreMod> mods) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { revision = 1, mods, nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/catalog/index", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null,
                    JsonSerializer.Serialize(new { revision = 1, items = Array.Empty<object>(), nextCursor = (string?)null }, Json)));
            if (path.StartsWith("/mods/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/mods/".Length..]);
                var mod = mods.FirstOrDefault(item => item.Id == id);
                return Task.FromResult(mod is null
                    ? new CatalogResponse(404, null, null)
                    : new CatalogResponse(200, null,
                        JsonSerializer.Serialize(new { mod, versions = new[] { mod.Latest } }, Json)));
            }
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }

    private sealed class Packages(IReadOnlyDictionary<string, byte[]> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string source, CancellationToken cancellationToken) => files.TryGetValue(source, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new InvalidOperationException($"unexpected download: {source}");
    }

    private sealed class HostVanilla : IBaseGameSource
    {
        public int Calls { get; private set; }
        public Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId,
            string targetDirectory, CancellationToken ct = default)
        {
            Calls++;
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us.exe"), "provisioned");
            Directory.CreateDirectory(Path.Combine(targetDirectory, "Among Us_Data"));
            File.WriteAllText(Path.Combine(targetDirectory, "Among Us_Data", "sharedassets0.assets"), "vanilla");
            return Task.FromResult(BaseGamePrepareResult.Ok);
        }
    }

    private sealed class GitHubHandler(byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[]
                {
                    new
                    {
                        id = 7, tag_name = "v1.0.0", name = "Release", published_at = "2026-01-01T00:00:00Z",
                        prerelease = false, draft = false,
                        assets = new[]
                        {
                            new
                            {
                                id = 42, name = "mod.zip", size = archive.Length,
                                browser_download_url = GitHubUrl
                            }
                        }
                    }
                }))
            });
    }

    private static byte[] FullZip() => Zip(("BepInEx/plugins/TownOfUsMira.dll", "full package"),
        ("Among Us_Data/sharedassets0.assets", "not a writable area"));

    private static byte[] DllZip() => Zip(("BepInEx/plugins/Submerged/Submerged.dll", "dll package"));

    private static ModpackDefinition Pack(params ModpackMod[] items) => new()
    {
        Id = "pak105-pack", Name = "Mira pack", Author = "Test", Version = "1", GameId = "among-us", Items = [.. items]
    };

    private static ModpackMod SusItem(string name, string version, string? externalId) => new()
    {
        Name = name, Version = version, Source = "susmodder", ModId = externalId
    };

    private static string GameRoot(string root) => Path.Combine(root, "steamapps", "common", "Among Us");

    private static (ModpackInstallService Service, Packages Packages) Fixture(string root,
        ThunderstoreVersion fullVersion, ThunderstoreVersion dllVersion, IReadOnlyList<ThunderstoreMod>? extra = null)
    {
        var full = FullZip();
        var dll = DllZip();
        var files = new Dictionary<string, byte[]>
        {
            [FullUrl] = full,
            [DllUrl] = dll,
            [LoaderUrl] = File.ReadAllBytes(LoaderPath),
        };
        var packages = new Packages(files);
        var mods = new List<ThunderstoreMod> { SusMod("sus-mira", "Mira", "Mira", fullVersion), SusMod("sus-submerged", "Submerged", "Submerged", dllVersion) };
        if (extra is not null) mods.AddRange(extra);
        var service = new ModpackInstallService(root, catalogTransport: new Catalog(mods), packageTransport: packages);
        return (service, packages);
    }

    private static void UninstallAll(string root, IReadOnlyList<InstalledRecipe> entries)
    {
        var definition = GameDefinitions.LoadAll().First(item => item.Slug == "among-us");
        var registry = new InstallRegistry(Path.Combine(root, "installs.json"));
        foreach (var entry in entries)
        {
            var context = new PlanExecutionContext(entry.GameRoot, entry.ProfileRoot, entry.StagingRoot,
                Path.Combine(root, "packages"), definition, entry.RecipeId, entry.ProfileRoot, entry.StateRoot);
            new PlanExecutor(context).Uninstall(entry.RecipeId, DivergencePolicy.Skip);
            registry.Remove(entry.RecipeId, entry.GameRoot);
        }
    }

    [Fact]
    public async Task SusModder_full_and_dll_compose_plans_installs_and_uninstalls_through_the_ledger()
    {
        RequireLoader();
        var root = TestAccounts.TempPath();
        try
        {
            var game = Game(root);
            var (service, _) = Fixture(root,
                SusVersion("1.7.3", FullUrl, FullZip(), "full", "2026-8-18", "tou.zip"),
                SusVersion("1.0.0", DllUrl, DllZip(), "dll", "ToU", "submerged.zip"));
            var host = new HostVanilla();
            service.InstallServiceFactory = (client, profiles, credentials, prompt) =>
                new ModInstallService(root, client, new Packages(new Dictionary<string, byte[]>()), profiles: profiles,
                    baseGameSource: host, steamCredentials: credentials, steamPrompt: prompt);
            var pack = Pack(
                SusItem("SusModder-Mira", "1.7.3", "Mira"),
                // The server matches by full name or by the external id; this item only has the id.
                SusItem("Submerged", "1.0.0", "Submerged"));

            var plan = await service.PrepareAsync(game, pack, TestContext.Current.CancellationToken);

            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.Equal(2, plan.Preparations.Count);
            Assert.Null(plan.SourcePlan);
            Assert.Contains(plan.Preparations[0].Plans.Single().Operations, operation => operation.Kind == "game.provision");
            Assert.Equal("archive.extract", Assert.Single(plan.Preparations[1].Plans.Single().Operations).Kind);

            var result = await service.InstallAsync(game, pack, plan, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            var gameRoot = GameRoot(root);
            Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "TownOfUsMira.dll")));
            // The archive's non-writable entry must not overwrite the provisioned vanilla file.
            Assert.Equal("vanilla", await File.ReadAllTextAsync(
                Path.Combine(gameRoot, "Among Us_Data", "sharedassets0.assets"), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Submerged", "Submerged.dll")));
            Assert.True(File.Exists(Path.Combine(gameRoot, "winhttp.dll")));
            Assert.Equal(1, host.Calls);
            var registry = new InstallRegistry(Path.Combine(root, "installs.json"));
            var entries = registry.ForGame("among-us");
            Assert.Equal(2, entries.Count);

            UninstallAll(root, entries);

            Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "TownOfUsMira.dll")));
            Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Submerged", "Submerged.dll")));
            Assert.Empty(new InstallRegistry(Path.Combine(root, "installs.json")).ForGame("among-us"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failure_of_the_second_susmodder_item_rolls_back_the_first_and_keeps_user_files()
    {
        RequireLoader();
        var root = TestAccounts.TempPath();
        try
        {
            var game = Game(root);
            var (service, _) = Fixture(root,
                SusVersion("1.7.3", FullUrl, FullZip(), "full", "2026-8-18", "tou.zip"),
                SusVersion("1.0.0", DllUrl, DllZip(), "dll", "ToU", "submerged.zip"));
            service.InstallServiceFactory = (client, profiles, credentials, prompt) =>
                new ModInstallService(root, client, new Packages(new Dictionary<string, byte[]>()), profiles: profiles,
                    baseGameSource: new HostVanilla(), steamCredentials: credentials, steamPrompt: prompt);
            // A file where the second item needs a directory: its extraction fails after the
            // first item already wrote files, which is the rollback case.
            var userFile = Path.Combine(GameRoot(root), "BepInEx", "plugins", "Submerged");
            await File.WriteAllTextAsync(userFile, "user-owned fixture", TestContext.Current.CancellationToken);
            var pack = Pack(
                SusItem("SusModder-Mira", "1.7.3", "Mira"),
                SusItem("SusModder-Submerged", "1.0.0", "Submerged"));
            var previousProfiles = new ProfileStore(root).For("among-us");

            var plan = await service.PrepareAsync(game, pack, TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);
            var result = await service.InstallAsync(game, pack, plan, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("Catalog.InstallPlanFailed", result.Error);
            Assert.False(File.Exists(Path.Combine(GameRoot(root), "BepInEx", "plugins", "TownOfUsMira.dll")));
            Assert.Equal("user-owned fixture", await File.ReadAllTextAsync(userFile, TestContext.Current.CancellationToken));
            Assert.Empty(new InstallRegistry(Path.Combine(root, "installs.json")).ForGame("among-us"));
            Assert.Equal(previousProfiles.Profiles.Select(profile => profile.Id),
                new ProfileStore(root).For("among-us").Profiles.Select(profile => profile.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Mixed_pack_keeps_thunderstore_github_and_susmodder_routing()
    {
        RequireLoader();
        var root = TestAccounts.TempPath();
        try
        {
            var game = Game(root);
            GitHubReleaseService.ClearCache();
            SourcePolicy.AllowGitHubRepository("example/mod");
            using var http = new HttpClient(new GitHubHandler(Zip(("plugins/mod.dll", "github bytes"))));
            var thunderstoreZip = Zip(("plugins/Test.dll", "thunderstore bytes"));
            var withGitHub = new ModpackInstallService(root,
                catalogTransport: new Catalog([
                    SusMod("sus-mira", "Mira", "Mira", SusVersion("1.7.3", FullUrl, FullZip(), "full", "2026-8-18", "tou.zip")),
                    ThunderstoreMod("1.0.0", thunderstoreZip)]),
                packageTransport: new Packages(new Dictionary<string, byte[]>
                {
                    [FullUrl] = FullZip(), [LoaderUrl] = File.ReadAllBytes(LoaderPath),
                    [ThunderstoreUrl] = thunderstoreZip,
                    [GitHubUrl] = Zip(("plugins/mod.dll", "github bytes")),
                }), githubService: new GitHubReleaseService(http));
            var pack = Pack(
                new ModpackMod { Name = "Test-Mod", Version = "1.0.0", Source = "thunderstore", ModId = "ts-id" },
                SusItem("SusModder-Mira", "1.7.3", "Mira"),
                new ModpackMod
                {
                    Name = "GitHub Mod", Version = "v1.0.0", Source = "github", Repository = "example/mod",
                    Tag = "v1.0.0", AssetId = "42", Link = GitHubUrl
                });

            var plan = await withGitHub.PrepareAsync(game, pack, TestContext.Current.CancellationToken);

            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.Equal(2, plan.Preparations.Count);
            Assert.Equal("v1.0.0", Assert.Single(plan.SourcePlan!.Items).Version);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Steam_prompt_and_credentials_reach_the_catalog_installer_and_default_to_the_shared_dialog()
    {
        var root = TestAccounts.TempPath();
        var defaultRoot = TestAccounts.TempPath();
        try
        {
            var mock = new Func<DepotPrompt, CancellationToken, Task<string>>((_, _) => Task.FromResult("12345"));
            Func<DepotPrompt, CancellationToken, Task<string>>? captured = null;
            SteamCredentialStore? capturedCredentials = null;
            var service = ServiceWithCapture(root, mock, (prompt, credentials) =>
            {
                captured = prompt;
                capturedCredentials = credentials;
            });
            var result = await InstallOneDll(service, Game(Path.Combine(root, "install")));

            Assert.True(result.Success, result.Error);
            Assert.Same(mock, captured);
            Assert.NotNull(capturedCredentials);
            Assert.Equal("12345", await captured!(DepotPrompt.SteamGuard, TestContext.Current.CancellationToken));

            // Without an injected prompt the modpack install still routes through the shared dialog.
            Func<DepotPrompt, CancellationToken, Task<string>>? defaultPrompt = null;
            var defaultService = ServiceWithCapture(defaultRoot, null, (prompt, _) => defaultPrompt = prompt);
            var defaultResult = await InstallOneDll(defaultService, Game(Path.Combine(defaultRoot, "install")));

            Assert.True(defaultResult.Success, defaultResult.Error);
            Assert.NotNull(defaultPrompt);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(defaultRoot)) Directory.Delete(defaultRoot, true);
        }
    }

    private static ModpackInstallService ServiceWithCapture(string root,
        Func<DepotPrompt, CancellationToken, Task<string>>? prompt,
        Action<Func<DepotPrompt, CancellationToken, Task<string>>, SteamCredentialStore> capture)
    {
        var dll = DllZip();
        var packages = new Packages(new Dictionary<string, byte[]> { [DllUrl] = dll });
        var catalog = new Catalog([SusMod("sus-submerged", "Submerged", "Submerged",
            SusVersion("1.0.0", DllUrl, dll, "dll", "ToU", "submerged.zip"))]);
        var service = new ModpackInstallService(root, catalogTransport: catalog, packageTransport: packages, steamPrompt: prompt);
        service.InstallServiceFactory = (client, profiles, credentials, resolvedPrompt) =>
        {
            capture(resolvedPrompt, credentials);
            return new ModInstallService(root, client, packages, profiles: profiles);
        };
        return service;
    }

    private static async Task<ModpackInstallResult> InstallOneDll(ModpackInstallService service, GameCatalogItem game)
    {
        var pack = Pack(SusItem("SusModder-Submerged", "1.0.0", "Submerged"));
        var plan = await service.PrepareAsync(game, pack, TestContext.Current.CancellationToken);
        Assert.True(plan.CanInstall, plan.BlockReason);
        return await service.InstallAsync(game, pack, plan, cancellationToken: TestContext.Current.CancellationToken);
    }


    [Fact]
    public void SusModder_filter_narrows_the_composition_and_keeps_all_entries()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            var pack = Pack(
                SusItem("SusModder-Mira", "1.7.3", "Mira"),
                new ModpackMod { Name = "Test-Mod", Version = "1.0.0", Source = "thunderstore", ModId = "ts-id" });
            store.Save(pack);
            var vm = new ModPackViewModel(pack, store, () => { }, root);

            var susmodder = Assert.Single(vm.SourceFilters, filter => filter.Id == "susmodder");
            Assert.Equal("Catalog.Source.susmodder", susmodder.LabelKey);
            vm.SelectedSourceFilter = susmodder;
            Assert.Equal("SusModder-Mira", Assert.Single(vm.VisibleMods).Name);

            vm.SelectedSourceFilter = vm.SourceFilters.Single(filter => filter.Id == "all");
            Assert.Equal(2, vm.VisibleMods.Count);
            Assert.Equal("SusModder", vm.VisibleMods.Single(mod => mod.Name == "SusModder-Mira").SourceLabel);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_Thunderstore_lookalike_never_wins_a_susmodder_item()
    {
        RequireLoader();
        var root = TestAccounts.TempPath();
        try
        {
            var game = Game(root);
            var full = FullZip();
            var real = SusMod("sus-mira", "13", "Mira", SusVersion("1.7.3", FullUrl, full, "full", "2026-8-18", "tou.zip"));
            var lookalike = new ThunderstoreMod("ts-lookalike", "among-us", "SusModder", "Mira", "SusModder-Mira",
                "lookalike", "SusModder", "https://thunderstore.io/package/SusModder/Mira", null, false,
                DateTimeOffset.UnixEpoch,
                new ThunderstoreVersion("1.7.3", "SusModder-Mira-1.7.3", ThunderstoreUrl, full.Length, DateTimeOffset.UnixEpoch, []));
            // Packages serves only the SusModder URL: picking the lookalike would fail the download.
            var packages = new Packages(new Dictionary<string, byte[]> { [FullUrl] = full, [LoaderUrl] = File.ReadAllBytes(LoaderPath) });
            var service = new ModpackInstallService(root, catalogTransport: new Catalog([lookalike, real]), packageTransport: packages);

            var plan = await service.PrepareAsync(game, Pack(SusItem("SusModder-Mira", "1.7.3", "13")),
                TestContext.Current.CancellationToken);

            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.Contains(plan.Preparations.Single().Plans.Single().Operations, operation => operation.Kind == "game.provision");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SusModder_item_with_a_mismatched_external_id_is_refused_and_the_name_is_only_a_fallback()
    {
        RequireLoader();
        var root = TestAccounts.TempPath();
        try
        {
            var game = Game(root);
            var (service, _) = Fixture(root,
                SusVersion("1.7.3", FullUrl, FullZip(), "full", "2026-8-18", "tou.zip"),
                SusVersion("1.0.0", DllUrl, DllZip(), "dll", "ToU", "submerged.zip"));

            // The name matches Mira, the id does not: an inconsistent pair is an error, not a guess.
            var refused = await service.PrepareAsync(game, Pack(SusItem("SusModder-Mira", "1.7.3", "999")),
                TestContext.Current.CancellationToken);
            Assert.False(refused.CanInstall);
            Assert.Equal("Catalog.InstallSusModderUnavailable", refused.BlockReason);

            // Without an id the full name is the documented fallback.
            var byName = await service.PrepareAsync(game, Pack(SusItem("SusModder-Mira", "1.7.3", null)),
                TestContext.Current.CancellationToken);
            Assert.True(byName.CanInstall, byName.BlockReason);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Mobile_confirmation_is_a_dialog_message_with_cancel()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            var vm = new SteamPromptViewModel();
            vm.ShowMobileConfirmation();
            Assert.True(vm.MobileConfirmationOpen);
            Assert.True(vm.HasDialog);

            var window = new Window { Width = 480, Height = 220, Content = new SteamPromptDialog { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var expected = LocalizationService.Instance["SteamInstall.MobileConfirmation"];
            var shown = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == expected);
            Assert.True(shown);
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            frame!.Save("/tmp/opencode/phaket-render/40-steam-mobile-confirm.png", PngBitmapEncoderOptions.Default);
            window.Close();

            vm.CancelDialogCommand.Execute(null);
            Assert.False(vm.MobileConfirmationOpen);
            Assert.False(vm.HasDialog);
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
    }
}