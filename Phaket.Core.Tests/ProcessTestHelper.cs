using System.Diagnostics;

namespace Phaket.Core.Tests;

// The long-running helper the process tests spawn (PAK-143). One process, never a shell wrapper:
// a Windows `cmd /c` would keep its child alive after TerminateProcess, so the "stop ended it"
// assertion would see the surviving child under the tracked PID.
internal static class ProcessTestHelper
{
    // Builds the helper start info for the target OS; neither form needs a shell.
    internal static ProcessStartInfo StartInfo(bool windows) => windows
        ? new ProcessStartInfo("ping", "-n 300 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("sleep", "300") { UseShellExecute = false };

    // True when the helper is a single process: a shell wrapper's `/c` and redirection would hide
    // the child that actually waits, so the tracked PID would die while the wait keeps running.
    internal static bool IsSingleProcess(string fileName, string arguments, bool windows) => windows
        ? !fileName.Contains("cmd", StringComparison.OrdinalIgnoreCase)
          && !arguments.Contains('>', StringComparison.Ordinal)
          && !arguments.Contains("/c", StringComparison.OrdinalIgnoreCase)
        : fileName != "sh" && !arguments.Contains("-c", StringComparison.Ordinal);

    internal static Process Start() =>
        Process.Start(StartInfo(OperatingSystem.IsWindows()))!;
}

public sealed class ProcessTestHelperTests
{
    // Windows itself is only exercised by CI, so the shape of the start info is asserted here.
    [Fact]
    public void WindowsHelperIsASingleProcessWithoutAShellWrapper()
    {
        var info = ProcessTestHelper.StartInfo(windows: true);

        Assert.Equal("ping", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(ProcessTestHelper.IsSingleProcess(info.FileName, info.Arguments, windows: true));
    }

    [Theory]
    [InlineData("cmd.exe", "/c ping -n 300 127.0.0.1 >nul", false)]
    [InlineData("cmd.exe", "/c ping -n 300 127.0.0.1", false)]
    [InlineData("ping", "-n 300 127.0.0.1 >nul", false)]
    [InlineData("ping", "-n 300 127.0.0.1", true)]
    [InlineData("sleep", "300", true)]
    public void TheWrapperShapeIsRecognised(string fileName, string arguments, bool single) =>
        Assert.Equal(single, ProcessTestHelper.IsSingleProcess(fileName, arguments, windows: true));

    [Fact]
    public void PosixHelperIsASingleProcess()
    {
        var info = ProcessTestHelper.StartInfo(windows: false);

        Assert.Equal("sleep", info.FileName);
        Assert.True(ProcessTestHelper.IsSingleProcess(info.FileName, info.Arguments, windows: false));
    }
}