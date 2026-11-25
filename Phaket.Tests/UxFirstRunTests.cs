using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// PAK-86: first-run UX reported by the user — the game picker must always be reachable,
// the breadcrumb must not show placeholders or duplicates, the DepotDownloader session
// note belongs to the Steam install flow only, and the device nickname must be unique.
public class UxFirstRunTests : IDisposable
{
    private readonly string previousLanguage = LocalizationService.Instance.CurrentLanguage;
    private readonly List<Window> windows = [];

    public UxFirstRunTests() => LocalizationService.Instance.CurrentLanguage = "pl-PL";

    public void Dispose()
    {
        foreach (var window in windows) window.Close();
        LocalizationService.Instance.CurrentLanguage = previousLanguage;
    }

    private Window Show(Control content, double width = 1280, double height = 800)
    {
        var window = content as Window ?? new Window { Content = content };
        window.Width = width;
        window.Height = height;
        windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Game_picker_stays_visible_and_prompts_when_no_game_is_selected()
    {
        var loc = LocalizationService.Instance;
        var vm = TestAccounts.ViewModel();
        Assert.True(vm.HasGame);
        var window = Show(new MainWindow(vm));
        var picker = window.FindControl<ComboBox>("GamePicker")!;

        // This is what the picker's two-way binding does while the library list is rebuilt.
        vm.SelectedGame = null;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.HasGame);
        Assert.True(picker.IsEffectivelyVisible);
        var prompt = Assert.IsType<SelectGameViewModel>(vm.CurrentPage);
        Assert.Equal(loc["Shell.SelectGame"], prompt.EmptyTitle);
        Assert.Equal(loc["Shell.OpenLibrary"], prompt.ActionLabel);
        Assert.NotNull(prompt.ActionCommand);
        Assert.Equal("catalog", vm.ActiveNav);
        Assert.DoesNotContain(vm.Breadcrumb, crumb => crumb.Label == loc["Shell.NoGame"]);
    }

    [AvaloniaFact]
    public void Empty_library_shows_add_game_action_and_restores_selection_when_a_game_appears()
    {
        var vm = TestAccounts.ViewModel();
        var window = Show(new MainWindow(vm));
        var game = vm.Library.Games[0];

        vm.Library.Games.Clear();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.HasGames);
        Assert.False(window.FindControl<ComboBox>("GamePicker")!.IsEffectivelyVisible);
        var addGame = window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Command, vm.Library.AddFolderCommand));
        Assert.True(addGame.IsEffectivelyVisible);
        Assert.Equal(LocalizationService.Instance["Library.AddGame"], addGame.Content);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == LocalizationService.Instance["Shell.NoGamesInLibrary"] && text.IsEffectivelyVisible);

        vm.Library.Games.Add(game);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasGames);
        Assert.Same(game, vm.SelectedGame);
        Assert.IsType<ModpackCatalogViewModel>(vm.CurrentPage);
    }

    [AvaloniaFact]
    public void Picking_a_game_from_the_prompt_keeps_the_context_tab()
    {
        var vm = TestAccounts.ViewModel();
        var game = vm.Library.Games.First(item => !ReferenceEquals(item, vm.SelectedGame));
        vm.SelectedGame = null;
        vm.NavigateCommand.Execute("profiles");
        Assert.IsType<SelectGameViewModel>(vm.CurrentPage);
        Assert.Equal("profiles", vm.ActiveNav);

        vm.SelectedGame = game;

        Assert.Equal("profiles", vm.ActiveNav);
        Assert.IsType<ProfilesViewModel>(vm.CurrentPage);
        Assert.Equal(game.Title, vm.Profiles!.GameTitle);
    }

    [AvaloniaFact]
    public void Breadcrumb_has_no_placeholder_and_no_duplicate_crumb()
    {
        var loc = LocalizationService.Instance;
        var vm = TestAccounts.ViewModel();
        Assert.True(vm.HasGame);
        var game = vm.SelectedGame!;

        vm.NavigateCommand.Execute("library");
        Assert.Equal([new BreadcrumbItem(loc["Nav.Library"], null, true)], vm.Breadcrumb);

        vm.NavigateCommand.Execute("catalog");
        Assert.Equal(
        [
            new BreadcrumbItem(loc["Nav.Library"], "library", false),
            new BreadcrumbItem(game.Title, "catalog", false),
            new BreadcrumbItem(loc["Catalog.Title"], null, true)
        ], vm.Breadcrumb);

        vm.NavigateCommand.Execute("settings");
        Assert.Equal([new BreadcrumbItem(loc["Nav.Settings"], null, true)], vm.Breadcrumb);

        vm.Library.OpenGameCommand.Execute(game);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(
        [
            new BreadcrumbItem(loc["Nav.Library"], "library", false),
            new BreadcrumbItem(game.Title, null, true)
        ], vm.Breadcrumb);

        vm.NavigateCommand.Execute("mods");
        Assert.Equal(
        [
            new BreadcrumbItem(loc["Nav.Library"], "library", false),
            new BreadcrumbItem(game.Title, "catalog", false),
            new BreadcrumbItem(loc["Game.Mods"], null, true)
        ], vm.Breadcrumb);
    }

    [AvaloniaFact]
    public void Breadcrumb_crumbs_navigate_and_render_without_duplicates()
    {
        var loc = LocalizationService.Instance;
        var vm = TestAccounts.ViewModel();
        vm.NavigateCommand.Execute("catalog");
        var window = Show(new MainWindow(vm));

        var labels = window.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(button.Command, vm.NavigateBreadcrumbCommand))
            .Select(button => button.Content as string ?? "").ToArray();
        Assert.Equal(vm.Breadcrumb.Select(crumb => crumb.Label).ToArray(), labels);
        Assert.Equal(labels.Length, labels.Distinct().Count());
        Assert.DoesNotContain(loc["Shell.NoGame"], labels);

        vm.NavigateBreadcrumbCommand.Execute("library");
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<LibraryViewModel>(vm.CurrentPage);
        Assert.Equal("library", vm.ActiveNav);
    }

    [AvaloniaFact]
    public void Depot_session_tools_show_only_in_the_steam_install_flow()
    {
        var accounts = TestAccounts.Create();
        var library = new LibraryViewModel(_ => { }, accounts, null);
        var epic = library.Games.First();
        epic.EpicOwned = true;
        library.SelectedGame = epic;
        var window = Show(new LibraryView { DataContext = library });

        // The disclosure header is the reported symptom; its button sits inside a collapsed
        // expander, so the header is what proves the section is present in this context.
        bool SessionToolsVisible() => window.GetVisualDescendants().OfType<Expander>()
            .Any(expander => expander.IsEffectivelyVisible && expander.Header is TextBlock header
                && header.Text == LocalizationService.Instance["SteamInstall.SessionCleanup"]);

        // Reported symptom: the DepotDownloader session note appeared for Epic games and in the
        // plain library view, where no Steam install using DepotDownloader is running.
        Assert.False(SessionToolsVisible());
        Assert.False(library.ShowSteamSessionTools);

        library.SteamInstall = new SteamInstallFlowViewModel(
            new Phaket.Core.Platforms.OwnedSteamGame("945360", "Among Us", null, null, "api", false),
            new Phaket.Core.Omrl.GameDefinition("among-us", [], MainExecutable: "game.exe"),
            new Phaket.Core.Platforms.DepotDownloaderInstallAdapter(null, Path.Combine(TestAccounts.TempPath(), "config"), "/nonexistent"),
            () => Task.FromResult<string?>(null), credentialStore: accounts.SteamCredentials);
        Dispatcher.UIThread.RunJobs();

        Assert.True(library.ShowSteamSessionTools);
        Assert.True(SessionToolsVisible());

        library.CloseSteamInstallCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(SessionToolsVisible());
    }

    [AvaloniaFact]
    public async Task Avatar_shows_initials_of_the_device_nickname()
    {
        var vm = TestAccounts.ViewModel();
        await vm.Accounts.DeviceIdentity.Initialization;
        Dispatcher.UIThread.RunJobs();
        var nick = vm.Accounts.DeviceIdentity.Nick;
        Assert.False(string.IsNullOrWhiteSpace(nick));
        Assert.Equal(MainWindowViewModel.InitialsOf(nick), vm.AvatarInitials);

        var window = Show(new MainWindow(vm));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == vm.AvatarInitials && text.IsEffectivelyVisible);
    }

    [Fact]
    public void Initials_follow_the_nickname_shape()
    {
        Assert.Equal("G", MainWindowViewModel.InitialsOf("Gracz-1a2b"));
        Assert.Equal("BG", MainWindowViewModel.InitialsOf("Bartek Gradzik"));
        Assert.Equal("?", MainWindowViewModel.InitialsOf(""));
        Assert.Equal("?", MainWindowViewModel.InitialsOf(null));
    }

    // Visual review of the three reported states, saved next to the other shell renders.
    [AvaloniaFact]
    public async Task Render_first_run_ux_states()
    {
        var loc = LocalizationService.Instance;
        var vm = TestAccounts.ViewModel();
        var games = vm.Library.Games.ToArray();
        var window = Show(new MainWindow(vm));

        vm.Library.Games.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.HasGame);
        Assert.IsType<SelectGameViewModel>(vm.CurrentPage);
        Save(window, "50-no-game.png");

        foreach (var game in games) vm.Library.Games.Add(game);
        Dispatcher.UIThread.RunJobs();
        vm.NavigateCommand.Execute("catalog");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Breadcrumb.Count);
        Save(window, "51-game-selected.png");

        vm.NavigateCommand.Execute("account");
        await vm.Accounts.DeviceIdentity.Initialization;
        Dispatcher.UIThread.RunJobs();
        var warning = window.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Text == loc["Identity.KeyWarning"]);
        warning.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Assert.Matches($"^{loc["Identity.NickPrefix"]}-[0-9a-f]{{4}}$", vm.Accounts.DeviceIdentity.Nick);
        Save(window, "52-identity-friendly.png");
    }

    private static void Save(Window window, string name)
    {
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine("/tmp/opencode/phaket-render", name), PngBitmapEncoderOptions.Default);
    }

    [Fact]
    public async Task Default_nickname_is_unique_per_device_key_and_follows_the_fingerprint()
    {
        string prefix = LocalizationService.Instance["Identity.NickPrefix"];
        var first = await ReadIdentity();
        var second = await ReadIdentity();

        Assert.NotEqual(first.Nick, second.Nick);
        foreach (var identity in new[] { first, second })
        {
            Assert.NotEqual(DeviceKeyStore.DefaultNick, identity.Nick);
            Assert.Equal($"{prefix}-{identity.Fingerprint[..4]}", identity.Nick);
            Assert.True(DeviceKeyStore.IsValidNick(identity.Nick));
        }
    }

    [Fact]
    public async Task Legacy_shared_nickname_is_replaced_once_and_a_chosen_nickname_is_kept()
    {
        string directory = TestAccounts.TempPath();
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "settings.json");
        var secrets = new InMemorySecretStore();
        try
        {
            File.WriteAllText(settings, """{"deviceNick":"Phaket device"}""");
            var migrated = await new DeviceKeyStore(secrets, settings).GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(DeviceKeyStore.DefaultNick, migrated.Nick);
            Assert.Equal($"{LocalizationService.Instance["Identity.NickPrefix"]}-{migrated.Fingerprint[..4]}", migrated.Nick);
            using (var document = JsonDocument.Parse(File.ReadAllText(settings)))
                Assert.Equal(migrated.Nick, document.RootElement.GetProperty("deviceNick").GetString());

            await new DeviceKeyStore(secrets, settings).SaveNickAsync("Zosia", cancellationToken: TestContext.Current.CancellationToken);
            var own = await new DeviceKeyStore(secrets, settings).GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Zosia", own.Nick);
            Assert.Equal(migrated.Fingerprint, own.Fingerprint);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public void Key_warning_is_calm_and_stays_visible_on_the_identity_card()
    {
        var loc = LocalizationService.Instance;
        foreach (var language in new[] { "pl-PL", "en" })
        {
            loc.CurrentLanguage = language;
            string text = loc["Identity.KeyWarning"];
            Assert.DoesNotContain("!", text);
            foreach (var scary in new[] { "wyciek", "leak", "niebezpiecz", "danger", "alert" })
                Assert.DoesNotContain(scary, text, StringComparison.OrdinalIgnoreCase);
            // The consequence and the remedy stay in the text; only the tone changed.
            Assert.Contains(language == "pl-PL" ? "kopię zapasową" : "backup", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(language == "pl-PL" ? "magazynem sekretów" : "secret store", text, StringComparison.OrdinalIgnoreCase);
        }

        loc.CurrentLanguage = "pl-PL";
        var vm = TestAccounts.ViewModel();
        var window = Show(new MainWindow(vm));
        vm.NavigateCommand.Execute("account");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == loc["Identity.KeyWarning"] && text.IsEffectivelyVisible);
    }

    private static async Task<DevicePublicIdentity> ReadIdentity()
    {
        string directory = TestAccounts.TempPath();
        Directory.CreateDirectory(directory);
        try
        {
            return await new DeviceKeyStore(new InMemorySecretStore(), Path.Combine(directory, "settings.json")).GetPublicIdentityAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
