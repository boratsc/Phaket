using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// The game screen stays simple: one row of three actions (Play, Mods, "...")
// and every other action reachable from the "..." menu with a working command.
public class GameViewMenuTests
{
    private static GameViewModel Create(Action<string> opened) => new(
        new GameCatalogItem(new GameDefinition("among-us", ["BepInEx/**"]), "", ""),
        () => opened("back"),
        openMods: () => opened("mods"),
        openProfiles: () => opened("profiles"),
        openBackup: () => opened("backup"),
        openCompatibility: () => opened("compatibility"),
        openInstall: () => opened("install"),
        openBuilds: () => opened("builds"),
        openShare: () => opened("share"),
        openGraph: () => opened("graph"),
        openReport: () => opened("report"));

    private static (Window Window, GameViewModel Vm, MenuFlyout Flyout, MenuItem[] Items) OpenMenu(Action<string> opened)
    {
        var vm = Create(opened);
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.GameView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var more = window.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is MenuFlyout);
        var flyout = (MenuFlyout)more.Flyout!;
        flyout.ShowAt(more);
        Dispatcher.UIThread.RunJobs();
        return (window, vm, flyout, flyout.Items.OfType<MenuItem>().ToArray());
    }

    [AvaloniaFact]
    public void More_menu_offers_every_secondary_action_with_a_resolved_command()
    {
        var (window, vm, _, items) = OpenMenu(_ => { });
        try
        {
            Assert.Equal(9, items.Length);
            Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Header as string)));
            Assert.All(items, i => Assert.NotNull(i.Command));
            var commands = items.Select(i => i.Command).ToHashSet();
            Assert.DoesNotContain(vm.PlayCommand, commands);
            Assert.DoesNotContain(vm.OpenModsCommand, commands);
            Assert.DoesNotContain(vm.BackCommand, commands);
            foreach (var expected in new IRelayCommand[]
            {
                vm.OpenInstallCommand, vm.OpenProfilesCommand, vm.OpenBackupCommand, vm.OpenBuildsCommand,
                vm.OpenCompatibilityCommand, vm.OpenShareCommand, vm.OpenGraphCommand, vm.OpenReportCommand,
                vm.PrepareLaunchOptionsCommand
            })
                Assert.Contains(expected, commands);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Menu_item_command_runs_the_navigation_callback()
    {
        var opened = new List<string>();
        var (window, vm, _, items) = OpenMenu(opened.Add);
        try
        {
            var item = items.Single(i => Equals(i.Command, vm.OpenBuildsCommand));
            Assert.True(item.IsEnabled);
            item.Command!.Execute(null);
            Assert.Equal(["builds"], opened);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Only_back_play_and_mods_are_actionable_buttons_on_the_screen()
    {
        var vm = Create(_ => { });
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.GameView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var more = window.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is MenuFlyout);
            Assert.Null(more.Command);
            // Duplicate cards are gone: Back, Play, Mods and "..." are the only buttons outside
            // the launch-options card (Apply/Rollback are hidden until a plan is pending).
            var actionable = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible && b.Command is not null)
                .Select(b => b.Command)
                .ToHashSet();
            Assert.Equal([vm.PlayCommand, vm.OpenModsCommand, vm.BackCommand], actionable);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Apply_and_rollback_appear_only_around_a_pending_plan()
    {
        // The launch-options card is Steam-only: the fixture needs a real Steam installation.
        var gameRoot = Directory.CreateTempSubdirectory("phaket-game-menu-").FullName;
        var opened = new List<string>();
        var vm = Create(opened.Add);
        vm.Game.Detected = new SteamInstallation(new Installation("fixture", "steam", gameRoot),
            new SteamAppManifest("945360", "Among Us", "1", "public", "1", "fixture", 0),
            new SteamLibrary(gameRoot, false), GameAvailability.Moddable);
        Assert.True(vm.Game.IsSelectedSteam);
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.GameView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            var apply = buttons.Single(b => Equals(b.Command, vm.ConfirmLaunchOptionsCommand));
            var rollback = buttons.Single(b => Equals(b.Command, vm.RollbackLaunchOptionsCommand));
            Assert.False(apply.IsEffectivelyVisible);
            Assert.False(rollback.IsEffectivelyVisible);
            vm.PendingPlan = new LaunchOptionsPlan(new SteamAccount("1", "config"), vm.Game.SteamAppId, null, "%command%");
            Dispatcher.UIThread.RunJobs();
            Assert.True(apply.IsEffectivelyVisible);
            Assert.False(rollback.IsEffectivelyVisible);
        }
        finally { window.Close(); Directory.Delete(gameRoot, true); }
    }
}
