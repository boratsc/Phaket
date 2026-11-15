using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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
        frame.Save(Path.Combine(dir, name));
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
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.CommandParameter, target));
        Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
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
                new Phaket.Core.Accounts.SteamApiKeyStore(Path.Combine(dir, "steam-apikey.bin"), memory),
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
        var settings = new SettingsViewModel(new Phaket.Core.Accounts.SteamApiKeyStore(Path.Combine(directory, "steam-apikey.bin"), store), Path.Combine(directory, "settings.json"));
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
    public void Render_all_screens()
    {
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        Save(window, "01-library.png");

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

        window.Close();
    }
}
