using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class ProfileHistoryTests
{
    [AvaloniaFact]
    public void Clean_profile_is_empty_and_required_mods_cannot_be_removed()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        try
        {
            var store = new ProfileStore(root);
            var profile = store.AddProfile("fixture-game", "Modded", [
                new ModEntry { Name = "Required", Version = "1", Required = true },
                new ModEntry { Name = "Optional", Version = "2" },
            ]);
            var game = new GameCatalogItem(new GameDefinition("fixture-game", []), "", "");
            var vm = new ProfilesViewModel(game, () => { }, root);
            var clean = vm.Profiles.Single(item => item.IsClean);
            vm.SelectCommand.Execute(clean);
            Assert.Equal("clean-game", new ProfileStore(root).Active("fixture-game")!.Id);
            Assert.Empty(vm.Composition);
            Assert.Equal(0, clean.ModCount);

            vm.SelectCommand.Execute(vm.Profiles.Single(item => item.Id == profile.Id));
            Assert.Equal(profile.Id, new ProfileStore(root).Active("fixture-game")!.Id);
            var required = vm.Composition.Single(mod => mod.Model.Required);
            vm.AskRemoveModCommand.Execute(required);
            Assert.True(vm.IsRequiredMod);
            vm.ConfirmRemoveModCommand.Execute(null);
            Assert.Equal(2, new ProfileStore(root).Active("fixture-game")!.Mods.Count);

            vm.AskRemoveModCommand.Execute(vm.Composition.Single(mod => !mod.Model.Required));
            vm.ConfirmRemoveModCommand.Execute(null);
            Assert.Single(new ProfileStore(root).Active("fixture-game")!.Mods);
            Assert.True(new ProfileStore(root).Active("fixture-game")!.Mods[0].Required);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Profile_store_records_changes_and_view_model_compares_and_restores_snapshot()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(RenderArtifacts.Directory);
        var oldLanguage = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        try
        {
            var store = new ProfileStore(root);
            var profile = store.AddProfile("fixture-game", "Test profile", [new ModEntry { Name = "Example", Version = "1.0" }]);
            profile.Mods[0].Version = "2.0";
            store.Save();

            var history = new ProfileHistoryStore(root).For("fixture-game", profile.Id);
            Assert.Equal(2, history.Count);
            Assert.Equal("Profiles.History.Changed", history[^1].Description);

            var game = new GameCatalogItem(new GameDefinition("fixture-game", []), "", "");
            var vm = new ProfilesViewModel(game, () => { }, root);
            var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.ProfilesView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                frame.Save(RenderArtifacts.Path("22-profiles.png"), PngBitmapEncoderOptions.Default);
            }
            Assert.Contains(vm.Profiles, p => p.IsClean && p.ModCount == 0);
            var active = vm.Profiles.Single(p => p.Id == profile.Id);
            Assert.True(active.IsActive);
            Assert.Single(vm.Composition);
            var oldSnapshot = vm.History.Last();
            vm.CompareCommand.Execute(oldSnapshot);
            Assert.Contains(vm.Diff, row => row.Name == "Example" && row.FromVersion == "2.0" && row.ToVersion == "1.0" && row.StateText == LocalizationService.Instance["Profiles.Updated"]);

            vm.AskRollbackCommand.Execute(oldSnapshot);
            Assert.True(vm.IsRollbackModal);
            vm.ConfirmRollbackCommand.Execute(null);

            var restored = new ProfileStore(root).Active("fixture-game")!;
            Assert.Equal("1.0", Assert.Single(restored.Mods).Version);
            Assert.Single(new ProfileStore(root).Backups("fixture-game"));
            Assert.Equal(3, new ProfileHistoryStore(root).For("fixture-game", profile.Id).Count);
            window.Close();
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = oldLanguage;
            Directory.Delete(root, true);
        }
    }
}
