using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Catalog;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-67: the catalog needs a server that is not published yet, so an unreachable
// endpoint must read as "catalog offline" in the user's language, never as a raw
// exception. The endpoint comes from settings.json and retrying is plain Refresh.
public class CatalogOfflineTests
{
    private static ThunderstoreMod Mod() => new("id", "among-us", "Author", "Test", "Author-Test", "Desc",
        "Author", "https://example.com", null, false, DateTimeOffset.UtcNow,
        new ThunderstoreVersion("1.2", "Author-Test-1.2", "https://example.com", 42, DateTimeOffset.UtcNow, []));

    private sealed class Flaky : ICatalogTransport
    {
        public Exception? Failure;
        public Task<CatalogResponse> GetAsync(string path, string? etag, CancellationToken cancellationToken)
        {
            if (Failure is { } failure) throw failure;
            object body = path.StartsWith("/mods/index")
                ? new { revision = 1, mods = new[] { Mod() }, nextCursor = (string?)null }
                : new { revision = 1, reset = false, changes = Array.Empty<object>() };
            return Task.FromResult(new CatalogResponse(200, null,
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }

    [AvaloniaFact]
    public async Task Network_failure_shows_localized_offline_state_and_retry_recovers()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        var root = Directory.CreateTempSubdirectory("phaket-catalog-offline-").FullName;
        try
        {
            loc.CurrentLanguage = "pl-PL";
            var fake = new Flaky { Failure = new HttpRequestException("boom", new SocketException(111)) };
            var vm = new ModCatalogViewModel("among-us", root, fake, false);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.IsOffline);
            Assert.Equal(loc["Catalog.Offline"], vm.StatusMessage);

            loc.CurrentLanguage = "en";
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(loc["Catalog.Offline"], vm.StatusMessage);

            // The retry is the existing Refresh command: once the server answers, the state clears.
            fake.Failure = null;
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.False(vm.IsOffline);
            Assert.Equal("", vm.StatusMessage);
            Assert.Single(vm.Mods);
        }
        finally
        {
            loc.CurrentLanguage = previous;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Missing_server_answer_is_offline_not_an_exception()
    {
        // A live 404 (the published catalog does not exist yet) and a refused connection
        // must both end in the offline state rather than escaping to the UI.
        var root = Directory.CreateTempSubdirectory("phaket-catalog-404-").FullName;
        try
        {
            var vm = new ModCatalogViewModel("among-us", root,
                new Flaky { Failure = new HttpRequestException($"catalog snapshot failed: 404") }, false);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.IsOffline);

            var offline = new ModCatalogViewModel("among-us", root, autoSync: false) { Endpoint = "http://127.0.0.1:1" };
            await offline.RefreshCommand.ExecuteAsync(null);
            Assert.True(offline.IsOffline);
            Assert.NotEqual("", offline.StatusMessage);

            // A malformed address is reported, and the view stays usable.
            var bad = new ModCatalogViewModel("among-us", root, autoSync: false) { Endpoint = "catalog.example" };
            await bad.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(LocalizationService.Instance["Builds.InvalidEndpoint"], bad.StatusMessage);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Catalog_endpoint_defaults_to_the_saved_setting()
    {
        var root = Directory.CreateTempSubdirectory("phaket-catalog-endpoint-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.json"),
                """{ "catalogEndpoint": "http://127.0.0.1:18081" }""");
            var vm = new ModCatalogViewModel("among-us", root, autoSync: false);
            Assert.Equal("http://127.0.0.1:18081", vm.Endpoint);

            Directory.CreateDirectory(root + "-empty");
            Assert.Equal(CatalogSettings.DefaultEndpoint,
                new ModCatalogViewModel("among-us", root + "-empty", autoSync: false).Endpoint);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(root + "-empty", true);
        }
    }

    [AvaloniaFact]
    public async Task Offline_state_is_rendered_in_the_mods_catalog_tab()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        loc.CurrentLanguage = "pl-PL";
        var root = Directory.CreateTempSubdirectory("phaket-catalog-offline-ui-").FullName;
        var game = new GameCatalogItem(Phaket.Services.GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
        var vm = new ModsViewModel(game, () => { }, root);
        // Blocking the UI thread here would deadlock the refresh: await it instead.
        if (vm.Catalog.RefreshCommand.ExecutionTask is { } initial) await initial;
        vm.Catalog.Endpoint = "http://127.0.0.1:1";
        await vm.Catalog.RefreshCommand.ExecuteAsync(null);
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.ModsView { DataContext = vm } };
        window.Show();
        try
        {
            ((TabControl)((Phaket.Views.ModsView)window.Content!).Content!).SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.Catalog.IsOffline);
            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text).Where(t => t is not null).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(loc["Catalog.Offline"], texts);
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            frame!.Save("/tmp/opencode/phaket-render/mod-catalog-offline.png");
        }
        finally
        {
            loc.CurrentLanguage = previous;
            window.Close();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Builds_view_reports_the_same_offline_state()
    {
        var game = new GameCatalogItem(Phaket.Services.GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
        var vm = new BuildsViewModel(game, () => { });
        // The shared settings file is not writable in tests: point the view at a dead port.
        // The sync fails before it can touch the local catalog cache.
        vm.Endpoint = "http://127.0.0.1:1";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsOffline);
        Assert.Equal(LocalizationService.Instance["Catalog.Offline"], vm.StatusMessage);
    }

    [AvaloniaFact]
    public void Settings_link_navigates_to_help()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        loc.CurrentLanguage = "pl-PL";
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.NavigateCommand.Execute("settings");
            Dispatcher.UIThread.RunJobs();
            var link = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Content as string, loc["Settings.MoreInHelp"]));
            link.Command!.Execute(link.CommandParameter);
            Assert.Same(vm.Help, vm.CurrentPage);
            Assert.Equal("help", vm.ActiveNav);
        }
        finally
        {
            loc.CurrentLanguage = previous;
            window.Close();
        }
    }
}
