using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Models;
using Phaket.Controls;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class RestyleTests
{
    private static GameCatalogItem Game() => new(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");

    [AvaloniaFact]
    public void Library_has_game_list_and_detail_without_banner_or_status_badges()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var vm = new LibraryViewModel(_ => { }, TestAccounts.Create(), null);
        var unsupported = new GameCatalogItem(
            new Phaket.Core.Omrl.GameDefinition("epic-owned-render-test", [], Title: new Dictionary<string, string> { ["pl-PL"] = "Nieobsługiwana gra", ["en"] = "Unsupported game" }), "", "")
        { EpicOwned = true };
        vm.Games.Add(unsupported);
        vm.SelectedGame = unsupported;
        var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), list => list.ItemsSource == vm.Games);
            Assert.Contains(window.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("detailPane"));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == LocalizationService.Instance["Ui.Library.Unsupported"]);
            using var frame = window.CaptureRenderedFrame();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            frame!.Save("/tmp/opencode/phaket-render/36-library.png", PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
            LocalizationService.Instance.CurrentLanguage = language;
        }
    }

    [AvaloniaFact]
    public void Global_checkbox_style_is_loaded_and_has_gold_focus_accent()
    {
        string stylePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Phaket/Styles/Components.axaml"));
        string styles = File.ReadAllText(stylePath);
        Assert.Contains("Selector=\"CheckBox\"", styles, StringComparison.Ordinal);
        Assert.Contains("Selector=\"CheckBox:focus-visible\"", styles, StringComparison.Ordinal);
        Assert.Contains("DynamicResource GoldBrush", styles, StringComparison.Ordinal);
        Assert.True(Application.Current!.Resources.ContainsKey("SystemAccentColor"));
    }

    [AvaloniaFact]
    public void Footer_version_and_codename_come_from_one_source()
    {
        // PAK-91: MainWindow.axaml must not repeat the csproj version or the codename.
        string xaml = File.ReadAllText(Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Phaket/MainWindow.axaml")));
        Assert.DoesNotContain("phaket 0.1.0", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Czeladzki Saturn", xaml, StringComparison.Ordinal);

        var vm = TestAccounts.ViewModel();
        var window = new MainWindow(vm);
        window.Show();
        var footer = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(vm.VersionText, footer);
        Assert.Contains(vm.AppCodename, footer);
        // The version follows the assembly (csproj <Version>), never a literal in the view.
        Assert.Equal("phaket " + typeof(MainWindowViewModel).Assembly.GetName().Version!.ToString(3), vm.VersionText);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_contains_secret_store_help_tip()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var directory = TestAccounts.TempPath();
        try
        {
            var settings = new SettingsViewModel(Path.Combine(directory, "settings.json"));
            settings.SelectSectionCommand.Execute("secrets");
            var window = new Window { Width = 1280, Height = 800, Content = new SettingsView { DataContext = settings } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var tip = Assert.Single(window.GetVisualDescendants().OfType<HelpTip>(),
                    item => item.Label == LocalizationService.Instance["HelpTip.Secrets.Label"]);
                Assert.Equal(LocalizationService.Instance["HelpTip.Secrets.Content"], tip.Message);
                using var frame = window.CaptureRenderedFrame();
                Directory.CreateDirectory("/tmp/opencode/phaket-render");
                frame!.Save("/tmp/opencode/phaket-render/37-settings.png", PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task GitHub_picker_wiring_pins_repository_tag_asset_into_modpack()
    {
        var directory = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(directory);
            var pack = new ModpackDefinition { Id = "picker-pack", Name = "Picker pack", Author = "Test", GameId = "among-us", Version = "1.0.0" };
            store.Save(pack);
            GitHubReleaseService.ClearCache();
            using var service = new GitHubReleaseService(new HttpClient(new ReleaseHandler()));
            var vm = new ModPackViewModel(pack, store, () => { }, directory, gitHubService: service);
            vm.SelectAddSourceCommand.Execute("github");
            Assert.NotNull(vm.GitHubPicker);
            vm.GitHubPicker!.RepositoryInput = "owner/repo";
            await vm.GitHubPicker.SearchCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            vm.GitHubPicker.SelectedRelease = vm.GitHubPicker.Releases.Single();
            vm.GitHubPicker.SelectedAsset = vm.GitHubPicker.Assets.Single();
            vm.GitHubPicker.PinSelected();

            Assert.True(vm.AddPinnedSourceCommand.CanExecute(null));
            vm.SourceModName = "Mod A";
            vm.AddPinnedSourceCommand.Execute(null);

            var pinned = Assert.Single(store.ForGame("among-us")).Items.Single();
            Assert.Equal("github", pinned.Source);
            Assert.Equal("Mod A", pinned.Name);
            Assert.Equal("v1.2.3", pinned.Version);
            Assert.Equal("owner/repo", pinned.Repository);
            Assert.Equal("v1.2.3", pinned.Tag);
            Assert.Equal("77", pinned.AssetId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":1,"tag_name":"v1.2.3","name":"Release","prerelease":false,"draft":false,"assets":[{"id":77,"name":"mod.zip","size":128,"browser_download_url":"https://github.com/owner/repo/releases/download/v1.2.3/mod.zip"}]}]""", System.Text.Encoding.UTF8, "application/json")
            });
    }
}
