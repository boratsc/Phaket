using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution.SaveBackup;

// GameId is the human-facing game name (used for the backup folder), InstallationId is
// the stable identity the folder falls back to when the name sanitizes to nothing.
public sealed record SaveBackupRequest(string GameId, string InstallationId, IReadOnlyList<string> Locations);

// Local save backups (PAK-141): copy the saves (and anything a recipe may
// overwrite) BEFORE a recipe runs, keep a manifest, list and restore them.
// The caller supplies the save paths; game definitions do not declare them yet.
//
// Backups live in backupRoot/<sanitized game name>/<id>/: manifest.json plus
// files/<location-key>/... for each save location's tree.
//
// PAK-141 review: the save tree is user data and a manifest is data, not a path grant.
// Every walk refuses to enter directory links, every write/delete is resolved against the
// approved root, and a restore validates the whole manifest before it touches a file.
public sealed class SaveBackupService
{
    public const long DefaultLimitBytes = 1024L * 1024 * 1024;
    public const int MaxNameLength = 120;
    // A backup without copies: records that nothing existed, so a restore removes
    // what the recipe later wrote. Returned by CreateBackupAsync for an empty location list.
    public const string EmptySchemaVersion = "save-backup-empty/v1";
    // A plan-scoped profile of the save folders when the full copy does not fit the
    // limit: records paths/mtimes only, so the install is never blocked by space and a
    // profile restore never deletes anything.
    public const string ProfileSchemaVersion = "save-backup-profile/v1";
    private const string ManifestFileName = "manifest.json";
    private const string FilesDirectoryName = "files";
    private const string JournalFileName = ".phaket-restore-journal";
    private const string TempSuffix = ".phaket-tmp";
    private const string CreateTempDirectory = ".phaket-create.tmp";

    // Invalid file name characters (Heroic's INVALID_FILENAME_CHARS) plus control characters.
    private static readonly Regex InvalidNameChars = new(@"[<>:""/\\|?*\p{Cc}]", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingDotsOrSpaces = new(@"[. ]+$", RegexOptions.CultureInvariant);

    // Backup folder for a game: the sanitized title, falling back to the installation id.
    public static string BackupFolderName(string? title, string installationId)
    {
        var sanitized = Clean(title);
        return sanitized.Length > 0 ? sanitized : Clean(installationId);
    }

    private static string Clean(string? value)
    {
        var text = TrailingDotsOrSpaces.Replace(InvalidNameChars.Replace(value ?? "", "_"), "").Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
    }

    public Task<SaveBackupManifest> CreateBackupAsync(SaveBackupRequest request, string backupRoot, long limitBytes,
        CancellationToken ct = default) => Task.Run(() => CreateBackup(request, backupRoot, limitBytes, ct, BeforeCopy), ct);

    // Test seam only (the app never sets it): called with each scanned file between the
    // pre-scan stat and its copy, so a stale scan can be produced deterministically.
    internal Action<string>? BeforeCopy { get; set; }

    public Task<IReadOnlyList<SaveBackupManifest>> ListBackupsAsync(string backupRoot, string gameFolder,
        CancellationToken ct = default) => Task.Run(() => ListBackups(backupRoot, gameFolder), ct);

    // Restores one location from a backup folder to exactly the state it was captured in.
    public Task<SaveBackupManifest> RestoreAsync(string backupDirectory, string location, CancellationToken ct = default) =>
        Task.Run(() => Restore(backupDirectory, location, ct), ct);

    // Deletes the oldest backups of one game until keepCount/keepBytes hold.
    // Irreversible: without consent it refuses before anything is touched. Deletes only
    // folders that ListBackupsAsync itself returned for this root.
    public Task<IReadOnlyList<SaveBackupManifest>> PruneAsync(string backupRoot, string gameFolder, int keepCount,
        long keepBytes, bool consent, CancellationToken ct = default) =>
        Task.Run(() => Prune(backupRoot, gameFolder, keepCount, keepBytes, consent, ct), ct);

    private static SaveBackupManifest CreateBackup(SaveBackupRequest request, string backupRoot, long limitBytes, CancellationToken ct,
        Action<string>? beforeCopy = null)
    {
        if (limitBytes <= 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "backup limit must be positive"));
        var locations = request.Locations.Distinct(PathComparer).Select(Path.GetFullPath).ToArray();
        // A link as the save location itself would copy a tree the caller never approved. A
        // missing location is not a link: it is recorded as "did not exist", and its absence is
        // what lets RecordAsync fall back to a file-less profile when the copy does not fit.
        foreach (var location in locations)
            if (IsLinkedDirectory(location))
                throw new PhaketException(ErrorCodes.SaveBackupRestoreRefused, ("path", location));

        // Stat everything first: the limit is decided before a single byte is copied.
        var trees = locations.Select(location =>
        {
            ct.ThrowIfCancellationRequested();
            var existed = Directory.Exists(location);
            var files = existed ? SaveTree.Of(location, ct).Select(f => f.FullPath).ToArray() : [];
            return (Location: location, Existed: existed, Files: files,
                Bytes: files.Sum(f => new FileInfo(f).Length));
        }).ToArray();
        var total = trees.Sum(t => t.Bytes);
        if (total > limitBytes)
            throw new PhaketException(ErrorCodes.SaveBackupTooLarge, ("size", FormatBytes(total)), ("limit", FormatBytes(limitBytes)));

        var gameRoot = Path.Combine(backupRoot, BackupFolderName(request.GameId, request.InstallationId));
        var createdAt = DateTimeOffset.UtcNow;
        var id = NewId(gameRoot, createdAt);
        var temp = Path.Combine(gameRoot, CreateTempDirectory); // a killed copy leaves no half backup
        Directory.CreateDirectory(gameRoot);
        try
        {
            // Created only after the pre-scan: its existence is what tells a watcher the copy is
            // about to start, and its own .tmp suffix keeps it out of any later restore.
            Directory.CreateDirectory(temp);
            var fileEntries = new List<SaveBackupFile>(trees.Sum(t => t.Files.Length));
            long copied = 0;
            for (var i = 0; i < trees.Length; i++)
                foreach (var file in trees[i].Files)
                {
                    ct.ThrowIfCancellationRequested();
                    // Test seam: runs after the pre-scan stat and before the copy, so a test can
                    // make the scan demonstrably stale instead of racing a real writer.
                    beforeCopy?.Invoke(file);
                    var key = RelativeOf(trees[i].Location, file);
                    var destination = Path.Combine(temp, FilesDirectoryName, LocationKey(trees[i].Location), key);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // The scan can be stale: the real byte count is checked as it copies, so
                    // the limit holds even if a file grew or the tree changed meanwhile.
                    copied += CopyWithinLimit(file, destination, limitBytes - copied, request.GameId);
                    var info = new FileInfo(destination);
                    fileEntries.Add(new(key, info.Length, info.LastWriteTimeUtc, HashFile(destination)));
                }

            var manifest = new SaveBackupManifest(
                trees.Length == 0 ? EmptySchemaVersion : SaveBackupManifest.CurrentSchemaVersion,
                id, createdAt, request.GameId, request.InstallationId,
                trees.Select(t => new SaveBackupLocation(t.Location, t.Existed,
                        t.Files.Select(f => RelativeOf(t.Location, f)).OrderBy(f => f, StringComparer.Ordinal).ToArray())).ToArray(),
                fileEntries.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(), total, null);
            manifest = manifest with { StateHash = manifest.ComputeStateHash() };
            File.WriteAllText(Path.Combine(temp, ManifestFileName), SaveBackupManifest.Format(manifest));
            Directory.Move(temp, Path.Combine(gameRoot, id));
            return manifest;
        }
        catch
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            throw;
        }
    }

    // Copies one file and counts what was actually read: a save tree that grew after the
    // (cheap) pre-scan is detected here, the number the limit is checked against.
    private static long CopyWithinLimit(string source, string destination, long remaining, string gameId)
    {
        long written = 0;
        // Only-Read: the source stays locked against writes until the whole file is copied, so
        // no byte can slip in after the check below and make the limit a lie.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > remaining)
                throw new PhaketException(ErrorCodes.SaveBackupChangedDuringCopy, ("game", gameId), ("path", source),
                    ("pending", (written - remaining).ToString(CultureInfo.InvariantCulture)));
            output.Write(buffer, 0, read);
        }
        return written;
    }

    private static IReadOnlyList<SaveBackupManifest> ListBackups(string backupRoot, string gameFolder) =>
        FindBackups(backupRoot, gameFolder).OrderByDescending(b => b.Manifest.CreatedAt).Select(b => b.Manifest).ToArray();

    // The whole backup is validated and every hash verified before the first write: a
    // damaged or forged copy never leaves a half-restored (or half-deleted) save folder.
    private static SaveBackupManifest Restore(string backupDirectory, string location, CancellationToken ct)
    {
        var manifest = ReadManifest(backupDirectory);
        if (manifest.StateHash != manifest.ComputeStateHash())
            throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));
        var targetRoot = Path.GetFullPath(location);
        if (!IsPlainDirectory(targetRoot))
            throw new PhaketException(ErrorCodes.SaveBackupRestoreRefused, ("path", targetRoot));
        var entry = manifest.Locations.FirstOrDefault(l => SamePath(l.Location, location))
            ?? throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"backup {manifest.Id} does not cover save location {location}"));
        var sourceRoot = Path.Combine(backupDirectory, FilesDirectoryName, LocationKey(targetRoot));
        // A profile record keeps every file in place; a backup with no files at all is not a
        // backup: refusing to restore beats guessing what to keep.
        if (manifest.SchemaVersion == ProfileSchemaVersion)
        {
            Directory.CreateDirectory(targetRoot);
            return manifest;
        }
        if (manifest.Files.Count == 0)
            throw new PhaketException(ErrorCodes.SaveBackupRestoreRefused, ("path", targetRoot));
        // The copy tree is walked without following links; a link where it should be is refused.
        if (!IsPlainDirectory(Path.Combine(backupDirectory, FilesDirectoryName)) || !IsPlainDirectory(sourceRoot))
            throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));

        ValidateManifest(manifest, entry, backupDirectory);

        // Phase 1: verify every hash before anything on disk is touched.
        var restore = new Dictionary<string, SaveBackupFile>(StringComparer.Ordinal);
        foreach (var file in manifest.Files.Where(f => entry.Files.Contains(f.RelativePath, StringComparer.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            var source = SourcePath(sourceRoot, file.RelativePath);
            if (source is null || !File.Exists(source) || HashFile(source) != file.Sha256)
                throw new PhaketException(ErrorCodes.SaveBackupHashMismatch, ("id", manifest.Id), ("path", file.RelativePath));
            restore[file.RelativePath] = file;
        }
        // Only what the backup listed but did not keep is deleted: a profile record, or a file
        // the copy tree lost. A file that appeared after the backup is the user's and stays.
        var deletes = entry.Files.Where(f => !restore.ContainsKey(f)).ToArray();

        // Phase 2: copies before deletions, journalled, so an interruption can be finished or undone.
        var journal = new List<(string RelativePath, string Backup)>();
        foreach (var file in restore.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            RestoreOne(targetRoot, sourceRoot, file.RelativePath);
            journal.Add((file.RelativePath, file.Sha256));
            WriteJournal(targetRoot, entry, deletes, journal);
        }
        foreach (var relative in deletes)
        {
            ct.ThrowIfCancellationRequested();
            DeleteInside(targetRoot, relative);
        }
        PruneEmptyDirectories(targetRoot, ct);
        if (!entry.Existed && !Directory.EnumerateFileSystemEntries(targetRoot).Any()) Directory.Delete(targetRoot);
        DeleteJournal(targetRoot, ct);
        return manifest;
    }

    // Structure and containment are checked before any write: a manifest is data, and a
    // rewritten one with a valid state hash must not turn into a delete-anything grant.
    private static void ValidateManifest(SaveBackupManifest manifest, SaveBackupLocation entry, string backupDirectory)
    {
        if (manifest.SchemaVersion is not (SaveBackupManifest.CurrentSchemaVersion or EmptySchemaVersion or ProfileSchemaVersion))
            throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));
        // The backup folder must be exactly <root>/<sanitized game>/<id>: a manifest copied
        // out of place is not the copy its own description claims to be.
        var directory = Path.GetFullPath(backupDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(directory);
        var expectedFolder = SaveBackupService.BackupFolderName(manifest.GameId, manifest.InstallationId);
        if (parent is null || !string.Equals(Path.GetFileName(parent), expectedFolder, Comparison)
            || !string.Equals(Path.GetFileName(directory), manifest.Id, Comparison))
            throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));

        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenFilesPath = new HashSet<string>(PathComparer);
        foreach (var file in manifest.Files)
        {
            if (!IsSafeRelativePath(file.RelativePath) || !seenFiles.Add(file.RelativePath)
                || !seenFilesPath.Add(Path.GetFullPath(Path.Combine(entry.Location, file.RelativePath))))
                throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));
        }
        var seenLocations = new HashSet<string>(PathComparer);
        foreach (var location in manifest.Locations)
        {
            if (!Path.IsPathRooted(location.Location) || !seenLocations.Add(location.Location))
                throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));
            foreach (var relative in location.Files)
                if (!IsSafeRelativePath(relative) || !seenFiles.Contains(relative))
                    throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));
        }
    }

    // A manifest key is a plain relative posix path: no root, no drive, no ..
    private static bool IsSafeRelativePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl))
            return false;
        var parts = relative.Split('/');
        return parts.All(p => p.Length > 0 && p != "." && p != "..");
    }

    // One file: copied next to its target, then swapped in place. The swap itself is
    // atomic; the journal lets an interrupted restore between files be finished.
    private static void RestoreOne(string targetRoot, string sourceRoot, string relative)
    {
        var destination = PathBoundary.Resolve(targetRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + TempSuffix;
        File.Copy(SourcePath(sourceRoot, relative)!, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    private static void DeleteInside(string targetRoot, string relative)
    {
        var path = PathBoundary.Resolve(targetRoot, relative);
        if (File.Exists(path)) File.Delete(path);
    }

    // Only empty directories: a directory that still holds anything is not ours to drop.
    private static void PruneEmptyDirectories(string targetRoot, CancellationToken ct)
    {
        foreach (var directory in Directory.GetDirectories(targetRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    // The pending writes of an interrupted restore: one line per file, "<sha>\t<relative path>".
    // It only ever lives inside the save location itself.
    private static void WriteJournal(string targetRoot, SaveBackupLocation entry, IEnumerable<string> deletes,
        IEnumerable<(string RelativePath, string Backup)> done)
    {
        var text = new StringBuilder();
        text.AppendLine(entry.Location);
        foreach (var (relative, hash) in done) text.AppendLine($"{hash}\t{relative}");
        foreach (var relative in deletes) text.AppendLine($"!\t{relative}");
        File.WriteAllText(JournalPath(targetRoot), text.ToString());
    }

    private static void DeleteJournal(string targetRoot, CancellationToken ct)
    {
        var path = JournalPath(targetRoot);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
    }

    private static string JournalPath(string targetRoot) => Path.Combine(targetRoot, JournalFileName);

    // Profile records the save location as it is now WITHOUT copying any file. Used
    // when the planned overwrites are too large to copy: the install proceeds (the
    // recipe must not be blocked by space) and a restore can only remove files that
    // did not exist — so files the user selected inside the location stay untouched.
    public static async Task<SaveBackupManifest> RecordAsync(SaveBackupRequest request, string backupRoot,
        long limitBytes, CancellationToken ct = default)
    {
        try { return await new SaveBackupService().CreateBackupAsync(request, backupRoot, limitBytes, ct).ConfigureAwait(false); }
        catch (PhaketException e) when (e.Error.Code == ErrorCodes.SaveBackupTooLarge) { }
        var createdAt = DateTimeOffset.UtcNow;
        var locations = request.Locations.Distinct(PathComparer).Select(Path.GetFullPath)
            .Select(l => new SaveBackupLocation(l, Directory.Exists(l), [])).ToArray();
        var gameRoot = Path.Combine(backupRoot, BackupFolderName(request.GameId, request.InstallationId));
        var id = NewId(gameRoot, createdAt);
        Directory.CreateDirectory(Path.Combine(gameRoot, id));
        var manifest = new SaveBackupManifest(SaveBackupService.ProfileSchemaVersion, id, createdAt,
            request.GameId, request.InstallationId, locations, [], 0, null);
        manifest = manifest with { StateHash = manifest.ComputeStateHash() };
        File.WriteAllText(Path.Combine(gameRoot, id, ManifestFileName), SaveBackupManifest.Format(manifest));
        return manifest;
    }

    private static IReadOnlyList<SaveBackupManifest> Prune(string backupRoot, string gameFolder, int keepCount,
        long keepBytes, bool consent, CancellationToken ct)
    {
        if (!consent) throw new PhaketException(ErrorCodes.SaveBackupPruneConsentRequired);
        if (keepCount < 0 || keepBytes < 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "prune limits cannot be negative"));
        // Only the folders this root itself listed, each still under it after resolving
        // links: a wrong root or a planted link cannot point the delete at anything else.
        var root = ResolveForPrune(backupRoot, gameFolder);
        var newestFirst = FindBackups(backupRoot, gameFolder).OrderByDescending(b => b.Manifest.CreatedAt).ToList();
        long total = newestFirst.Sum(b => b.Manifest.TotalBytes);
        var remaining = new List<SaveBackupManifest>(newestFirst.Count);
        for (var i = 0; i < newestFirst.Count; i++)
        {
            if (newestFirst.Count - i <= keepCount && total <= keepBytes) { remaining.Add(newestFirst[i].Manifest); continue; }
            ct.ThrowIfCancellationRequested();
            if (!PathBoundary.IsWithin(root, ResolveLinks(newestFirst[i].Directory)))
                throw new PhaketException(ErrorCodes.SaveBackupRestoreRefused, ("path", newestFirst[i].Directory));
            Directory.Delete(newestFirst[i].Directory, recursive: true);
            total -= newestFirst[i].Manifest.TotalBytes;
        }
        return remaining;
    }

    private static string ResolveForPrune(string backupRoot, string gameFolder)
    {
        var rootFull = Path.GetFullPath(backupRoot);
        var rootReal = ResolveLinks(rootFull);
        var folderFull = Path.GetFullPath(Path.Combine(rootFull, gameFolder));
        var folderReal = Path.GetFullPath(Path.Combine(rootReal, gameFolder));
        if (gameFolder.Length == 0 || Path.IsPathRooted(gameFolder) || !PathBoundary.IsWithin(rootFull, folderFull)
            || !PathBoundary.IsWithin(rootReal, folderReal) || !IsPlainDirectory(folderFull))
            throw new PhaketException(ErrorCodes.SaveBackupRestoreRefused, ("path", folderFull));
        return folderReal;
    }

    // Folders of one game under backupRoot; an unreadable manifest is skipped so one
    // damaged folder does not hide the rest.
    private static IEnumerable<(string Directory, SaveBackupManifest Manifest)> FindBackups(string backupRoot, string gameFolder)
    {
        if (!Directory.Exists(backupRoot)) return [];
        return Directory.GetFiles(backupRoot, ManifestFileName, SearchOption.AllDirectories)
            .Where(file => string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))), gameFolder, Comparison))
            .Select(file => (Directory: Path.GetDirectoryName(file)!, Manifest: TryReadManifest(file)))
            .Where(b => b.Manifest is not null)
            .Select(b => (b.Directory, Manifest: b.Manifest!));
    }

    private static SaveBackupManifest ReadManifest(string backupDirectory) =>
        SaveBackupManifest.Read(Path.Combine(backupDirectory, ManifestFileName));

    private static SaveBackupManifest? TryReadManifest(string path)
    {
        try { return SaveBackupManifest.Read(path); }
        catch (PhaketException) { return null; }
    }

    // A stored key never escapes its folder: a manifest is data, not a path grant.
    private static string? SourcePath(string sourceRoot, string key)
    {
        try
        {
            if (!IsSafeRelativePath(key)) return null;
            var full = Path.GetFullPath(Path.Combine(sourceRoot, key));
            return PathBoundary.IsWithin(Path.GetFullPath(sourceRoot), full) ? full : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    // UTC timestamp folder name; the suffix keeps two backups in the same millisecond apart.
    private static string NewId(string gameRoot, DateTimeOffset createdAt)
    {
        var stamp = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture);
        var id = stamp;
        for (var suffix = 1; Directory.Exists(Path.Combine(gameRoot, id)); suffix++) id = $"{stamp}-{suffix}";
        return id;
    }

    // A short, stable folder key for one save location.
    private static string LocationKey(string location) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(location))))[..16];

    private static string RelativeOf(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    // An existing, non-linked file: anything a link points at is outside the approved tree.
    private static bool IsRegularFile(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    // A directory that is not itself a link (a link to one would walk another tree).
    private static bool IsPlainDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        try { return new DirectoryInfo(path).LinkTarget is null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // Only an existing directory that is a link; a missing path is not one.
    private static bool IsLinkedDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Comparison);

    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.#} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";

    // Final (link-resolved) path; components that do not exist yet stay as given.
    private static string ResolveLinks(string path)
    {
        try
        {
            var current = path;
            while (current is not null)
            {
                if (Directory.Exists(current) || File.Exists(current))
                    return new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return path;
    }
}

// The files of one save tree as the user sees it, without walking through directory
// links: a link to another folder is not copied and never listed for deletion.
internal static class SaveTree
{
    public sealed record Entry(string FullPath, string RelativePath);

    public static IEnumerable<Entry> Of(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo child) pending.Push(child.FullName);
                else yield return new Entry(entry.FullName,
                    Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'));
            }
        }
    }
}