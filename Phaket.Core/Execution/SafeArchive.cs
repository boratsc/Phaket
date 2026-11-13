using System.IO.Compression;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// Zip extraction with the alpha safety limits (spec §9): zip-slip, decompression
// bombs, symlink entries and dangerous permissions are rejected, never applied.
public static class SafeArchive
{
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxEntries = 100_000;
    public const int MaxRatio = 100;

    public static IReadOnlyList<string> Extract(string archivePath, string targetRoot, string artifactId,
        int stripComponents = 0, Action<string>? beforeOverwrite = null)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > MaxEntries)
            throw Unsafe(artifactId, $"too many entries: {zip.Entries.Count}");

        var root = Path.GetFullPath(targetRoot);
        Directory.CreateDirectory(root);
        var created = new List<string>();
        long total = 0;
        long compressedTotal = 0;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (stripComponents > 0)
            {
                var parts = name.Split('/');
                if (parts.Length <= stripComponents) continue;
                name = string.Join('/', parts.Skip(stripComponents));
            }
            if (name.Length == 0) continue;

            // Symlink entries (unix mode in external attributes) are rejected.
            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((unixMode & 0xF000) == 0xA000)
                throw Unsafe(artifactId, $"symlink entry: {entry.FullName}");

            if (name.Split('/').Any(s => s is "" or "." or "..") || name.Any(char.IsControl) || name.Contains(':'))
                throw Unsafe(artifactId, $"unsafe entry path: {entry.FullName}");

            var destination = Path.GetFullPath(Path.Combine(root, name));
            if (!PathBoundary.IsWithin(root, destination))
                throw Unsafe(artifactId, $"entry escapes target: {entry.FullName}");

            if (entry.Length == 0 && name.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            total += entry.Length;
            compressedTotal += entry.CompressedLength;
            if (total > MaxTotalBytes)
                throw Unsafe(artifactId, "uncompressed size limit exceeded");
            if (compressedTotal > 0 && total / compressedTotal > MaxRatio)
                throw Unsafe(artifactId, "decompression ratio limit exceeded");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Base-state capture must happen BEFORE the file is overwritten.
            beforeOverwrite?.Invoke(destination);
            // Atomic-ish write: temp file in the same directory, then move.
            var temp = destination + ".phaket-tmp";
            using (var source = entry.Open())
            using (var output = File.Create(temp))
            {
                source.CopyTo(output);
            }
            File.Move(temp, destination, overwrite: true);
            // Normalized permissions: no setuid/setgid; data files not executable.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            created.Add(destination);
        }
        return created;
    }

    private static Omrl.PhaketException Unsafe(string artifactId, string reason) =>
        new Omrl.PhaketException(Omrl.ErrorCodes.ArchiveUnsafe, ("artifact", artifactId), ("reason", reason));
}
