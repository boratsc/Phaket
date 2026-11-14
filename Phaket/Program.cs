using Avalonia;

namespace Phaket;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before Main is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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
