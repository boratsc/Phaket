using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Pakhet;

[assembly: AvaloniaTestApplication(typeof(Pakhet.Tests.TestAppBuilder))]

namespace Pakhet.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_opens_and_exposes_accessible_name()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("Pakhet", window.Title);
        // Czytniki ekranu widzą tytuł okna przez automation peer (UIA/AT-SPI).
        Assert.Equal("Pakhet", new WindowAutomationPeer(window).GetName());

        window.Close();
    }
}
