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

// PAK-145/146-B: "add/remove the shortcut in Steam" never writes before the yes, the toggle
// reflects the file, and "play" keeps the package's saves beside the game or says once that it
// cannot. Every fixture lives in temp; the real Steam folder is never touched.
public class SteamShortcutViewModelTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-steam-vm-").FullName;
    private const int AppId = -1742743473; // the fixture's real among-us shortcut id

    // A Steam root with one user, the same layout Steam keeps.
    private string SteamRoot()
    {
        var steam = Path.Combine(root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "userdata", "123", "config"));
        return steam;
    }

    private string GameRoot()
    {
        var game = Path.Combine(root, "Among Us");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Among Us.exe"), "fixture");
        return game;
    }

    private SteamShortcutService Running(bool running) => new(() => running);

    private static GameDefinition Definition(string linuxMode = "env") => new("among-us", ["BepInEx/**"],
        StoreIds: new Dictionary<string, string> { ["steam"] = "945360" },
        MainExecutable: "Among Us.exe",
        SavePaths: ["profile://steam/945360"],
        SavesIsolation: new SavesIsolation(new Dictionary<string, SavesIsolationMode>
        {
            ["linux"] = linuxMode == "env"
                ? new SavesIsolationMode("env", ["HOME", "XDG_CONFIG_HOME"])
                : new SavesIsolationMode("shared", Reason: "unity-locallow-known-folder"),
        }));

    private GameCatalogItem Item(string gameRoot, GameDefinition definition, string steamRoot)
    {
        // The library sets Detected after constructing the item, so caches update; a test does the same.
        var item = new GameCatalogItem(definition, "", "");
        item.Detected = new SteamInstallation(new Installation("steam-945360", "among-us", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "555", "Among Us", 1, null, 0),
            new SteamLibrary(steamRoot, false), GameAvailability.Moddable);
        return item;
    }

    // A package folder Phaket materialized: the mods live in it, so the launch starts from it.
    private string PackageRoot()
    {
        var state = Path.Combine(root, "appdata", "packages", "state", "among-us", "ABCDEF012345");
        Directory.CreateDirectory(Path.Combine(state, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(state, "Among Us.exe"), "fixture");
        return state;
    }

    private GameViewModel Vm(GameCatalogItem item, bool steamRunning, out string steamRoot)
    {
        steamRoot = SteamRoot();
        var vm = new GameViewModel(item, () => { }, appDataRoot: Path.Combine(root, "appdata"))
        {
            SteamShortcuts = Running(steamRunning),
            SteamHome = steamRoot,
            SteamRoots = [steamRoot],
            SteamUser = "123",
            HostOs = "linux",
            Launch = new LaunchService(Path.Combine(root, "processes.json")),
        };
        return vm;
    }

    private static void Settle(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted, "the command did not finish");
        task.GetAwaiter().GetResult();
    }

    private static void RunCommand(System.Windows.Input.ICommand command) =>
        Settle(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)command).ExecuteAsync(null));

    [AvaloniaFact]
    public void AddingWithConsentWritesTheShortcutAndTheToggleFollowsTheFile()
    {
        var vm = Vm(Item(GameRoot(), Definition(), ""), steamRunning: false, out var steam);
        Assert.False(vm.HasSteamShortcut);
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Add"], vm.SteamToggleLabel);

        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.True(vm.SteamConsentCardVisible);
        Assert.False(File.Exists(SteamShortcutService.ShortcutsPath(steam, "123"))); // asking writes nothing

        RunCommand(vm.ConfirmSteamShortcutCommand);
        var path = SteamShortcutService.ShortcutsPath(steam, "123");
        Assert.True(File.Exists(path));
        var entry = Assert.Single(SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(File.ReadAllBytes(path))));
        Assert.True(SteamShortcutsFile.IsPhaket(entry));
        Assert.Equal(vm.Game.RootPath + Path.DirectorySeparatorChar + "Among Us.exe",
            entry.GetString("Exe")!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(vm.Game.RootPath, entry.GetString("StartDir")!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("", entry.GetString("LaunchOptions"));
        Assert.True(vm.HasSteamShortcut);
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Remove"], vm.SteamToggleLabel);
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Added"], vm.StatusMessage);
        // SteamAppId differs from the shortcut id: the toggle must not trust the store id.
        Assert.Equal(945360, int.Parse(vm.Game.SteamAppId));
        Assert.NotEqual(945360, entry.GetInt32("appid"));
    }

    [AvaloniaFact]
    public void SayingNoWritesNothing()
    {
        var vm = Vm(Item(GameRoot(), Definition(), ""), steamRunning: false, out var steam);
        vm.ToggleSteamShortcutCommand.Execute(null);
        vm.CancelSteamShortcutCommand.Execute(null);
        Assert.False(vm.SteamConsentCardVisible);
        Assert.False(vm.HasSteamShortcut);
        Assert.False(File.Exists(SteamShortcutService.ShortcutsPath(steam, "123")));
        Assert.Equal("", vm.StatusMessage);
    }

    [AvaloniaFact]
    public void ARunningSteamIsARestartWarningNotABlocker()
    {
        var vm = Vm(Item(GameRoot(), Definition(), ""), steamRunning: true, out _);
        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.True(vm.ShowSteamRestart);
        Assert.Equal("Steam był uruchomiony. Zamknij i uruchom Steam ponownie, aby zobaczyć grę w bibliotece.",
            LocalizationService.Instance["SteamShortcut.RestartSteam"]);
        RunCommand(vm.ConfirmSteamShortcutCommand);
        Assert.True(vm.HasSteamShortcut); // still written
        Assert.Equal(LocalizationService.Instance["SteamShortcut.AddedRestartSteam"], vm.StatusMessage);
    }

    // A Steam file may be Phaket's only through shortcuts this game did not write: the toggle
    // must stay "add" and a removal must never take somebody else's entry.
    [AvaloniaFact]
    public void AForeignPhaketShortcutDoesNotCountAsThisGameAndIsNotRemoved()
    {
        var steam = SteamRoot();
        File.WriteAllBytes(SteamShortcutService.ShortcutsPath(steam, "123"), Convert.FromHexString(SteamShortcutBytes.OneEntry));
        var vm = Vm(Item(GameRoot(), Definition(), ""), steamRunning: false, out _);
        Assert.False(vm.HasSteamShortcut);
        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.False(vm.SteamRemoveMode);
        vm.CancelSteamShortcutCommand.Execute(null);
        Assert.True(File.Exists(SteamShortcutService.ShortcutsPath(steam, "123")));
    }

    [AvaloniaFact]
    public void RemovingNeedsTheSameConsentAndKeepsABackup()
    {
        var steam = SteamRoot();
        var vm = Vm(Item(GameRoot(), Definition(), ""), steamRunning: false, out _);
        vm.ToggleSteamShortcutCommand.Execute(null);
        RunCommand(vm.ConfirmSteamShortcutCommand);
        Assert.True(vm.HasSteamShortcut);

        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.True(vm.SteamRemoveMode);
        Assert.Equal(LocalizationService.Instance["SteamShortcut.RemoveConsent"], vm.SteamConsentText);
        Assert.True(vm.HasSteamShortcut); // asking removes nothing
        RunCommand(vm.ConfirmSteamShortcutCommand);
        Assert.False(vm.HasSteamShortcut);
        Assert.Equal(LocalizationService.Instance["SteamShortcut.Removed"], vm.StatusMessage);
        // The entry is gone; the Phaket-tagged removal never created a fresh one.
        Assert.Empty(SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(File.ReadAllBytes(
            SteamShortcutService.ShortcutsPath(steam, "123")))));
    }

    [AvaloniaFact]
    public void ABrokenShortcutsFileIsReportedAndNothingChanges()
    {
        var steam = SteamRoot();
        var path = SteamShortcutService.ShortcutsPath(steam, "123");
        File.WriteAllText(path, "\u0001broken");
        var before = File.ReadAllBytes(path);

        var vm = Vm(Item(GameRoot(), Definition(), ""), steamRunning: false, out _);
        Assert.False(vm.HasSteamShortcut);
        // The page opens before anything is read: the broken file is reported on the first look.
        vm.RefreshSteamShortcut();
        Assert.Contains("pliku skrótów Steam", vm.StatusMessage);
        Assert.Contains(path, vm.StatusMessage);

        vm.ToggleSteamShortcutCommand.Execute(null);
        Assert.True(vm.SteamConsentCardVisible);
        RunCommand(vm.ConfirmSteamShortcutCommand);
        Assert.False(vm.HasSteamShortcut);
        Assert.False(vm.SteamConsentCardVisible);
        Assert.Contains("pliku skrótów Steam", vm.StatusMessage);
        Assert.DoesNotContain("pliku skrótów Steam", LocalizationService.Instance["SteamShortcut.Failed"]);
        Assert.Equal(before, File.ReadAllBytes(path)); // never replaced
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}