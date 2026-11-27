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
// redirected saves, tells the user once when that is impossible, and seeds only once. Linux
// proves the redirect from inside the sh helper; Windows proves the Shared contract through
// the tracked helper process and the absence of a package-local save redirect.
public class GameLaunchIsolationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-launch-iso-").FullName;

    // The steam:// requests the run made; set only through the app's own launcher seam.
    // The module guard already intercepts every request; the spy additionally records this fixture's.
    private static readonly List<string> Called = [];

    // Records the launch request and reports success without starting anything.
    // Restores the module's hermetic guard, never null and never the system launcher.
    private sealed class SteamLauncherSpy : IDisposable
    {
        private readonly Func<string, bool>? previous = LaunchService.SteamLauncher;
        public SteamLauncherSpy() => LaunchService.SteamLauncher = uri => { Called.Add(uri); return true; };
        public void Dispose() => LaunchService.SteamLauncher = previous;
    }

    private string Store => Path.Combine(root, "processes.json");
    // One app data root per test: the install registry lives there, as it does in the app.
    private readonly string appData = Path.Combine(Directory.CreateTempSubdirectory("phaket-iso-appdata-").FullName, "phaket");
    private string AppData => appData;
    private string SteamRoot { get; }

    public GameLaunchIsolationTests() => SteamRoot = Path.Combine(root, "steam");

    // What the library scan produces for a detected install; the fixture's exe makes it installed.
    // A package seeded from "~/.config/unity3d" carries that real folder in its definition.
    private GameCatalogItem Item(string gameRoot, string linuxMode = "env", IReadOnlyList<string>? saveLocations = null)
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
                ["windows"] = new SavesIsolationMode("shared", Reason: "unity-locallow-known-folder"),
            }),
            SaveLocations: saveLocations is null ? null
                : new SaveLocationsByOs(new Dictionary<string, IReadOnlyList<string>> { ["linux"] = saveLocations }));
        // What the library scan produces for a detected install; the fixture's exe makes it installed.
        if (!Directory.Exists(gameRoot))
            throw new InvalidOperationException($"the installation folder must exist: {gameRoot}");
        var item = new GameCatalogItem(definition, "", "");
        item.Detected = new SteamInstallation(new Installation("steam-945360", "among-us", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "555", "Among Us", 1, null, 0),
            new SteamLibrary(SteamRoot, false), GameAvailability.Moddable);
        return item;
    }

    // POSIX: a sh script named like the game, recording HOME/XDG_CONFIG_HOME/PWD. Windows:
    // a real ping PE under the definition's executable name; Shared saves need no env capture.
    private static (string Executable, string Arguments) InstallHelper(string directory)
    {
        Directory.CreateDirectory(directory);
        var game = Path.Combine(directory, "Among Us.exe");
        if (OperatingSystem.IsWindows())
        {
            // ping is a Windows system binary, starts immediately and stays alive for ~300 s.
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), game);
            return (game, "-n 300 127.0.0.1");
        }
        File.WriteAllText(game, "#!/bin/sh\nprintf 'HOME=%s\\nXDG_CONFIG_HOME=%s\\nPWD=%s\\n' \"$HOME\" \"$XDG_CONFIG_HOME\" \"$PWD\" > \"$(dirname \"$0\")/launch-env.txt\"\nsleep 30\n");
        File.SetUnixFileMode(game, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (game, "");
    }

    // The launch the page performs: the helper as the game exe, with the fixture's own arguments.
    // Its default suits the Steam-only tests, whose game never starts.
    private static void Play(GameViewModel vm, (string Executable, string Arguments) helper = default)
    {
        var task = vm.PlayCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted, "play did not finish");
        task.GetAwaiter().GetResult();
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

    // Linux writes the environment atomically; wait for that proof instead of racing the helper.
    private static string WaitForFile(string path, GameViewModel vm, LaunchService launch)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < deadline,
                $"launch environment was not written within 15 seconds: '{path}'; " +
                $"{LaunchDiagnostic(vm)}; {HelperDiagnostic(launch)}");
            Thread.Sleep(100);
        }
        return File.ReadAllText(path);
    }

    private static string HelperDiagnostic(LaunchService launch)
    {
        var record = launch.Tracked("945360");
        if (record is null) return "no tracked helper process";
        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(record.Pid);
        if (!found) return $"helper pid {record.Pid} has exited; exit code and stderr are unavailable";
        if (identity is null || identity.StartValue != record.StartTime || identity.Unit != record.StartTimeUnit ||
            !string.Equals(identity.BootId, record.BootId, StringComparison.OrdinalIgnoreCase))
            return $"helper pid {record.Pid} identity is unavailable; exit code and stderr are unavailable";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(record.Pid);
            return process.HasExited
                ? $"helper pid {record.Pid} exited with code {process.ExitCode}; stderr was not redirected"
                : $"helper pid {record.Pid} is still running; exit code pending; stderr was not redirected";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"helper pid {record.Pid} has exited; exit code and stderr are unavailable ({e.GetType().Name})";
        }
    }

    // A tracked record is what a successful start leaves behind; wait for it under CI load.
    private static ProcessRecord WaitForTracked(LaunchService launch)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (launch.Tracked("945360") is { } record) return record;
            Assert.True(DateTime.UtcNow < deadline, "no tracked helper process for 945360 within 10 seconds");
            Thread.Sleep(10);
        }
    }

    // The page says the same sentence on a refused start; the wording checked is the whole answer.
    // A direct launch is successful only with a live PID plus its start-time identity, not merely
    // a Store-named status shared with the shortcut path.
    private static void AssertLaunched(GameViewModel vm, LaunchService launch)
    {
        Assert.True(vm.LastLaunch is { Started: true, Tracked: true },
            $"direct launch was not started and tracked: {LaunchDiagnostic(vm)}");
        var record = WaitForTracked(launch);
        Assert.True(record is { Pid: > 0, StartTime: not null, StartTimeUtc: not null },
            $"the launch record has no process identity: {LaunchDiagnostic(vm)}");
        Assert.True(ProcessIdentity.TryGetProcessIdentity(record.Pid).found,
            $"helper pid {record.Pid} is not alive: {LaunchDiagnostic(vm)}");
    }

    // Windows Unity keeps saves in LocalLow: a direct package launch is Shared, never redirected.
    private static void AssertSharedPackageLaunch(GameViewModel vm, LaunchService launch, string package)
    {
        AssertLaunched(vm, launch);
        Assert.True(vm.LastLaunch is { SavesShared: true },
            $"the launch did not report shared saves: {LaunchDiagnostic(vm)}");
        Assert.True(vm.IsSavesSharedNoticeVisible,
            $"no shared-saves notice: {LaunchDiagnostic(vm)}");
        Assert.False(Directory.Exists(Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName)),
            "a Shared launch created a package-local save redirect");
    }

    // What the launcher refused with, or the exe it was asked to start.
    private static string LaunchDiagnostic(GameViewModel vm) =>
        $"{vm.LastLaunch?.Failure ?? "no failure recorded"} (exe '{vm.LaunchRoot}')";

    // The POSIX fixture is sh plus a sleep child, so retain whole-tree cleanup there.
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

    // A successful signal can leave the PID visible for a few milliseconds under CI load.
    private static void AssertGoneWithin(int pid, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (ProcessIdentity.TryGetProcessIdentity(pid).found)
            Assert.True(DateTime.UtcNow < deadline, $"process {pid} still alive after {seconds}s");
    }

    private void StopHelpers()
    {
        var tracker = new ProcessTracker(Store);
        foreach (var record in tracker.Load())
        {
            if (OperatingSystem.IsWindows())
            {
                var result = tracker.TryStop(record, force: true);
                Assert.True(result.Status is ProcessStopStatus.Stopped or ProcessStopStatus.NotRunning,
                    $"fixture helper {record.Pid} was not stopped: {result}");
            }
            else
            {
                KillHelper(record.Pid);
            }
            AssertGoneWithin(record.Pid);
        }
    }

    [AvaloniaFact]
    public void ManagedPackageLaunchesWithItsOwnSavesAndNoNotice()
    {
        // Windows Unity reads LocalLow, which no environment variable moves: a package there
        // shares the user's saves and is said so instead of promising a redirect. Per-OS matrix.
        if (OperatingSystem.IsWindows())
        {
            ManagedPackageSharesSavesOnWindows();
            return;
        }
        var package = PackageRoot();
        var helper = InstallHelper(package);
        // The Steam install must look real to the page: an exe alone is not an installation,
        // the Steam manifest is what the library scan trusts.
        var game = SteamInstallationFolder();
        var vm = Create(Item(game), out var launch);
        vm.HelperArguments = helper.Arguments;
        try
        {
            Play(vm, helper);
            AssertLaunched(vm, launch);
            var seen = WaitForFile(Path.Combine(package, "launch-env.txt"), vm, launch);
            var home = Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName, "config");
            Assert.Contains($"HOME={home}", seen);
            Assert.Contains($"XDG_CONFIG_HOME={home}", seen);
            Assert.Contains($"PWD={package}", seen);
            Assert.False(vm.IsSavesSharedNoticeVisible);
            // Steam is not a launcher here: Phaket started the process, so it can stop it.
            Assert.NotNull(launch.Tracked("945360"));
        }
        finally { StopHelpers(); }
    }

    [AvaloniaFact]
    public void SeedingRunsOnceAndStartsFromTheExistingSaves()
    {
        var package = PackageRoot();
        var helper = InstallHelper(package);
        var game = SteamInstallationFolder();
        // The vanilla saves: where the GAME keeps them (~/.config/unity3d), never Phaket's own
        // profile copy. Windows Unity reads LocalLow, which no environment moves, so a package
        // there shares the user's saves and seeds nothing (asserted below). "~" follows the
        // process HOME, so pointing HOME at this fixture keeps the runner's own folder out.
        var sandboxHome = Directory.CreateTempSubdirectory("phaket-seed-home-").FullName;
        var realSaves = Path.Combine(sandboxHome, ".config", "unity3d", "Innersloth", "Among Us");
        Directory.CreateDirectory(realSaves);
        File.WriteAllText(Path.Combine(realSaves, "Player.txt"), "postęp");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", sandboxHome);
        var vm = Create(Item(game, saveLocations: ["~/.config/unity3d/Innersloth/Among Us"]), out var launch);
        vm.HelperArguments = helper.Arguments;
        var home = Path.Combine(package, SavesIsolationPlanner.HomeDirectoryName, "config");
        try
        {
            Play(vm, helper);
            if (OperatingSystem.IsWindows())
            {
                // Windows: shared saves, no redirect and no seed — said once, game still starts.
                AssertSharedPackageLaunch(vm, launch, package);
                Assert.False(File.Exists(Path.Combine(home, PackageSavesSeed.MarkerFileName)),
                    "a Shared launch created a seed marker");
                return;
            }
            Assert.Equal("postęp", File.ReadAllText(Path.Combine(home, "Player.txt")));
            var marker = Path.Combine(home, PackageSavesSeed.MarkerFileName);
            var firstWrite = File.GetLastWriteTimeUtc(marker);
            // The game writes its own save on the first run: the seed must never overwrite it.
            File.WriteAllText(Path.Combine(home, "Player.txt"), "nowszy postęp");
            Play(vm, helper);
            Assert.Equal("nowszy postęp", File.ReadAllText(Path.Combine(home, "Player.txt")));
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            StopHelpers();
            try { Directory.Delete(sandboxHome, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [AvaloniaFact]
    public void SharedSavesAreSaidOnceAndTheGameStillStarts()
    {
        var package = PackageRoot();
        var helper = InstallHelper(package);
        var vm = Create(Item(SteamInstallationFolder(), "shared"), out var launch);
        vm.HelperArguments = helper.Arguments;
        try
        {
            Play(vm, helper);
            AssertSharedPackageLaunch(vm, launch, package);
            Assert.Equal("Ta gra współdzieli zapisy z wersją podstawową. Nie uruchamiaj obu wersji jednocześnie, żeby nie nadpisać postępu.",
                LocalizationService.Instance["Game.SavesShared"]);
            Assert.Equal("Ta gra współdzieli zapisy z wersją podstawową.", LocalizationService.Instance["Game.SavesSharedTitle"]);
            if (!OperatingSystem.IsWindows())
            {
                var seen = WaitForFile(Path.Combine(package, "launch-env.txt"), vm, launch);
                // Linux proves the shared launch passes no redirect: the helper sees the real profile.
                var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Assert.Contains($"HOME={realHome}", seen);
                Assert.DoesNotContain(SavesIsolationPlanner.HomeDirectoryName, seen);
            }
            vm.DismissSavesSharedNoticeCommand.Execute(null);
            Assert.False(vm.IsSavesSharedNoticeVisible);
        }
        finally { StopHelpers(); }
    }

    [AvaloniaFact]
    public void VanillaInstallationLaunchesThroughSteamInsteadOfTheExe()
    {
        // The Steam install exists: a folder without the exe is not an installation at all.
        var game = Path.Combine(root, "Among Us");
        Directory.CreateDirectory(game);
        var vm = Create(Item(game), out var launch);
        using var requested = new SteamLauncherSpy();
        var callsBefore = Called.Count;
        try
        {
            Play(vm);
            // Vanilla always asks Steam to run the store app, exactly as before PAK-146: no exe
            // of ours, so mod loading and the save redirect never apply to it.
            Assert.Equal("steam://rungameid/945360", Assert.Single(Called.Skip(callsBefore)));
            Assert.Equal(LocalizationService.Instance["Game.Launched"], vm.StatusMessage);
            Assert.False(File.Exists(Path.Combine(game, "launch-env.txt")));
            Assert.False(Directory.Exists(Path.Combine(game, SavesIsolationPlanner.HomeDirectoryName)));
        }
        finally { StopHelpers(); }
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
        using var requested = new SteamLauncherSpy();
        var path = SteamShortcutService.ShortcutsPath(SteamRoot, "123");
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Add"], vm.SteamToggleLabel);
        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.True(vm.SteamConsentCardVisible);
        Assert.False(File.Exists(path)); // asking writes nothing
        vm.ConfirmSteamShortcutCommand.Execute(null);
        this.Settle(() => vm.SteamConsentCardVisible == false && vm.HasSteamShortcut);
        Assert.True(File.Exists(path));
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Remove"], vm.SteamToggleLabel);
        // Through the shortcut Steam starts the package by its own id — never the store app,
        // which would run the vanilla game — and its saves are the user's own (Steam carries
        // no environment of ours), so the page says so once.
        var callsBefore = Called.Count;
        Play(vm);
        var shortcutId = SteamShortcutsFile.GenerateShortcutId(
            SteamShortcutsFile.Quote(Path.Combine(game, "Among Us.exe")), vm.Game.Title);
        Assert.Equal($"steam://rungameid/{shortcutId}", Assert.Single(Called.Skip(callsBefore)));
        Assert.Equal(LocalizationService.Instance["Game.Launched"], vm.StatusMessage);
        Assert.True(vm.IsSavesSharedNoticeVisible);
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

    // The Windows half of ManagedPackageLaunchesWithItsOwnSavesAndNoNotice: LocalLow cannot be
    // moved by an environment variable, so the package shares the user's saves — and says so.
    private void ManagedPackageSharesSavesOnWindows()
    {
        var package = PackageRoot();
        var helper = InstallHelper(package);
        var vm = Create(Item(SteamInstallationFolder()), out var launch);
        vm.HelperArguments = helper.Arguments;
        try
        {
            Play(vm, helper);
            AssertSharedPackageLaunch(vm, launch, package);
        }
        finally { StopHelpers(); }
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}