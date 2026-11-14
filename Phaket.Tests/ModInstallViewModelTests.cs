using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

public class ModInstallViewModelTests
{
    private static readonly InstallTarget Target = new("", "steam", true);
    private static InstallPreparation Preparation(IReadOnlyList<ConflictProposal>? conflicts = null) => new(
        "among-us", "Owner-Mod", "2.0", "http://localhost", Target,
        [new OperationPlan("1", new RecipeRef("mod", "2"), "i", "p",
            [new PlannedOperation("op", "copy", "game://plugins/Mod.dll", null, null, null, [], "", "", "", [])], [], [], false, [])],
        [], conflicts ?? [], [new InstallPreview("Owner-Mod", "2.0", ["plugins/Mod.dll"], 42)],
        "/tmp/sandbox", "", "", "", "");

    [AvaloniaFact]
    public async Task AcceptanceIsRequiredAndSuccessRefreshes()
    {
        var refreshed = 0; var executed = 0;
        var vm = new ModCatalogViewModel("among-us", "/tmp/opencode/ui4-test", autoSync: false, installedChanged: () => refreshed++);
        vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation());
        vm.ExecuteOverride = (_, accepted, _) => { Assert.True(accepted); executed++; return Task.FromResult(new InstallOutcome(true, null, [])); };
        await vm.BeginInstallAsync("Owner-Mod", "2.0", Target);
        Assert.False(vm.ExecuteInstallCommand.CanExecute(null));
        await vm.ExecuteInstallCommand.ExecuteAsync(null);
        Assert.Equal(0, executed);
        vm.PlanAccepted = true;
        Assert.True(vm.ExecuteInstallCommand.CanExecute(null));
        await vm.ExecuteInstallCommand.ExecuteAsync(null);
        Assert.Equal(1, executed); Assert.Equal(1, refreshed); Assert.False(vm.HasPreview);
    }

    [AvaloniaFact]
    public async Task ConflictsAreReplannedWithSuggestedOverrides()
    {
        var vm = new ModCatalogViewModel("among-us", "/tmp/opencode/ui4-test", autoSync: false);
        var calls = 0;
        vm.PrepareOverride = (_, _, _, _, overrides) => {
            calls++;
            if (calls == 1) return Task.FromResult(Preparation([new ConflictProposal("Owner-Dependency", ["1", "2"], ["A", "B"], "2")]));
            Assert.Equal("2", overrides!["Owner-Dependency"]); return Task.FromResult(Preparation());
        };
        await vm.BeginInstallAsync("Owner-Mod", "2.0", Target);
        Assert.True(vm.HasConflicts); Assert.False(vm.HasPreview);
        await vm.UseSuggestedCommand.ExecuteAsync(null);
        Assert.Equal(2, calls); Assert.True(vm.HasPreview); Assert.False(vm.PlanAccepted);
    }

    [AvaloniaFact]
    public async Task VersionButtonStartsPreparation()
    {
        var root = Directory.CreateTempSubdirectory("phaket-ui4-button-").FullName;
        try {
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var vm = new ModsViewModel(game, () => { }, root);
            if (vm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
            var version = new ThunderstoreVersion("2.0", "Owner-Mod-2.0", "https://example.com", 42, DateTimeOffset.UnixEpoch, []);
            var mod = new ThunderstoreMod("id", "among-us", "Owner", "Mod", "Owner-Mod", "", "Owner", "https://example.com", null, false, DateTimeOffset.UnixEpoch, version);
            vm.Catalog.PrepareOverride = (name, v, _, target, _) => {
                Assert.Equal("Owner-Mod", name); Assert.Equal("2.0", v); Assert.True(target.Sandbox);
                return Task.FromResult(Preparation());
            };
            vm.Catalog.SelectedMod = mod;
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            vm.Catalog.Details = new ThunderstoreModDetails(mod, [version], DateTimeOffset.UnixEpoch);
            vm.SelectedTabIndex = 1;
            var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.ModsView { DataContext = vm } };
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalizationService.Instance["ModInstall.Install"]));
            Assert.NotNull(button.Command); Assert.True(button.Command.CanExecute(button.CommandParameter));
            button.Command.Execute(button.CommandParameter);
            if (vm.Catalog.InstallVersionCommand.ExecutionTask is { } task) await task;
            Assert.True(vm.Catalog.HasPreview); window.Close();
        } finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task SuccessReloadsInstalledRegistry()
    {
        var root = Directory.CreateTempSubdirectory("phaket-ui4-refresh-").FullName;
        try {
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var vm = new ModsViewModel(game, () => { }, root);
            if (vm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
            vm.Catalog.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation());
            vm.Catalog.ExecuteOverride = (_, _, _) => {
                new InstallRegistry(Path.Combine(root, "installs.json")).Add(new InstalledRecipe("mod", "2", "among-us", "steam", "/game", "/profile", "/stage", "/state", null, DateTimeOffset.UnixEpoch, "Owner-Mod", "2.0"));
                return Task.FromResult(new InstallOutcome(true, null, []));
            };
            Assert.Empty(vm.InstalledRecipes);
            await vm.Catalog.BeginInstallAsync("Owner-Mod", "2.0", Target);
            vm.Catalog.PlanAccepted = true;
            await vm.Catalog.ExecuteInstallCommand.ExecuteAsync(null);
            Assert.Equal("mod", Assert.Single(vm.InstalledRecipes).RecipeId);
        } finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task UpdateStartsPlanForInstalledTargetAndRendersPreview()
    {
        var root = Directory.CreateTempSubdirectory("phaket-ui4-").FullName;
        try {
            var entry = new InstalledRecipe("mod", "1", "among-us", "steam", "/game", "/profile", "/stage", "/state", null, DateTimeOffset.UnixEpoch, "Owner-Mod", "1.0");
            new InstallRegistry(Path.Combine(root, "installs.json")).Add(entry);
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var vm = new ModsViewModel(game, () => { }, root);
            if (vm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
            var version = new ThunderstoreVersion("2.0", "Owner-Mod-2.0", "https://example.com", 42, DateTimeOffset.UnixEpoch, []);
            var mod = new ThunderstoreMod("id", "among-us", "Owner", "Mod", "Owner-Mod", "", "Owner", "https://example.com", null, false, DateTimeOffset.UnixEpoch, version);
            vm.Catalog.Mods.Add(mod);
            vm.Catalog.PrepareOverride = (name, v, _, target, _) => {
                Assert.Equal("Owner-Mod", name); Assert.Equal("2.0", v); Assert.False(target.Sandbox); Assert.Equal("/game", target.GameRoot);
                return Task.FromResult(Preparation() with { Target = target });
            };
            var installed = Assert.Single(vm.InstalledRecipes); Assert.True(installed.HasUpdate);
            await installed.UpdateCommand.ExecuteAsync(null);
            Assert.Equal(1, vm.SelectedTabIndex); Assert.True(vm.Catalog.HasPreview);
            var view = new Phaket.Views.ModsView { DataContext = vm };
            var window = new Window { Width = 1280, Height = 800, Content = view };
            window.Show(); Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save("/tmp/opencode/phaket-render/ui4-plan-preview.png"); window.Close();
        } finally { Directory.Delete(root, true); }
    }
}
