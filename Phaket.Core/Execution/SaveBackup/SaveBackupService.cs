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
        CancellationToken ct = default) => Task.Run(() => CreateBackup(request, backupRoot, limitBytes, ct), ct);

    public Task<IReadOnlyList<SaveBackupManifest>> ListBackupsAsync(string backupRoot, string gameFolder,
        CancellationToken ct = default) => Task.Run(() => ListBackups(backupRoot, gameFolder), ct);

    // Restores one location from a backup folder to exactly the state it was captured in.
    public Task<SaveBackupManifest> RestoreAsync(string backupDirectory, string location, CancellationToken ct = default) =>
        Task.Run(() => Restore(backupDirectory, location), ct);

    // Deletes the oldest backups of one game until keepCount/keepBytes hold.
    // Irreversible: without consent it refuses before anything is touched.
    public Task<IReadOnlyList<SaveBackupManifest>> PruneAsync(string backupRoot, string gameFolder, int keepCount,
        long keepBytes, bool consent, CancellationToken ct = default) =>
        Task.Run(() => Prune(backupRoot, gameFolder, keepCount, keepBytes, consent), ct);

    private static SaveBackupManifest CreateBackup(SaveBackupRequest request, string backupRoot, long limitBytes, CancellationToken ct)
    {
        if (limitBytes <= 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "backup limit must be positive"));
        var locations = request.Locations.Distinct(PathComparer).Select(Path.GetFullPath).ToArray();

        // Stat everything first: the limit is decided before a single byte is copied.
        var trees = locations.Select(location =>
        {
            ct.ThrowIfCancellationRequested();
            var files = Directory.Exists(location)
                ? Directory.GetFiles(location, "*", SearchOption.AllDirectories).Where(IsRegularFile).ToArray()
                : [];
            return (Location: location, Existed: Directory.Exists(location), Files: files,
                Bytes: files.Sum(f => new FileInfo(f).Length));
        }).ToArray();
        var total = trees.Sum(t => t.Bytes);
        if (total > limitBytes)
            throw new PhaketException(ErrorCodes.SaveBackupTooLarge, ("size", FormatBytes(total)), ("limit", FormatBytes(limitBytes)));

        var gameRoot = Path.Combine(backupRoot, BackupFolderName(request.GameId, request.InstallationId));
        var createdAt = DateTimeOffset.UtcNow;
        var id = NewId(gameRoot, createdAt);
        var temp = Path.Combine(gameRoot, $".{id}.tmp"); // a killed copy leaves no half backup
        Directory.CreateDirectory(gameRoot);
        try
        {
            var fileEntries = new List<SaveBackupFile>(trees.Sum(t => t.Files.Length));
            for (var i = 0; i < trees.Length; i++)
                foreach (var file in trees[i].Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var key = RelativeOf(trees[i].Location, file);
                    var destination = Path.Combine(temp, FilesDirectoryName, LocationKey(trees[i].Location), key);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
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
            Directory.CreateDirectory(temp);
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

    private static IReadOnlyList<SaveBackupManifest> ListBackups(string backupRoot, string gameFolder) =>
        FindBackups(backupRoot, gameFolder).OrderByDescending(b => b.Manifest.CreatedAt).Select(b => b.Manifest).ToArray();

    // The whole backup is verified before the first write: a damaged copy never
    // leaves a half-restored save folder.
    private static SaveBackupManifest Restore(string backupDirectory, string location)
    {
        var manifest = ReadManifest(backupDirectory);
        if (manifest.StateHash != manifest.ComputeStateHash())
            throw new PhaketException(ErrorCodes.SaveBackupManifestInvalid, ("id", manifest.Id));
        var targetRoot = Path.GetFullPath(location);
        var entry = manifest.Locations.FirstOrDefault(l => SamePath(l.Location, location))
            ?? throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"backup {manifest.Id} does not cover save location {location}"));
        var sourceRoot = Path.Combine(backupDirectory, FilesDirectoryName, LocationKey(targetRoot));

        var expected = new Dictionary<string, SaveBackupFile>(StringComparer.Ordinal);
        foreach (var file in manifest.Files.Where(f => entry.Files.Contains(f.RelativePath, StringComparer.Ordinal)))
        {
            var source = SourcePath(sourceRoot, file.RelativePath);
            if (source is null || !File.Exists(source) || HashFile(source) != file.Sha256)
                throw new PhaketException(ErrorCodes.SaveBackupHashMismatch, ("id", manifest.Id), ("path", file.RelativePath));
            expected[file.RelativePath] = file;
        }

        Directory.CreateDirectory(targetRoot);
        foreach (var file in expected.Values)
        {
            var destination = PathBoundary.Resolve(targetRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".phaket-tmp";
            File.Copy(SourcePath(sourceRoot, file.RelativePath)!, temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }

        // Exactly the manifest state: a file added after the backup goes away, and so
        // do subdirectories the backup never contained. A profile (no copies) only
        // removes, never deletes what the user kept there.
        if (manifest.SchemaVersion == ProfileSchemaVersion)
        {
            Directory.CreateDirectory(targetRoot);
            return manifest;
        }
        var keep = expected.Keys.Select(k => Path.GetFullPath(Path.Combine(targetRoot, k))).ToHashSet(PathComparer);
        foreach (var file in Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories))
            if (!keep.Contains(Path.GetFullPath(file))) File.Delete(file);
        foreach (var directory in Directory.GetDirectories(targetRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        if (!entry.Existed && !Directory.EnumerateFileSystemEntries(targetRoot).Any()) Directory.Delete(targetRoot);
        return manifest;
    }

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
        long keepBytes, bool consent)
    {
        if (!consent) throw new PhaketException(ErrorCodes.SaveBackupPruneConsentRequired);
        if (keepCount < 0 || keepBytes < 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "prune limits cannot be negative"));
        var newestFirst = FindBackups(backupRoot, gameFolder).OrderByDescending(b => b.Manifest.CreatedAt).ToList();
        long total = newestFirst.Sum(b => b.Manifest.TotalBytes);
        var remaining = new List<SaveBackupManifest>(newestFirst.Count);
        for (var i = 0; i < newestFirst.Count; i++)
        {
            // Epoch-stamped ids sort like their timestamps; prune deletes by folder, not by name.
            if (newestFirst.Count - i <= keepCount && total <= keepBytes) { remaining.Add(newestFirst[i].Manifest); continue; }
            Directory.Delete(newestFirst[i].Directory, recursive: true);
            total -= newestFirst[i].Manifest.TotalBytes;
        }
        return remaining;
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
            if (Path.IsPathRooted(key) || key.Split('/', '\\').Any(s => s == "..")) return null;
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

    private static bool IsRegularFile(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

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
}