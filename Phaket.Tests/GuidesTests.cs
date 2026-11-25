using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Phaket.Controls;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public class GuidesTests
{
    [AvaloniaFact]
    public void Guide_content_is_localized_and_game_specific()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        try
        {
            foreach (var language in new[] { "pl-PL", "en" })
            {
                loc.CurrentLanguage = language;
                var all = new HelpViewModel().AllGuides;
                Assert.Equal(5, all.Count);
                Assert.All(all, guide => Assert.NotEmpty(guide.Steps));
                var vm = new HelpViewModel(Game("valheim"));
                Assert.Equal(new[] { "Guides.General.ProfilePackage", "Guides.General.Compatibility", "Guides.Valheim.FirstSetup" }, vm.Guides.Select(g => g.Id).ToArray());
                Assert.Equal(4, vm.Guides.Single(guide => guide.Id == "Guides.Valheim.FirstSetup").Steps.Count);
                vm.Game = Game("among-us");
                Assert.Equal(new[] { "Guides.General.ProfilePackage", "Guides.General.Compatibility", "Guides.AmongUs.TownOfUs", "Guides.AmongUs.CleanGame" }, vm.Guides.Select(g => g.Id).ToArray());
                Assert.Equal(4, vm.Guides.Single(guide => guide.Id == "Guides.AmongUs.TownOfUs").Steps.Count);

                var keys = all.SelectMany(guide => new[] { $"{guide.Id}.Title", $"{guide.Id}.Summary" }
                    .Concat(guide.Steps.Select((_, i) => $"{guide.Id}.Step{i + 1}")))
                    .Concat(new[] { "Guides.Title", "Guides.EmptyTitle", "Guides.EmptyMessage", "Guides.EmptyList", "Guides.Search" })
                    .Concat(new[] { "Profile", "Compatibility", "GitHubRelease", "RecipeStudio" }.SelectMany(name => new[] { $"HelpTip.{name}.Label", $"HelpTip.{name}.Content" }));
                Assert.All(keys, key => Assert.Contains(key, loc.Keys(language)));
                Assert.DoesNotContain(keys, key => string.IsNullOrWhiteSpace(loc[key]) || loc[key] == key);
            }
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void Help_view_renders_article_and_changes_with_shell_game_selection()
    {
        var shell = TestAccounts.ViewModel();
        var valheim = shell.Library.Games.First(game => game.Definition.Slug == "valheim");
        var amongUs = shell.Library.Games.First(game => game.Definition.Slug == "among-us");
        shell.SelectedGame = valheim;
        shell.NavigateCommand.Execute("help");
        var window = new MainWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<HelpView>().Single();
            var vm = (HelpViewModel)view.DataContext!;
            Assert.Equal("valheim", vm.Game?.Definition.Slug);
            Assert.Contains(vm.SelectedGuide!.Title, window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            Assert.Equal(3, vm.SelectedGuide.Steps.Count);

            shell.SelectedGame = amongUs;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("among-us", vm.Game?.Definition.Slug);
            Assert.Contains("Guides.AmongUs.TownOfUs", vm.Guides.Select(guide => guide.Id));
            Assert.DoesNotContain("Guides.Valheim.FirstSetup", vm.Guides.Select(guide => guide.Id));

            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            frame!.Save("/tmp/opencode/phaket-render/23-guides.png", PngBitmapEncoderOptions.Default);
            var article = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text == vm.SelectedGuide.Title && Avalonia.Automation.AutomationProperties.GetHeadingLevel(text) == 2);
            Assert.Equal(vm.SelectedGuide.Title, article.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Help_tip_opens_on_focus_and_hover_and_dismisses_on_escape()
    {
        var tip = new HelpTip { Label = "Label", Message = "Helpful description" };
        var window = new Window { Width = 400, Height = 240, Content = new StackPanel { Children = { tip } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var trigger = tip.FindControl<Button>("Trigger")!;
            var popup = tip.FindControl<Avalonia.Controls.Primitives.Popup>("TipPopup")!;
            Assert.Equal("Label", AutomationProperties.GetName(trigger));
            Assert.Equal("Helpful description", AutomationProperties.GetHelpText(trigger));

            trigger.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            var escape = new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent };
            trigger.RaiseEvent(escape);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.True(trigger.IsFocused);

            var pointerEntered = new PointerEventArgs(InputElement.PointerEnteredEvent, trigger, null!, trigger, new Avalonia.Point(2, 2), 0, new PointerPointProperties(), KeyModifiers.None);
            trigger.RaiseEvent(pointerEntered);
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);

            pointerEntered = new PointerEventArgs(InputElement.PointerExitedEvent, trigger, null!, trigger, new Avalonia.Point(40, 40), 0, new PointerPointProperties(), KeyModifiers.None);
            trigger.RaiseEvent(pointerEntered);
            Assert.True(popup.IsOpen);
            var popupKey = new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent };
            trigger.RaiseEvent(popupKey);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    private static GameCatalogItem Game(string slug) => new(GameDefinitions.LoadAll().First(definition => definition.Slug == slug), "", "");
}
