using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Services;
using Phaket.ViewModels;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// The explanations that used to sit under every settings field belong here instead:
// secrets, the (unneeded) Steam key, external tools and the catalog server.
public class HelpContentTests
{
    [AvaloniaTheory]
    [InlineData("pl-PL")]
    [InlineData("en")]
    public void Help_page_shows_localized_empty_state_without_a_selected_game(string language)
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
            Assert.Contains(loc["Guides.Title"], texts);
            Assert.Contains(loc["Guides.EmptyTitle"], texts);
            Assert.Contains(loc["Guides.EmptyMessage"], texts);
            Assert.DoesNotContain(texts, text => text!.Contains("dev/apikey", StringComparison.Ordinal));

            Directory.CreateDirectory(RenderArtifacts.Directory);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            frame!.Save(RenderArtifacts.Path("help-empty.png"), PngBitmapEncoderOptions.Default);
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
