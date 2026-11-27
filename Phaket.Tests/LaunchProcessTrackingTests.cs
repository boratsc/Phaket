using System.Diagnostics;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-143 integration: a direct launch is recorded with PID + start time and can be
// stopped; steam:// leaves no record; the view model stops fail-closed.
// Tests only ever spawn and stop their own helper process, never a real game.
// The helper is a POSIX `sleep` or a Windows `cmd /c ping -n 300`; both paths of ProcessTracker
// (pidfd/signal and OpenProcess/GetProcessTimes/TerminateProcess) are exercised for real.
public class LaunchProcessTrackingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-launch-track-").FullName;
    private string Store => Path.Combine(root, "processes.json");

    // Helper: a long-running child of ours. cmd's `ping` waits ~1 s per count and needs no
    // shell, so it dies with TerminateProcess just like the POSIX `sleep`.
    private static string HelperArgs() => OperatingSystem.IsWindows() ? "/c ping -n 300 127.0.0.1 >nul" : "";

    private static (string executable, string arguments) Helper()
    {
        if (OperatingSystem.IsWindows()) return ("cmd.exe", HelperArgs());
        var script = Path.Combine(Path.GetTempPath(), $"phaket-launch-helper-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 300\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, "");
    }

    // Launch and wait until the record is readable: under load the child may not be in /proc yet.
    private static Phaket.Core.Execution.ProcessRecord LaunchAndTrack(LaunchService service, string appId)
    {
        var (executable, arguments) = Helper();
        service.LaunchExecutable(executable, null, appId, "fixture", arguments);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (service.Tracked(appId) is { } record) return record;
            Assert.True(DateTime.UtcNow < deadline, $"no launch record for {appId}");
            Thread.Sleep(10);
        }
    }

    // Fail-closed means the helper keeps running; wait for it to appear before asserting that.
    private static void AssertAliveWithin(int pid, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!Phaket.Core.Execution.ProcessIdentity.TryGetProcessIdentity(pid).found)
            Assert.True(DateTime.UtcNow < deadline, $"process {pid} never appeared within {seconds}s");
    }

    // A successful signal only means the kernel accepted it: the pid leaves /proc a few
    // milliseconds later (p99 ~10 ms under load), so wait for the exit instead of racing it.
    private static void AssertGoneWithin(int pid, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (Phaket.Core.Execution.ProcessIdentity.TryGetProcessIdentity(pid).found)
            Assert.True(DateTime.UtcNow < deadline, $"process {pid} still alive after {seconds}s");
    }

    [Fact]
    public void DirectLaunchWritesARecordWithPidAndStartTime()
    {
        var service = new LaunchService(Store);
        var (executable, arguments) = Helper();
        var outcome = service.LaunchExecutable(executable, null, "among-us", "fixture", arguments);
        var record = Assert.Single(new Phaket.Core.Execution.ProcessTracker(Store).Load());
        try
        {
            Assert.True(outcome.Started);
            Assert.True(outcome.Tracked);
            Assert.True(record.Pid > 0);
            Assert.NotNull(record.StartTime);
            Assert.NotNull(record.StartTimeUtc);
            Assert.Equal("among-us", record.AppId);
            Assert.Equal("fixture", record.SessionLabel);
            Assert.Equal(record.Pid, service.Tracked("among-us")!.Pid);
        }
        finally { KillTree(record.Pid); }
    }

    [Fact]
    public void StopTerminatesTheTrackedHelper()
    {
        var service = new LaunchService(Store);
        var record = LaunchAndTrack(service, "among-us");
        // The helper script's own shell is the process we started; kill the whole fixture.
        try
        {
            var result = service.Stop(record);
            Assert.Equal(Phaket.Core.Execution.ProcessStopStatus.Stopped, result.Status);
            Assert.Empty(new Phaket.Core.Execution.ProcessTracker(Store).Load());
            AssertGoneWithin(record.Pid);
        }
        finally { KillTree(record.Pid); }
    }

    [Fact]
    public void SteamProtocolLaunchLeavesNoRecord()
    {
        var service = new LaunchService(Store);
        // The protocol is delegated to the platform shell; whether it resolves does not
        // matter for the record, which is absent by design (no PID on our side).
        service.LaunchGame("945360");
        Assert.False(File.Exists(Store));
        Assert.Null(service.Tracked("945360"));
    }

    [Fact]
    public void StopWithAMismatchedStartTimeIsRefusedAndSignalsNothing()
    {
        var service = new LaunchService(Store);
        var record = LaunchAndTrack(service, "among-us");
        try
        {
            var forged = record with { StartTime = record.StartTime + 1 };
            var result = service.Stop(forged);
            Assert.Equal(Phaket.Core.Execution.ProcessStopStatus.IdentityMismatch, result.Status);
            AssertAliveWithin(record.Pid);
        }
        finally { KillTree(record.Pid); }
    }

    [Fact]
    public void ViewModelAsksThenStopsTheTrackedProcess()
    {
        var service = new LaunchService(Store);
        var vm = new GameViewModel(new GameCatalogItem(new Phaket.Core.Omrl.GameDefinition("among-us",
            ["BepInEx/**"], StoreIds: new Dictionary<string, string> { ["steam"] = "945360" }), "", ""), () => { })
        {
            Launch = service,
        };
        var record = LaunchAndTrack(service, "945360");
        try
        {
            Assert.True(vm.CanStopGame);
            // Asking alone signals nothing.
            vm.StopGameCommand.Execute(null);
            Assert.NotNull(vm.PendingStop);
            AssertAliveWithin(record.Pid);
            // Confirmation goes through the real fail-closed tracker; the PID is our fixture.
            Assert.Equal(Phaket.Core.Execution.ProcessStopStatus.Stopped, service.Stop(record).Status);
            // The record is gone after the first stop, so a second one reports "no longer
            // running" — the other truthful fail-closed answer.
            AssertGoneWithin(record.Pid);
            Assert.Contains(vm.Launch.Stop(record).Status,
                new[] { Phaket.Core.Execution.ProcessStopStatus.Stopped, Phaket.Core.Execution.ProcessStopStatus.NotRunning });

            vm.ConfirmStopCommand.Execute(null);
            Assert.Null(vm.PendingStop);
            Assert.Contains(vm.StatusMessage, new[] { "Gra została zatrzymana.", "Ten proces już nie działa." });
            AssertGoneWithin(record.Pid);
        }
        finally { KillTree(record.Pid); }
    }

    [Fact]
    public void ViewModelFailsClosedWhenTheRecordDoesNotMatch()
    {
        var service = new LaunchService(Store);
        var vm = new GameViewModel(new GameCatalogItem(new Phaket.Core.Omrl.GameDefinition("among-us",
            ["BepInEx/**"], StoreIds: new Dictionary<string, string> { ["steam"] = "945360" }), "", ""), () => { })
        {
            Launch = service,
        };
        var record = LaunchAndTrack(service, "945360");
        try
        {
            // A tampered record (wrong start time) is what a recycled PID looks like.
            new Phaket.Core.Execution.ProcessTracker(Store).Save(record with { StartTime = record.StartTime + 1 });
            vm.StopGameCommand.Execute(null);
            vm.ConfirmStopCommand.Execute(null);
            Assert.Contains("Gra nie została zatrzymana", vm.StatusMessage);
            AssertAliveWithin(record.Pid);
        }
        finally { KillTree(record.Pid); }
    }

    [Fact]
    public void ViewModelWithoutATrackedGameSaysWhyItCannotStop()
    {
        var vm = new GameViewModel(new GameCatalogItem(new Phaket.Core.Omrl.GameDefinition("among-us",
            ["BepInEx/**"], StoreIds: new Dictionary<string, string> { ["steam"] = "945360" }), "", ""), () => { })
        {
            Launch = new LaunchService(Store),
        };
        Assert.False(vm.CanStopGame);
        vm.StopGameCommand.Execute(null);
        Assert.Null(vm.PendingStop);
    }

    private static void KillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}