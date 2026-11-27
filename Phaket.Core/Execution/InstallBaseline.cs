using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// Content identity is SHA-256. Size and mtime are kept for diagnostics and the
// whole-state hash; touching a file without changing bytes is not a conflict.
public sealed record BaselineFileState(string Sha256, DateTimeOffset Mtime, long Size);

// Last mutually accepted state of one installation: canonical game-relative paths
// ('/'-separated, no ".", "..", drive or control characters) plus a hash of the map,
// so an edited file is detected instead of trusted.
public sealed record InstallBaseline(
    string SchemaVersion, string InstallationId,
    IReadOnlyDictionary<string, BaselineFileState> Files, string StateHash)
{
    public const string CurrentSchemaVersion = "install-baseline/v1";

    // One baseline per installation, next to the ownership ledger in the
    // installation-level state root, so a second profile sees the same agreed state.
    public const string FileName = "baseline.json";
    public static string DefaultPath(string stateRoot) => Path.Combine(stateRoot, FileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static InstallBaseline Create(string installationId, IReadOnlyDictionary<string, BaselineFileState> files)
    {
        var normalized = Normalize(files);
        return new InstallBaseline(CurrentSchemaVersion, installationId, normalized, CanonicalJson.Hash(normalized));
    }

    // Baseline of a whole tree (e.g. the game directory) for one installation.
    public static InstallBaseline Capture(string installationId, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"baseline root does not exist: {fullRoot}"));
        var files = new Dictionary<string, BaselineFileState>(StringComparer.Ordinal);
        // ReparsePoint skips every link during the walk, so a planted DIRECTORY link is never
        // descended into. File links are added back explicitly and recorded as links.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };
        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", options).Concat(Links(fullRoot)))
        {
            // Account-specific game data (e.g. Among Us per-account folders) is normally
            // unreadable and can vanish mid-walk: an unreadable or already-gone file is
            // left out of the agreed state instead of aborting the whole capture.
            try
            {
                if (File.Exists(path))
                    files[Path.GetRelativePath(fullRoot, path).Replace('\\', '/')] = ReadFile(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return Create(installationId, files);
    }

    // File links skipped by the walk, found without following them and never treated as a
    // directory (a link to a directory contributes nothing: its content is not in this tree).
    private static IEnumerable<string> Links(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => IsLink(path)
                && new FileInfo(path).Attributes is var attributes
                && !attributes.HasFlag(FileAttributes.Directory));

    // A file link is identified by WHAT IT POINTS AT, never by the target's content:
    // the link text is hashed, so content outside the tree can never be recorded.
    private static bool IsLink(string path) =>
        new FileInfo(path).LinkTarget is not null
        || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static BaselineFileState ReadFile(string path) =>
        IsLink(path) ? LinkState(path) : ReadContent(path);

    private static BaselineFileState ReadContent(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new BaselineFileState(Convert.ToHexStringLower(SHA256.HashData(bytes)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), bytes.LongLength);
    }

    // The recorded bytes of a link are its destination; size 0 marks "not a regular file",
    // so a link never equals the file it points to.
    private static BaselineFileState LinkState(string path)
    {
        var target = System.Text.Encoding.UTF8.GetBytes(new FileInfo(path).LinkTarget ?? "");
        return new BaselineFileState(Convert.ToHexStringLower(SHA256.HashData(target)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), 0);
    }

    // Absent file is null. A symlink is its own (untrusted) state: hashed as a link, never
    // through its target, so a planted link cannot stand in for agreed content.
    public static BaselineFileState? LocalState(string path)
    {
        if (!File.Exists(path)) return null;
        return ReadFile(path);
    }

    // The agreed state a planner compares against: null when nothing was agreed yet (the plan
    // stays as before), a stale schema is ignored; anything else damaged is reported, never
    // silently replaced (an empty baseline would turn every local edit into a conflict).
    public static InstallBaseline? LoadForPlan(string stateRoot, string? installationId)
    {
        var path = DefaultPath(stateRoot);
        if (!File.Exists(path)) return null;
        try { return Load(path, installationId); }
        catch (PhaketException e) when (e.Error.Code == ErrorCodes.UnsupportedSchemaVersion) { return null; }
    }

    public static InstallBaseline Load(string path, string? expectedInstallationId = null)
    {
        if (!File.Exists(path)) return Create(expectedInstallationId ?? "", new Dictionary<string, BaselineFileState>());
        var loaded = JsonSerializer.Deserialize<InstallBaseline>(File.ReadAllText(path), Options)
            ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "baseline is empty"));
        if (loaded.SchemaVersion != CurrentSchemaVersion)
            throw new PhaketException(ErrorCodes.UnsupportedSchemaVersion, ("schemaVersion", loaded.SchemaVersion));
        if (expectedInstallationId is not null && loaded.InstallationId != expectedInstallationId)
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"baseline belongs to installation '{loaded.InstallationId}', not '{expectedInstallationId}'"));
        var normalized = Normalize(loaded.Files);
        if (!string.Equals(CanonicalJson.Hash(normalized), loaded.StateHash, StringComparison.Ordinal))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "baseline stateHash does not match its files"));
        return loaded with { Files = normalized };
    }

    // Temp file + move: a crash never leaves a half-written baseline behind.
    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static SortedDictionary<string, BaselineFileState?> NormalizeSnapshot(
        IReadOnlyDictionary<string, BaselineFileState?> snapshot)
    {
        var normalized = new SortedDictionary<string, BaselineFileState?>(StringComparer.Ordinal);
        foreach (var (path, file) in snapshot)
            normalized[CanonicalPath(path)] = file is null ? null : Valid(path, file);
        return normalized;
    }

    // SortedDictionary equality ignores key order (records compare dictionaries by
    // reference), so a round-trip test can compare the renormalized map instead.
    public static bool SameEntries(InstallBaseline left, InstallBaseline right) =>
        left.SchemaVersion == right.SchemaVersion && left.InstallationId == right.InstallationId
        && left.StateHash == right.StateHash && left.Files.Count == right.Files.Count
        && left.Files.All(kv => right.Files.TryGetValue(kv.Key, out var other) && kv.Value == other);

    private static SortedDictionary<string, BaselineFileState> Normalize(IReadOnlyDictionary<string, BaselineFileState> files)
    {
        var normalized = new SortedDictionary<string, BaselineFileState>(StringComparer.Ordinal);
        foreach (var (path, file) in files) normalized[CanonicalPath(path)] = Valid(path, file);
        return normalized;
    }

    private static BaselineFileState Valid(string path, BaselineFileState file)
    {
        if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"invalid SHA-256 for {path}"));
        if (file.Size < 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"invalid size for {path}"));
        return file with { Sha256 = file.Sha256.ToLowerInvariant() };
    }

    private static string CanonicalPath(string path)
    {
        var canonical = path.Replace('\\', '/');
        if (canonical.Length == 0 || canonical.StartsWith('/') || canonical.Contains(':') || canonical.Any(char.IsControl)
            || canonical.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"invalid baseline path: {path}"));
        return canonical;
    }
}