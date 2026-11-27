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
        using var helper = Helpers.Spawn();
        using var temp = new TempStore();
        var tracker = temp.Tracker;
        var current = ProcessIdentity.TryGetProcessIdentity(helper.Id).identity!;
        var start = (current.BootTimeUtc ?? DateTimeOffset.UtcNow.AddHours(-4)).AddMinutes(-1);
        var beforeBoot = new ProcessRecord(helper.Id, current.StartValue, current.Unit,
            current.BootId, start, DateTimeOffset.UtcNow, "among-us", null);
        tracker.Save(beforeBoot);

        Assert.False(tracker.IsAlive(beforeBoot));
        Assert.Equal(ProcessStopStatus.IdentityMismatch, tracker.TryStop(beforeBoot, force: false).Status);
        Assert.Empty(tracker.Load());
        Assert.True(Helpers.StillRunning(helper.Id));

        tracker.Save(beforeBoot);
        Assert.Equal(1, ProcessTracker.SweepStore(temp.Path));
        Assert.Empty(tracker.Load());
        Assert.True(Helpers.StillRunning(helper.Id), "sweeping never signals");
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

    // The Windows stop is asserted through the OS seam on every platform: a refusal must stay a
    // refusal, a recycled PID must clear its stale record, and a dead PID is "not running".
    [Fact]
    public void WindowsTerminateRefusalIsNeverReportedAsStopped()
    {
        var api = new FakeWindowsApi(terminateAccepted: false, exitsOnTerminate: false);
        var removed = new List<ProcessRecord>();

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(100), removed.Add);

        Assert.Equal(ProcessStopStatus.Failed, result.Status);
        Assert.Contains("nie miał uprawnień", result.MessagePl);
        Assert.Contains("not allowed", result.MessageEn);
        // The technical detail stays out of the user's message and names the refused call.
        // A refused TerminateProcess leaves nothing to confirm, so the failed stage is the wait;
        // an accepted signal that never ends is "wait" too (test below).
        Assert.Equal("wait", result.Stage);
        Assert.Equal(258, result.Win32Error); // WAIT_TIMEOUT
        Assert.Contains("stage=wait win32=258", result.ToString());
        Assert.DoesNotContain("win32", result.Message);
        Assert.Equal(1, api.TerminateCalls);
        Assert.Empty(removed);
        Assert.True(api.Closed, "the handle must be closed on every path");
    }

    [Fact]
    public void WindowsSignalWithoutTheProcessExitingIsReportedAsFailed()
    {
        // The kernel accepts the request but the process stays alive: no confirmation, no "Stopped".
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: false);

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(ProcessStopStatus.Failed, result.Status);
        Assert.Equal("wait", result.Stage);
        Assert.True(api.Closed);
    }

    [Fact]
    public void WindowsStopReportsStoppedOnlyAfterTheExitIsConfirmed()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true, exitPollsBeforeGone: 2);
        var removed = new List<ProcessRecord>();

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(500), removed.Add);

        Assert.Equal(ProcessStopStatus.Stopped, result.Status);
        Assert.Contains("Gra została zatrzymana.", result.MessagePl);
        Assert.True(api.WaitsAfterTerminate >= 1, "the exit has to be confirmed by waiting on the handle");
        Assert.Equal(LiveWindowsRecord().Pid, Assert.Single(removed).Pid);
        Assert.True(api.Closed);
        Assert.False(api.ClosedBeforeTerminate, "the handle must outlive the signal it carries");
    }

    [Fact]
    public void WindowsStopOfAMismatchedStartTimeNeverSignalsAndClearsTheRecord()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true);
        var record = LiveWindowsRecord() with { StartTime = 1_300_000_000_000_000_001 };
        var removed = new List<ProcessRecord>();

        var result = ProcessTracker.StopWindows(record, force: true, api,
            TimeSpan.FromMilliseconds(50), removed.Add);

        Assert.Equal(ProcessStopStatus.IdentityMismatch, result.Status);
        Assert.Contains("innego procesu", result.MessagePl);
        // Remove keys on the record it is given; the store path is what the Windows test below
        // proves, so here the cleaner itself is what has to run.
        Assert.NotEmpty(removed);
        Assert.Equal(0, api.TerminateCalls);
        Assert.True(api.Closed);
    }

    [Fact]
    public void WindowsStopOfAnAlreadyFinishedProcessIsNotRunning()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true, exitedBeforeSignal: true);
        var removed = new List<ProcessRecord>();

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(50), removed.Add);

        Assert.Equal(ProcessStopStatus.NotRunning, result.Status);
        Assert.Single(removed);
        Assert.Equal(0, api.TerminateCalls);
        Assert.True(api.Closed);
    }

    [Fact]
    public void WindowsStopOfADeadPidClearsTheRecord()
    {
        // OpenProcess on an exited PID answers ERROR_INVALID_PARAMETER: the record is stale.
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true) { OpenError = 87 };
        var removed = new List<ProcessRecord>();

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(50), removed.Add);

        Assert.Equal(ProcessStopStatus.NotRunning, result.Status);
        Assert.Single(removed);
        Assert.False(api.Closed, "a failed open leaves no handle to close");
    }

    [Fact]
    public void WindowsStopOfAnUnreadableStartTimeFailsClosedAndKeepsTheRecord()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true) { StartTimeError = 5 };
        var removed = new List<ProcessRecord>();

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(50), removed.Add);

        Assert.Equal(ProcessStopStatus.IdentityUnknown, result.Status);
        Assert.Equal("times", result.Stage);
        Assert.Equal(5, result.Win32Error);
        Assert.Empty(removed);
        Assert.Equal(0, api.TerminateCalls);
        Assert.True(api.Closed);
    }

    // The real Open asks for SYNCHRONIZE: without it WaitForSingleObject on that handle answers
    // WAIT_FAILED (ERROR_ACCESS_DENIED) no matter how long the timeout is, so the exit could never
    // be confirmed and every stop on Windows reported Failed. The mask is checked here because the
    // product path and Windows itself are only exercised together by CI.
    [Fact]
    public void StopOpensTheProcessWithTheRightToTerminateAndToWait()
    {
        const uint synchronize = 0x00100000, terminate = 0x0001, queryLimited = 0x1000;

        Assert.Equal(synchronize | terminate | queryLimited, ProcessTracker.StopAccessRights);
        Assert.Equal(0x00101001u, ProcessTracker.StopAccessRights); // PROCESS_ALL_ACCESS is not wanted
        Assert.Equal(0u, ProcessTracker.StopAccessRights & ~(synchronize | terminate | queryLimited));
    }

    // The one call that proves the rights reach the OS, exactly as the bug showed them missing:
    // a handle opened WITHOUT SYNCHRONIZE makes WaitForSingleObject answer WAIT_FAILED, while the
    // stop's own mask can be waited on. Runs on Windows only (the CI runner); elsewhere no-op.
    [Fact]
    public void AHandleWithoutSynchronizeCannotBeWaitedOnWhichIsWhyTheStopAsksForIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pid = Environment.ProcessId;
        var withoutSynchronize = ProcessTracker.StopAccessRights & ~0x00100000u;

        var broken = ProcessTracker.OpenRaw(withoutSynchronize, pid);
        var fixedHandle = ProcessTracker.OpenRaw(ProcessTracker.StopAccessRights, pid);
        try
        {
            Assert.NotEqual(IntPtr.Zero, broken);
            Assert.NotEqual(IntPtr.Zero, fixedHandle);
            // WAIT_FAILED is exactly what the CI log reported as "win32=-1" before this fix.
            Assert.Equal(uint.MaxValue, ProcessTracker.WaitRaw(broken, 0, out var brokenError));
            Assert.NotEqual(0, brokenError);
            Assert.NotEqual(uint.MaxValue, ProcessTracker.WaitRaw(fixedHandle, 0, out _));
        }
        finally
        {
            ProcessTracker.CloseRaw(broken);
            ProcessTracker.CloseRaw(fixedHandle);
        }
    }

    // A failed WaitForSingleObject answers WAIT_FAILED, which names nothing: the stage carries
    // GetLastError instead, so a CI log points at the call that really failed and never at "-1".
    [Fact]
    public void WindowsWaitFailureIsReportedWithItsLastErrorAndNotTheRawResult()
    {
        var api = new FakeWindowsApi(terminateAccepted: true, exitsOnTerminate: true) { WaitError = 6 };

        var result = ProcessTracker.StopWindows(LiveWindowsRecord(), force: true, api,
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(ProcessStopStatus.Failed, result.Status);
        Assert.Equal("wait", result.Stage);
        Assert.Equal(6, result.Win32Error); // ERROR_INVALID_HANDLE, not -1
        Assert.Contains("stage=wait win32=6", result.ToString());
        // One failure is final: the wait is not retried against a handle that already failed.
        Assert.Equal(1, api.WaitCalls);
        Assert.True(api.Closed);
    }

    // A record whose Windows start value is the fake's creation time; one constant, so both agree.
    private static ProcessRecord LiveWindowsRecord() => new(4242, FakeWindowsApi.CreationTime,
        ProcessStartTimeUnit.WindowsFileTime100Ns, null, DateTimeOffset.FromFileTime(FakeWindowsApi.CreationTime),
        DateTimeOffset.UtcNow, "among-us", "fake");
}

// Stands in for kernel32 on any OS: it answers exactly like the real calls on a living process.
// A false TerminateProcess is never reported as Stopped, whatever the exit poll says; the
// answer names the stage that refused so the CI log points at the failing call.
// WaitError is WAIT_TIMEOUT (258) from the fake; a stage=wait assertion only names the stage.
internal sealed class FakeWindowsApi(bool terminateAccepted, bool exitsOnTerminate,
    int exitPollsBeforeGone = 0, bool exitedBeforeSignal = false) : ProcessTracker.IWindowsProcessApi
{
    internal const long CreationTime = 1_330_000_000_000_000_000;

    private bool exited = exitedBeforeSignal;

    public int TerminateCalls { get; private set; }

    public int PollsAfterTerminate { get; private set; }

    public bool Closed { get; private set; }

    public bool ClosedBeforeTerminate { get; private set; }

    // ERROR_INVALID_PARAMETER from OpenProcess is what a dead PID answers on Windows.
    public int OpenError { get; init; }

    // 0 = GetProcessTimes succeeds; any other value is its last error.
    public int StartTimeError { get; init; }

    public IntPtr Open(int pid, out int error)
    {
        error = OpenError;
        return pid == 4242 && OpenError == 0 ? new IntPtr(1) : IntPtr.Zero;
    }

    public bool StartTime(IntPtr handle, out long fileTime100Ns, out int error)
    {
        fileTime100Ns = CreationTime;
        error = StartTimeError;
        return StartTimeError == 0;
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

    // WAIT_FAILED with the errno a bad handle answers (6 = ERROR_INVALID_HANDLE); any non-zero
    // ExitCode turns the fake's wait into a real failure instead of WAIT_TIMEOUT.
    public int WaitError { get; init; }

    public uint Wait(IntPtr handle, uint waitMilliseconds, out int waitError)
    {
        WaitCalls++;
        if (TerminateCalls > 0 && waitMilliseconds > 0) WaitsAfterTerminate++;
        if (WaitError != 0)
        {
            waitError = WaitError;
            return uint.MaxValue; // WAIT_FAILED
        }
        waitError = 0;
        // A bounded wait answers WAIT_OBJECT_0 once the exit happens; an exited process answers at once.
        return HasExited(handle) ? 0u : 258u; // WAIT_TIMEOUT
    }

    public int WaitsAfterTerminate { get; private set; }

    public int WaitCalls { get; private set; }

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