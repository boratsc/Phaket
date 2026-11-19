using System.Net;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

public sealed class ModpackCatalogTests
{
    private sealed class FakeApi : IModpackApiClient
    {
        public bool Offline { get; set; }
        public List<ModpackDefinition> Packs { get; set; } = [];
        public Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ModpackSearchPage(Packs, null, Offline));
        public Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(Packs.FirstOrDefault(p => p.Slug == slug));
    }

    private sealed class FakeInstaller : IModpackInstallService
    {
        public ModpackDefinition? Installed { get; private set; }
        public string? PreparedSource { get; private set; }
        public Task<ModpackInstallPlan> PrepareAsync(GameCatalogItem game, ModpackDefinition pack,
            CancellationToken cancellationToken = default)
        {
            PreparedSource = pack.Items.FirstOrDefault(item => item.Enabled)?.Source;
            return Task.FromResult(pack.Items.Any(item => item.Enabled && item.Source != "thunderstore")
                ? new ModpackInstallPlan("test", [], [], "Catalog.InstallSourceUnavailable")
                : new ModpackInstallPlan("test", ["plan"], [], ReadyForExecution: true));
        }
        public Task<ModpackInstallResult> InstallAsync(GameCatalogItem game, ModpackDefinition pack,
            ModpackInstallPlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Installed = pack;
            progress?.Report(1);
            return Task.FromResult(new ModpackInstallResult(true, "created-profile", null));
        }
    }

    private static GameCatalogItem Game() => new(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
    private static ModpackDefinition Pack(string name, params ModpackMod[] mods) => new()
    {
        Id = name, Name = name, Author = "Test author", GameId = "among-us", Version = "1.2.0", Items = [.. mods]
    };

    [Fact]
    public void Local_pack_count_excludes_clean_game_profile()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            store.Save(Pack("Czysta gra"));
            store.Save(Pack("Mój pakiet", new ModpackMod { Name = "Mod A", Version = "1" }));
            var vm = new ModpackCatalogViewModel(Game(), root, new FakeApi(), new FakeInstaller(), autoLoad: false);
            Assert.Equal(1, vm.MyPackCount);
            Assert.Single(vm.VisiblePacks);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Search_matches_mod_name_and_author()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            store.Save(Pack("North", new ModpackMod { Name = "Craft From Containers", Version = "2" }));
            store.Save(Pack("South", new ModpackMod { Name = "Plant Everything", Version = "3" }));
            var vm = new ModpackCatalogViewModel(Game(), root, new FakeApi(), new FakeInstaller(), autoLoad: false);
            vm.SearchText = "containers";
            Assert.Equal("North", Assert.Single(vm.VisiblePacks).Name);
            vm.SearchText = "test author";
            Assert.Equal(2, vm.VisiblePacks.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Offline_state_is_exposed_from_cached_api_response()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var handler = new StubHandler(_ => throw new HttpRequestException());
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://catalog.test") };
            var api = new ModpackApiClient(http, root);
            var vm = new ModpackCatalogViewModel(Game(), root, api, new FakeInstaller(), autoLoad: false);
            await vm.RefreshPublicCommand.ExecuteAsync(null);
            Assert.True(vm.IsOffline);
            Assert.Equal("Catalog.Offline", vm.StatusKey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Api_client_parses_and_offline_uses_last_search_response()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"packages":[{"slug":"one","gameId":"among-us","name":"One","author":"A","description":"D","latestVersion":3}],"nextCursor":null}""", Encoding.UTF8, "application/json")
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://catalog.test") };
            var api = new ModpackApiClient(http, root);
            var fresh = await api.SearchAsync("among-us", "one", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(fresh.Packages);
            handler.Respond = _ => throw new HttpRequestException();
            var offline = await api.SearchAsync("among-us", "one", cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(offline.IsOffline);
            Assert.Equal("One", Assert.Single(offline.Packages).Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Catalog_renders_and_install_creates_profile_via_fake_service()
    {
        var root = TestAccounts.TempPath();
        var priorLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            var api = new FakeApi { Packs = [Pack("Publiczny", new ModpackMod { Name = "Mod A", Version = "1", Source = "thunderstore" })] };
            var installer = new FakeInstaller();
            var game = Game();
            var vm = new ModpackCatalogViewModel(game, root, api, installer, autoLoad: false);
            vm.ShowPublic = true;
            await vm.RefreshPublicCommand.ExecuteAsync(null);
            vm.SelectPackCommand.Execute(vm.VisiblePacks[0]);
            var window = new Window { Width = 1280, Height = 800, Content = new ModpackCatalogView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using (var frame = window.CaptureRenderedFrame())
            {
                frame!.Save("/tmp/opencode/phaket-render/20-catalog.png");
                frame.Save("/tmp/opencode/phaket-render/38-catalog-polished.png");
            }
            await vm.StartInstallCommand.ExecuteAsync(null);
            Assert.Equal("thunderstore", installer.PreparedSource);
            Assert.True(vm.IsPlanOpen);
            await vm.ConfirmInstallCommand.ExecuteAsync(null);
            Assert.NotNull(installer.Installed);
            Assert.False(vm.IsFailureOpen);
            window.Close();
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = priorLanguage;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Installer_failure_is_shown_from_localized_plan_result()
    {
        var root = TestAccounts.TempPath();
        var priorLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "en";
            var pack = Pack("GitHub", new ModpackMod { Name = "Mod A", Version = "1", Source = "github", Enabled = true });
            var vm = new ModpackCatalogViewModel(Game(), root, new FakeApi(), new FakeInstaller(), autoLoad: false);
            vm.SelectPackCommand.Execute(pack);
            await vm.StartInstallCommand.ExecuteAsync(null);
            Assert.True(vm.IsFailureOpen);
            Assert.Equal("Catalog.InstallSourceUnavailable", vm.InstallErrorKey);
            Assert.Equal("This source is not supported by the installer.", LocalizationService.Instance[vm.InstallErrorKey!]);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = priorLanguage;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Profile_ready_state_requires_all_enabled_pack_mods_and_controls_play_action()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var pack = Pack("Ready", new ModpackMod { Name = "Mod A", Version = "1" });
            var profiles = new ProfileStore(root);
            profiles.AddProfile("among-us", "Ready", [new ModEntry { Name = "Mod A", Version = "1" }]);
            var game = Game();
            string installRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(installRoot);
            game.Installations.Add(new Phaket.Core.Platforms.SteamInstallation(
                new Phaket.Core.Domain.Installation("steam-test", "steam-edition", installRoot),
                new Phaket.Core.Platforms.SteamAppManifest("123", "Among Us", "1", "public", "1", "Among Us", 1),
                new Phaket.Core.Platforms.SteamLibrary(installRoot, false),
                Phaket.Core.Platforms.GameAvailability.Installed));
            var vm = new ModpackCatalogViewModel(game, root, new FakeApi(), new FakeInstaller(), autoLoad: false);
            vm.SelectPackCommand.Execute(pack);
            Assert.True(vm.IsProfileReady);
            Assert.False(vm.CanInstall);
            Assert.True(vm.CanPlay);
            Assert.False(vm.ShowPlayUnavailable);

            pack.Items[0].Version = "2";
            vm.SelectPackCommand.Execute(null);
            vm.SelectPackCommand.Execute(pack);
            Assert.False(vm.IsProfileReady);
            Assert.True(vm.CanInstall);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }
}
