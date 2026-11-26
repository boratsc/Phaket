using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Domain;
using Phaket.Core.Mods;
using Phaket.Core.Platforms;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class ModpackInstallAdapterTests
{
    private sealed class Transport(byte[] archive) : IPackageTransport
    {
        public int Requests { get; private set; }
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult<Stream>(new MemoryStream(archive, writable: false));
        }
    }

    private sealed class GitHubHandler(byte[] archive, string repo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[] {
                    new { id = 7, tag_name = "v1.0.0", name = "Release", published_at = "2026-01-01T00:00:00Z", prerelease = false, draft = false,
                        assets = new[] { new { id = 42, name = "mod.zip", size = archive.Length, browser_download_url = $"https://github.com/{repo}/releases/download/v1.0.0/mod.zip" } } }
                }))
            });
    }

    private sealed class RouteTransport(byte[] archive, int failRequest = -1) : IPackageTransport
    {
        public int Requests { get; private set; }
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
        {
            Requests++;
            if (Requests == failRequest) throw new HttpRequestException("fixture failure");
            return Task.FromResult<Stream>(new MemoryStream(archive, writable: false));
        }
    }

    private sealed class Catalog(ICatalogTransport inner) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken) =>
            inner.GetAsync(path, ifNoneMatch, cancellationToken);
    }

    private static byte[] Zip(string entry, string text)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry(entry).Open())) writer.Write(text);
        return output.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static GameCatalogItem Game(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Among Us.exe"), "fixture");
        var game = new GameCatalogItem(GameDefinitions.LoadAll().Single(item => item.Slug == "among-us"), "", "");
        game.Installations.Add(new SteamInstallation(new Installation("test", "steam-edition", root),
            new SteamAppManifest("945360", "Among Us", "1", "public", "123", "Among Us", 1),
            new SteamLibrary(root, false), GameAvailability.Installed));
        return game;
    }

    private static ModpackDefinition Pack(params ModpackMod[] items) => new()
    {
        Id = "test-pack", Name = "Test pack", Author = "Test", Version = "1", GameId = "among-us", Items = [.. items]
    };

    [AvaloniaFact]
    public async Task Render_public_catalog_with_resolved_github_source()
    {
        var root = TestAccounts.TempPath();
        var language = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            var pack = new ModpackDefinition
            {
                Id = "render-github", Slug = "render-github", Name = "GitHub release pack", Author = "Phaket",
                Version = "1.0.0", GameId = "among-us", IsPublic = true,
                Items = [new ModpackMod { Name = "Mod przez GitHub", Version = "v1.0.0", Source = "github", Repository = "example/render", Tag = "v1.0.0", AssetId = "42", Link = "https://github.com/example/render/releases/download/v1.0.0/mod.zip" }]
            };
            var api = new RenderApi(pack);
            var installer = new ModpackInstallService(root);
            var game = new GameCatalogItem(GameDefinitions.LoadAll().Single(item => item.Slug == "among-us"), "", "");
            var viewModel = new ModpackCatalogViewModel(game, root, api, installer, autoLoad: false);
            viewModel.ShowPublic = true;
            await viewModel.RefreshPublicCommand.ExecuteAsync(null);
            viewModel.SelectPackCommand.Execute(viewModel.VisiblePacks.Single());
            var window = new Window { Width = 1280, Height = 800, Content = new ModpackCatalogView { DataContext = viewModel } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            frame!.Save("/tmp/opencode/phaket-render/39-catalog-github.png", PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RenderApi(ModpackDefinition pack) : IModpackApiClient
    {
        public Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModpackSearchPage([pack], null));
        public Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<ModpackDefinition?>(pack);
    }

    [Fact]
    public async Task Public_github_is_resolved_and_installed_through_core_transaction()
    {
        var root = TestAccounts.TempPath();
        var archive = Zip("plugins/mod.dll", "github fixture");
        var gameRoot = Path.Combine(root, "game");
        try
        {
            GitHubReleaseService.ClearCache();
            SourcePolicy.AllowGitHubRepository("example/mod");
            var packageTransport = new RouteTransport(archive);
            using var client = new HttpClient(new GitHubHandler(archive, "example/mod"));
            var service = new ModpackInstallService(root, packageTransport: packageTransport,
                githubService: new GitHubReleaseService(client));
            var game = Game(gameRoot);
            var pack = Pack(new ModpackMod
            {
                Name = "GitHub Mod", Version = "v1.0.0", Source = "github", Repository = "example/mod",
                Tag = "v1.0.0", AssetId = "42", Link = "https://github.com/example/mod/releases/download/v1.0.0/mod.zip"
            });

            var plan = await service.PrepareAsync(game, pack, TestContext.Current.CancellationToken);

            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.Equal("v1.0.0", Assert.Single(plan.SourcePlan!.Items).Version);
            Assert.Equal(1, packageTransport.Requests);
            var installed = await service.InstallAsync(game, pack, plan, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(installed.Success, installed.Error);
            Assert.Equal("github fixture", File.ReadAllText(Path.Combine(gameRoot, "BepInEx", "plugins", "GitHub_Mod", "mod.dll")));
            Assert.True(File.Exists(Path.Combine(root, "profiles.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Public_nexus_without_local_archive_is_blocked_with_manual_path_message()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var service = new ModpackInstallService(root);
            var plan = await service.PrepareAsync(Game(Path.Combine(root, "game")), new ModpackDefinition
            {
                Name = "Public", Author = "Author", Version = "1", GameId = "among-us", IsPublic = true,
                Items = [new ModpackMod { Name = "Nexus Mod", Version = "1", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/123" }]
            });
            Assert.Equal("Catalog.InstallNexusLocalArchiveRequired", plan.BlockReason);
            Assert.Contains("Wskaż pobrane archiwum", LocalizationService.Instance[plan.BlockReason!], StringComparison.OrdinalIgnoreCase);
            var priorLanguage = LocalizationService.Instance.CurrentLanguage;
            LocalizationService.Instance.CurrentLanguage = "en";
            Assert.Contains("Point to the downloaded archive", LocalizationService.Instance[plan.BlockReason!], StringComparison.OrdinalIgnoreCase);
            LocalizationService.Instance.CurrentLanguage = priorLanguage;

            // The local editor keeps its own hint; a missing archive there is the user's own field.
            var localPlan = await service.PrepareAsync(Game(Path.Combine(root, "game", "local")), new ModpackDefinition
            {
                Name = "Local", Author = "Author", Version = "1", GameId = "among-us",
                Items = [new ModpackMod { Name = "Nexus Mod", Version = "1", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/123" }]
            });
            Assert.Equal("Catalog.InstallNexusManualRequiredHint", localPlan.BlockReason);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Server_supplied_manual_path_never_reaches_the_installer_and_stale_cache_is_cleaned()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        // Shaped exactly like Phaket.Api Packages.Detail: snapshot.items[].manualPath is a path on the publisher's disk.
        string poisoned = Path.Combine(root, "publisher-side", "secret.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(poisoned)!);
        File.WriteAllText(poisoned, "not the user's archive");
        string detail = $$$"""
        {"id":"aaaa","slug":"public-pack","gameId":"among-us","name":"Public pack","description":"",
         "author":"publisher","fingerprint":"abcd1234","version":3,
         "snapshot":{"schemaVersion":1,"items":[{"modName":"Nexus Mod","modVersion":"1","source":"nexus",
         "link":"https://www.nexusmods.com/amongus/mods/123","manualPath":"{{{poisoned.Replace("\\", "\\\\")}}}","dependencies":[]}]}}
        """;
        var gameRoot = Path.Combine(root, "game");
        try
        {
            using var http = new HttpClient(new SingleReplyHandler(HttpStatusCode.OK, detail))
            { BaseAddress = new Uri("https://catalog.example.test/") };
            using var api = new ModpackApiClient(http, root);
            var pack = await api.GetAsync("public-pack", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(pack);
            Assert.True(pack!.IsPublic);
            Assert.Null(Assert.Single(pack.Items).ManualPath);

            var plan = await new ModpackInstallService(root).PrepareAsync(Game(gameRoot), pack, TestContext.Current.CancellationToken);
            Assert.Equal("Catalog.InstallNexusLocalArchiveRequired", plan.BlockReason);
            Assert.DoesNotContain(Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories),
                path => !string.Equals(path, Path.Combine(gameRoot, "Among Us.exe"), StringComparison.Ordinal));

            // A user who picks the archive themselves is not blocked by the trust boundary.
            var userArchive = WriteArchive(root, "user-picked.zip", Zip("plugins/mod.dll", "user bytes"));
            pack.Items[0].ManualPath = userArchive;
            var userPlan = await new ModpackInstallService(root, packageTransport: new Transport([]))
                .PrepareAsync(Game(gameRoot), pack, TestContext.Current.CancellationToken);
            Assert.True(userPlan.CanInstall, userPlan.BlockReason);
            Assert.Equal("Nexus Mod", Assert.Single(userPlan.SourcePlan!.Items).Name);
            Assert.DoesNotContain(Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories),
                path => !string.Equals(path, Path.Combine(gameRoot, "Among Us.exe"), StringComparison.Ordinal));

            // A cache file written by an older build must not resurrect the server path.
            string cacheDirectory = Path.Combine(root, "modpack-cache");
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(Path.Combine(cacheDirectory, "detail-public-pack.json"),
                JsonSerializer.Serialize(new ModpackDefinition
                {
                    Id = "public-pack", Slug = "public-pack", Name = "Public pack", GameId = "among-us", IsPublic = true,
                    Items = [new ModpackMod { Name = "Nexus Mod", Version = "1", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/123", ManualPath = poisoned }]
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            using var offline = new HttpClient(new OfflineHandler()) { BaseAddress = new Uri("https://catalog.example.test/") };
            using var offlineApi = new ModpackApiClient(offline, root);
            var cached = await offlineApi.GetAsync("public-pack", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Null(Assert.Single(cached!.Items).ManualPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/releases/download/v1/mod.zip", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/mod.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/mod.zip", true)]
    [InlineData("https://evil.githubusercontent.com/mod.zip", false)]
    [InlineData("https://objects.githubusercontent.com.evil.test/mod.zip", false)]
    [InlineData("http://github.com/owner/repo/releases/download/v1/mod.zip", false)]
    public void GitHub_asset_urls_are_limited_to_the_installer_host_allowlist(string url, bool allowed) =>
        Assert.Equal(allowed, GitHubReleaseService.IsSafeDownloadUrl(url));

    private sealed class SingleReplyHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }

    [Fact]
    public async Task Github_host_and_bad_hash_fail_before_any_game_files_are_written()
    {
        var root = TestAccounts.TempPath();
        var archive = Zip("plugins/mod.dll", "integrity fixture");
        var gameRoot = Path.Combine(root, "game");
        try
        {
            GitHubReleaseService.ClearCache();
            SourcePolicy.AllowGitHubRepository("example/security-mod");
            var handler = new GitHubHandler(archive, "example/security-mod");
            using var client = new HttpClient(handler);
            var github = new GitHubReleaseService(client);
            var transport = new RouteTransport(archive);
            var game = Game(gameRoot);
            var badHashPack = Pack(new ModpackMod
            {
                Name = "Broken Hash", Version = "v1.0.0", Source = "github", Repository = "example/security-mod",
                Tag = "v1.0.0", AssetId = "42", Link = "https://github.com/example/security-mod/releases/download/v1.0.0/mod.zip",
                Sha256 = new string('0', 64)
            });
            var badHashPlan = await new ModpackInstallService(root, packageTransport: transport, githubService: github)
                .PrepareAsync(game, badHashPack, TestContext.Current.CancellationToken);
            Assert.False(badHashPlan.CanInstall);
            Assert.Equal("Catalog.InstallHashMismatch", badHashPlan.BlockReason);
            Assert.Equal(1, transport.Requests);
            Assert.DoesNotContain(Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories),
                path => !string.Equals(path, Path.Combine(gameRoot, "Among Us.exe"), StringComparison.Ordinal));

            var rejectedTransport = new RouteTransport(archive);
            var unsafePack = Pack(new ModpackMod
            {
                Name = "Host Check", Version = "v1.0.0", Source = "github", Repository = "example/security-mod",
                Tag = "v1.0.0", AssetId = "42", Link = "https://evil.invalid/archive.zip"
            });
            var unsafePlan = await new ModpackInstallService(root, packageTransport: rejectedTransport, githubService: github)
                .PrepareAsync(game, unsafePack, TestContext.Current.CancellationToken);
            Assert.False(unsafePlan.CanInstall);
            Assert.Equal("Catalog.InstallHostNotAllowed", unsafePlan.BlockReason);
            Assert.Equal(0, rejectedTransport.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failure_on_second_core_source_rolls_back_first_step_and_restores_profile_state()
    {
        var root = TestAccounts.TempPath();
        var gameRoot = Path.Combine(root, "game");
        var firstArchive = Zip("plugins/first.dll", "first fixture");
        var secondArchive = Zip("plugins/second.dll", "second fixture");
        string existingFile = Path.Combine(gameRoot, "BepInEx", "plugins", "Second", "second.dll");
        try
        {
            var game = Game(gameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(existingFile)!);
            await File.WriteAllTextAsync(existingFile, "user-owned fixture", cancellationToken: TestContext.Current.CancellationToken);
            var profiles = new ProfileStore(root);
            var oldData = profiles.For("among-us");
            var oldActive = oldData.ActiveProfileId;
            var originalProfiles = oldData.Profiles.Select(profile => profile.Id).ToArray();
            var service = new ModpackInstallService(root);
            var pack = Pack(
                new ModpackMod { Name = "First", Version = "1", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/123", ManualPath = WriteArchive(root, "first.zip", firstArchive) },
                new ModpackMod { Name = "Second", Version = "1", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/456", ManualPath = WriteArchive(root, "second.zip", secondArchive) });
            var plan = await service.PrepareAsync(game, pack, TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);

            var result = await service.InstallAsync(game, pack, plan, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("Catalog.InstallPlanFailed", result.Error);
            Assert.False(File.Exists(existingFile.Replace("Second", "First", StringComparison.Ordinal)));
            Assert.Equal("user-owned fixture", await File.ReadAllTextAsync(existingFile, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(oldActive, new ProfileStore(root).For("among-us").ActiveProfileId);
            Assert.Equal(originalProfiles, new ProfileStore(root).For("among-us").Profiles.Select(profile => profile.Id));
            Assert.Empty(new InstallRegistry(Path.Combine(root, "installs.json")).ForGame("among-us"));
            var firstJournal = PlanExecutor.LoadJournal(plan.SourcePlan!.Items[0].ExecutionContext.ProfileRoot);
            Assert.StartsWith("uninstall:", firstJournal?.PlanId ?? "", StringComparison.Ordinal);
            var secondJournal = PlanExecutor.LoadJournal(plan.SourcePlan.Items[1].ExecutionContext.ProfileRoot);
            Assert.Equal(JournalState.RolledBack, secondJournal?.State);
            Assert.DoesNotContain(Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories),
                path => path.Contains("First", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string WriteArchive(string root, string name, byte[] bytes)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Maps_github_and_nexus_to_core_contract_and_surfaces_hash_warning()
    {
        var root = TestAccounts.TempPath();
        // Pak105 registers the same example/mod namespace for its own probe. A release asset
        // already in this process-wide allowlist makes the GitHub warning disappear, so the
        // gate starts from a known state here and the entry is dropped again below.
        SourcePolicy.AllowGitHubRepository("example/mod", false);
        try
        {
            var gameRoot = Path.Combine(root, "game");
            var archive = Zip("plugins/mod.dll", "pinned bytes");
            var local = Path.Combine(root, "nexus.zip");
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(local, archive, cancellationToken: TestContext.Current.CancellationToken);
            using var client = new HttpClient(new GitHubHandler(archive, "example/mod"));
            var github = new GitHubReleaseService(client);
            var transport = new Transport(archive);
            var service = new ModpackInstallService(root, packageTransport: transport,
                catalogTransport: new Catalog(new EmptyCatalogTransport()), githubService: github);
            var pack = Pack(
                new ModpackMod { Name = "GitHub Mod", Version = "v1.0.0", Source = "github", Repository = "example/mod", Tag = "v1.0.0", AssetId = "42", Link = "https://github.com/example/mod/releases/download/v1.0.0/mod.zip" },
                new ModpackMod { Name = "Nexus Mod", Version = "2.0", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/123", ManualPath = local });

            var plan = await service.PrepareAsync(Game(gameRoot), pack, TestContext.Current.CancellationToken);

            Assert.True(plan.CanInstall, plan.BlockReason);
            var sourcePlan = Assert.IsType<ModpackSourceInstallPlan>(plan.SourcePlan);
            Assert.Equal(2, sourcePlan.Items.Count);
            Assert.Contains("GITHUB_HASH_UNVERIFIED", plan.Warnings);
            Assert.Equal("v1.0.0", sourcePlan.Items[0].Version);
            Assert.Equal("2.0", sourcePlan.Items[1].Version);
            Assert.Contains(plan.Steps, step => step.Contains("file.copy", StringComparison.Ordinal));
        }
        finally
        {
            SourcePolicy.AllowGitHubRepository("example/mod", false);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(".7z")]
    [InlineData(".rar")]
    public async Task Unsupported_nexus_archive_maps_to_bilingual_user_message(string extension)
    {
        var root = TestAccounts.TempPath();
        var oldLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var archive = Path.Combine(root, "manual" + extension);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(archive, "not a zip", cancellationToken: TestContext.Current.CancellationToken);
            var service = new ModpackInstallService(root);
            var plan = await service.PrepareAsync(Game(Path.Combine(root, "game")), Pack(
                new ModpackMod { Name = "Nexus Mod", Version = "1", Source = "nexus", Link = "https://www.nexusmods.com/amongus/mods/123", ManualPath = archive }), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Catalog.InstallArchiveUnsupported", plan.BlockReason);
            Assert.Equal("Catalog.InstallArchiveUnsupported", ModpackInstallService.GetMappedErrorForTest(ErrorCodes.ArchiveFormatUnsupported));
            Assert.Contains("ZIP", ErrorMessages.Format(ErrorCodes.ArchiveFormatUnsupported, "pl-PL", new Dictionary<string, string> { ["artifact"] = "mod.rar", ["format"] = extension }));
            Assert.Contains("ZIP", ErrorMessages.Format(ErrorCodes.ArchiveFormatUnsupported, "en", new Dictionary<string, string> { ["artifact"] = "mod.rar", ["format"] = extension }));
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = oldLanguage;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Core_hash_and_host_errors_map_to_localized_catalog_messages_without_changing_pins()
    {
        var item = new ModpackMod { Name = "GitHub Mod", Version = "v1.0.0", Source = "github", Tag = "v1.0.0" };

        Assert.Equal("Catalog.InstallHashMismatch", ModpackInstallService.GetMappedErrorForTest(ErrorCodes.ArtifactHashMismatch));
        Assert.Equal("Catalog.InstallSizeMismatch", ModpackInstallService.GetMappedErrorForTest(ErrorCodes.ArtifactSizeMismatch));
        Assert.Equal("Catalog.InstallHostNotAllowed", ModpackInstallService.GetMappedErrorForTest(ErrorCodes.ModSourceHostNotAllowed));
        Assert.Equal("v1.0.0", item.Version);
    }

    [Fact]
    public async Task Thunderstore_only_plan_uses_existing_catalog_install_preparation()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var archive = Zip("plugins/mod.dll", "bytes");
            var catalogMod = new ThunderstoreMod("mod-id", "among-us", "Test", "Mod", "Test-Mod", "desc", "owner",
                "https://thunderstore.io/package/download/Test/Mod/1.0.0/", null, false, DateTimeOffset.UtcNow,
                new ThunderstoreVersion("1.0.0", "Test-Mod", "https://thunderstore.io/package/download/Test/Mod/1.0.0/", archive.Length, DateTimeOffset.UtcNow, []));
            var transport = new CatalogIndexTransport(catalogMod);
            var service = new ModpackInstallService(root, catalogTransport: transport, packageTransport: new Transport(archive));
            var plan = await service.PrepareAsync(Game(Path.Combine(root, "game")), Pack(
                new ModpackMod { Name = "Test-Mod", Version = "1.0.0", Source = "thunderstore", ModId = "mod-id" }), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(plan.CanInstall, plan.BlockReason);
            Assert.Single(plan.Preparations);
            Assert.Null(plan.SourcePlan);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class EmptyCatalogTransport : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogResponse(404, null, null));
    }

    private sealed class CatalogIndexTransport(ThunderstoreMod mod) : ICatalogTransport
    {
        public Task<CatalogResponse> GetAsync(string path, string? ifNoneMatch, CancellationToken cancellationToken)
        {
            if (path.StartsWith("/mods/changes?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, "{\"revision\":1,\"changes\":[],\"reset\":true}"));
            if (path.StartsWith("/mods/index?", StringComparison.Ordinal))
                return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(new { revision = 1, mods = new[] { mod }, nextCursor = (string?)null })));
            if (path.StartsWith("/mods/", StringComparison.Ordinal) && path != "/mods/changes")
                return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(new { mod, versions = new[] { mod.Latest } })));
            return Task.FromResult(new CatalogResponse(404, null, null));
        }
    }
}
