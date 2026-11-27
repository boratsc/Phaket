using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Accounts;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-144: the pin is visible in the library, a store update raises a localized
// warning, and the duplicate report lists copies without offering deletion.
public class LibraryPinTests
{
    private sealed class EpicAccount : IAccountService
    {
        public string Provider => "epic";
        public AccountInfo? Current => null;
        public event Action? AccountChanged { add { } remove { } }
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default) => Task.FromResult(new AccountInfo("epic", "1", "n", null, DateTimeOffset.UtcNow));
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static LibraryViewModel Create() => new(_ => { }, new EpicAccount(), null,
        _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([]), () => []);

    private static SteamInstallation Install(string root, string appId, string buildId, string manifestId = "555", long lastUpdated = 0, bool flatpak = false)
    {
        Directory.CreateDirectory(root);
        return new SteamInstallation(new Installation("steam-" + appId, "among-us-steam", root),
            new SteamAppManifest(appId, "Among Us", buildId, "public", manifestId, "Among Us", 1, null, lastUpdated),
            new SteamLibrary(root, flatpak), GameAvailability.Moddable);
    }

    private static InstalledRecipe Entry(string gameRoot, string build = "12345678", string manifest = "555") =>
        new("test.recipe", "1.0.0", "among-us", "steam", gameRoot, gameRoot + "-profile", gameRoot + "-staging",
            gameRoot + "-state", null, DateTimeOffset.UnixEpoch, PinnedAt: DateTimeOffset.UtcNow,
            PinnedBuildId: build, PinnedManifestId: manifest);

    [AvaloniaFact]
    public void PinBadgeAndBannerTextsComeFromLocalizedResources()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        try
        {
            loc.CurrentLanguage = "pl-PL";
            Assert.Equal("Wersja przypięta", loc["Ui.Library.PinBadge"]);
            Assert.Equal("Wersja przypięta", loc["Pin.Badge"]);
            Assert.Contains("Steam zaktualizował", loc["Pin.UpdateTitle"]);
            Assert.Contains("{0}", loc["Pin.UpdateChanged"]);
            Assert.Contains("{1}", loc["Pin.UpdateChanged"]);
            Assert.Equal("Później", loc["Pin.Later"]);
            loc.CurrentLanguage = "en";
            Assert.Equal("Version pinned", loc["Ui.Library.PinBadge"]);
            Assert.Equal("Later", loc["Pin.Later"]);
            Assert.DoesNotContain("Usuń", loc["Duplicates.Explanation"]);
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void PinnedGameShowsBadgeAndToggleLabelInTheCard()
    {
        var game = new GameCatalogItem(new GameDefinition("among-us", []), "", "");
        Assert.Equal(LocalizationService.Instance["Pin.Pin"], game.PinLabel);
        game.IsVersionPinned = true;
        Assert.Equal(LocalizationService.Instance["Pin.Unpin"], game.PinLabel);
    }

    [AvaloniaFact]
    public void VersionChangeOnAPinnedInstallationIsAWarning()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pin-status-").FullName;
        try
        {
            var detected = Install(root, "945360", "87654321");
            var changed = Assert.IsType<PinStatus.VersionChanged>(VersionPin.Check(Entry(detected.Installation.RootPath), detected.Manifest));
            Assert.Equal("12345678", changed.From);
            Assert.Equal("87654321", changed.To);
            Assert.IsType<PinStatus.Ok>(VersionPin.Check(Entry(detected.Installation.RootPath, "87654321", "555"), detected.Manifest));
            Assert.True(VersionPin.Check(Entry(detected.Installation.RootPath), null).NeedsAttention);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void LibraryScanPinsTheCardAndWarnsWhenSteamUpdatedTheGame()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pin-scan-").FullName;
        try
        {
            var gameRoot = Path.Combine(root, "Among Us");
            var vm = Create();
            vm.UseRegistry(Path.Combine(root, "installs.json"));
            var game = vm.Games.First(g => g.Definition.Slug == "among-us");
            var installation = Install(gameRoot, game.SteamAppId, "87654321");
            new InstallRegistry(Path.Combine(root, "installs.json")).Add(Entry(gameRoot));

            // The same component wiring the scan uses: detect, pin badge, then the banner.
            game.Detected = installation;
            game.IsVersionPinned = true;
            Assert.True(game.IsVersionPinned);

            var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.LibraryView { DataContext = vm } };
            window.Show();
            vm.HasPinUpdateWarning = true;
            vm.PinUpdateMessage = string.Format(LocalizationService.Instance["Pin.UpdateChanged"], "12345678", "87654321");
            Dispatcher.UIThread.RunJobs();
            var later = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, vm.DismissPinWarningCommand));
            Assert.True(later.IsEffectivelyVisible);
            later.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.HasPinUpdateWarning);
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void LibraryRendersPinWarningAt1280x800()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pin-render-").FullName;
        try
        {
            var vm = Create();
            var game = vm.Games.First(g => g.Definition.Slug == "among-us");
            game.Detected = Install(Path.Combine(root, "Among Us"), game.SteamAppId, "87654321");
            game.IsVersionPinned = true;
            vm.HasPinUpdateWarning = true;
            vm.PinUpdateMessage = string.Format(LocalizationService.Instance["Pin.UpdateChanged"], "12345678", "87654321");
            var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.LibraryView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory(RenderArtifacts.Directory);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(RenderArtifacts.Path("pin-warning-1280x800.png"), PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void DuplicateReportListsCopiesAndHasNoRemovalCommand()
    {
        var (a, b) = (Directory.CreateTempSubdirectory("phaket-dup-a-").FullName, Directory.CreateTempSubdirectory("phaket-dup-b-").FullName);
        try
        {
            var opened = new List<string>();
            var diagnostics = new DuplicateDiagnosticsViewModel
            {
                Scanner = () => SteamAdapter.GroupDuplicates([
                    Install(Path.Combine(a, "Among Us"), "945360", "1", lastUpdated: 10),
                    Install(Path.Combine(b, "Among Us"), "945360", "1", lastUpdated: 20, flatpak: true)]),
                Opener = opened.Add,
            };
            diagnostics.ScanDuplicatesCommand.Execute(null);
            var group = Assert.Single(diagnostics.Groups);
            Assert.Equal("Among Us", group.Title);
            Assert.Equal(string.Format(LocalizationService.Instance["Duplicates.Found"], 1), diagnostics.Status);
            var recent = Assert.Single(group.Copies, c => c.IsMostRecent);
            Assert.Equal(Path.Combine(b, "Among Us"), recent.RootPath);
            Assert.Contains("Flatpak", group.Copies.Single(c => c.IsFlatpak).Label);
            // Only navigation: the view model exposes no deletion command at all.
            Assert.Equal(["OpenInstallFolderCommand", "ScanDuplicatesCommand"],
                diagnostics.GetType().GetProperties().Where(p => p.PropertyType.Name.StartsWith("IRelayCommand", StringComparison.Ordinal))
                    .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

            diagnostics.OpenInstallFolderCommand.Execute(recent);
            Assert.Equal([recent.RootPath], opened);
            Assert.True(Directory.Exists(recent.RootPath)); // opening never removes anything
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [AvaloniaFact]
    public void DuplicateReportWithNothingFoundIsLocalized()
    {
        var previous = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var diagnostics = new DuplicateDiagnosticsViewModel { Scanner = () => [] };
            diagnostics.ScanDuplicatesCommand.Execute(null);
            Assert.Empty(diagnostics.Groups);
            Assert.Equal(LocalizationService.Instance["Duplicates.None"], diagnostics.Status);
            LocalizationService.Instance.CurrentLanguage = "en";
            Assert.Equal("No duplicate games in Steam libraries.", LocalizationService.Instance["Duplicates.None"]);
        }
        finally { LocalizationService.Instance.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void GuidesRenderTheDuplicateSectionWithoutDeleteButtons()
    {
        var (a, b) = (Directory.CreateTempSubdirectory("phaket-dup-guide-a-").FullName, Directory.CreateTempSubdirectory("phaket-dup-guide-b-").FullName);
        try
        {
            var diagnostics = new DuplicateDiagnosticsViewModel
            {
                Scanner = () => SteamAdapter.GroupDuplicates([
                    Install(Path.Combine(a, "Among Us"), "945360", "1", lastUpdated: 1),
                    Install(Path.Combine(b, "Among Us"), "945360", "1", lastUpdated: 2)]),
                Opener = _ => { },
            };
            var vm = new HelpViewModel(new GameCatalogItem(new GameDefinition("among-us", []), "", "")) { Diagnostics = diagnostics };
            diagnostics.ScanDuplicatesCommand.Execute(null);
            var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.HelpView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                Assert.True(buttons.Count > 0);
                Assert.Contains(buttons, b2 => b2.Command is CommunityToolkit.Mvvm.Input.IRelayCommand c && Equals(c, diagnostics.ScanDuplicatesCommand));
                // The per-copy "open folder" command is the one the list template binds; the list
                // renders lazily in headless, so its presence is asserted on the view model above.
                Assert.Equal(LocalizationService.Instance["Duplicates.Explanation"], Assert.Single(diagnostics.Groups).Hint);
                Assert.DoesNotContain(buttons, b2 => (b2.Content?.ToString() ?? "").Contains("Usuń", StringComparison.OrdinalIgnoreCase));
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }
}