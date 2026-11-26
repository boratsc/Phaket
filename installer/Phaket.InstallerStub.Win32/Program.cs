using System.Globalization;
using System.Text;

namespace Phaket.InstallerStub.Launcher;

/// <summary>Cienki launcher: wybiera UI (okno Win32 albo konsola) i woła wspólną logikę instalacji.</summary>
internal static class Program
{
    // Wybór UI (PAK-114): okno Win32 przez P/Invoke — działa w publish AOT bez WinForms/WinUI,
    // więc nie ciągnie frameworka UI i plik zostaje <5 MB. Fallback: konsola (sesja bez pulpitu).
    public static async Task<int> Main()
    {
        var pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pl", StringComparison.OrdinalIgnoreCase);
        var options = StubOptions.FromEnvironment();
        using var log = OpenLog();

        if (Environment.UserInteractive && OperatingSystem.IsWindows())
        {
            using var window = new ProgressUi(pl);
            window.Start();
            if (!window.WindowFailed)
                return await InstallRunner.RunAsync(window, options, log);
        }

        if (Console.IsOutputRedirected) return InstallRunner.Failed;   // bez okna i bez konsoli nie ma jak pokazać błędu
        return await InstallRunner.RunAsync(
            new ConsoleUi(pl) { Output = Console.Out, Error = Console.Error }, options, log);
    }

    // Log w %TEMP% (nie w katalogu instalacji, który aktualizacje podmieniają).
    private static TextWriter? OpenLog()
    {
        try
        {
            return new StreamWriter(Path.Combine(Path.GetTempPath(), "PhaketSetup.log"), false, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}