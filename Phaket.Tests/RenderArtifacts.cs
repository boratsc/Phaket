using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

// Rendered frames are debug artifacts, not fixtures: several test classes run in parallel and
// used to write the same file name, so a collision failed a test instead of the logic under it.
// Every name carries the process id and a counter; the readable part stays first for a human
// browsing the folder.
internal static class RenderArtifacts
{
    public static readonly string Directory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phaket-render");
    private static readonly string Run = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static int counter;

    // "45-conflict-decision-pl-PL.png" -> "45-conflict-decision-pl-PL--4821-1.png"
    public static string Path(string name) => System.IO.Path.Combine(Directory,
        $"{System.IO.Path.GetFileNameWithoutExtension(name)}--{Run}-{Interlocked.Increment(ref counter)}.png");

    public static string Save(Window window, string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path(name);
        frame!.Save(path, PngBitmapEncoderOptions.Default);
        return path;
    }
}