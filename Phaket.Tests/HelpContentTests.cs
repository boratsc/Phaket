using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// The explanations that used to sit under every settings field belong here instead:
// secrets, the (unneeded) Steam key, external tools and the catalog server.
public class HelpContentTests
{
    [AvaloniaTheory]
    [InlineData("pl-PL")]
    [InlineData("en")]
    public void Help_page_covers_secrets_steam_key_tools_and_catalog(string language)
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        loc.CurrentLanguage = language;
        var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.HelpView { DataContext = new HelpViewModel() } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet(StringComparer.Ordinal);
            foreach (string key in new[]
            {
                "Help.SecretsTitle", "Help.SteamKeyTitle", "Help.ToolsTitle", "Help.CatalogTitle",
                "Help.Secrets", "Help.SecretsMigration", "Help.SecretsFallback", "Help.SteamKey",
                "Help.Tools", "Help.Catalog", "Help.CatalogEndpoint"
            })
                Assert.Contains(loc[key], texts);

            // Licenses verified against the upstream repositories on 2026-11-15.
            Assert.Contains(loc["Help.ToolsLegendary"], texts);
            Assert.Contains(loc["Help.ToolsDepotDownloader"], texts);
            Assert.Contains("GPL v3", loc["Help.ToolsLegendary"]);
            Assert.Contains("GPL v2", loc["Help.ToolsDepotDownloader"]);
            Assert.DoesNotContain(texts, t => t!.Contains("dev/apikey", StringComparison.Ordinal));

            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Avalonia.Point(0, scroll.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            frame!.Save("/tmp/opencode/phaket-render/help-bottom.png");
        }
        finally
        {
            loc.CurrentLanguage = previous;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_no_longer_shows_a_steam_api_key_field()
    {
        var window = new MainWindow(TestAccounts.ViewModel()) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            ((MainWindowViewModel)window.DataContext!).NavigateCommand.Execute("settings");
            Dispatcher.UIThread.RunJobs();
            var loc = LocalizationService.Instance;
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(loc["Settings.SteamKey"], texts);
            Assert.Contains(loc["Settings.Catalog.Title"], texts);
            Assert.Contains(loc["Settings.MoreInHelp"], window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string));
        }
        finally { window.Close(); }
    }
}
