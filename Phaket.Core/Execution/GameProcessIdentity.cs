using System.Globalization;
using System.Runtime.InteropServices;

namespace Phaket.Core.Execution;

public enum ProcessStartTimeUnit
{
    Unknown,
    WindowsFileTime100Ns,
    LinuxClockTicks,
}

/// <summary>PID plus the operating system's process-start identity.</summary>
public sealed record ProcessIdentity(
    int Pid,
    DateTimeOffset? StartTimeUtc,
    long? StartValue = null,
    ProcessStartTimeUnit Unit = ProcessStartTimeUnit.Unknown,
    string? BootId = null,
    DateTimeOffset? BootTimeUtc = null)
{
    public bool IsCertain => StartTimeUtc is not null && StartValue is not null &&
        (Unit == ProcessStartTimeUnit.WindowsFileTime100Ns ||
         Unit == ProcessStartTimeUnit.LinuxClockTicks && !string.IsNullOrWhiteSpace(BootId));

    public static (bool found, ProcessIdentity? identity) TryGetProcessIdentity(int pid)
    {
        if (pid <= 0) return (false, null);
        if (OperatingSystem.IsWindows()) return TryGetWindowsIdentity(pid);
        if (OperatingSystem.IsLinux()) return TryGetLinuxIdentity(pid);
        return (true, new ProcessIdentity(pid, null));
    }

    private static (bool found, ProcessIdentity? identity) TryGetWindowsIdentity(int pid)
    {
        const uint queryLimitedInformation = 0x1000;
        const int errorInvalidParameter = 87;

        var handle = OpenProcess(queryLimitedInformation, inheritHandle: false, pid);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == errorInvalidParameter
                ? (false, null)
                : (true, new ProcessIdentity(pid, null));
        }

        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == errorInvalidParameter
                    ? (false, null)
                    : (true, new ProcessIdentity(pid, null));
            }

            var raw = ((long)creation.High << 32) | creation.Low;
            if (raw <= 0) return (true, new ProcessIdentity(pid, null));
            var start = DateTimeOffset.FromFileTime(raw);
            var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());
            return (true, new ProcessIdentity(pid, start, raw,
                ProcessStartTimeUnit.WindowsFileTime100Ns, BootTimeUtc: boot));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (true, new ProcessIdentity(pid, null));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static (bool found, ProcessIdentity? identity) TryGetLinuxIdentity(int pid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (FileNotFoundException) { return (false, null); }
        catch (DirectoryNotFoundException) { return (false, null); }
        catch (UnauthorizedAccessException) { return (true, new ProcessIdentity(pid, null)); }
        catch (IOException ex)
        {
            return Errno(ex) is 2 or 3
                ? (false, null)
                : (true, new ProcessIdentity(pid, null));
        }

        // comm (field 2) may contain spaces and parentheses; the last ')' is the delimiter.
        var end = stat.LastIndexOf(')');
        if (end < 0) return (true, new ProcessIdentity(pid, null));
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) || startTicks < 0)
            return (true, new ProcessIdentity(pid, null));

        var bootId = TryReadText("/proc/sys/kernel/random/boot_id")?.Trim();
        var bootSeconds = TryReadLinuxBootSeconds();
        var ticksPerSecond = TryGetLinuxClockTicksPerSecond();
        DateTimeOffset? boot = bootSeconds is { } seconds && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        if (boot is not { } bootTime || ticksPerSecond <= 0 || string.IsNullOrWhiteSpace(bootId))
            return (true, new ProcessIdentity(pid, null, startTicks,
                ProcessStartTimeUnit.LinuxClockTicks, bootId, boot));

        try
        {
            var offsetTicks = (long)((decimal)startTicks * 10_000_000m / ticksPerSecond);
            var start = bootTime.AddTicks(offsetTicks);
            return (true, new ProcessIdentity(pid, start, startTicks,
                ProcessStartTimeUnit.LinuxClockTicks, bootId, boot));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (true, new ProcessIdentity(pid, null, startTicks,
                ProcessStartTimeUnit.LinuxClockTicks, bootId, boot));
        }
    }

    private static string? TryReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static long? TryReadLinuxBootSeconds()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("btime ", StringComparison.Ordinal)) continue;
                return long.TryParse(line["btime ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    ? seconds
                    : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static long TryGetLinuxClockTicksPerSecond()
    {
        const int scClkTck = 2;
        try
        {
            var value = sysconf(scClkTck);
            return value > 0 ? value : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return 0;
        }
    }

    private static int Errno(IOException exception) => exception.HResult & 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime,
        out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    private static extern long sysconf(int name);
}
