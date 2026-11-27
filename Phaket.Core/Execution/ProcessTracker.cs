using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Core.Execution;

public enum ProcessRecordStatus { Running, Stopped, Stale }
public enum ProcessStopStatus { Stopped, NotRunning, IdentityMismatch, IdentityUnknown, Failed }

public sealed record ProcessRecord(
    int Pid,
    long? StartTime,
    ProcessStartTimeUnit StartTimeUnit,
    string? BootId,
    DateTimeOffset? StartTimeUtc,
    DateTimeOffset LaunchedAt,
    string? AppId,
    string? SessionLabel,
    ProcessRecordStatus Status = ProcessRecordStatus.Running)
{
    public static ProcessRecord FromIdentity(ProcessIdentity identity, string? appId = null,
        string? sessionLabel = null, DateTimeOffset? launchedAt = null) =>
        new(identity.Pid, identity.StartValue, identity.Unit, identity.BootId,
            identity.StartTimeUtc, launchedAt ?? DateTimeOffset.UtcNow, appId, sessionLabel);
}

// stage/win32 name the failed OS call for logs and tests; they never reach the user's message.
public sealed record ProcessStopResult(ProcessStopStatus Status, string MessagePl, string MessageEn,
    string? Stage = null, int? Win32Error = null)
{
    public string Message => $"{MessagePl} / {MessageEn}";

    // One line for a test or a log: "Failed [stage=terminate win32=5]".
    public override string ToString() =>
        Stage is null ? $"{Status}: {Message}" : $"{Status} [stage={Stage} win32={Win32Error}]: {Message}";
}

/// <summary>Persistent launch records and fail-closed process stopping.</summary>
public sealed class ProcessTracker
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string storePath;

    public ProcessTracker(string? storePath = null)
    {
        this.storePath = Path.GetFullPath(storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "processes.json"));
    }

    public string StorePath => storePath;

    public IReadOnlyList<ProcessRecord> Load() => LoadList().ToArray();

    public void Save(ProcessRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var records = LoadList();
        records.RemoveAll(r => r.Pid == record.Pid);
        records.Add(record);
        WriteAll(records);
    }

    public void Remove(ProcessRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var records = LoadList();
        if (records.Remove(record)) WriteAll(records);
    }

    public bool IsAlive(ProcessRecord record) => Inspect(record) == ProcessCheck.Alive;

    public int Sweep()
    {
        var records = LoadList();
        var removed = records.RemoveAll(r => Inspect(r) is ProcessCheck.Missing or ProcessCheck.Mismatch);
        if (removed > 0) WriteAll(records);
        return removed;
    }

    public ProcessStopResult TryStop(ProcessRecord record, bool force)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!HasCertainIdentity(record))
        {
            return Inspect(record) == ProcessCheck.Missing
                ? RemoveAndResult(record, ProcessStopStatus.NotRunning)
                : StopResult(ProcessStopStatus.IdentityUnknown);
        }

        return OperatingSystem.IsWindows() ? StopWindows(record, force)
            : OperatingSystem.IsLinux() ? StopLinux(record, force)
            : StopResult(ProcessStopStatus.IdentityUnknown);
    }

    private List<ProcessRecord> LoadList()
    {
        if (!File.Exists(storePath)) return [];
        return JsonSerializer.Deserialize<List<ProcessRecord>>(File.ReadAllText(storePath), Json) ?? [];
    }

    private void WriteAll(List<ProcessRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var temp = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
            File.Move(temp, storePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private ProcessCheck Inspect(ProcessRecord record)
    {
        if (record.Pid <= 0) return ProcessCheck.Missing;
        var (found, identity) = ProcessIdentity.TryGetProcessIdentity(record.Pid);
        if (!found || identity is null) return ProcessCheck.Missing;
        if (!identity.IsCertain || !HasCertainIdentity(record)) return ProcessCheck.Unknown;
        return Matches(record, identity) ? ProcessCheck.Alive : ProcessCheck.Mismatch;
    }

    private static bool HasCertainIdentity(ProcessRecord record) =>
        record.StartTimeUtc is not null && record.StartTime is not null &&
        (record.StartTimeUnit == ProcessStartTimeUnit.WindowsFileTime100Ns ||
         record.StartTimeUnit == ProcessStartTimeUnit.LinuxClockTicks && !string.IsNullOrWhiteSpace(record.BootId));

    private static bool Matches(ProcessRecord record, ProcessIdentity identity)
    {
        if (record.Pid != identity.Pid || !identity.IsCertain || !HasCertainIdentity(record)) return false;
        if (identity.BootTimeUtc is { } boot && record.StartTimeUtc < boot) return false;
        if (record.StartTimeUnit != identity.Unit || record.StartTime != identity.StartValue) return false;
        return record.StartTimeUnit != ProcessStartTimeUnit.LinuxClockTicks ||
            string.Equals(record.BootId, identity.BootId, StringComparison.OrdinalIgnoreCase);
    }

    private ProcessStopResult RemoveAndResult(ProcessRecord record, ProcessStopStatus status)
    {
        Remove(record);
        return StopResult(status);
    }

    // The Windows stop receives its cleaner, so the same code runs in a test that owns no store.
    private static ProcessStopResult Removed(ProcessRecord record, ProcessStopStatus status, Action<ProcessRecord>? remove)
    {
        remove?.Invoke(record);
        return Localized(status);
    }

    private static ProcessStopResult StopResult(ProcessStopStatus status, string? stage = null, int? win32 = null) =>
        Localized(status) with { Stage = stage, Win32Error = win32 };

    // UI picks the language; the record of what happened is the same in both.
    public static ProcessStopResult Localized(ProcessStopStatus status) => status switch
    {
        ProcessStopStatus.Stopped => new(status,
            "Gra została zatrzymana.", "The game has been stopped."),
        ProcessStopStatus.NotRunning => new(status,
            "Ten proces już nie działa.", "This process is no longer running."),
        ProcessStopStatus.IdentityMismatch => new(status,
            "Ten PID należy już do innego procesu. Gra nie została zatrzymana.",
            "This PID now belongs to another process. The game was not stopped."),
        ProcessStopStatus.IdentityUnknown => new(status,
            "Nie udało się potwierdzić tożsamości procesu. Gra nie została zatrzymana.",
            "The process identity could not be verified. The game was not stopped."),
        _ => new(status,
            "Phaket nie miał uprawnień do zatrzymania procesu lub system odmówił sygnału.",
            "Phaket was not allowed to stop the process, or the system rejected the signal."),
    };

    // The OS calls behind the Windows stop, so a test can prove the failure paths without Windows.
    internal interface IWindowsProcessApi
    {
        // Opens with StopAccessRights so one handle verifies the start time, carries the right to
        // terminate, and can be waited on.
        IntPtr Open(int pid, out int error);

        bool StartTime(IntPtr handle, out long fileTime100Ns, out int error);

        bool Terminate(IntPtr handle, out int error);

        // True once the kernel object reports a finished process; false while it is still active
        // or the answer cannot be read.
        bool HasExited(IntPtr handle);

        // WaitForSingleObject's result: WAIT_OBJECT_0 (0) means the process handle is signalled.
        // waitError carries GetLastError() only when the result is WAIT_FAILED; 0 otherwise.
        uint Wait(IntPtr handle, uint waitMilliseconds, out int waitError);

        void Close(IntPtr handle);
    }

    private static readonly IWindowsProcessApi Windows = new Win32ProcessApi();

    // The mask one handle carries through the whole stop. SYNCHRONIZE is not optional:
    // WaitForSingleObject refuses a process handle without it (WAIT_FAILED, ERROR_ACCESS_DENIED),
    // so the exit could never be confirmed and every stop would report Failed.
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x00100000;
    internal const uint StopAccessRights =
        ProcessTerminate | ProcessQueryLimitedInformation | Synchronize; // 0x00101001

    // The bare OpenProcess call, so a test can see the mask the stop's handle is really opened with.
    internal static IntPtr OpenRaw(uint desiredAccess, int pid) =>
        OpenProcess(desiredAccess, inheritHandle: 0, pid);

    // The same wait a handle needs, exposed so a test can show what a missing right costs.
    internal static uint WaitRaw(IntPtr handle, uint milliseconds, out int error)
    {
        var result = WaitForSingleObject(handle, milliseconds);
        error = result == WaitFailed ? Marshal.GetLastPInvokeError() : 0;
        return result;
    }

    internal static void CloseRaw(IntPtr handle) => CloseHandle(handle);

    private ProcessStopResult StopWindows(ProcessRecord record, bool force) =>
        StopWindows(record, force, Windows, TimeSpan.FromSeconds(5), removeRecord: Remove);

    internal static ProcessStopResult StopWindows(ProcessRecord record, bool force, IWindowsProcessApi api,
        TimeSpan exitTimeout, Action<ProcessRecord>? removeRecord = null)
    {
        // The same answers the Linux stop already gives for a dead PID (87 = ERROR_INVALID_PARAMETER).
        const int errorInvalidParameter = 87;

        var handle = api.Open(record.Pid, out var openError);
        if (handle == IntPtr.Zero)
        {
            return openError == errorInvalidParameter
                ? Removed(record, ProcessStopStatus.NotRunning, removeRecord)
                : StopResult(ProcessStopStatus.IdentityUnknown, "open", openError);
        }

        try
        {
            // An exited process keeps its kernel object (and its handle answers), so this is
            // "not running" before the start time is even asked for.
            if (api.HasExited(handle)) return Removed(record, ProcessStopStatus.NotRunning, removeRecord);
            if (!api.StartTime(handle, out var raw, out var timeError))
                return timeError == errorInvalidParameter
                    ? Removed(record, ProcessStopStatus.NotRunning, removeRecord)
                    : StopResult(ProcessStopStatus.IdentityUnknown, "times", timeError);
            if (raw <= 0) return StopResult(ProcessStopStatus.IdentityUnknown, "times", 0);
            ProcessIdentity identity;
            try
            {
                identity = new ProcessIdentity(record.Pid, DateTimeOffset.FromFileTime(raw), raw,
                    ProcessStartTimeUnit.WindowsFileTime100Ns);
            }
            catch (ArgumentOutOfRangeException)
            {
                return StopResult(ProcessStopStatus.IdentityUnknown, "times", 0);
            }

            // A PID that now answers with another start time is a recycled PID: the record is
            // stale, never a stop order, so it is cleared before answering (as on Linux).
            if (!Matches(record, identity)) return Removed(record, ProcessStopStatus.IdentityMismatch, removeRecord);

            // shortcut: Windows has no portable graceful stop signal, so both modes use TerminateProcess;
            // upgrade when the UI needs a window-close request before forced termination.
            _ = force;

            var accepted = api.Terminate(handle, out var terminateError);
            // The process handle is signalled exactly when the process ends: that is the
            // confirmation TerminateProcess itself does not give. One bounded wait is enough;
            // the loop only covers an early wake before the deadline.
            var deadline = DateTime.UtcNow + exitTimeout;
            uint wait;
            int waitError;
            while (true)
            {
                wait = api.Wait(handle, Remaining(deadline), out waitError);
                // A real wait failure is final: retrying the same handle only multiplies it.
                if (wait == WaitFailed || wait == WaitObject0 || DateTime.UtcNow >= deadline) break;
            }
            // WAIT_FAILED alone names nothing, so the diagnostic carries GetLastError instead of
            // the raw 0xFFFFFFFF (-1) that a log reader cannot tell from a negative errno.
            if (wait == WaitFailed) return StopResult(ProcessStopStatus.Failed, "wait", waitError);
            if (!api.HasExited(handle)) return StopResult(ProcessStopStatus.Failed, "wait", (int)wait);
            // Access denied from TerminateProcess on a process that then finished is "success", but a
            // refusal with the process still alive is not something to report as stopped.
            if (!accepted) return StopResult(ProcessStopStatus.Failed, "terminate", terminateError);
            return Removed(record, ProcessStopStatus.Stopped, removeRecord);
        }
        finally
        {
            api.Close(handle);
        }
    }

    // Every answer is read as a plain int + Marshal.GetLastPInvokeError, never through a bool
    // marshalled by the runtime: the last error belongs to this exact call, and a 0/non-0 BOOL
    // needs no marshaller (whose failure is silent) on either the .NET or the native side.
    private sealed class Win32ProcessApi : IWindowsProcessApi
    {
        public IntPtr Open(int pid, out int error)
        {
            var handle = OpenRaw(StopAccessRights, pid);
            error = Marshal.GetLastPInvokeError();
            return handle;
        }

        public bool StartTime(IntPtr handle, out long fileTime100Ns, out int error)
        {
            fileTime100Ns = 0;
            if (GetProcessTimes(handle, out var creation, out _, out _, out _) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }
            error = 0;
            fileTime100Ns = ((long)creation.High << 32) | creation.Low;
            return true;
        }

        public bool Terminate(IntPtr handle, out int error)
        {
            var terminated = TerminateProcess(handle, exitCode: 0);
            error = Marshal.GetLastPInvokeError();
            return terminated != 0;
        }

        public bool HasExited(IntPtr handle) => Wait(handle, waitMilliseconds: 0, out _) == WaitObject0;

        public uint Wait(IntPtr handle, uint waitMilliseconds, out int waitError) =>
            WaitRaw(handle, waitMilliseconds, out waitError);

        public void Close(IntPtr handle) => CloseHandle(handle);
    }

    private ProcessStopResult StopLinux(ProcessRecord record, bool force)
    {
        const int errorPermission = 1;
        const int errorNoSuchProcess = 3;
        int fd;
        try
        {
            fd = pidfd_open(record.Pid, flags: 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return StopResult(ProcessStopStatus.Failed);
        }

        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == errorNoSuchProcess
                ? RemoveAndResult(record, ProcessStopStatus.NotRunning)
                : error == errorPermission
                    ? StopResult(ProcessStopStatus.Failed)
                    : StopResult(ProcessStopStatus.IdentityUnknown);
        }

        try
        {
            var (found, identity) = ProcessIdentity.TryGetProcessIdentity(record.Pid);
            if (!found || identity is null) return RemoveAndResult(record, ProcessStopStatus.NotRunning);
            if (!identity.IsCertain) return StopResult(ProcessStopStatus.IdentityUnknown);
            if (!Matches(record, identity)) return RemoveAndResult(record, ProcessStopStatus.IdentityMismatch);

            // The pidfd pins this exact process, so PID reuse between the check and signal cannot redirect it.
            if (pidfd_send_signal(fd, force ? 9 : 15, IntPtr.Zero, flags: 0) == 0)
                return RemoveAndResult(record, ProcessStopStatus.Stopped);

            var signalError = Marshal.GetLastPInvokeError();
            return signalError == errorNoSuchProcess
                ? RemoveAndResult(record, ProcessStopStatus.NotRunning)
                : StopResult(ProcessStopStatus.Failed);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return StopResult(ProcessStopStatus.Failed);
        }
        finally
        {
            close(fd);
        }
    }

    internal static int SweepStore(string storePath)
    {
        try { return new ProcessTracker(storePath).Sweep(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return 0; }
    }

    private enum ProcessCheck { Alive, Missing, Mismatch, Unknown }

    // WAIT_OBJECT_0 from WaitForSingleObject: the kernel object is signalled, i.e. the process has exited.
    private const uint WaitObject0 = 0;

    // WAIT_FAILED from WaitForSingleObject: the call itself failed, and GetLastError names why.
    private const uint WaitFailed = uint.MaxValue;

    // The wait left until a deadline, clamped to what WaitForSingleObject accepts (INFINITE is -1).
    // At least 1 ms: a 0 wait is a poll, so a clamped 0 would spin the loop until the deadline.
    // INFINITE - 1 (0x7FFFFFFE) is the largest legal timeout: passing INFINITE itself makes every
    // call block forever and 0x7FFFFFFF makes it fail with WAIT_FAILED (ERROR_INVALID_PARAMETER).
    private static uint Remaining(DateTime deadline) =>
        (uint)Math.Clamp((deadline - DateTime.UtcNow).TotalMilliseconds, 1, MaxWaitMilliseconds);

    // INFINITE - 1: the largest timeout WaitForSingleObject accepts.
    private const uint MaxWaitMilliseconds = 0x7FFFFFFE;

    // int, not bool: the runtime must not marshal the BOOL, and the last error is read right
    // after the call, so no wrapper can overwrite it first.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, int inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern int GetProcessTimes(IntPtr process, out FileTime creationTime,
        out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern int TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint waitMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern int CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int pidfd_open(int pid, uint flags);

    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static extern int pidfd_send_signal(int pidfd, int signal, IntPtr info, uint flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);
}
