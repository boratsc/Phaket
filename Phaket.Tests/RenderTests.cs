using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Renders the PoC screens at the target size for visual review (saved to
// /tmp/opencode/phaket-render). Not an assertion suite — visual verification.
public class RenderTests
{
    private static void Save(Window window, string name)
    {
        var dir = "/tmp/opencode/phaket-render";
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Save(Path.Combine(dir, name));
    }

    [AvaloniaTheory]
    [InlineData("mods", "05-mods-from-library.png")]
    [InlineData("profiles", "06-profiles-from-library.png")]
    [InlineData("help", "08-help.png")]
    [InlineData("account", "15-account.png")]
    public void Render_sidebar_navigation_from_library(string target, string filename)
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.CommandParameter, target));
        Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(target, vm.ActiveNav);
        if (target == "mods") Assert.IsType<ModsViewModel>(vm.CurrentPage);
        else if (target == "profiles") Assert.IsType<ProfilesViewModel>(vm.CurrentPage);
        else if (target == "help") Assert.IsType<HelpViewModel>(vm.CurrentPage);
        else Assert.IsType<AccountsViewModel>(vm.CurrentPage);
        Save(window, filename);
        if (target == "mods") vm.Mods!.BackCommand.Execute(null);
        if (target == "profiles") vm.Profiles!.BackCommand.Execute(null);
        if (target != "help" && target != "account")
        {
            Assert.IsType<GameViewModel>(vm.CurrentPage);
            Assert.Equal("library", vm.ActiveNav);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Render_all_screens()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        Save(window, "01-library.png");

        vm.Library.OpenGameCommand.Execute(vm.Library.Games.First());
        Dispatcher.UIThread.RunJobs();
        Save(window, "02-game.png");

        vm.OpenMods();
        Dispatcher.UIThread.RunJobs();
        Save(window, "05-mods.png");

        vm.OpenProfiles();
        Dispatcher.UIThread.RunJobs();
        Save(window, "06-profiles.png");

        vm.OpenBackup();
        vm.Backup!.CreateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Save(window, "07-backup.png");

        vm.OpenCompatibility();
        Dispatcher.UIThread.RunJobs();
        Save(window, "09-compatibility.png");

        vm.OpenInstall();
        Dispatcher.UIThread.RunJobs();
        Save(window, "10-install.png");

        vm.OpenBuilds();
        Dispatcher.UIThread.RunJobs();
        Save(window, "11-builds.png");

        vm.OpenShare();
        Dispatcher.UIThread.RunJobs();
        Save(window, "12-share.png");

        vm.OpenGraph();
        Dispatcher.UIThread.RunJobs();
        Save(window, "13-graph.png");

        vm.OpenReport();
        Dispatcher.UIThread.RunJobs();
        Save(window, "14-report.png");

        vm.NavigateCommand.Execute("studio");
        vm.RecipeStudio.AddStepCommand.Execute("archive.extract");
        vm.RecipeStudio.AddStepCommand.Execute("file.copy");
        vm.RecipeStudio.ValidateCommand.Execute(null);
        vm.RecipeStudio.PlanCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Save(window, "02-studio.png");

        vm.RecipeStudio.SwitchViewCommand.Execute(null);
        vm.RecipeStudio.SaveYamlCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Save(window, "03-studio-yaml.png");

        vm.NavigateCommand.Execute("settings");
        Dispatcher.UIThread.RunJobs();
        Save(window, "04-settings.png");

        window.Close();
    }
}
