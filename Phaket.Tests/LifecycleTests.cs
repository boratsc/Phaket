using System.Globalization;
using System.Net.Http;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// Review F2b: the shell keeps one catalog view model per window (install queue, downloads page,
// update badge), stops the chat when leaving communities, and publishes through the signed
// clients instead of leaving them unwired.
public sealed class LifecycleTests
{
    private sealed class StubApi : IModpackApiClient
    {
        public List<ModpackDefinition> Packs { get; init; } = [];

        public Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ModpackSearchPage(Packs, null));

        public Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(Packs.FirstOrDefault(p => p.Slug == slug));
    }

    private sealed class GatedInstaller(Task gate) : IModpackInstallService
    {
        public int PrepareCount { get; private set; }
        public int InstallCount { get; private set; }

        public Task<ModpackInstallPlan> PrepareAsync(GameCatalogItem game, ModpackDefinition pack,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult(new ModpackInstallPlan("test", ["plan"], [], ReadyForExecution: true));
        }

        public async Task<ModpackInstallResult> InstallAsync(GameCatalogItem game, ModpackDefinition pack,
            ModpackInstallPlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCount++;
            await gate;
            return new ModpackInstallResult(true, "created-profile", null);
        }
    }

    private sealed class StubPublisher : IModpackPublisher
    {
        public List<(string Slug, bool CreateNew)> Requests { get; } = [];
        public Exception? Failure { get; init; }

        public Task<ModpackPublishResult> PublishAsync(ModpackDefinition package, string slug, bool createNew = true,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((slug, createNew));
            return Failure is null
                ? Task.FromResult(new ModpackPublishResult(slug, 3))
                : Task.FromException<ModpackPublishResult>(Failure);
        }
    }

    private sealed class StubProfilePublisher : IProfilePublishService
    {
        public ProfilePublishRequest? Last { get; private set; }
        public Exception? Failure { get; set; }

        public Task<ProfilePublishResult> PublishAsync(ProfilePublishRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Failure is null
                ? Task.FromResult(new ProfilePublishResult(request.Slug, 2))
                : Task.FromException<ProfilePublishResult>(Failure);
        }
    }

    private sealed class RegistrationHandler(string fingerprint, List<string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"deviceId":"device","fingerprint":"{{fingerprint}}","nick":"Phaket device"}""")
            });
        }
    }

    private static GameCatalogItem Game() => new(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");

    private static ModpackDefinition Pack(string name, params ModpackMod[] mods) => new()
    {
        Id = name, Name = name, Author = "Local author", GameId = "among-us", Version = "1.0.0", Items = [.. mods]
    };

    [AvaloniaFact]
    public void Second_visit_of_the_catalog_reuses_the_view_model_the_downloads_page_watches()
    {
        var vm = TestAccounts.ViewModel();
        try
        {
            var first = vm.ModpackCatalog;
            Assert.NotNull(first);

            vm.NavigateCommand.Execute("library");
            vm.NavigateCommand.Execute("catalog");
            vm.NavigateCommand.Execute("game");

            Assert.Same(first, vm.ModpackCatalog);
            Assert.Same(first, vm.CurrentPage);

            // The downloads page follows the live instance, not the one it was handed at startup.
            first!.SelectedPack = Pack("Running pack", new ModpackMod { Name = "Mod A", Version = "1" });
            first.IsInstallRunning = true;
            Assert.Contains(vm.Downloads.Items, item => item.StatusKey == "Downloads.Installing");
            first.IsInstallRunning = false;
            Assert.DoesNotContain(vm.Downloads.Items, item => item.StatusKey == "Downloads.Installing");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Running_installation_blocks_a_second_parallel_run()
    {
        string root = TestAccounts.TempPath();
        var gate = new TaskCompletionSource();
        var installer = new GatedInstaller(gate.Task);
        var vm = new ModpackCatalogViewModel(Game(), root, new StubApi(), installer, autoLoad: false);
        try
        {
            vm.SelectedPack = Pack("Pack", new ModpackMod { Name = "Mod A", Version = "1", Source = "thunderstore" });
            await vm.StartInstallCommand.ExecuteAsync(null);
            Assert.True(vm.IsPlanOpen);

            var running = vm.ConfirmInstallCommand.ExecuteAsync(null);
            Assert.True(vm.IsInstallRunning);

            // Re-entering the tab or clicking again must not open a second download/apply pass.
            await vm.StartInstallCommand.ExecuteAsync(null);
            await vm.ConfirmInstallCommand.ExecuteAsync(null);
            Assert.Equal(1, installer.InstallCount);
            Assert.Equal(1, installer.PrepareCount);

            gate.SetResult();
            await running;
            Assert.False(vm.IsInstallRunning);
            Assert.Equal("Catalog.InstallSuccess", vm.StatusKey);

            await vm.StartInstallCommand.ExecuteAsync(null);
            Assert.Equal(2, installer.PrepareCount);
        }
        finally
        {
            vm.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Disposed_catalog_stops_being_kept_alive_by_the_localization_service()
    {
        string root = TestAccounts.TempPath();
        try
        {
            WeakReference reference = CreateAndDispose(root);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(reference.IsAlive);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        static WeakReference CreateAndDispose(string appDataRoot)
        {
            var vm = new ModpackCatalogViewModel(Game(), appDataRoot, new StubApi(),
                new GatedInstaller(Task.CompletedTask), autoLoad: false);
            vm.Dispose();
            return new WeakReference(vm);
        }
    }

    [AvaloniaFact]
    public void Leaving_communities_closes_the_chat_and_its_polling()
    {
        var vm = TestAccounts.ViewModel();
        try
        {
            vm.CurrentPage = vm.Communities;
            vm.Communities.IsChatOpen = true;

            vm.NavigateCommand.Execute("catalog");

            Assert.False(vm.Communities.IsChatOpen);
        }
        finally { vm.Dispose(); }
    }

    // Every test below moves the shared LocalizationService singleton, so they run on the UI
    // thread like the rest of the suite: a language change re-evaluates bindings of views that
    // other classes may still have mounted.
    [AvaloniaFact]
    public async Task Publishing_a_local_pack_uses_the_signed_publisher_and_keeps_the_slug()
    {
        string root = TestAccounts.TempPath();
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var pack = Pack("Mój pakiet", new ModpackMod { Name = "Mod A", Version = "1" });
        new ModpackStore(root).Save(pack);
        var publisher = new StubPublisher();
        var vm = new ModpackCatalogViewModel(Game(), root, new StubApi(),
            new GatedInstaller(Task.CompletedTask), autoLoad: false, publisher: publisher);
        try
        {
            vm.SelectedPack = vm.LocalPacks.Single(p => p.Name == pack.Name);
            Assert.True(vm.HasPublishAction);
            Assert.True(vm.CanPublish);

            await vm.PublishCommand.ExecuteAsync(null);

            Assert.Single(publisher.Requests);
            Assert.Equal("moj-pakiet", publisher.Requests[0].Slug);
            Assert.True(publisher.Requests[0].CreateNew);
            Assert.Contains("moj-pakiet", vm.PublishMessage);
            Assert.Contains("3", vm.PublishMessage);
            // The slug survives a reload, so a second publication adds a version to the same pack.
            Assert.Equal("moj-pakiet", new ModpackStore(root).ForGame("among-us").Single(p => p.Name == pack.Name).Slug);

            vm.ShowPublic = true;
            Assert.False(vm.HasPublishAction);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            vm.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Publication_reports_failure_without_losing_the_local_pack()
    {
        string root = TestAccounts.TempPath();
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "en";
        var publisher = new StubPublisher { Failure = new HttpRequestException("offline") };
        var vm = new ModpackCatalogViewModel(Game(), root, new StubApi(),
            new GatedInstaller(Task.CompletedTask), autoLoad: false, publisher: publisher);
        try
        {
            var pack = Pack("Offline pack", new ModpackMod { Name = "Mod A", Version = "1" });
            vm.SelectedPack = pack;
            await vm.PublishCommand.ExecuteAsync(null);

            Assert.Equal(LocalizationService.Instance["Catalog.PublishFailed"], vm.PublishMessage);
            Assert.Null(pack.Slug);
            Assert.False(vm.IsPublishing);

            vm.SelectedPack = Pack("Empty pack");
            await vm.PublishCommand.ExecuteAsync(null);
            Assert.Equal(LocalizationService.Instance["Catalog.PublishInvalid"], vm.PublishMessage);
            // An empty pack never reaches the network.
            Assert.Single(publisher.Requests);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            vm.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Profile_page_publishes_the_active_profile_snapshot()
    {
        string root = TestAccounts.TempPath();
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var publisher = new StubProfilePublisher();
        try
        {
            var profile = new ProfileStore(root).AddProfile("among-us", "Mój profil",
                [new ModEntry { Name = "Mod A", Version = "1.2" }]);
            var vm = new ProfilesViewModel(Game(), () => { }, root, publisher: publisher);
            vm.SelectCommand.Execute(vm.Profiles.Single(item => item.Id == profile.Id));
            Assert.True(vm.CanPublish);

            await vm.PublishProfileCommand.ExecuteAsync(null);

            var request = publisher.Last!;
            Assert.Equal("moj-profil", request.Slug);
            Assert.Equal("among-us", request.GameId);
            Assert.Equal("Mój profil", request.Name);
            Assert.Equal(1, request.Snapshot.GetProperty("schemaVersion").GetInt32());
            var item = request.Snapshot.GetProperty("items")[0];
            Assert.Equal("Mod A", item.GetProperty("modName").GetString());
            Assert.Equal("1.2", item.GetProperty("modVersion").GetString());
            Assert.Contains("moj-profil", vm.ShareStatus);

            publisher.Failure = new HttpRequestException("offline");
            await vm.PublishProfileCommand.ExecuteAsync(null);
            Assert.Equal(LocalizationService.Instance["Profiles.PublishFailed"], vm.ShareStatus);
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Profile_history_keeps_the_newest_hundred_snapshots_of_a_profile()
    {
        string root = TestAccounts.TempPath();
        var store = new ProfileHistoryStore(root);
        try
        {
            for (int i = 1; i <= 102; i++)
                store.Record("among-us", new GameProfile
                {
                    Id = "pinned",
                    Name = "Pinned",
                    Mods = [new ModEntry { Name = "Mod", Version = i.ToString(CultureInfo.InvariantCulture) }]
                });

            var history = store.For("among-us", "pinned");
            Assert.Equal(100, history.Count);
            Assert.Equal("102", history[^1].Snapshot.Mods[0].Version);
            Assert.Equal("3", history[0].Snapshot.Mods[0].Version);
            // The cap is on disk too, so the file stops growing after a hundred visits.
            Assert.Equal(100, new ProfileHistoryStore(root).For("among-us", "pinned").Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Device_registration_is_remembered_by_fingerprint_and_not_repeated()
    {
        string root = TestAccounts.TempPath();
        var secrets = new Phaket.Core.Tests.InMemorySecretStore();
        await secrets.SetAsync(DeviceKeyStore.SecretKey, new byte[32], TestContext.Current.CancellationToken);
        var keys = new DeviceKeyStore(secrets, Path.Combine(root, "settings.json"));
        string fingerprint = (await keys.GetPublicIdentityAsync(TestContext.Current.CancellationToken)).Fingerprint[..8];
        var paths = new List<string>();
        var identity = new DeviceIdentityServices(keys, new DeviceKeySigner(keys, registerBeforeSigning: false),
            () => new RegistrationHandler(fingerprint, paths));

        await identity.RegisterDeviceAsync("https://register.example.test/", TestContext.Current.CancellationToken);
        await identity.RegisterDeviceAsync("https://register.example.test/", TestContext.Current.CancellationToken);

        Assert.Equal("/devices", Assert.Single(paths));
        Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public async Task Publish_action_is_rendered_in_my_modpacks_at_1280x800()
    {
        string root = TestAccounts.TempPath();
        var language = LocalizationService.Instance.CurrentLanguage;
        var publisher = new StubPublisher();
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            new ModpackStore(root).Save(Pack("Mój pakiet", new ModpackMod { Name = "Mod A", Version = "1" }));
            var vm = new ModpackCatalogViewModel(Game(), root, new StubApi(),
                new GatedInstaller(Task.CompletedTask), autoLoad: false, publisher: publisher);
            vm.SelectedPack = vm.LocalPacks.First();

            var window = new Window { Width = 1280, Height = 800, Content = new ModpackCatalogView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Command, vm.PublishCommand));
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEnabled);
            Assert.Equal("Opublikuj w katalogu", button.Content);
            Assert.Equal("Opublikuj w katalogu", AutomationProperties.GetName(button));

            Directory.CreateDirectory(RenderArtifacts.Directory);
            using (var frame = window.CaptureRenderedFrame())
                frame!.Save(RenderArtifacts.Path("43-publish.png"), PngBitmapEncoderOptions.Default);

            await vm.PublishCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Opublikowano jako moj-pakiet · wersja 3.", vm.PublishMessage);
            window.Close();
            vm.Dispose();
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Neutral_channel_names_are_translated_and_unknown_names_are_kept()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        var converter = ChannelNameConverter.Instance;
        var culture = CultureInfo.InvariantCulture;
        try
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            Assert.Equal("Ogólny", converter.Convert("general", typeof(string), null, culture));
            Assert.Equal("Pomoc", converter.Convert(" Help ", typeof(string), null, culture));
            Assert.Equal("modding", converter.Convert("modding", typeof(string), null, culture));
            LocalizationService.Instance.CurrentLanguage = "en";
            Assert.Equal("General", converter.Convert("general", typeof(string), null, culture));
            Assert.Equal("Help", converter.Convert("help", typeof(string), null, culture));
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; }
    }
}
