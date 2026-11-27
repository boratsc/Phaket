using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Launch;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Platforms.SteamShortcuts;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-146 B in the product: "Graj" starts a managed installation from its package folder with
// redirected saves, tells the user once when that is impossible, and seeds only once. The
// launch is the real helper process (POSIX sh), so the environment the game saw is asserted
// from inside the process, not from the view model's plan.
public class GameLaunchIsolationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-launch-iso-").FullName;

    private string Store => Path.Combine(root, "processes.json");
    // One app data root per test: the install registry lives there, as it does in the app.
    private readonly string appData = Path.Combine(Directory.CreateTempSubdirectory("phaket-iso-appdata-").FullName, "phaket");
    private string AppData => appData;
    private string SteamRoot { get; }

    public GameLaunchIsolationTests() => SteamRoot = Path.Combine(root, "steam");

    // What the library scan produces for a detected install; the fixture's exe makes it installed.
    private GameCatalogItem Item(string gameRoot, string linuxMode = "env")
    {
        var definition = new GameDefinition("among-us", ["BepInEx/**"],
            StoreIds: new Dictionary<string, string> { ["steam"] = "945360" },
            MainExecutable: "Among Us.exe",
            SavePaths: ["profile://steam/945360"],
            SavesIsolation: new SavesIsolation(new Dictionary<string, SavesIsolationMode>
            {
                ["linux"] = linuxMode == "env"
                    ? new SavesIsolationMode("env", ["HOME", "XDG_CONFIG_HOME"])
                    : new SavesIsolationMode("shared", Reason: "unity-locallow-known-folder"),
            }));
        // What the library scan produces for a detected install; the fixture's exe makes it installed.
        if (!Directory.Exists(gameRoot))
            throw new InvalidOperationException($"the installation folder must exist: {gameRoot}");
        var item = new GameCatalogItem(definition, "", "");
        item.Detected = new SteamInstallation(new Installation("steam-945360", "among-us", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "555", "Among Us", 1, null, 0),
            new SteamLibrary(SteamRoot, false), GameAvailability.Moddable);
        return item;
    }

    // The helper stands in for the game: it writes the directory it started in and its path
    // variables into the package folder, which is what the game would read for its saves.
    // POSIX: a sh script. Windows: text is not an executable, so a real pwsh (Windows PowerShell
    // 5.1 as the fallback) runs the script; a console app in a non-interactive process has an
    // empty HOME, so the plan's HOME/XDG_CONFIG_HOME are what the launch asserts.
    private static string InstallHelper(string directory)
    {
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "Among Us.exe");
        if (OperatingSystem.IsWindows())
        {
            var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            File.WriteAllText(exe, File.Exists(pwsh) ? pwsh : windowsPowerShell);
            File.WriteAllText(Path.Combine(directory, "launch-env.ps1"), """
                $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
                $lines = @("HOME=$env:HOME", "XDG_CONFIG_HOME=$env:XDG_CONFIG_HOME", "PWD=$($PWD.Path)")
                [IO.File]::WriteAllLines((Join-Path $dir 'launch-env.txt'), $lines)
                Start-Sleep -Seconds 30
                """);
            return exe;
        }
        File.WriteAllText(exe, "#!/bin/sh\nprintf 'HOME=%s\\nXDG_CONFIG_HOME=%s\\nPWD=%s\\n' \"$HOME\" \"$XDG_CONFIG_HOME\" \"$PWD\" > \"$(dirname \"$0\")/launch-env.txt\"\nsleep 30\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return exe;
    }

    private string SteamInstallationFolder()
    {
        var game = Path.Combine(root, "SteamLibrary", "steamapps", "common", "Among Us");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Among Us.exe"), "fixture");
        File.WriteAllText(Path.Combine(root, "SteamLibrary", "steamapps", "appmanifest_945360.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"945360\"\n\t\"name\"\t\t\"Among Us\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Among Us\"\n\t\"buildid\"\t\t\"1\"\n}\n");
        return game;
    }

    // Steam on this machine with one user: "add to Steam" has a target to write to.
    private void SteamUserRoot()
    {
        Directory.CreateDirectory(Path.Combine(SteamRoot, "userdata", "123", "config"));
    }

    // A stand-in executable for flows that never start the game (the shortcut question).
    private static string PlaceExecutable(string directory)
    {
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "Among Us.exe");
        File.WriteAllText(exe, "fixture");
        return exe;
    }

    // A package folder Phaket materialized for this installation (mods in it, so managed).
    // The modded copy Phaket installed (the registry's record): a launch starts from it.
    private string PackageRoot()
    {
        var managed = Path.Combine(root, "modded", "Among Us");
        Directory.CreateDirectory(Path.Combine(managed, "BepInEx", "plugins"));
        new InstallRegistry(Path.Combine(AppData, "installs.json")).Add(new InstalledRecipe(
            "among-us.recipe", "1.0.0", "among-us", "steam", managed, Path.Combine(root, "profile"),
            Path.Combine(root, "staging"), Path.Combine(root, "state"), null, DateTimeOffset.UnixEpoch));
        return managed;
    }

    private GameViewModel Create(GameCatalogItem item, out LaunchService launch)
    {
        launch = new LaunchService(Store);
        return Create(item, launch);
    }

    private GameViewModel Create(GameCatalogItem item, LaunchService launch) =>
        // No Steam on this machine: the page asks nothing and "play" starts the package itself.
        new(item, () => { }, appDataRoot: AppData)
        {
            Launch = launch,
            HostOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            SteamHome = SteamRoot,
            SteamRoots = [SteamRoot],
        };

    // The command is async: pump the dispatcher until its flag flips (a fixture Steam writes fast).
    private void Settle(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "the command did not finish");
    }

    private static void Play(GameViewModel vm)
    {
        var task = vm.PlayCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted, "play did not finish");
        task.GetAwaiter().GetResult();
    }

    // The page says the same sentence on a refused start; the wording checked is the whole answer.
    // A tracked record is what a successful start leaves behind, so the second check names the
    // real cause: there is no PID without a started process.
    private static void AssertLaunched(GameViewModel vm)
    {
        var expected = LocalizationService.Instance["Game.Launched"];
        Assert.True(expected == vm.StatusMessage, $"status '{vm.StatusMessage}', expected '{expected}'");
        Assert.True(vm.Launch.Tracked("945360") is not null, "the launch was refused before any process started");
    }

    // The helper is ours: end it by PID, so no ping/pwsh sleep outlives the run.
    private static void KillHelper(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void Kill(LaunchService launch)
    {
        foreach (var record in new ProcessTracker(Store).Load())
            KillHelper(record.Pid);
    }

    [AvaloniaFact]
    public void ManagedPackageLaunchesWithItsOwnSavesAndNoNotice()
    {
        var package = PackageRoot();
        InstallHelper(package);
        // The Steam install must look real to the page: an exe alone is not an installation,
        // the Steam manifest is what the library scan trusts.
        var game = SteamInstallationFolder();
        var vm = Create(Item(game), out var launch);
        try
        {
            Play(vm);
            AssertLaunched(vm);
            var seen = File.ReadAllText(Path.Combine(package, "launch-env.txt"));
            var home = Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName, "config");
            Assert.Contains($"HOME={home}", seen);
            Assert.Contains($"XDG_CONFIG_HOME={home}", seen);
            Assert.Contains($"PWD={package}", seen);
            Assert.False(vm.IsSavesSharedNoticeVisible);
            // Steam is not a launcher here: Phaket started the process, so it can stop it.
            Assert.NotNull(launch.Tracked("945360"));
        }
        finally { Kill(launch); }
    }

    [AvaloniaFact]
    public void SeedingRunsOnceAndStartsFromTheExistingSaves()
    {
        var package = PackageRoot();
        InstallHelper(package);
        var game = SteamInstallationFolder();
        // The vanilla saves: the profile root the seed reads. The package keeps the save tree as
        // it was (relative to the save root), the first save tree keeps its plain layout.
        var vm = Create(Item(game), out var launch);
        var profile = Path.Combine(AppData, "profiles", "among-us", SavesLaunchPreparation.Hash(vm.LaunchRoot)[..12]);
        // The vanilla save location comes from the definition (profile://steam/945360).
        var vanilla = Path.Combine(profile, "steam", "945360");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla, "Player.txt"), "postęp");
        var home = Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName, "config");
        try
        {
            Play(vm);
            Assert.Equal("postęp", File.ReadAllText(Path.Combine(home, "Player.txt")));
            var marker = Path.Combine(home, PackageSavesSeed.MarkerFileName);
            var firstWrite = File.GetLastWriteTimeUtc(marker);
            // The game writes its own save on the first run: the seed must never overwrite it.
            File.WriteAllText(Path.Combine(home, "Player.txt"), "nowszy postęp");
            Play(vm);
            Assert.Equal("nowszy postęp", File.ReadAllText(Path.Combine(home, "Player.txt")));
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(marker));
        }
        finally { Kill(launch); }
    }

    [AvaloniaFact]
    public void SharedSavesAreSaidOnceAndTheGameStillStarts()
    {
        var package = PackageRoot();
        InstallHelper(package);
        var vm = Create(Item(SteamInstallationFolder(), "shared"), out var launch);
        try
        {
            Play(vm);
            Assert.True(vm.IsSavesSharedNoticeVisible);
            Assert.Equal("Ta gra współdzieli zapisy z wersją podstawową. Nie uruchamiaj obu wersji jednocześnie, żeby nie nadpisać postępu.",
                LocalizationService.Instance["Game.SavesShared"]);
            Assert.Equal("Ta gra współdzieli zapisy z wersją podstawową.", LocalizationService.Instance["Game.SavesSharedTitle"]);
            var seen = File.ReadAllText(Path.Combine(package, "launch-env.txt"));
            // A shared launch passes no redirect at all: the game keeps the profile it always
            // had. A console app's HOME is empty, so "empty" is the truthful comparison, not
            // the test runner's real profile (Linux keeps its value and asserts it).
            var realHome = OperatingSystem.IsWindows() ? "" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains($"HOME={realHome}", seen); // nothing redirected
            Assert.DoesNotContain(SavesIsolationPlanner.HomeDirectoryName, seen);
            Assert.True(vm.Launch.Tracked("945360") is not null,
                $"the game did not start: '{vm.StatusMessage}'"); // starting was never refused
            vm.DismissSavesSharedNoticeCommand.Execute(null);
            Assert.False(vm.IsSavesSharedNoticeVisible);
        }
        finally { Kill(launch); }
    }

    [AvaloniaFact]
    public void VanillaInstallationLaunchesAsBeforeWithoutRedirect()
    {
        // The Steam install exists: a folder without the exe is not an installation at all.
        var game = Path.Combine(root, "Among Us");
        Directory.CreateDirectory(game);
        var vm = Create(Item(game), out var launch);
        try
        {
            Play(vm);
            // The vanilla exe is not the game: the launch fails in plain words and nothing is
            // written beside the installation, which is exactly the old behaviour.
            Assert.Equal(LocalizationService.Instance["Game.LaunchFailed"], vm.StatusMessage);
            Assert.False(vm.IsSavesSharedNoticeVisible);
            Assert.False(File.Exists(Path.Combine(game, "launch-env.txt")));
            Assert.False(Directory.Exists(Path.Combine(game, SavesIsolationPlanner.HomeDirectoryName)));
        }
        finally { Kill(launch); }
    }

    // "Dodaj do Steam" at the shipped 1280x800: the question first, then one entry in Steam,
    // then "play" goes through Steam instead of starting the exe ourselves.
    [AvaloniaFact]
    public void AddingAShortcutAsksFirstAndPlayThenGoesThroughSteam()
    {
        SteamUserRoot();
        var game = Path.Combine(root, "Among Us");
        PlaceExecutable(game);
        var vm = Create(Item(game), out var launch);
        var path = SteamShortcutService.ShortcutsPath(SteamRoot, "123");
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Add"], vm.SteamToggleLabel);
        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.True(vm.SteamConsentCardVisible);
        Assert.False(File.Exists(path)); // asking writes nothing
        vm.ConfirmSteamShortcutCommand.Execute(null);
        this.Settle(() => vm.SteamConsentCardVisible == false && vm.HasSteamShortcut);
        Assert.True(File.Exists(path));
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Remove"], vm.SteamToggleLabel);
        Play(vm);
        Assert.Equal(LocalizationService.Instance["Game.Launched"], vm.StatusMessage);
        Assert.Null(launch.Tracked("945360")); // Steam started it, not us: nothing to stop
    }

    // The consent card and the shared-saves note as the page draws them at the target size.
    [AvaloniaFact]
    public void RendersTheConsentCardAndTheSharedSavesNoteAt1280x800()
    {
        var game = Path.Combine(root, "Among Us");
        PlaceExecutable(game);
        SteamUserRoot(); // the question only opens when there is a Steam profile to write to
        var vm = Create(Item(game), out _);
        vm.HasSteamShortcut = true; // remove variant, so the label and wording are the other half
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.GameView { DataContext = vm } };
        window.Show();
        try
        {
            vm.ToggleSteamShortcutCommand.Execute(null);
            vm.IsSavesSharedNoticeVisible = true;
            Dispatcher.UIThread.RunJobs();
            var confirm = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, vm.ConfirmSteamShortcutCommand));
            Assert.True(confirm.IsEffectivelyVisible);
            Assert.Equal(vm.SteamConsentYes, confirm.Content);
            // Both cards stack under the rows above; scroll to them so the frame shows their real look
            // (the bars under the window are the page's own buttons, not an empty log area).
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            RenderArtifacts.Save(window, "steam-consent-and-saves-shared-1280x800.png");
        }
        finally { window.Close(); }
    }

    // The menu entry the user clicks, in the page source (the headless menu never opens).
    // The shortcut entry sits in "Więcej" (a one-time setup does not deserve a second big button):
    // the view is parsed and its menu compared with the one command the page exposes.
    [AvaloniaFact]
    public void TheToggleIsAMenuEntryOfMore()
    {
        var xaml = File.ReadAllText(Path.Combine(SourceRoot(), "Phaket", "Views", "GameView.axaml"));
        var marker = xaml.IndexOf("ToggleSteamShortcutCommand", StringComparison.Ordinal);
        Assert.True(marker > 0, "the shortcut entry is missing from the game page");
        var menuOpen = xaml.LastIndexOf("<MenuFlyout", marker, StringComparison.Ordinal);
        var menuClose = xaml.IndexOf("</MenuFlyout>", marker, StringComparison.Ordinal);
        Assert.True(menuOpen > 0 && menuClose > marker, "the shortcut entry sits outside the menu");
        Assert.Contains("Game.More", xaml[..menuOpen]); // that menu is the button named "Więcej"
        Assert.Contains("SteamToggleLabel", xaml[marker..menuClose]); // its header follows the file
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Add"], new GameViewModel(
            Item(Directory.CreateTempSubdirectory("phaket-toggle-").FullName), () => { }).SteamToggleLabel);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Phaket.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Phaket.slnx not found above the test output");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}