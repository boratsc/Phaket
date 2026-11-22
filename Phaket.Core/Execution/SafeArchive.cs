using System.IO.Compression;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// Zip extraction with the alpha safety limits (spec §9): zip-slip, decompression
// bombs, symlink entries and dangerous permissions are rejected. The whole
// archive is validated BEFORE any write (two-pass), and destinations are
// symlink-resolved so existing links cannot redirect writes outside the root.
public static class SafeArchive
{
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxEntries = 100_000;
    public const int MaxRatio = 100;

    // Pass 1: validate every entry and compute resolved destinations. Every entry is
    // validated even when `include` filters it out, so an archive with a traversal
    // entry is rejected regardless of what the recipe asks to extract.
    // `include` holds path globs matched against the path AFTER stripComponents.
    public static IReadOnlyList<string> PlanWrites(string archivePath, string targetRoot, string artifactId,
        int stripComponents = 0, IReadOnlyList<string>? include = null)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(targetRoot);
        var planned = new List<string>();
        long total = 0, compressed = 0;
        if (zip.Entries.Count > MaxEntries)
            throw Unsafe(artifactId, $"too many entries: {zip.Entries.Count}");
        foreach (var entry in zip.Entries)
        {
            var destination = ValidateEntry(entry, root, artifactId, stripComponents);
            if (destination is null) continue; // directory entry
            if (!Included(entry.FullName, stripComponents, include)) continue;
            total += entry.Length;
            compressed += entry.CompressedLength;
            if (total > MaxTotalBytes) throw Unsafe(artifactId, "uncompressed size limit exceeded");
            if (compressed > 0 && total / compressed > MaxRatio) throw Unsafe(artifactId, "decompression ratio limit exceeded");
            planned.Add(destination);
        }
        return planned;
    }

    // Directories the extract would create that do NOT exist yet, deepest last.
    // Recorded in the journal BEFORE any write so a partial failure can be cleaned.
    public static IReadOnlyList<string> PlanDirectories(string archivePath, string targetRoot, string artifactId,
        int stripComponents = 0, IReadOnlyList<string>? include = null)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(targetRoot);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeName(entry.FullName, stripComponents);
            if (name is null || name.Length == 0) continue;
            // With an include list, directories are exactly the parents of included
            // files; the archive's own directory entries are not extracted. An empty
            // list (not null) includes nothing, so no directories are planned.
            if (include is not null)
            {
                if (name.EndsWith('/')) continue;
                if (!Included(entry.FullName, stripComponents, include)) continue;
            }
            var trimmed = name.TrimEnd('/');
            if (trimmed.Length == 0) continue;
            // Every ancestor directory of each entry, resolved to its real path.
            var dir = Path.GetDirectoryName(trimmed)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(dir))
            {
                var resolved = ResolveDestination(root, dir, artifactId);
                if (!dirs.Add(resolved)) break; // ancestor already seen -> its parents too
                dir = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            }
        }
        // Shallowest-first (deterministic): rollback deletes in reverse (deepest-first).
        return dirs.Where(d => !Directory.Exists(d))
            .OrderBy(d => d.Length)
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToArray();
    }

    // Pass 2: write the pre-validated entries. beforeOverwrite runs immediately
    // before each file write (base-state capture).
    public static IReadOnlyList<string> Extract(string archivePath, string targetRoot, string artifactId,
        int stripComponents = 0, Action<string>? beforeOverwrite = null, IReadOnlyList<string>? include = null)
    {
        var planned = PlanWrites(archivePath, targetRoot, artifactId, stripComponents, include);
        using var zip = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(targetRoot);
        Directory.CreateDirectory(root);
        var created = new List<string>();
        var index = 0;
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeName(entry.FullName, stripComponents);
            if (name is null) continue;
            if (include is not null)
            {
                // Parent directories appear on demand when included files are written.
                if (name.EndsWith('/')) continue;
                if (!Included(entry.FullName, stripComponents, include)) continue;
            }
            if (name.Length == 0 || entry.FullName.Replace('\\', '/').EndsWith('/'))
            {
                var dirName = name.TrimEnd('/');
                if (dirName.Length > 0) Directory.CreateDirectory(ResolveDestination(root, dirName, artifactId));
                continue;
            }
            var destination = planned[index++];
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            beforeOverwrite?.Invoke(destination);
            var temp = destination + ".phaket-tmp";
            using (var source = entry.Open())
            using (var output = File.Create(temp))
            {
                source.CopyTo(output);
            }
            File.Move(temp, destination, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            created.Add(destination);
        }
        return created;
    }

    private static string? NormalizeName(string fullName, int stripComponents)
    {
        var name = fullName.Replace('\\', '/');
        if (stripComponents > 0)
        {
            var parts = name.Split('/');
            if (parts.Length <= stripComponents) return null;
            name = string.Join('/', parts.Skip(stripComponents));
        }
        return name.Length == 0 ? null : name;
    }

    // stripComponents is applied first, then the stripped path is matched: an
    // archive that wraps everything in one folder still matches `BepInEx/**`.
    // Null means "no filter" (the whole archive); an empty list is a filter that
    // matches nothing and must not silently fail open.
    public static bool MatchesInclude(string fullName, int stripComponents, IReadOnlyList<string>? include) =>
        Included(fullName, stripComponents, include);

    private static bool Included(string fullName, int stripComponents, IReadOnlyList<string>? include)
    {
        if (include is null) return true;
        if (include.Count == 0) return false;
        var name = NormalizeName(fullName, stripComponents)?.TrimEnd('/');
        return name is { Length: > 0 } && include.Any(pattern => Glob.IsMatch(pattern, name));
    }

    // Returns the resolved file destination, or null for directory entries.
    private static string? ValidateEntry(ZipArchiveEntry entry, string root, string artifactId, int stripComponents)
    {
        var name = NormalizeName(entry.FullName, stripComponents);
        if (name is null) return null;

        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        if ((unixMode & 0xF000) == 0xA000)
            throw Unsafe(artifactId, $"symlink entry: {entry.FullName}");

        var isDirectory = name.EndsWith('/');
        var trimmed = name.TrimEnd('/');
        if (trimmed.Length == 0) return null;
        if (trimmed.Split('/').Any(s => s is "" or "." or "..") || trimmed.Any(char.IsControl) || trimmed.Contains(':'))
            throw Unsafe(artifactId, $"unsafe entry path: {entry.FullName}");

        var destination = ResolveDestination(root, trimmed, artifactId);
        return isDirectory ? null : destination;
    }

    private static string ResolveDestination(string root, string relative, string artifactId)
    {
        var lexical = Path.GetFullPath(Path.Combine(root, relative));
        if (!PathBoundary.IsWithin(root, lexical))
            throw Unsafe(artifactId, $"entry escapes target: {relative}");
        try
        {
            // Resolves existing symlinks; rejects redirects outside the root.
            return PathBoundary.Resolve(root, relative);
        }
        catch (PhaketException)
        {
            throw Unsafe(artifactId, $"entry redirects outside target: {relative}");
        }
    }

    private static PhaketException Unsafe(string artifactId, string reason) =>
        new(ErrorCodes.ArchiveUnsafe, ("artifact", artifactId), ("reason", reason));
}
