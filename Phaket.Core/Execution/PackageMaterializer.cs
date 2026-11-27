using System.Runtime.InteropServices;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// PAK-146 (model B): a package tree is materialized from the read-only vanilla cache with
// copy-on-write where the filesystem supports it (reflink) and a full copy otherwise.
// Hard links are never used: two packages sharing an inode would silently write into each
// other, and a clone source is only ever opened read-only.
//
// Per file: Probe -> TryClone -> fallback copy. A failed clone is not an error: it is the
// "filesystem or volume does not support it" answer the fallback exists for.
public interface IFileCloner
{
    // Whether clones are worth attempting for this destination tree.
    bool Probe(string directory);
    bool TryClone(string source, string destination);
}

public enum CloneSupport { None, BtrfsOrXfs, ReFS, Unknown }

// Builds a complete tree under "<target>.partial" and renames it into place last, so a
// reader never sees a half-materialized package. A crash leaves only a ".partial" tree,
// removed by the next run.
public static class PackageMaterializer
{
    public const string PartialSuffix = ".partial";
    public const string OrphanSuffix = PartialSuffix + "-old-";

    // Same rule as PathBoundary: Windows paths are case-insensitive, everything else is not.
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static Task<IReadOnlyList<string>> MaterializeAsync(
        string sourceDir, string targetDir, IProgress<(long copiedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default) =>
        MaterializeAsync(sourceDir, targetDir, progress, DefaultCloner(), ct);

    // freeBytes: injected so tests can force the disk-full refusal before any copy.
    public static async Task<IReadOnlyList<string>> MaterializeAsync(
        string sourceDir, string targetDir, IProgress<(long copiedBytes, long totalBytes)>? progress,
        IFileCloner cloner, CancellationToken ct = default, Func<string, long>? freeBytes = null)
    {
        var source = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(source))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"vanilla cache missing: {source}"));
        // Writing into the cache (or into itself) is never a materialization. Both sides are
        // canonicalized: links are resolved over existing components and the comparison is
        // case-insensitive on Windows, so an alias of the cache is refused too. Nesting is
        // checked once while the target is absent; the target tree itself never holds links.
        var realSource = PathBoundary.Resolve(source, ".");
        var realTarget = ResolvePending(target);
        AssertOutsideCache(realSource, realTarget, target);

        RecoverOrphans(target);

        var files = EnumerateContent(source);
        var total = files.Sum(f => new FileInfo(f).Length);
        var free = freeBytes?.Invoke(target) ?? FreeBytes(Path.GetDirectoryName(target)!);
        if (free < total)
        {
            var missingMb = (total - Math.Max(free, 0) + (1 << 20) - 1) >> 20;
            throw new PhaketException(ErrorCodes.DiskFull,
                ("requiredMb", missingMb.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("path", target));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + PartialSuffix;
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        var created = new List<string>(files.Count);
        try
        {
            Directory.CreateDirectory(partial);
            var useClone = cloner.Probe(partial);
            long copied = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = file[(source.Length + 1)..];
                var destination = Path.Combine(partial, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!useClone || !cloner.TryClone(file, destination))
                    CopyFile(file, destination);
                created.Add(Path.Combine(target, relative));
                copied += new FileInfo(file).Length;
                progress?.Report((copied, total));
            }
            VerifyTreeHash(source, partial);
            PromotePartial(partial, target);
            return created;
        }
        catch
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            throw;
        }
    }

    // An existing target is moved aside first: on a failed promotion the previous package
    // stays intact instead of being deleted. A same-volume rename keeps it atomic.
    private static void PromotePartial(string partial, string target)
    {
        if (!Directory.Exists(target)) { Directory.Move(partial, target); return; }
        var stale = target + OrphanSuffix + Guid.NewGuid().ToString("N");
        Directory.Move(target, stale);
        try { Directory.Move(partial, target); }
        catch { Directory.Move(stale, target); throw; }
        Directory.Delete(stale, recursive: true);
    }

    // A process killed between the two renames of PromotePartial leaves target absent and a
    // "partial-old-*" tree behind. That tree is the last good package: putting it back is the
    // only recovery that neither loses the package nor blocks the next run.
    private static void RecoverOrphans(string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) return;
        var orphans = Directory.GetDirectories(parent, Path.GetFileName(target) + OrphanSuffix + "*")
            .OrderByDescending(d => d, StringComparer.FromComparison(Comparison))
            .ToList();
        if (orphans.Count == 0) return;
        if (!Directory.Exists(target))
        {
            Directory.Move(orphans[0], target);
            orphans.RemoveAt(0);
        }
        // Anything left over is a stale copy of a package that is already in place.
        foreach (var orphan in orphans) Directory.Delete(orphan, recursive: true);
    }

    // The target does not exist yet, so only its existing ancestor can hold links.
    private static string ResolvePending(string target)
    {
        var existing = target;
        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
        var rest = target[existing.Length..].TrimStart(Path.DirectorySeparatorChar);
        return PathBoundary.Resolve(existing, rest.Length == 0 ? "." : rest);
    }

    private static void AssertOutsideCache(string realSource, string realTarget, string target)
    {
        if (PathBoundary.IsWithin(realSource, realTarget) || PathBoundary.IsWithin(realTarget, realSource))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", target));
    }

    // Game content only: the marker and the hash sidecar are Phaket metadata (same rule as
    // PlanExecutor.EnumerateVanilla and VanillaCache.ComputeTreeHash).
    private static List<string> EnumerateContent(string source) =>
        Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not (VanillaCache.MarkerFileName or VanillaCache.HashFileName))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    // The hash recorded at fetch time is the trust anchor; a cache written before PAK-100
    // carries none, so there is nothing to compare against.
    private static void VerifyTreeHash(string source, string partial)
    {
        if (VanillaCache.ReadTreeHash(source) is { } expected && VanillaCache.ComputeTreeHash(partial) != expected)
            throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", source));
    }

    private static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        // Timestamps are preserved like the pre-PAK-146 copy; attribute translation across
        // volumes is not (it throws on the other platform's flags).
        try { File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Free space on the volume holding path; long.MaxValue when it cannot be read, which
    // skips the refusal rather than blocking a copy that might work. DriveInfo reads statvfs
    // on Linux and GetDiskFreeSpaceEx on Windows, so no P/Invoke of our own is involved.
    public static long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            var available = new DriveInfo(root).AvailableFreeSpace;
            return available > 0 ? available : long.MaxValue;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    // Probe: two scratch files in the destination directory, one cloned onto the other,
    // both removed. Data only — the answer is for logs and UI diagnostics.
    public static CloneSupport TryDetectCloneSupport(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var name = Path.Combine(directory, ".phaket-probe-" + Guid.NewGuid().ToString("N"));
            var source = name + ".a";
            var destination = name + ".b";
            try
            {
                File.WriteAllBytes(source, [0x50, 0x48, 0x4B, 0x01]);
                File.WriteAllBytes(destination, [0, 0, 0, 0, 0, 0]);
                return TryCloneFile(source, destination) switch
                {
                    CloneResult.Success => OperatingSystem.IsWindows() ? CloneSupport.ReFS : CloneSupport.BtrfsOrXfs,
                    CloneResult.Unsupported => CloneSupport.None,
                    _ => CloneSupport.Unknown
                };
            }
            finally
            {
                TryDelete(source);
                TryDelete(destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CloneSupport.Unknown;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static IFileCloner DefaultCloner() => new PlatformCloner();

    // One file into an existing tree (the executor copies file by file so it can journal each
    // base state): clone, then full copy. The caller-visible behaviour is File.Copy's.
    public static void CopyOne(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (TryCloneFile(source, destination) == CloneResult.Success) return;
        CopyFile(source, destination);
    }

    // Every file attempts a clone and the platform decides; the probe would only repeat the
    // same ioctl once more.
    private sealed class PlatformCloner : IFileCloner
    {
        public bool Probe(string directory) => true;
        public bool TryClone(string source, string destination) => TryCloneFile(source, destination) == CloneResult.Success;
    }

    internal enum CloneResult { Success, Unsupported, Failed }

    internal static CloneResult TryCloneFile(string source, string destination) =>
        OperatingSystem.IsWindows() ? Windows.TryClone(source, destination) : Linux.TryClone(source, destination);

    // ioctl(FICLONE) shares extents between two files on one btrfs/XFS filesystem.
    private static class Linux
    {
        private const int O_WRONLY = 1;
        private const int O_CREAT = 0x40;
        private const int O_TRUNC = 0x200;
        private const int O_CLOEXEC = 0x80000;
        private const long FICLONE = 0x40049409; // _IOW(0x94, 9, int)

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags, int mode);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, long request, int arg);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);

        public static CloneResult TryClone(string source, string destination)
        {
            // Read-only is the only way the cache side is ever opened.
            var readable = Open(source, O_CLOEXEC, 0);
            if (readable < 0) return CloneResult.Failed;
            var writable = Open(destination, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0x1B4 /* 0664 */);
            if (writable < 0) { Close(readable); return CloneResult.Failed; }
            try
            {
                if (Ioctl(writable, FICLONE, readable) == 0) return CloneResult.Success;
                // EOPNOTSUPP 95 / EXDEV 18: another filesystem or no reflink support.
                return Marshal.GetLastPInvokeError() is 95 or 18 ? CloneResult.Unsupported : CloneResult.Failed;
            }
            finally
            {
                Close(readable);
                Close(writable);
            }
        }
    }

    // FSCTL_DUPLICATE_EXTENTS_TO_FILE: "The FSCTL is sent on a handle opened to the target file"
    // and DUPLICATE_EXTENTS_DATA {FileHandle, SourceFileOffset, TargetFileOffset, ByteCount} carries
    // "a handle to the source file" (MS-FSCC 2.3.7.1, winioctl.h). The review claim of a
    // SourceHandle/DestinationHandle/SizeOfThis structure is not in the Windows contract.
    // The block-clone rules (Block cloning on ReFS) require cluster-aligned offset/length, align
    // the destination file first, and cap a single clone at 4 GB - a violating ioctl answers
    // STATUS_INVALID_PARAMETER, which this method would read as "unsupported" while leaving an
    // empty destination behind, so those are fixed up here instead. A non-ReFS volume answers
    // ERROR_INVALID_PARAMETER / ERROR_NOT_SUPPORTED, which is still the fallback signal.
    // shortcut: ReFS/Windows has no CI runner yet, so neither the ioctl nor its alignment is
    // exercised; the raw extent copy is anyway gated by the hash check in MaterializeAsync.
    // Re-check at the first Windows run of release.yml.
    private static class Windows
    {
        private const uint FsctlDuplicateExtentsToFile = 0x94344;
        private const int ErrorNotSupported = 50;
        private const int ErrorInvalidParameter = 87;
        private const long MaxCloneBytes = 4L << 30;

        // GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING, NO_BUFFERING.
        private const uint SourceAccess = 0x80000000;
        private const uint DestinationAccess = 0x40000000;
        private const uint ShareAll = 3;
        private const uint OpenExisting = 3;
        private const uint NoBuffering = 0x20000000;

        [DllImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, ref DuplicateExtentsData inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32", EntryPoint = "GetFileSizeEx", SetLastError = true)]
        private static extern bool GetFileSizeEx(IntPtr handle, out long size);

        // "The destination region must not extend past the end of the file": the clone only fills
        // the first cluster, the rest is allocated by SetEndOfFile beforehand.
        [DllImport("kernel32", EntryPoint = "SetFilePointerEx", SetLastError = true)]
        private static extern bool SetFilePointerEx(IntPtr handle, long distance, out long newPosition, uint method);

        [DllImport("kernel32", EntryPoint = "SetEndOfFile", SetLastError = true)]
        private static extern bool SetEndOfFile(IntPtr handle);

        [DllImport("kernel32", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeInformation(string root, char[]? volumeName, int volumeNameSize, out int serial, out int maxComponentLength, out int flags, char[]? fileSystemName, int fileSystemNameSize);

        [DllImport("kernel32", EntryPoint = "CloseHandle")]
        private static extern bool CloseHandle(IntPtr handle);

        private static readonly IntPtr Invalid = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct DuplicateExtentsData
        {
            public IntPtr FileHandle;
            public long SourceFileOffset;
            public long TargetFileOffset;
            public long ByteCount;
        }

        public static CloneResult TryClone(string source, string destination)
        {
            // GENERIC_READ + FILE_SHARE_READ only for the cache side.
            var sourceHandle = CreateFile(source, SourceAccess, 1, IntPtr.Zero, OpenExisting, NoBuffering, IntPtr.Zero);
            if (sourceHandle == Invalid) return CloneResult.Failed;
            var destinationHandle = Invalid;
            try
            {
                if (!GetFileSizeEx(sourceHandle, out var size) || size <= 0) return CloneResult.Success;
                // Always created, even for a failed attempt: the caller's fallback copy then
                // overwrites it instead of tripping over a missing parent.
                destinationHandle = CreateFile(destination, DestinationAccess, ShareAll, IntPtr.Zero, 2 /* CREATE_ALWAYS */, NoBuffering, IntPtr.Zero);
                if (destinationHandle == Invalid) return CloneResult.Failed;
                if (!SetFilePointerEx(destinationHandle, size, out _, 0) || !SetEndOfFile(destinationHandle))
                    return CloneResult.Failed;
                var align = ClusterSize(destination);
                var request = new DuplicateExtentsData { FileHandle = sourceHandle };
                for (long offset = 0; offset < size;)
                {
                    request.SourceFileOffset = request.TargetFileOffset = offset;
                    request.ByteCount = Math.Min((size - offset + align - 1) / align * align, MaxCloneBytes);
                    if (!DeviceIoControl(destinationHandle, FsctlDuplicateExtentsToFile, ref request, Marshal.SizeOf<DuplicateExtentsData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        return error is ErrorNotSupported or ErrorInvalidParameter ? CloneResult.Unsupported : CloneResult.Failed;
                    }
                    offset += request.ByteCount;
                }
                // The fallback copy is skipped on Success, so the size is the only check that the
                // clone left the whole file behind; a mismatch must not reach the caller as Success.
                if (!GetFileSizeEx(destinationHandle, out var written) || written != size) return CloneResult.Failed;
                return CloneResult.Success;
            }
            finally
            {
                if (destinationHandle != Invalid) CloseHandle(destinationHandle);
                CloseHandle(sourceHandle);
            }
        }

        // ReFS documents a 64 KiB cluster and refuses a non-cluster-aligned range; the sector is
        // its divisor, so it is a safe alignment when the volume cannot report the cluster.
        private static long ClusterSize(string path)
        {
            if (Path.GetPathRoot(path) is { } root && GetVolumeInformation(root, null, 0, out _, out var sector, out var cluster, null, 0))
                return Math.Max(cluster >= sector ? cluster : sector, 1);
            return 1;
        }
    }
}
