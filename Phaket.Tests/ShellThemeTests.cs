using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

public class ShellThemeTests
{
    [AvaloniaFact]
    public void Shell_and_all_new_empty_screens_render()
    {
        var vm = TestAccounts.ViewModel();
        var previousLanguage = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var window = new MainWindow(vm) { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<ModpackCatalogViewModel>(vm.CurrentPage);
            using var initial = window.CaptureRenderedFrame();
            Assert.NotNull(initial);
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            initial.Save("/tmp/opencode/phaket-render/40-version-bump.png", PngBitmapEncoderOptions.Default);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "phaket 0.1.0");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Czeladzki Saturn");

            foreach (var (target, expected) in new (string, Type)[]
            {
                ("discover", typeof(DiscoverViewModel)),
                ("communities", typeof(CommunitiesViewModel)),
                ("downloads", typeof(DownloadsViewModel)),
                ("catalog", typeof(ModpackCatalogViewModel))
            })
            {
                vm.NavigateCommand.Execute(target);
                Dispatcher.UIThread.RunJobs();
                Assert.IsType(expected, vm.CurrentPage);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
            }
        }
        finally
        {
            window.Close();
            LocalizationService.Instance.CurrentLanguage = previousLanguage;
        }
    }
}
