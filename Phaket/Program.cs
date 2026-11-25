using Avalonia;
using Velopack;

namespace Phaket;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before any Avalonia or app initialization: Velopack handles the installer
        // hooks (first run, update, uninstall) and exits the process when invoked as a hook.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();

        // Natywny Wayland (Avalonia 12.1+) jest eksperymentalny i nie ma jeszcze backendu AT-SPI,
        // więc domyślnie X11/XWayland z pełną dostępnością. Opt-in: PHAKET_NATIVE_WAYLAND=1;
        // UseWaylandWithFallback wraca do X11, gdy kompozytor Wayland jest niedostępny.
        if (OperatingSystem.IsLinux() &&
            Environment.GetEnvironmentVariable("PHAKET_NATIVE_WAYLAND") == "1")
        {
            builder = builder.UseWaylandWithFallback();
        }

        return builder.WithInterFont().LogToTrace();
    }
}
