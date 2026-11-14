using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Catalog;
using Phaket.ViewModels;

namespace Phaket.Tests;

public class ModCatalogViewModelTests
{
    [AvaloniaFact]
    public async Task AddModOpensCatalogWithoutChangingProfile()
    {
        var root = Directory.CreateTempSubdirectory("phaket-add-catalog-").FullName;
        try
        {
            var game = new GameCatalogItem(Phaket.Services.GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var vm = new ModsViewModel(game, () => { }, root);
            if (vm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
            var profilesPath = Path.Combine(root, "profiles.json");
            var original = File.ReadAllText(profilesPath);
            var view = new Phaket.Views.ModsView { DataContext = vm };
            var window = new Avalonia.Controls.Window { Content = view };
            window.Show();
            vm.AddModCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, vm.SelectedTabIndex);
            Assert.Equal(1, ((Avalonia.Controls.TabControl)view.Content!).SelectedIndex);
            Assert.Equal(original, File.ReadAllText(profilesPath));
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fake : ICatalogTransport
    {
        public bool Offline;
        public bool Empty;
        public required ThunderstoreMod Mod;
        public Task<CatalogResponse> GetAsync(string path, string? etag, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException();
            object body = path.StartsWith("/mods/index") ? new { revision = 1, mods = Empty ? Array.Empty<ThunderstoreMod>() : new[] { Mod }, nextCursor = (string?)null } :
                path.StartsWith("/mods/changes") ? new { revision = 1, reset = false, changes = Array.Empty<object>() } : new { mod = Mod, versions = new[] { Mod.Latest } };
            return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }
    [AvaloniaFact]
    public async Task SyncSearchDetailsAndOfflineCache()
    {
        var root = Directory.CreateTempSubdirectory("phaket-catalog-test-").FullName;
        try {
            var version = new ThunderstoreVersion("1.2", "Author-Test-1.2", "https://example.com", 42, DateTimeOffset.UtcNow, ["dependency"]);
            var mod = new ThunderstoreMod("id", "among-us", "Author", "Test", "Author-Test", "Description", "Author", "https://example.com", null, true, DateTimeOffset.UtcNow, version);
            var fake = new Fake { Mod = mod };
            var vm = new ModCatalogViewModel("among-us", root, fake, false);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Single(vm.Mods); Assert.True(vm.Mods[0].IsDeprecated); Assert.False(vm.IsOffline); Assert.NotEqual("—", vm.LastSyncText);
            vm.SearchText = "author"; Assert.Single(vm.Mods);
            vm.SearchText = "absent"; Assert.Empty(vm.Mods); Assert.True(vm.IsEmpty);
            vm.SearchText = "test"; vm.SelectedMod = vm.Mods[0];
            for (var i = 0; i < 100 && vm.Details is null; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.NotNull(vm.Details); Assert.Equal("dependency", Assert.Single(Assert.Single(vm.Details.Versions).Dependencies));
            fake.Offline = true;
            await vm.RefreshCommand.ExecuteAsync(null); Assert.True(vm.IsOffline); Assert.Single(vm.Mods);
            vm.SelectedMod = null; vm.SelectedMod = vm.Mods[0];
            for (var i = 0; i < 100 && vm.Details is null; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.NotNull(vm.Details);
        } finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task EmptyCatalog()
    {
        var root = Directory.CreateTempSubdirectory("phaket-catalog-empty-").FullName;
        try {
            var version = new ThunderstoreVersion("1", "a", "https://example.com", 0, DateTimeOffset.UtcNow, []);
            var mod = new ThunderstoreMod("a", "among-us", "a", "a", "a", "", "a", "https://example.com", null, false, DateTimeOffset.UtcNow, version);
            var vm = new ModCatalogViewModel("among-us", root, new Fake { Mod = mod, Empty = true }, false);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Empty(vm.Mods); Assert.True(vm.IsEmpty); Assert.False(vm.IsOffline);
        } finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task LiveAmongUsCatalogWhenAvailable()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18081"), Timeout = TimeSpan.FromSeconds(2) };
        try { using var response = await http.GetAsync("/mods/index?game=among-us&limit=1"); if (!response.IsSuccessStatusCode) Assert.Skip("Live server unavailable"); }
        catch (HttpRequestException) { Assert.Skip("Live server unavailable"); } catch (TaskCanceledException) { Assert.Skip("Live server unavailable"); }
        var root = Directory.CreateTempSubdirectory("phaket-catalog-live-").FullName;
        try {
            var vm = new ModCatalogViewModel("among-us", root, new HttpCatalogTransport(http), false);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.False(vm.IsOffline); Assert.True(vm.Mods.Count > 0, vm.StatusMessage);
            Console.WriteLine($"Live Among Us mods: {vm.Mods.Count}");
        } finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task RenderCatalog()
    {
        var root = Directory.CreateTempSubdirectory("phaket-catalog-render-").FullName;
        try {
            var game = new Phaket.ViewModels.GameCatalogItem(Phaket.Services.GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var vm = new ModsViewModel(game, () => { }, root);
            vm.Catalog.Endpoint = "http://127.0.0.1:18081";
            if (vm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
            await vm.Catalog.RefreshCommand.ExecuteAsync(null);
            if (vm.Catalog.IsOffline) Assert.Skip("Live server unavailable");
            Assert.NotEmpty(vm.Catalog.Mods);
            vm.Catalog.SelectedMod = vm.Catalog.Mods[0];
            for (var i = 0; i < 100 && vm.Catalog.Details is null; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var view = new Phaket.Views.ModsView { DataContext = vm };
            var window = new Avalonia.Controls.Window { Width = 1280, Height = 800, Content = view };
            window.Show();
            var tabs = (Avalonia.Controls.TabControl)view.Content!;
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            Assert.NotNull(frame);
            frame.Save("/tmp/opencode/phaket-render/mod-catalog.png");
            window.Close();
        } finally { Directory.Delete(root, true); }
    }

}
