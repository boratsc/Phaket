using System.Diagnostics;
using Phaket.Core.Execution;

namespace Phaket.Core.Tests;

// PAK-143: a launch record pairs the PID with the OS process-start identity, so a recycled PID can
// never authorize a signal. Tests only ever spawn and stop their own helper processes.
public sealed class Pak143ProcessIdentityTests
{
    [Fact]
    public void CurrentProcessHasAConsistentIdentity()
    {
        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(Environment.ProcessId);

        Assert.True(found);
        Assert.NotNull(identity);
        Assert.Equal(Environment.ProcessId, identity.Pid);
        Assert.True(identity.IsCertain);
        Assert.False(ProcessIdentity.TryGetProcessIdentity(Environment.ProcessId).identity!.StartValue is null);
        Assert.InRange(identity.StartTimeUtc!.Value, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddMinutes(1));
        if (OperatingSystem.IsWindows())
            Assert.Equal(ProcessStartTimeUnit.WindowsFileTime100Ns, identity.Unit);
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(ProcessStartTimeUnit.LinuxClockTicks, identity.Unit);
            Assert.False(string.IsNullOrWhiteSpace(identity.BootId));
        }
    }

    [Fact]
    public void MissingProcessIsReportedWithoutAnException()
    {
        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(int.MaxValue);

        Assert.False(found);
        Assert.Null(identity);
        Assert.False(ProcessIdentity.TryGetProcessIdentity(0).found);
        Assert.False(ProcessIdentity.TryGetProcessIdentity(-5).found);
    }

    [Fact]
    public void HelperProcessKeepsTheSameIdentityWhileRunning()
    {
        using var helper = Helpers.Spawn();

        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(helper.Id);

        Assert.True(found);
        Assert.NotNull(identity);
        Assert.True(identity.IsCertain);
        Assert.Equal(identity.StartValue, ProcessIdentity.TryGetProcessIdentity(helper.Id).identity!.StartValue);
    }

    [Fact]
    public void RecordRoundTripKeepsTheStartIdentity()
    {
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var (_, identity) = ProcessIdentity.TryGetProcessIdentity(helper.Id);
        var record = ProcessRecord.FromIdentity(identity!, "among-us", "Among Us — vanilla", new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

        temp.Tracker.Save(record);
        var stored = Assert.Single(new ProcessTracker(temp.Path).Load());

        Assert.Equal(record, stored);
        Assert.Equal(identity!.StartValue, stored.StartTime);
        Assert.Equal(identity.Unit, stored.StartTimeUnit);
        Assert.Equal("Among Us — vanilla", stored.SessionLabel);
    }

    [Fact]
    public void SaveReplacesTheRecordForTheSamePid()
    {
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var identity = ProcessIdentity.TryGetProcessIdentity(helper.Id).identity!;
        var tracker = temp.Tracker;

        tracker.Save(ProcessRecord.FromIdentity(identity, "other", "first"));
        tracker.Save(ProcessRecord.FromIdentity(identity, "own", "second"));

        var stored = Assert.Single(tracker.Load());
        Assert.Equal("own", stored.AppId);
        Assert.Equal("second", stored.SessionLabel);
    }
}

public sealed class Pak143ProcessTrackerTests
{
    [Fact]
    public void LivingRecordIsAliveAndStopOnlySignalsTheVerifiedProcess()
    {
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var record = Helpers.Record(helper);
        tracker.Save(record);

        Assert.True(tracker.IsAlive(record));
        var result = tracker.TryStop(record, force: true);

        Assert.Equal(ProcessStopStatus.Stopped, result.Status);
        Assert.Contains("Gra została zatrzymana.", result.MessagePl);
        Assert.Contains("The game has been stopped.", result.MessageEn);
        Assert.Empty(tracker.Load());
        Assert.True(Helpers.WaitUntilGone(helper.Id), "the verified stop must end the helper");
    }

    [Fact]
    public void RecycledPidIsRefusedAndTheRecordIsCleared()
    {
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var identity = ProcessIdentity.TryGetProcessIdentity(helper.Id).identity!;
        var impostor = ProcessRecord.FromIdentity(identity with { StartValue = identity.StartValue + 1 }, "among-us");
        tracker.Save(impostor);

        Assert.False(tracker.IsAlive(impostor));
        var result = tracker.TryStop(impostor, force: true);

        Assert.Equal(ProcessStopStatus.IdentityMismatch, result.Status);
        Assert.Contains("innego procesu", result.MessagePl);
        Assert.Empty(tracker.Load());
        Assert.True(Helpers.StillRunning(helper.Id), "the helper must survive an unverified stop");
    }

    [Fact]
    public void UncertainIdentityFailsClosedAndKeepsTheRecord()
    {
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var identity = ProcessIdentity.TryGetProcessIdentity(helper.Id).identity!;
        var record = ProcessRecord.FromIdentity(identity with { StartTimeUtc = null, StartValue = null });
        tracker.Save(record);

        Assert.False(tracker.IsAlive(record));
        var result = tracker.TryStop(record, force: true);

        Assert.Equal(ProcessStopStatus.IdentityUnknown, result.Status);
        Assert.Contains("Nie udało się potwierdzić", result.MessagePl);
        Assert.Single(tracker.Load());
        Assert.True(Helpers.StillRunning(helper.Id));
    }

    [Fact]
    public void RecordFromABootBeforeTheCurrentBootIsDeadAndSwept()
    {
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var current = ProcessIdentity.TryGetProcessIdentity(Environment.ProcessId).identity!;
        var start = (current.BootTimeUtc ?? DateTimeOffset.UtcNow.AddHours(-4)).AddMinutes(-1);
        var beforeBoot = new ProcessRecord(Environment.ProcessId, current.StartValue, current.Unit,
            current.BootId, start, DateTimeOffset.UtcNow, "among-us", null);
        tracker.Save(beforeBoot);

        Assert.False(tracker.IsAlive(beforeBoot));
        Assert.Equal(ProcessStopStatus.IdentityMismatch, tracker.TryStop(beforeBoot, force: false).Status);
        Assert.Empty(tracker.Load());
        Assert.True(Helpers.StillRunning(Environment.ProcessId));

        tracker.Save(beforeBoot);
        Assert.Equal(1, ProcessTracker.SweepStore(temp.Path));
        Assert.Empty(tracker.Load());
        Assert.True(Helpers.StillRunning(Environment.ProcessId), "sweeping never signals");
    }

    [Fact]
    public void SweepDropsDeadRecordsAndKeepsTheLivingOne()
    {
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var live = Helpers.Record(helper);
        var dead = live with { Pid = int.MaxValue };
        tracker.Save(live);
        tracker.Save(dead);

        Assert.Equal(1, ProcessTracker.SweepStore(temp.Path));
        Assert.Equal(live, Assert.Single(tracker.Load()));
    }

    [Fact]
    public void StopOfADeadProcessClearsTheRecordWithoutSignalling()
    {
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var record = new ProcessRecord(int.MaxValue, 42, ProcessStartTimeUnit.LinuxClockTicks,
            "boot", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "among-us", null);
        tracker.Save(record);

        var result = tracker.TryStop(record, force: false);

        Assert.Equal(ProcessStopStatus.NotRunning, result.Status);
        Assert.Contains("już nie działa", result.MessagePl);
        Assert.Empty(tracker.Load());
    }

    [Fact]
    public void CorruptStoreMakesSweepQuietButLoadStillReportsIt()
    {
        using var temp = new TempStore();
        File.WriteAllText(temp.Path, "{ not json");

        Assert.Equal(0, ProcessTracker.SweepStore(temp.Path));
        Assert.Throws<System.Text.Json.JsonException>(() => temp.Tracker.Load());
        Assert.Equal("{ not json", File.ReadAllText(temp.Path));
    }

    // The Windows stop is asserted through the OS seam on every platform: the CI failure was a
    // silent "Stopped" while the process kept running, so each refusal must stay a refusal.
    [Fact]
    public void WindowsTerminateRefusalIsNeverReportedAsStopped()
    {
        var api = new FakeWindowsApi(terminateAccepted: false, exitsOnTerminate: false);

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5));

        Assert.Equal(ProcessStopStatus.Failed, result.Status);
        Assert.Contains("nie miał uprawnień", result.MessagePl);
        Assert.Contains("not allowed", result.MessageEn);
        Assert.Equal(1, api.TerminateCalls);
        Assert.True(api.Closed, "the handle must be closed on every path");
    }

    [Fact]
    public void WindowsSignalWithoutTheProcessExitingIsReportedAsFailed()
    {
        // The kernel accepts the request but the process stays alive: no confirmation, no "Stopped".
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: false);

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));

        Assert.Equal(ProcessStopStatus.Failed, result.Status);
        Assert.True(api.Closed);
    }

    [Fact]
    public void WindowsStopReportsStoppedOnlyAfterTheExitIsConfirmed()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true, exitPollsBeforeGone: 2);

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(5));

        Assert.Equal(ProcessStopStatus.Stopped, result.Status);
        Assert.Contains("Gra została zatrzymana.", result.MessagePl);
        Assert.True(api.PollsAfterTerminate >= 3, "the exit has to be confirmed by polling the handle");
        Assert.True(api.Closed);
        Assert.False(api.ClosedBeforeTerminate, "the handle must outlive the signal it carries");
    }

    [Fact]
    public void WindowsStopOfAMismatchedStartTimeNeverSignals()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true);
        var record = LiveWindowsRecord() with { StartTime = 1_300_000_000_000_000_001 };

        var result = ProcessTracker.StopWindows(record, force: true, api,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));

        Assert.Equal(ProcessStopStatus.IdentityMismatch, result.Status);
        Assert.Equal(0, api.TerminateCalls);
        Assert.True(api.Closed);
    }

    [Fact]
    public void WindowsStopOfAnAlreadyFinishedProcessIsNotRunning()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true, exitedBeforeSignal: true);

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));

        Assert.Equal(ProcessStopStatus.NotRunning, result.Status);
        Assert.Equal(0, api.TerminateCalls);
        Assert.True(api.Closed);
    }

    // A record whose Windows start value is the fake's creation time; one constant, so both agree.
    private static ProcessRecord LiveWindowsRecord() => new(4242, FakeWindowsApi.CreationTime,
        ProcessStartTimeUnit.WindowsFileTime100Ns, null, DateTimeOffset.FromFileTime(FakeWindowsApi.CreationTime),
        DateTimeOffset.UtcNow, "among-us", "fake");
}

// Stands in for kernel32 on any OS: it answers exactly like the real calls on a living process.
internal sealed class FakeWindowsApi(bool terminateAccepted, bool exitsOnTerminate,
    int exitPollsBeforeGone = 0, bool exitedBeforeSignal = false) : ProcessTracker.IWindowsProcessApi
{
    internal const long CreationTime = 1_330_000_000_000_000_000;

    private bool exited = exitedBeforeSignal;

    public int TerminateCalls { get; private set; }

    public int PollsAfterTerminate { get; private set; }

    public bool Closed { get; private set; }

    public bool ClosedBeforeTerminate { get; private set; }

    public IntPtr Open(int pid, out int error)
    {
        error = 0;
        return pid == 4242 ? new IntPtr(1) : IntPtr.Zero;
    }

    public bool StartTime(IntPtr handle, out long fileTime100Ns)
    {
        fileTime100Ns = CreationTime;
        return true;
    }

    public bool Terminate(IntPtr handle, out int error)
    {
        TerminateCalls++;
        error = terminateAccepted ? 0 : 5;
        return terminateAccepted;
    }

    public bool HasExited(IntPtr handle)
    {
        if (TerminateCalls > 0) PollsAfterTerminate++;
        if (!exited && exitsOnTerminate && TerminateCalls > 0 && PollsAfterTerminate > exitPollsBeforeGone)
            exited = true;
        return exited;
    }

    public void Close(IntPtr handle)
    {
        Closed = true;
        if (TerminateCalls == 0) ClosedBeforeTerminate = true;
    }
}

internal static class Helpers
{
    internal sealed class Helper(Process process) : IDisposable
    {
        public int Id => process.Id;

        public void Dispose()
        {
            // The helper is our direct child: the Process handle identifies it without PID reuse risk.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    // The helper keeps running for minutes and dies with Kill: `sleep` on POSIX, `ping` (each
    // count waits ~1 s) on Windows — one process each, no shell wrapper, so both identity
    // paths are exercised for real and TerminateProcess ends the whole tracked helper.
    internal static Helper Spawn()
    {
        var process = ProcessTestHelper.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !ProcessIdentity.TryGetProcessIdentity(process.Id).found)
            Thread.Sleep(10);
        return new Helper(process);
    }

    internal static ProcessRecord Record(Helper process) =>
        ProcessRecord.FromIdentity(ProcessIdentity.TryGetProcessIdentity(process.Id).identity!,
            "among-us", "sleep");

    internal static bool StillRunning(int pid) => ProcessIdentity.TryGetProcessIdentity(pid).found;

    internal static bool WaitUntilGone(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (StillRunning(pid) && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return !StillRunning(pid);
    }
}

internal sealed class TempStore : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak143-").FullName;

    internal string Path => System.IO.Path.Combine(root, "processes.json");
    internal ProcessTracker Tracker => new(Path);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}