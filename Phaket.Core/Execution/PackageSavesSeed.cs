using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Launch;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// PAK-146 (model B): the first isolated launch of a package starts from the user's existing
// saves, copied once into <package>/.phaket-home. The source is only ever read — the vanilla
// saves stay where they are, so vanilla and the package never fight over one folder.
// Copy-if-absent: a file the package already has (the game wrote it) is never overwritten.
public static class PackageSavesSeed
{
    public const string MarkerFileName = ".phaket-seed.json";
    public const long DefaultLimitBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public enum SeedStatus { NotApplicable, Seeded, AlreadySeeded, TooLarge, Failed }

    public sealed record SeedResult(SeedStatus Status, int FilesCopied, long BytesCopied);

    private sealed record Marker(string SchemaVersion, string Os, IReadOnlyList<string> Sources, DateTimeOffset SeededAt);

    // targets: the redirected directories the game will see (one per environment variable,
    // e.g. HOME -> .phaket-home/config); sources: where the vanilla game keeps its saves
    // (~/.config/unity3d/..., never Phaket's profile copy). Each target is seeded once, or never
    // when the save trees are over the limit — an empty destination would look seeded.
    public static SeedResult Seed(IEnumerable<string> targets, IReadOnlyList<string> sources, string os,
        long limitBytes = DefaultLimitBytes, Func<string, long>? freeBytes = null)
    {
        var results = targets.Distinct(StringComparer.Ordinal)
            .Select(target => SeedOne(target, sources, os, limitBytes, freeBytes))
            .ToArray();
        return new SeedResult(Combine(results), results.Sum(r => r.FilesCopied), results.Sum(r => r.BytesCopied));
    }

    private static SeedStatus Combine(IReadOnlyList<SeedResult> results)
    {
        if (results.Any(r => r.Status == SeedStatus.Seeded)) return SeedStatus.Seeded;
        if (results.All(r => r.Status == SeedStatus.NotApplicable)) return SeedStatus.NotApplicable;
        if (results.Any(r => r.Status == SeedStatus.Failed)) return SeedStatus.Failed;
        if (results.Any(r => r.Status == SeedStatus.TooLarge)) return SeedStatus.TooLarge;
        return SeedStatus.AlreadySeeded;
    }

    private static SeedResult SeedOne(string target, IReadOnlyList<string> sources, string os,
        long limitBytes, Func<string, long>? freeBytes)
    {
        var fullTarget = Path.GetFullPath(target);
        var marker = Path.Combine(fullTarget, MarkerFileName);
        if (File.Exists(marker)) return new SeedResult(SeedStatus.AlreadySeeded, 0, 0);

        var roots = sources.Where(Directory.Exists)
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0) return new SeedResult(SeedStatus.NotApplicable, 0, 0);

        var files = new List<(string Source, string Relative)>();
        long total = 0;
        foreach (var root in roots)
            foreach (var entry in Walk(root))
            {
                // The footprint is whole save trees, not only what is still missing.
                total += entry.Size;
                if (total <= limitBytes) files.Add((entry.FullPath, entry.Relative));
            }
        if (total > limitBytes) return new SeedResult(SeedStatus.TooLarge, 0, 0);
        // Below the limit but not on the disk: an empty destination would look seeded.
        var free = freeBytes?.Invoke(fullTarget) ?? PackageMaterializer.FreeBytes(Path.GetDirectoryName(fullTarget) ?? fullTarget);
        if (free < total) return new SeedResult(SeedStatus.TooLarge, 0, 0);

        int copied = 0;
        long bytes = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        // Several save locations can hold the same relative file (a per-store prefix is not
        // part of the key): the first save tree keeps its layout, the next ones land under
        // saves/<n>/ so no file silently replaces another.
        foreach (var (source, relative) in files)
        {
            var candidate = used.Add(relative) ? relative : $"saves/{used.Count}/{relative}";
            string destination;
            try { destination = PathBoundary.Resolve(fullTarget, candidate); }
            catch (PhaketException) { continue; }
            // The game may already have written this file: the package's own state wins.
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var length = new FileInfo(source).Length;
            try
            {
                File.Copy(source, destination, overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            copied++;
            bytes += length;
        }

        WriteMarker(marker, new Marker("package-saves-seed/v1", os, roots, DateTimeOffset.UtcNow));
        return new SeedResult(SeedStatus.Seeded, copied, bytes);
    }

    private sealed record Entry(string FullPath, string Relative, long Size);

    // Directory links are not followed (same rule as SaveTree): a link is not save data and
    // its target is outside the source tree. File links are skipped too — a seeded copy must
    // not turn a pointer into the user's real file.
    private static IEnumerable<Entry> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo child) { pending.Push(child.FullName); continue; }
                var size = ((FileInfo)entry).Length;
                yield return new Entry(entry.FullName,
                    Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'), size);
            }
        }
    }

    private static void WriteMarker(string path, Marker marker)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(marker, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

// Ties the save definition, the capability matrix and the seeding together: the host asks
// for one plan per launch; nothing here touches a process or the UI.
public static class PackageSavesIsolation
{
    // request.PackageRoot null = vanilla launch (or steam://): no redirect, saves shared.
    public sealed record Request(string Os, SavesIsolationMode Mode, string? PackageRoot,
        IReadOnlyList<string>? SavePaths, string ProfileRoot);

    public sealed record Result(SavesIsolationPlan Isolation, PackageSavesSeed.SeedResult? Seed);

    public static Result Prepare(Request request, long limitBytes = PackageSavesSeed.DefaultLimitBytes)
    {
        var isolation = SavesIsolationPlanner.Plan(request.Os, request.Mode, request.PackageRoot);
        if (isolation.Mode != SavesIsolationMode.EnvRedirect)
            return new Result(isolation, null);
        // Only the game's real folders are read: a copy Phaket keeps under the profile is not
        // the user's progress (it would seed an empty or stale tree).
        var sources = SaveSources(request.SavePaths ?? []).Where(Directory.Exists).ToArray();
        var seed = PackageSavesSeed.Seed(isolation.Environment.Values, sources, request.Os, limitBytes);
        return new Result(isolation, seed);
    }

    // A save location in the user's home ("~/...") or an absolute path; nothing else qualifies.
    public static string[] SaveSources(IReadOnlyList<string>? savePaths) =>
        (savePaths ?? [])
            .Select(path => GameDefinition.ResolveLocation(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToArray();
}