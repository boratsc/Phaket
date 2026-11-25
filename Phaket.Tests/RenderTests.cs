using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Renders the PoC screens at the target size for visual review (saved to
// /tmp/opencode/phaket-render). Not an assertion suite — visual verification.
public class RenderTests
{
    private static void Save(Window window, string name)
    {
        var dir = "/tmp/opencode/phaket-render";
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, name), PngBitmapEncoderOptions.Default);
    }

    [AvaloniaTheory]
    [InlineData("mods", "05-mods-from-library.png")]
    [InlineData("profiles", "06-profiles-from-library.png")]
    [InlineData("help", "08-help.png")]
    [InlineData("account", "15-account.png")]
    public void Render_sidebar_navigation_from_library(string target, string filename)
    {
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        vm.NavigateCommand.Execute(target);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(target, vm.ActiveNav);
        if (target == "mods") Assert.IsType<ModsViewModel>(vm.CurrentPage);
        else if (target == "profiles") Assert.IsType<ProfilesViewModel>(vm.CurrentPage);
        else if (target == "help") Assert.IsType<HelpViewModel>(vm.CurrentPage);
        else Assert.IsType<AccountsViewModel>(vm.CurrentPage);
        Save(window, filename);
        if (target == "mods") vm.Mods!.BackCommand.Execute(null);
        if (target == "profiles") vm.Profiles!.BackCommand.Execute(null);
        if (target != "help" && target != "account")
        {
            Assert.IsType<GameViewModel>(vm.CurrentPage);
            Assert.Equal("library", vm.ActiveNav);
        }
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, false, "settings-secrets.png")]
    [InlineData(true, false, "settings-secrets-locked.png")]
    [InlineData(false, true, "settings-secrets-fallback.png")]
    public async Task Render_settings_secret_store(bool locked, bool fallback, string filename)
    {
        string dir = Path.Combine(Path.GetTempPath(), "phaket-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "epic.bin"), Convert.ToBase64String([1, 2, 3]));
        // The state text below is asserted in Polish: the shared language singleton must not be
        // left on "en" by another class (collections run in nondeterministic order).
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        try
        {
            var memory = new Phaket.Core.Tests.InMemorySecretStore();
            var settings = new SettingsViewModel(
                Path.Combine(dir, "settings.json"),
                new SettingsViewModel.SecretStoreSeam(
                    locked ? new Phaket.Core.Accounts.LinuxLibsecretStore(new LockedNative()) : memory,
                    dir, ["epic"], "libsecret"));
            var window = new MainWindow(new MainWindowViewModel(TestAccounts.Create(), settings)) { Width = 1280, Height = 800 };
            window.Show();
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.NavigateCommand.Execute("settings");
            Dispatcher.UIThread.RunJobs();
            await settings.RefreshStatusCommand.ExecuteAsync(null);
            if (fallback)
            {
                settings.FallbackConsent = true;
                settings.FallbackPassword = "haslo-do-magazynu";
                await settings.UnlockFallbackCommand.ExecuteAsync(null);
                Assert.Equal("consent-file", settings.SecretProvider);
                Assert.Equal("", settings.FallbackPassword);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.NeedsMigration);
            Assert.Equal(locked ? "zablokowany" : "dostępny", settings.SecretStateText);
            Save(window, filename);
            // Second frame: the delete control brought into view at 1280x800.
            var deleteButton = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Command == settings.DeleteSecretsCommand);
            deleteButton.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.True(deleteButton.TranslatePoint(new Point(0, 0), window)!.Value.Y > 0);
            Save(window, filename.Replace(".png", "-bottom.png"));
            window.Close();
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(dir, true);
        }
    }

    // PAK-60: Steam install credentials section on the Accounts page.
    [AvaloniaTheory]
    [InlineData(false, false, "accounts-steam-credentials.png")]
    [InlineData(true, false, "accounts-steam-credentials-saved.png")]
    [InlineData(true, true, "accounts-steam-credentials-dialog.png")]
    public async Task Render_accounts_steam_credentials(bool stored, bool dialog, string filename)
    {
        var store = new Phaket.Core.Tests.InMemorySecretStore();
        string directory = TestAccounts.TempPath();
        var accounts = new AccountServices(store, directory);
        if (stored) await accounts.SteamCredentials.SaveAsync("phaket.tester", "haslo-testowe");
        var settings = new SettingsViewModel(Path.Combine(directory, "settings.json"));
        var window = new MainWindow(new MainWindowViewModel(accounts, settings)) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.NavigateCommand.Execute("account");
            Dispatcher.UIThread.RunJobs();
            await vm.Accounts.SteamSignin.RefreshCommand.ExecuteAsync(null);
            if (dialog) _ = vm.Accounts.SteamSignin.AskAsync("phaket.tester");
            Dispatcher.UIThread.RunJobs();
            var signin = vm.Accounts.SteamSignin;
            var action = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Command, signin.StartCommand));
            // Show the whole credentials card: scroll it so its own top edge meets the viewport top.
            var card = action.GetVisualAncestors().OfType<Border>().First();
            var scroll = action.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroll.Offset += new Vector(0, card.TranslatePoint(new Point(0, 0), scroll)!.Value.Y - 24);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Save(window, filename);
            if (dialog) vm.Accounts.SteamSignin.CancelCommand.Execute(null);
        }
        finally { window.Close(); }
    }

    // PAK-60: the one-time Steam sign-in dialog inside the Steam install flow.
    [AvaloniaFact]
    public async Task Render_steam_install_signin_dialog()
    {
        var accounts = TestAccounts.Create();
        var library = new LibraryViewModel(_ => { }, accounts, null);
        var flow = new SteamInstallFlowViewModel(new Phaket.Core.Platforms.OwnedSteamGame("945360", "Among Us", null, null, "api", false),
            new Phaket.Core.Omrl.GameDefinition("among-us", [], MainExecutable: "game.exe"),
            new Phaket.Core.Platforms.DepotDownloaderInstallAdapter(null, Path.Combine(TestAccounts.TempPath(), "config"), "/nonexistent"),
            () => Task.FromResult<string?>(null), credentialStore: accounts.SteamCredentials);
        flow.OsArch = "64";
        flow.Username = "phaket.tester";
        flow.Preview = new(new("945360", "/tmp/games", "/tmp/games/945360", "game.exe", "linux", "64", null, null, []), null);
        library.SteamInstall = flow;
        _ = flow.SigninDialog!.AskAsync("phaket.tester");
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.LibraryView { DataContext = library } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(flow.SigninDialog.IsOpen);
            // The dialog sits at the end of the flow card: scroll that card's own
            // ScrollViewer (the nearest ancestor of the dialog button) to its end.
            var confirm = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Command, flow.SigninDialog.ConfirmCommand));
            var inner = confirm.GetVisualAncestors().OfType<ScrollViewer>().First();
            inner.Offset = new Vector(0, inner.Extent.Height - inner.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            Save(window, "steam-install-signin-dialog.png");
            flow.SigninDialog.CancelCommand.Execute(null);
        }
        finally { window.Close(); }
    }

    private sealed class LockedNative : Phaket.Core.Accounts.ILibsecretNative
    {
        public string? Invoke(string operation, string key, string? value, CancellationToken cancellationToken) =>
            throw new Phaket.Core.Accounts.SecretStoreException("SECRET_STORE_LOCKED", "Magazyn jest zablokowany / Secret store is locked.");
    }

    [AvaloniaFact]
    public void Render_new_shell_screens()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        Save(window, "00-shell-catalog.png");
        Save(window, "10-shell-final-assets.png");
        Save(window, "25-after-fix.png");
        foreach (var (target, filename) in new[]
        {
            ("discover", "16-discover.png"),
            ("communities", "17-communities.png"),
            ("downloads", "18-downloads.png")
        })
        {
            vm.NavigateCommand.Execute(target);
            Dispatcher.UIThread.RunJobs();
            Save(window, filename);
        }
        window.Close();
        LocalizationService.Instance.CurrentLanguage = language;
    }

    [AvaloniaFact]
    public void Render_all_screens()
    {
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        Save(window, "01-library.png");
        var unsupported = new GameCatalogItem(
            new Phaket.Core.Omrl.GameDefinition("epic-owned-render-test", [], Title: new Dictionary<string, string> { ["pl-PL"] = "Nieobsługiwana gra", ["en"] = "Unsupported game" }), "", "")
        { EpicOwned = true };
        vm.Library.Games.Add(unsupported);
        vm.Library.SelectedGame = unsupported;
        var libraryWindow = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.LibraryView { DataContext = vm.Library } };
        libraryWindow.Show();
        Dispatcher.UIThread.RunJobs();
        Save(libraryWindow, "36-library.png");
        libraryWindow.Close();
        vm.Library.Games.Remove(unsupported);
        vm.Library.SelectedGame = vm.Library.Games.FirstOrDefault();
        Dispatcher.UIThread.RunJobs();

        vm.Library.OpenGameCommand.Execute(vm.Library.Games.First());
        Dispatcher.UIThread.RunJobs();
        Save(window, "02-game.png");

        vm.OpenMods();
        Dispatcher.UIThread.RunJobs();
        Save(window, "05-mods.png");

        vm.OpenProfiles();
        Dispatcher.UIThread.RunJobs();
        Save(window, "06-profiles.png");

        vm.OpenBackup();
        vm.Backup!.CreateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Save(window, "07-backup.png");

        vm.OpenCompatibility();
        Dispatcher.UIThread.RunJobs();
        Save(window, "09-compatibility.png");

        vm.OpenInstall();
        Dispatcher.UIThread.RunJobs();
        Save(window, "10-install.png");

        vm.OpenBuilds();
        Dispatcher.UIThread.RunJobs();
        Save(window, "11-builds.png");

        vm.OpenShare();
        Dispatcher.UIThread.RunJobs();
        Save(window, "12-share.png");

        vm.OpenGraph();
        Dispatcher.UIThread.RunJobs();
        Save(window, "13-graph.png");

        vm.OpenReport();
        Dispatcher.UIThread.RunJobs();
        Save(window, "14-report.png");

        vm.NavigateCommand.Execute("studio");
        vm.RecipeStudio.AddStepCommand.Execute("archive.extract");
        vm.RecipeStudio.AddStepCommand.Execute("file.copy");
        vm.RecipeStudio.ValidateCommand.Execute(null);
        vm.RecipeStudio.PlanCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Save(window, "02-studio.png");

        vm.RecipeStudio.SwitchViewCommand.Execute(null);
        vm.RecipeStudio.SaveYamlCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Save(window, "03-studio-yaml.png");

        vm.NavigateCommand.Execute("settings");
        Dispatcher.UIThread.RunJobs();
        Save(window, "04-settings.png");
        Save(window, "37-settings.png");

        window.Close();
    }

    // PAK-88: panel dodawania moda w każdym z trzech źródeł i w obu motywach.
    private static ModPackViewModel RenderModPack(string root)
    {
        var store = new ModpackStore(root);
        var pack = new ModpackDefinition
        {
            Id = "render-pack", Name = "Skład Renderowy", Author = "Phaket", Version = "1.2.0", GameId = "among-us",
            Items = [.. Enumerable.Range(1, 24).Select(i => new ModpackMod
            {
                Name = $"Mod {i:00}", Version = $"1.{i}.0", Required = i == 1,
                Source = i % 4 == 0 ? "github" : "thunderstore"
            })]
        };
        store.Save(pack);
        var vm = new ModPackViewModel(pack, store, () => { }, root);
        // Selecting the Thunderstore source starts a background refresh; keep it off the network.
        vm.ThunderstoreCatalog.Endpoint = "http://127.0.0.1:1/";
        return vm;
    }

    // Ten sam układ co shell: pionowe marginesy poza ScrollViewerem, strona w ScrollViewerze.
    private static Window ModPackRenderWindow(ModPackViewModel vm) => new()
    {
        Width = 1280, Height = 800,
        Content = new Grid
        {
            Margin = new Thickness(0, 24, 0, 26),
            Children =
            {
                new ScrollViewer
                {
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new Border
                    {
                        Margin = new Thickness(30, 0, 30, 0),
                        Child = new Phaket.Views.ModPackView { DataContext = vm }
                    }
                }
            }
        }
    };

    private static Border ModalCard(Window window) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("modalCard") && b.IsEffectivelyVisible);

    [AvaloniaTheory]
    [InlineData("thunderstore", "dark", "40-modpack-add-thunderstore-dark.png")]
    [InlineData("github", "dark", "41-modpack-add-github-dark.png")]
    [InlineData("nexus", "dark", "42-modpack-add-nexus-dark.png")]
    [InlineData("thunderstore", "light", "40-modpack-add-thunderstore-light.png")]
    [InlineData("github", "light", "41-modpack-add-github-light.png")]
    [InlineData("nexus", "light", "42-modpack-add-nexus-light.png")]
    public async Task Render_modpack_add_panel(string source, string theme, string filename)
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        var variant = Application.Current!.RequestedThemeVariant;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        Application.Current.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var root = TestAccounts.TempPath();
        Window? window = null;
        ModPackViewModel? vm = null;
        try
        {
            vm = RenderModPack(root);
            window = ModPackRenderWindow(vm);
            window.Show();
            vm.OpenAddModsCommand.Execute(null);
            vm.SelectAddSourceCommand.Execute(source);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            var card = ModalCard(window);
            var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
            var center = origin.Y + card.Bounds.Height / 2;
            Save(window, filename);
            Assert.Equal(760, card.Bounds.Width, 1);
            Assert.True(origin.Y >= 0 && origin.Y + card.Bounds.Height <= window.ClientSize.Height,
                $"{filename}: dialog poza oknem (y={origin.Y}, wysokość={card.Bounds.Height})");
            Assert.True(Math.Abs(center - window.ClientSize.Height / 2) <= 2,
                $"{filename}: dialog nie wyśrodkowany (środek={center}, okno={window.ClientSize.Height})");
        }
        finally
        {
            if (vm?.ThunderstoreCatalog.RefreshCommand.ExecutionTask is { } refresh) await refresh;
            window?.Close();
            Application.Current.RequestedThemeVariant = variant;
            LocalizationService.Instance.CurrentLanguage = language;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Modpack_add_panel_card_and_fields_share_one_width_across_sources()
    {
        var root = TestAccounts.TempPath();
        ModPackViewModel? vm = null;
        try
        {
            vm = RenderModPack(root);
            var window = ModPackRenderWindow(vm);
            window.Show();
            try
            {
                vm.OpenAddModsCommand.Execute(null);
                var cardWidths = new List<double>();
                var fieldWidths = new List<double>();
                foreach (var source in new[] { "thunderstore", "github", "nexus" })
                {
                    vm.SelectAddSourceCommand.Execute(source);
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();
                    var card = ModalCard(window);
                    cardWidths.Add(card.Bounds.Width);
                    fieldWidths.Add(card.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible).Bounds.Width);
                }
                Assert.Equal(760, cardWidths[0], 1);
                Assert.All(cardWidths, width => Assert.Equal(cardWidths[0], width, 1));
                Assert.All(fieldWidths, width => Assert.Equal(fieldWidths[0], width, 1));
            }
            finally { window.Close(); }
        }
        finally
        {
            if (vm?.ThunderstoreCatalog.RefreshCommand.ExecutionTask is { } refresh) await refresh;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Render_modpack_add_panel_in_shell()
    {
        // Prawdziwy shell: pasek górny i statusu poza obszarem strony — dialog musi się zmieścić w całości.
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.NavigateCommand.Execute("catalog");
            Dispatcher.UIThread.RunJobs();
            vm.ModpackCatalog!.CreatePackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            var card = ModalCard(window);
            var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
            Save(window, "44-modpack-add-shell.png");
            Assert.True(origin.Y >= 62 && origin.Y + card.Bounds.Height <= 767,
                $"dialog poza obszarem strony (y={origin.Y}, wysokość={card.Bounds.Height})");
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Modpack_rows_expose_tooltip_only_for_required_mods()
    {
        // PAK-87: pusty ToolTip.Tip otwierał pustą ramkę — mody niezależne nie mają podpowiedzi.
        var root = TestAccounts.TempPath();
        try
        {
            var vm = RenderModPack(root);
            var window = ModPackRenderWindow(vm);
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var tips = window.GetVisualDescendants().OfType<Border>()
                    .Where(row => row.Classes.Contains("modRow"))
                    .Select(ToolTip.GetTip).ToList();
                Assert.Equal(24, tips.Count);
                var tip = Assert.Single(tips, t => t is not null);
                Assert.Equal(LocalizationService.Instance["Catalog.RequiredDependencyProtected"], tip);
            }
            finally { window.Close(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Render_modpack_add_panel_help_tip_open()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var root = TestAccounts.TempPath();
        ModPackViewModel? vm = null;
        try
        {
            vm = RenderModPack(root);
            var window = ModPackRenderWindow(vm);
            window.Show();
            try
            {
                vm.OpenAddModsCommand.Execute(null);
                vm.SelectAddSourceCommand.Execute("github");
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();
                var tip = window.GetVisualDescendants().OfType<Phaket.Controls.HelpTip>().Single();
                tip.FindControl<Button>("Trigger")!.Focus(NavigationMethod.Tab);
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();
                Assert.True(tip.FindControl<Avalonia.Controls.Primitives.Popup>("TipPopup")!.IsOpen);
                Save(window, "43-modpack-help-tip.png");
            }
            finally { window.Close(); }
        }
        finally
        {
            if (vm?.ThunderstoreCatalog.RefreshCommand.ExecutionTask is { } refresh) await refresh;
            LocalizationService.Instance.CurrentLanguage = language;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(900, 700, "50-modpack-add-narrow-900.png")]
    [InlineData(640, 520, "51-modpack-add-narrow-640.png")]
    public void Modpack_add_panel_fits_the_minimum_window(double width, double height, string filename)
    {
        // PAK-89: MinWidth okna to 640 — karta dialogu musi się kurczyć, a przyciski zostać w oknie.
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = width, Height = height };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.NavigateCommand.Execute("catalog");
            Dispatcher.UIThread.RunJobs();
            vm.ModpackCatalog!.CreatePackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            var card = ModalCard(window);
            var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(origin.X >= 0 && origin.X + card.Bounds.Width <= window.ClientSize.Width,
                $"{filename}: karta poza oknem (x={origin.X}, szerokość={card.Bounds.Width}, okno={window.ClientSize.Width})");
            foreach (var key in new[] { "Catalog.Cancel", "Catalog.SavePack" })
            {
                var label = LocalizationService.Instance[key];
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Last(b => b.IsEffectivelyVisible && b.Content as string == label);
                var buttonOrigin = button.TranslatePoint(new Point(0, 0), window)!.Value;
                Assert.True(buttonOrigin.X >= 0 && buttonOrigin.X + button.Bounds.Width <= window.ClientSize.Width
                    && buttonOrigin.Y + button.Bounds.Height <= window.ClientSize.Height,
                    $"{filename}: przycisk '{label}' obcięty (x={buttonOrigin.X}, szerokość={button.Bounds.Width}, okno={window.ClientSize.Width})");
            }
            Save(window, filename);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Catalog_nexus_dialog_centers_in_the_viewport_with_a_long_pack_list()
    {
        // Dialogi katalogu są w przewijanej stronie: cień i karta mają wysokość widoku, nie listy.
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            for (var i = 1; i <= 30; i++)
                store.Save(new ModpackDefinition
                {
                    Id = $"pack-{i:00}", Name = $"Pakiet {i:00}", Author = "Test", Version = "1.0.0", GameId = "among-us",
                    Items = [new ModpackMod { Name = $"Mod {i:00}", Version = "1.0.0", Source = "thunderstore" }]
                });
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            using var vm = new ModpackCatalogViewModel(game, root, autoLoad: false);
            var view = new Phaket.Views.ModpackCatalogView { DataContext = vm };
            var window = new Window
            {
                Width = 1280, Height = 800,
                Content = new Grid
                {
                    Margin = new Thickness(0, 24, 0, 26),
                    Children =
                    {
                        new ScrollViewer
                        {
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            Content = new Border { Margin = new Thickness(30, 0, 30, 0), Child = view }
                        }
                    }
                }
            };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                vm.NexusPicker = new NexusManualViewModel { Name = "Mod", PinnedVersion = "1.0.0" };
                vm.IsNexusPickerOpen = true;
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();
                var card = ModalCard(window);
                var scroll = view.GetVisualAncestors().OfType<ScrollViewer>().First();
                var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
                var scrollOrigin = scroll.TranslatePoint(new Point(0, 0), window)!.Value;
                var center = origin.Y + card.Bounds.Height / 2;
                var viewportCenter = scrollOrigin.Y + scroll.Bounds.Height / 2;
                Save(window, "52-catalog-nexus-dialog-long-list.png");
                Assert.True(Math.Abs(center - viewportCenter) <= 2,
                    $"dialog nie wyśrodkowany w widoku (środek={center}, widok={viewportCenter})");
                Assert.True(origin.Y >= 0 && origin.Y + card.Bounds.Height <= window.ClientSize.Height,
                    $"dialog poza oknem (y={origin.Y}, wysokość={card.Bounds.Height})");
            }
            finally { window.Close(); }
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
