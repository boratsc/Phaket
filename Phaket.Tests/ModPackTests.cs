using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class ModPackTests
{
    [Fact]
    public void Bulk_operations_never_select_toggle_or_remove_required_dependencies()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var pack = new ModpackDefinition
            {
                Id = "pack", Name = "Pack", GameId = "among-us", Author = "Author", Version = "1",
                Items =
                [
                    new() { Name = "BepInEx", Version = "1", Source = "thunderstore", Required = true },
                    new() { Name = "Mod A", Version = "2", Source = "github" },
                    new() { Name = "Mod B", Version = "3", Source = "nexus" }
                ]
            };
            var store = new ModpackStore(root);
            store.Save(pack);
            var vm = new ModPackViewModel(pack, store, () => { });
            vm.AllVisibleSelectable = true;
            Assert.True(vm.Mods.Single(m => m.Name == "BepInEx").Required);
            Assert.False(vm.Mods.Single(m => m.Name == "BepInEx").IsSelected);
            Assert.Equal(2, vm.SelectedCount);
            vm.ToggleSelectedCommand.Execute(null);
            Assert.True(vm.Mods.Where(m => !m.Required).All(m => !m.Enabled));
            vm.DeleteSelectedCommand.Execute(null);
            var remaining = Assert.Single(store.ForGame("among-us"));
            Assert.Equal("BepInEx", Assert.Single(remaining.Items).Name);
            Assert.True(remaining.Items[0].Required);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Search_and_source_filter_only_show_matching_mods_and_protect_required_items()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            var pack = new ModpackDefinition
            {
                Name = "Pack", Author = "Author", GameId = "among-us", Version = "1",
                Items =
                [
                    new() { Name = "BepInEx", Version = "1", Source = "thunderstore", Required = true },
                    new() { Name = "Plant Everything", Version = "2", Source = "thunderstore" },
                    new() { Name = "Manual Mod", Version = "3", Source = "nexus" }
                ]
            };
            store.Save(pack);
            var vm = new ModPackViewModel(pack, store, () => { });

            vm.SearchText = "plant";
            Assert.Equal("Plant Everything", Assert.Single(vm.VisibleMods).Name);
            vm.SearchText = "";
            vm.SelectedSourceFilter = vm.SourceFilters.Single(filter => filter.Id == "nexus");
            Assert.Equal("Manual Mod", Assert.Single(vm.VisibleMods).Name);
            Assert.False(vm.Mods.Single(mod => mod.Name == "BepInEx").IsSelectable);
            Assert.False(string.IsNullOrWhiteSpace(vm.Mods.Single(mod => mod.Name == "BepInEx").ProtectedReason));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Local_store_writes_atomically_and_rejects_invalid_paths_as_names()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            var pack = new ModpackDefinition { Name = "Pack", Author = "Author", GameId = "among-us", Version = "1" };
            store.Save(pack);
            Assert.True(File.Exists(Path.Combine(root, "modpacks.json")));
            Assert.False(File.Exists(Path.Combine(root, "modpacks.json.tmp")));
            pack.Items = [new ModpackMod { Name = "bad", Version = "1", Source = "local-script" }];
            Assert.Throws<ArgumentException>(() => store.Save(pack));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Modpack_composition_renders_headless()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            var pack = new ModpackDefinition
            {
                Name = "Północna wyprawa", GameId = "valheim", Author = "Test", Version = "1.4.2",
                Items =
                [
                    new() { Name = "Plant Everything", Version = "1.20.0", Source = "thunderstore" }
                ]
            };
            store.Save(pack);
            var vm = new ModPackViewModel(pack, store, () => { });
            var window = new Window { Width = 1280, Height = 800, Content = new ModPackView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory(RenderArtifacts.Directory);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(RenderArtifacts.Path("21-modpack.png"), PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
