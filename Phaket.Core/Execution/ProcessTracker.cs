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

public sealed record ProcessStopResult(ProcessStopStatus Status, string MessagePl, string MessageEn)
{
    public string Message => $"{MessagePl} / {MessageEn}";
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

    private static ProcessStopResult StopResult(ProcessStopStatus status) => Localized(status);

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

    private ProcessStopResult StopWindows(ProcessRecord record, bool force)
    {
        const uint terminate = 0x0001;
        const uint queryLimitedInformation = 0x1000;
        const int errorInvalidParameter = 87;
        const int errorAccessDenied = 5;

        var handle = OpenProcess(terminate | queryLimitedInformation, inheritHandle: false, record.Pid);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == errorInvalidParameter
                ? RemoveAndResult(record, ProcessStopStatus.NotRunning)
                : error == errorAccessDenied
                    ? StopResult(ProcessStopStatus.Failed)
                    : StopResult(ProcessStopStatus.IdentityUnknown);
        }

        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                return Marshal.GetLastPInvokeError() == errorInvalidParameter
                    ? RemoveAndResult(record, ProcessStopStatus.NotRunning)
                    : StopResult(ProcessStopStatus.IdentityUnknown);
            }

            var raw = ((long)creation.High << 32) | creation.Low;
            if (raw <= 0) return StopResult(ProcessStopStatus.IdentityUnknown);
            ProcessIdentity identity;
            try
            {
                identity = new ProcessIdentity(record.Pid, DateTimeOffset.FromFileTime(raw), raw,
                    ProcessStartTimeUnit.WindowsFileTime100Ns,
                    BootTimeUtc: DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64()));
            }
            catch (ArgumentOutOfRangeException)
            {
                return StopResult(ProcessStopStatus.IdentityUnknown);
            }

            if (!Matches(record, identity)) return RemoveAndResult(record, ProcessStopStatus.IdentityMismatch);

            // shortcut: Windows has no portable graceful stop signal, so both modes use TerminateProcess;
            // upgrade when the UI needs a window-close request before forced termination.
            _ = force;
            return TerminateProcess(handle, exitCode: 0)
                ? RemoveAndResult(record, ProcessStopStatus.Stopped)
                : StopResult(ProcessStopStatus.Failed);
        }
        finally
        {
            CloseHandle(handle);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime,
        out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

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
