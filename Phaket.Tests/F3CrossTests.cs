using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Models;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// F3 cross-wave closures: the archive picker in front of a public Nexus install (F2a contract),
// the chat reconnect button, and re-publishing a profile as a new version instead of a taken slug.
public class F3CrossTests
{
    private const string NexusLink = "https://www.nexusmods.com/amongus/mods/123";

    private static GameCatalogItem Game() => new(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");

    private static ModpackMod NexusItem() => new()
    {
        Name = "Nexus Mod", Version = "1.2.3", Source = "nexus", Link = NexusLink
    };

    private static ModpackDefinition Pack(bool isPublic, params ModpackMod[] items) => new()
    {
        Id = "pack", Slug = isPublic ? "public-pack" : null, Name = isPublic ? "Public pack" : "Local pack",
        Author = isPublic ? "Publisher" : "Me", Version = "1.0.0", GameId = "among-us",
        IsPublic = isPublic, Items = [.. items]
    };

    private static string WriteArchive(string root, string name)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "archive bytes");
        return path;
    }

    private static void Save(Window window, string name)
    {
        var dir = "/tmp/opencode/phaket-render";
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, name), PngBitmapEncoderOptions.Default);
    }

    private sealed class FakeApi : IModpackApiClient
    {
        public List<ModpackDefinition> Packs { get; } = [];
        public Task<ModpackSearchPage> SearchAsync(string gameId, string? query, string? cursor = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ModpackSearchPage(Packs, null, false));
        public Task<ModpackDefinition?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(Packs.FirstOrDefault(p => p.Slug == slug));
    }

    private sealed class FakeInstaller : IModpackInstallService
    {
        public int PrepareCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public List<string?> PreparedManualPaths { get; } = [];

        public Task<ModpackInstallPlan> PrepareAsync(GameCatalogItem game, ModpackDefinition pack,
            CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            PreparedManualPaths.Add(pack.Items.FirstOrDefault(item => item.Source == "nexus")?.ManualPath);
            return Task.FromResult(new ModpackInstallPlan("test", ["plan"], [], ReadyForExecution: true));
        }

        public Task<ModpackInstallResult> InstallAsync(GameCatalogItem game, ModpackDefinition pack,
            ModpackInstallPlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            progress?.Report(1);
            return Task.FromResult(new ModpackInstallResult(true, "created-profile", null));
        }
    }

    // Stands in for the package service: a slug is taken once, so every later publication must be a version.
    private sealed class ServerLikeProfilePublisher : IProfilePublishService
    {
        private readonly Dictionary<string, int> versions = new(StringComparer.Ordinal);
        public List<ProfilePublishRequest> Requests { get; } = [];

        public Task<ProfilePublishResult> PublishAsync(ProfilePublishRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.CreateNew)
            {
                if (versions.ContainsKey(request.Slug))
                    return Task.FromException<ProfilePublishResult>(new InvalidOperationException("PACKAGE_SLUG_TAKEN"));
                versions[request.Slug] = 1;
            }
            else if (versions.TryGetValue(request.Slug, out int stored)) versions[request.Slug] = ++stored;
            else return Task.FromException<ProfilePublishResult>(new InvalidOperationException("PACKAGE_NOT_FOUND"));
            return Task.FromResult(new ProfilePublishResult(request.Slug, versions[request.Slug]));
        }
    }

    private sealed class NoCommunities : ICommunitiesApiClient
    {
        public bool IsDemoMode => false;
        public Task<CommunityPage> ListAsync(string? query = null, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommunityPage([], null));
        public Task<CommunityDetails?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult<CommunityDetails?>(null);
        public Task<CommunitySummary> CreateAsync(CreateCommunityRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<CommunitySummary>(new NotSupportedException());
        public Task<CommunitySummary> JoinAsync(string slug, string accessMode, string? password, string? inviteCode,
            CancellationToken cancellationToken = default) => Task.FromException<CommunitySummary>(new NotSupportedException());
        public Task LeaveAsync(string slug, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CommunityMessage>> GetMessagesAsync(string slug, string channelId, long after = 0,
            int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CommunityMessage>>([]);
        public Task<CommunityMessage?> SendMessageAsync(string slug, string channelId, string content,
            CancellationToken cancellationToken = default) => Task.FromResult<CommunityMessage?>(null);
    }

    // The catalog view model on the public tab, with the package details already loaded.
    private static async Task<(ModpackCatalogViewModel Vm, ModpackDefinition Pack)> PublicCatalogAsync(
        string root, FakeApi api, FakeInstaller installer, ModpackDefinition pack)
    {
        var vm = new ModpackCatalogViewModel(Game(), root, api, installer, autoLoad: false);
        api.Packs.Add(pack);
        await vm.RefreshPublicCommand.ExecuteAsync(null);
        vm.ShowPublicPacksCommand.Execute(null);
        vm.SelectedPack = pack;
        return (vm, pack);
    }

    private static async Task OpenPickerAsync(ModpackCatalogViewModel vm)
    {
        await vm.StartInstallCommand.ExecuteAsync(null);
        Assert.True(vm.IsNexusPickerOpen);
    }

    [AvaloniaFact]
    public async Task Public_nexus_item_opens_the_archive_picker_before_any_plan()
    {
        string root = TestAccounts.TempPath();
        var api = new FakeApi();
        var installer = new FakeInstaller();
        var (vm, pack) = await PublicCatalogAsync(root, api, installer, Pack(true, NexusItem()));
        try
        {
            await OpenPickerAsync(vm);

            Assert.False(vm.IsPlanOpen);
            Assert.Equal(0, installer.PrepareCalls);
            // Link and pinned version come from the package, the archive stays empty for the user.
            Assert.Equal(NexusLink, vm.NexusPicker!.Url);
            Assert.Equal("Nexus Mod", vm.NexusPicker.Name);
            Assert.Equal("1.2.3", vm.NexusPicker.PinnedVersion);
            Assert.Equal("", vm.NexusPicker.LocalArchivePath);
            Assert.Null(Assert.Single(pack.Items).ManualPath);
        }
        finally { vm.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Picked_archive_is_the_users_path_and_the_plan_continues_after_it()
    {
        string root = TestAccounts.TempPath();
        var api = new FakeApi();
        var installer = new FakeInstaller();
        var (vm, pack) = await PublicCatalogAsync(root, api, installer, Pack(true, NexusItem()));
        try
        {
            await OpenPickerAsync(vm);
            string archive = WriteArchive(root, "picked.zip");

            vm.NexusPicker!.LocalArchivePath = archive;
            vm.NexusPicker.PinSelectionCommand.Execute(null);

            Assert.False(vm.IsNexusPickerOpen);
            Assert.Null(vm.NexusPicker);
            Assert.Equal(Path.GetFullPath(archive), Assert.Single(pack.Items).ManualPath);
            Assert.Equal([Path.GetFullPath(archive)], installer.PreparedManualPaths);
            Assert.Equal(1, installer.PrepareCalls);
            Assert.True(vm.IsPlanOpen);
            // The plan is still only a plan: nothing is written before the user confirms it.
            Assert.Equal(0, installer.InstallCalls);
        }
        finally { vm.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Cancelling_the_picker_installs_nothing_and_leaves_the_path_empty()
    {
        string root = TestAccounts.TempPath();
        var api = new FakeApi();
        var installer = new FakeInstaller();
        var (vm, pack) = await PublicCatalogAsync(root, api, installer, Pack(true, NexusItem()));
        try
        {
            await OpenPickerAsync(vm);

            vm.CancelNexusPickerCommand.Execute(null);

            Assert.False(vm.IsNexusPickerOpen);
            Assert.Null(vm.NexusPicker);
            Assert.Equal("Catalog.NexusArchiveSkipped", vm.InstallErrorKey);
            Assert.True(vm.IsFailureOpen);
            Assert.Equal(0, installer.PrepareCalls);
            Assert.Equal(0, installer.InstallCalls);
            Assert.Null(Assert.Single(pack.Items).ManualPath);
            Assert.False(File.Exists(Path.Combine(root, "modpacks.json")));

            // Retrying the same pack asks for the archive again instead of writing anything.
            await vm.RetryInstallCommand.ExecuteAsync(null);
            Assert.True(vm.IsNexusPickerOpen);
            Assert.Equal(0, installer.PrepareCalls);
        }
        finally { vm.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Local_pack_editor_flow_is_not_replaced_by_the_catalog_picker()
    {
        string root = TestAccounts.TempPath();
        var api = new FakeApi();
        var installer = new FakeInstaller();
        var pack = Pack(false, NexusItem());
        new ModpackStore(root).Save(pack);
        var vm = new ModpackCatalogViewModel(Game(), root, api, installer, autoLoad: false);
        vm.ReloadLocal();
        vm.SelectedPack = vm.VisiblePacks.Single();
        try
        {
            await vm.StartInstallCommand.ExecuteAsync(null);

            Assert.False(vm.IsNexusPickerOpen);
            Assert.Equal(1, installer.PrepareCalls);
            Assert.Null(Assert.Single(pack.Items).ManualPath);
        }
        finally { vm.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Profile_republish_sends_a_new_version_under_the_stored_slug()
    {
        string root = TestAccounts.TempPath();
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var publisher = new ServerLikeProfilePublisher();
        var profile = new ProfileStore(root).AddProfile("among-us", "Mój profil",
            [new ModEntry { Name = "Mod A", Version = "1.2" }]);
        var vm = new ProfilesViewModel(Game(), () => { }, root, publisher: publisher);
        try
        {
            vm.SelectCommand.Execute(vm.Profiles.Single(item => item.Id == profile.Id));

            await vm.PublishProfileCommand.ExecuteAsync(null);
            Assert.True(publisher.Requests[0].CreateNew);
            Assert.Equal("moj-profil", publisher.Requests[0].Slug);

            await vm.PublishProfileCommand.ExecuteAsync(null);

            Assert.Equal(2, publisher.Requests.Count);
            Assert.False(publisher.Requests[1].CreateNew);
            Assert.Equal("moj-profil", publisher.Requests[1].Slug);
            Assert.Contains("wersja 2", vm.ShareStatus, StringComparison.Ordinal);
            // The slug is user data now: a restart keeps publishing versions instead of failing on a taken slug.
            Assert.Equal("moj-profil", new ProfileStore(root).For("among-us").Profiles.Single(p => p.Id == profile.Id).Slug);

            var reopened = new ProfilesViewModel(Game(), () => { }, root, publisher: publisher);
            reopened.SelectCommand.Execute(reopened.Profiles.Single(item => item.Id == profile.Id));
            await reopened.PublishProfileCommand.ExecuteAsync(null);
            Assert.Equal(3, publisher.Requests.Count);
            Assert.False(publisher.Requests[2].CreateNew);
            Assert.Contains("wersja 3", reopened.ShareStatus, StringComparison.Ordinal);
            Assert.Single(publisher.Requests, request => request.CreateNew);
        }
        finally { LocalizationService.Instance.CurrentLanguage = language; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Profiles_without_a_slug_are_read_without_losing_any_data()
    {
        string root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "profiles.json"), """
            {"among-us":{"Profiles":[{"Id":"p1","Name":"Stary profil","Mods":[{"Name":"Mod A","Version":"1.0",
            "Enabled":true,"Compatible":true,"Required":false}]}],"ActiveProfileId":"p1"}}
            """);
        try
        {
            var store = new ProfileStore(root);
            Assert.False(store.IsLoadFailed);
            var profile = Assert.Single(store.For("among-us").Profiles);
            Assert.Null(profile.Slug);
            Assert.Equal("Stary profil", profile.Name);
            Assert.Equal("1.0", Assert.Single(profile.Mods).Version);

            store.Save();

            var reloaded = new ProfileStore(root).For("among-us").Profiles.Single(p => p.Id == "p1");
            Assert.Null(reloaded.Slug);
            Assert.Equal("Mod A", Assert.Single(reloaded.Mods).Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Chat_lost_state_shows_the_reconnect_button_wired_to_the_existing_command()
    {
        string root = TestAccounts.TempPath();
        var vm = new CommunitiesViewModel(null, root, new NoCommunities(), new FakeApi());
        var view = new CommunitiesView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Button Button() => view.GetVisualDescendants().OfType<Button>()
                .Single(button => (string?)button.GetValue(AutomationProperties.NameProperty) ==
                    LocalizationService.Instance["Chat.RetryPolling"]);

            vm.IsChatOpen = true;
            vm.ChatStatus = LocalizationService.Instance["Chat.PollFailed"];
            Dispatcher.UIThread.RunJobs();
            Assert.False(Button().IsVisible);

            vm.ChatStatus = LocalizationService.Instance["Chat.ConnectionLost"];
            Dispatcher.UIThread.RunJobs();
            var button = Button();
            Assert.True(button.IsVisible);
            Assert.Same(vm.RetryChatPollingCommand, button.Command);
            Assert.True(button.IsEnabled);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Runs on the UI thread: switching the language raises PropertyChanged on views other tests left behind.
    [AvaloniaFact]
    public void F3_strings_are_present_in_both_languages()
    {
        var prior = LocalizationService.Instance.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "pl-PL", "en" })
            {
                LocalizationService.Instance.CurrentLanguage = language;
                foreach (string key in new[] { "Chat.RetryPolling", "Catalog.NexusPickerTitle", "Catalog.NexusArchiveSkipped" })
                {
                    string text = LocalizationService.Instance[key];
                    Assert.NotEqual(key, text);
                    Assert.NotEmpty(text);
                }
            }
        }
        finally { LocalizationService.Instance.CurrentLanguage = prior; }
    }

    [AvaloniaFact]
    public async Task Render_nexus_archive_picker_over_the_catalog()
    {
        string root = TestAccounts.TempPath();
        var api = new FakeApi();
        var (vm, _) = await PublicCatalogAsync(root, api, new FakeInstaller(), Pack(true, NexusItem()));
        var window = new Window { Width = 1280, Height = 800, Content = new ModpackCatalogView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            await OpenPickerAsync(vm);
            Save(window, "44-nexus-picker.png");
        }
        finally
        {
            window.Close();
            vm.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
