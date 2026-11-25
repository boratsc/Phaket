using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Omrl;
using Phaket.Core.Tools;
using Phaket.Services;
using Phaket.ViewModels;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// Settings "Tools" section (Narzędzia): the version pin goes to settings.json through the
// merging Save(), so foreign nodes survive; the release list loads lazily when the combo is
// opened; an unreachable GitHub ends as a localized TOOL_OFFLINE sentence, never as a raw exception.
public class SettingsToolsTests
{
    // ToolProvider names the asset per host OS/arch; the fixture must advertise the same one.
    private static string LegendaryAsset => OperatingSystem.IsWindows()
        ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_windows_arm64.exe" : "legendary_windows_x64.exe"
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_linux_arm64" : "legendary_linux_x64";
    private const string Latest = "Najnowsza (automatycznie)";

    private sealed record Harness(string Root, string SettingsPath, string ToolsRoot, SettingsViewModel Vm) : IDisposable
    {
        public void Dispose()
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            try { System.IO.Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    // Fake GitHub Releases: a canned release list, a stub binary, or a network failure.
    private sealed class Stub : HttpMessageHandler
    {
        public string Releases = "[]";
        public byte[] Body = [];
        public Exception? Failure;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Failure is { } failure
                ? Task.FromException<HttpResponseMessage>(failure)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.RequestUri!.ToString().Contains("/releases", StringComparison.Ordinal)
                        ? new StringContent(Releases, System.Text.Encoding.UTF8, "application/json")
                        : new ByteArrayContent(Body)
                });
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Releases(params (string Tag, byte[] Body, bool Trusted)[] releases) => "[" + string.Join(',', releases.Select(r =>
    {
        string digest = r.Trusted ? "\"sha256:" + Digest(r.Body) + "\"" : "null";
        return $$"""{"tag_name":"{{r.Tag}}","assets":[{"name":"{{LegendaryAsset}}","browser_download_url":"https://asset.test/tool","digest":{{digest}}}]}""";
    })) + "]";

    private static Harness Create(string? initial = null, Stub? stub = null)
    {
        string root = System.IO.Directory.CreateTempSubdirectory("phaket-tools-").FullName;
        string settings = Path.Combine(root, "settings.json");
        string toolsRoot = Path.Combine(root, "tools");
        if (initial is not null) File.WriteAllText(settings, initial);
        var vm = new SettingsViewModel(settings,
            new SettingsViewModel.SecretStoreSeam(new Phaket.Core.Tests.InMemorySecretStore(), root),
            new SettingsViewModel.ToolSeam(new ToolProvider(toolsRoot, new HttpClient(stub ?? new Stub())), toolsRoot));
        return new Harness(root, settings, toolsRoot, vm);
    }

    // What Core/Tools reads back from settings.json: the tag, or "<null>" for latest.
    private static string Pinned(string settingsPath, string tool)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var version = doc.RootElement.GetProperty("tools").GetProperty(tool).GetProperty("version");
        return version.ValueKind == JsonValueKind.String ? version.GetString()! : "<null>";
    }

    [AvaloniaFact]
    public async Task Pinning_a_version_writes_settings_and_keeps_other_nodes()
    {
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        using var h = Create("""{ "language": "en", "catalogEndpoint": "http://catalog.test/", "theme": "dark", "tools": { "legendary": { "version": "0.20.0" } } }""");
        Assert.Equal("0.20.0", h.Vm.Legendary.SelectedVersion);

        h.Vm.Legendary.SelectedVersion = h.Vm.Legendary.LatestLabel;
        Assert.Equal("<null>", Pinned(h.SettingsPath, "legendary"));

        string tag = "0.21.1";
        h.Vm.Legendary.Versions.Add(tag);
        h.Vm.Legendary.SelectedVersion = tag;
        Assert.Equal(tag, Pinned(h.SettingsPath, "legendary"));

        // Merge, not replace: untouched nodes survive; the tool section keeps the shape Core reads.
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(h.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Equal("en", doc.RootElement.GetProperty("language").GetString());
        Assert.Equal("http://catalog.test/", doc.RootElement.GetProperty("catalogEndpoint").GetString());
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        Assert.Equal("<null>", Pinned(h.SettingsPath, "depotdownloader"));
        Assert.Equal(tag, ToolSettings.GetPinnedVersion("legendary", h.SettingsPath));
    }

    [AvaloniaFact]
    public void No_pin_reads_as_latest_and_never_writes_the_file()
    {
        using var h = Create();
        Assert.Equal(h.Vm.Legendary.LatestLabel, h.Vm.Legendary.SelectedVersion);
        Assert.Equal(h.Vm.Legendary.LatestLabel, h.Vm.Legendary.Versions[0]);
        Assert.False(File.Exists(h.SettingsPath));
    }

    [AvaloniaFact]
    public async Task Opening_the_combo_loads_release_tags_and_keeps_the_pin()
    {
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var stub = new Stub { Releases = Releases(("0.21.1", [], true), ("0.20.34", [], true)) };
        using var h = Create("""{ "tools": { "legendary": { "version": "0.20.34" } } }""", stub);
        // Before opening: the latest entry plus the pin read from settings.json.
        Assert.Equal([Latest, "0.20.34"], h.Vm.Legendary.Versions);

        h.Vm.Legendary.OnDropDownOpened();
        if (h.Vm.Legendary.ReloadReleasesCommand.ExecutionTask is { } load) await load;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([Latest, "0.21.1", "0.20.34"], h.Vm.Legendary.Versions);
        Assert.Equal("0.20.34", h.Vm.Legendary.SelectedVersion);
        Assert.Equal("", h.Vm.Legendary.ReleasesStatus);
        Assert.False(h.Vm.Legendary.ReleasesFailed);
        // Reading the list is not a user choice: the file keeps the pin it already had.
        Assert.Equal("0.20.34", Pinned(h.SettingsPath, "legendary"));
    }

    [AvaloniaFact]
    public async Task Unreachable_github_reports_offline_in_the_users_language()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "pl-PL";
        var stub = new Stub { Failure = new HttpRequestException("boom") };
        using var h = Create("""{ "tools": { "legendary": { "version": "0.20.34" } } }""", stub);
        // A dead network must not stop the page from opening or the pin from being shown.
        h.Vm.Legendary.OnDropDownOpened();
        if (h.Vm.Legendary.ReloadReleasesCommand.ExecutionTask is { } load) await load;
        Dispatcher.UIThread.RunJobs();

        Assert.True(h.Vm.Legendary.ReleasesFailed);
        Assert.Equal(ErrorMessages.Format("TOOL_OFFLINE", "pl-PL"), h.Vm.Legendary.ReleasesStatus);
        Assert.Equal([Latest, "0.20.34"], h.Vm.Legendary.Versions);

        await h.Vm.Legendary.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(ErrorMessages.Format("TOOL_OFFLINE", "pl-PL"), h.Vm.Legendary.UpdateStatus);
        Assert.DoesNotContain("at Phaket", h.Vm.Legendary.UpdateStatus, StringComparison.Ordinal);

        loc.CurrentLanguage = "en";
        await h.Vm.Legendary.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(ErrorMessages.Format("TOOL_OFFLINE", "en"), h.Vm.Legendary.UpdateStatus);
    }

    [AvaloniaFact]
    public async Task Check_updates_downloads_a_newer_release_and_reports_it()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "pl-PL";
        byte[] bytes = "tool"u8.ToArray();
        var stub = new Stub { Releases = Releases(("0.21.1", bytes, true)), Body = bytes };
        using var h = Create(stub: stub);
        Assert.Equal(loc["Settings.Tools.NotInstalled"], h.Vm.Legendary.InstalledText);

        await h.Vm.Legendary.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(loc["Settings.Tools.Updated"], h.Vm.Legendary.UpdateStatus);
        Assert.Equal("0.21.1", h.Vm.Legendary.InstalledVersion);
        Assert.Equal(loc["Settings.Tools.Installed"] + " 0.21.1", h.Vm.Legendary.InstalledText);
        Assert.True(File.Exists(Path.Combine(h.ToolsRoot, "legendary", "0.21.1", LegendaryAsset)));

        // Nothing newer the second time: the installed folder means the version is current.
        await h.Vm.Legendary.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(loc["Settings.Tools.UpToDate"], h.Vm.Legendary.UpdateStatus);
    }

    [AvaloniaFact]
    public async Task An_untrusted_release_is_reported_not_downgraded_silently()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "pl-PL";
        var stub = new Stub { Releases = Releases(("0.99.0", [], false)) };
        using var h = Create(stub: stub);
        await h.Vm.Legendary.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(ErrorMessages.Format("TOOL_VERIFICATION_FAILED", "pl-PL"), h.Vm.Legendary.UpdateStatus);
        Assert.Equal(loc["Settings.Tools.NotInstalled"], h.Vm.Legendary.InstalledText);
    }

    private static void Install(string toolsRoot, string tool, string version, string binary)
    {
        System.IO.Directory.CreateDirectory(Path.Combine(toolsRoot, tool, version));
        File.WriteAllText(Path.Combine(toolsRoot, tool, version, binary), "x");
    }

    [AvaloniaFact]
    public void Installed_version_is_read_from_the_tools_folders()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "pl-PL";
        using var h = Create();
        Assert.Equal("", h.Vm.Legendary.InstalledVersion);
        Assert.Equal(loc["Settings.Tools.NotInstalled"], h.Vm.Legendary.InstalledText);

        // ".stage-*" is a Core internal, not a version; Depot counts only when its binary is there.
        System.IO.Directory.CreateDirectory(Path.Combine(h.ToolsRoot, "legendary", ".stage-temp"));
        System.IO.Directory.CreateDirectory(Path.Combine(h.ToolsRoot, "depotdownloader", "DepotDownloader_3.5.0"));
        Install(h.ToolsRoot, "legendary", "0.21.1", LegendaryAsset);
        Install(h.ToolsRoot, "legendary", "0.9.0", LegendaryAsset);
        Install(h.ToolsRoot, "depotdownloader", "DepotDownloader_3.10.0", OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader");
        Install(h.ToolsRoot, "depotdownloader", "DepotDownloader_3.4.0", OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader");

        // The folder scan runs when the section is used, not by watching the file system.
        h.Vm.Legendary.OnDropDownOpened();
        h.Vm.Depot.OnDropDownOpened();
        Assert.Equal("0.21.1", h.Vm.Legendary.InstalledVersion);
        Assert.Equal(loc["Settings.Tools.Installed"] + " 0.21.1", h.Vm.Legendary.InstalledText);
        Assert.Equal("DepotDownloader_3.10.0", h.Vm.Depot.InstalledVersion);
        Assert.Equal(Latest, h.Vm.Legendary.SelectedVersion);
    }

    [AvaloniaFact]
    public async Task Offline_state_offers_a_retry_that_loads_the_list_once_the_network_is_back()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "pl-PL";
        var stub = new Stub { Failure = new HttpRequestException("boom") };
        using var h = Create(stub: stub);
        h.Vm.SelectSectionCommand.Execute("tools");
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.SettingsView { DataContext = h.Vm } };
        window.Show();
        try
        {
            h.Vm.Legendary.OnDropDownOpened();
            if (h.Vm.Legendary.ReloadReleasesCommand.ExecutionTask is { } load) await load;
            Dispatcher.UIThread.RunJobs();

            var retry = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Command == h.Vm.Legendary.ReloadReleasesCommand);
            Assert.True(retry.IsVisible);
            Assert.Equal(loc["Settings.Tools.Retry"], AutomationProperties.GetName(retry));
            System.IO.Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            frame!.Save("/tmp/opencode/phaket-render/10-settings-tools-offline.png", PngBitmapEncoderOptions.Default);

            // The retry is the same command the button calls: with a working network the list appears.
            stub.Failure = null;
            stub.Releases = Releases(("0.21.1", [], true));
            await h.Vm.Legendary.ReloadReleasesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([Latest, "0.21.1"], h.Vm.Legendary.Versions);
            Assert.Equal("", h.Vm.Legendary.ReleasesStatus);
            Assert.False(h.Vm.Legendary.ReleasesFailed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Tools_section_is_rendered_with_labels_for_screen_readers()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        loc.CurrentLanguage = "pl-PL";
        using var h = Create();
        h.Vm.SelectSectionCommand.Execute("tools");
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.SettingsView { DataContext = h.Vm } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(loc["Settings.Tools.Title"], window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Equal("tools", h.Vm.SelectedSection);
            Assert.True(h.Vm.IsToolsSection);
            var combos = window.GetVisualDescendants().OfType<ComboBox>()
                .Where(c => c.DataContext is ToolSettingsSection).ToArray();
            Assert.Equal(2, combos.Length);
            foreach (var combo in combos)
            {
                var section = (ToolSettingsSection)combo.DataContext!;
                Assert.Equal(section.Title, AutomationProperties.GetName(combo));
                Assert.True(combo.Focusable);
            }
            var checks = window.GetVisualDescendants().OfType<Button>()
                .Where(b => Equals(b.Content as string, loc["Settings.Tools.CheckUpdates"])).ToArray();
            Assert.Equal(2, checks.Length);
            Assert.All(checks, b => Assert.Equal(loc["Settings.Tools.CheckUpdates"], AutomationProperties.GetName(b)));
            System.IO.Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            frame!.Save("/tmp/opencode/phaket-render/09-settings-tools.png", PngBitmapEncoderOptions.Default);
        }
        finally
        {
            loc.CurrentLanguage = previous;
            window.Close();
        }
    }
}
