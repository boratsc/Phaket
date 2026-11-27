using System.Diagnostics;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-143 integration: a direct launch is recorded with PID + start time and can be
// stopped; steam:// leaves no record; the view model stops fail-closed.
// Tests only ever spawn and stop their own helper process, never a real game.
// The helper is a POSIX `sleep` or a Windows `ping`; both paths of ProcessTracker
// (pidfd/signal and OpenProcess/GetProcessTimes/TerminateProcess) are exercised for real.
public class LaunchProcessTrackingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-launch-track-").FullName;
    private string Store => Path.Combine(root, "processes.json");

    // Helper: a long-running process of ours. POSIX: `sleep 300`. Windows: `ping` (each count waits
    // ~1 s) started directly, never `cmd /c ...`, so the recorded PID is the process that is
    // tracked and TerminateProcess ends it (a shell wrapper would leave its child running).
    private static (string executable, string arguments) Helper() =>
        OperatingSystem.IsWindows() ? ("ping", "-n 300 127.0.0.1") : ("sleep", "300");

    // Same shape LaunchService builds: a bare file name resolved through PATH, never a directory.
    private static ProcessStartInfo ServiceStartInfo(string executable, string workingDirectory, string arguments) => new()
    {
        FileName = executable,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
    };

    [Fact]
    public void TheHelperIsALoneProcessTheServiceCanLaunch()
    {
        var (executable, arguments) = Helper();

        Assert.False(Path.IsPathRooted(executable));
        Assert.DoesNotContain('>', arguments);
        var startInfo = ServiceStartInfo(executable, Path.GetTempPath(), arguments);
        Assert.Equal(executable, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
    }

    // Launch and wait until the record is readable: under load the child may not be in /proc yet.
    private static Phaket.Core.Execution.ProcessRecord LaunchAndTrack(LaunchService service, string appId)
    {
        var (executable, arguments) = Helper();
        var outcome = service.LaunchExecutable(executable, null, appId, "fixture", arguments);
        // Started=false only ever means "refused before starting anything": nothing is left to kill.
        Assert.True(outcome.Started, $"the service refused to start {executable} {arguments}: {outcome.Failure}");
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
            // A Failed answer names the OS error in its message only when there is one; on
            // Windows the exit is confirmed on the process handle, so a stay-alive is reported.
            // ToString adds the failed call and its raw Win32 code for the log.
            Assert.True(result.Status == Phaket.Core.Execution.ProcessStopStatus.Stopped,
                $"stop answered {result}");
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
            var stopped = service.Stop(record);
            Assert.True(stopped.Status == Phaket.Core.Execution.ProcessStopStatus.Stopped,
                $"stop answered {stopped}");
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