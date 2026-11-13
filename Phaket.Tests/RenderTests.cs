using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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

    [AvaloniaFact]
    public void Render_all_screens()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Dispatcher.UIThread.RunJobs();
        Save(window, "01-library.png");

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
