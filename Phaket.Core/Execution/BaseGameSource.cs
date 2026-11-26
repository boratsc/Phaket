using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// Base-game (vanilla) provisioning (spec gap closed by the first real recipes):
// mods like Town of Us Mira need an EXACT game version, which the user's Steam
// library usually does not have. SUSModder solves this with its own server;
// Phaket never does — the only sources are official tooling: DepotDownloader
// (Steam, manifest-pinned) and legendary (Epic). The engine plans and journals;
// the host supplies the runner (process isolation, auth, progress).
public interface IBaseGameSource
{
    // Store id ("steam" | "epic"), game version label (e.g. "2026.8.18"),
    // optional pinned manifest id. A failure carries a readable, localized error code
    // (the executor then fails with it) instead of a raw tool string. The call may be
    // interactive (Steam sign-in), so it is async and must not run on the UI thread.
    Task<BaseGamePrepareResult> TryPrepareAsync(string store, string version, string? manifestId,
        string targetDirectory, CancellationToken ct = default);
}

public sealed record BaseGamePrepareResult(bool Success, string? ErrorCode = null, IReadOnlyDictionary<string, string>? Parameters = null)
{
    public static BaseGamePrepareResult Ok { get; } = new(true);

    public static BaseGamePrepareResult Fail(string code, params (string Key, string Value)[] parameters) =>
        new(false, code, parameters.ToDictionary(p => p.Key, p => p.Value));
}

// Per-version vanilla cache marker. A cache directory without a matching marker
// is never trusted (SUSModder rule: exact version or nothing).
public sealed record VanillaCacheMarker(
    string Store, string Version, string? ManifestId, string Source, DateTimeOffset FetchedAt);

public static class VanillaCache
{
    public const string MarkerFileName = ".phaket-vanilla.json";

    // PAK-100: one cache for every package of the same game+store+version, not one per
    // mod package, so two full packages no longer download the same vanilla twice.
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phaket", "vanilla-cache");

    // Neither root may be empty: with an empty one the key would silently land in the
    // working directory instead of the app data root.
    public static string DirectoryFor(string root, string game, string store, string version) =>
        Path.Combine(Key(root, game, store, version), Sanitize(version));

    // A mutable cache caller can see while a fetch is in flight AND the one a failed or
    // stale fetch goes to. Promotion into the cache is one rename, so a reader never sees
    // a half-copied tree.
    public static string StagingDirectoryFor(string root, string game, string store, string version) =>
        Path.Combine(Key(root, game, store, version), Sanitize(version) + ".partial-" + Guid.NewGuid().ToString("N"));

    public static string LockPath(string root, string game, string store, string version) =>
        Path.Combine(Key(root, game, store, version), ".lock");

    // The root is app data (never a cache key) — only game, store and version name directories.
    private static string Key(string root, string game, string store, string version) =>
        Path.Combine(RequireRoot(root), Sanitize(game), Sanitize(store), Sanitize(version));

    private static string RequireRoot(string root) =>
        string.IsNullOrWhiteSpace(root)
            ? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "vanilla cache root required"))
            : root;

    // Two installs of the same game+store+version serialize their fetch in-process (the
    // ProcessGate) and across processes on the lock file.
    public static IDisposable Lock(string lockPath)
    {
        var gate = ProcessGate(Path.GetFullPath(lockPath));
        gate.Wait();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new LockHandle(gate, stream);
        }
        catch (IOException)
        {
            gate.Release();
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "another process is fetching this vanilla"));
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    private static SemaphoreSlim ProcessGate(string lockPath) => Gates.GetOrAdd(lockPath, _ => new SemaphoreSlim(1, 1));

    private sealed class LockHandle(SemaphoreSlim gate, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            gate.Release();
        }
    }

    // Moves a completed fetch into the cache. One rename, so a reader never sees a half-copied
    // tree; a leftover entry is stale (no matching marker or a killed fetch), replaced never merged.
    public static void Promote(string stagingDirectory, string cacheDirectory)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
        // A leftover entry is stale (no matching marker or a killed fetch): it is replaced,
        // never merged.
        if (System.IO.Directory.Exists(cacheDirectory))
            System.IO.Directory.Delete(cacheDirectory, recursive: true);
        System.IO.Directory.Move(stagingDirectory, cacheDirectory);
    }

    // Marker written last: its presence is what makes a fetched directory a cache entry.
    public static VanillaCacheMarker? ReadMarker(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, MarkerFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<VanillaCacheMarker>(File.ReadAllText(path));
        }
        catch (JsonException) { return null; }
    }

    public static void WriteMarker(string cacheDirectory, VanillaCacheMarker marker)
    {
        System.IO.Directory.CreateDirectory(cacheDirectory);
        // PAK-100: the tree hash is recorded (or removed) with the marker, so a corrupted entry
        // is detected before it is trusted or copied; a fetch that succeeded but failed here
        // stays "not cached" and is fetched again rather than trusted.
        try
        {
            if (ComputeTreeHash(cacheDirectory) is { } hash)
            {
                var temp = Path.Combine(cacheDirectory, MarkerFileName) + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(marker));
                File.Move(temp, Path.Combine(cacheDirectory, MarkerFileName), overwrite: true);
                temp = Path.Combine(cacheDirectory, HashFileName) + ".tmp";
                File.WriteAllText(temp, hash);
                File.Move(temp, Path.Combine(cacheDirectory, HashFileName), overwrite: true);
                return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var name in new[] { MarkerFileName, HashFileName })
            if (File.Exists(Path.Combine(cacheDirectory, name)))
                File.Delete(Path.Combine(cacheDirectory, name));
    }

    // SHA-256 over "relative path, lowercase file hash" of the game content (marker files
    // excluded); null when no file could be hashed.
    public static string? ComputeTreeHash(string cacheDirectory)
    {
        if (!System.IO.Directory.Exists(cacheDirectory)) return null;
        try
        {
            long count = 0;
            var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in EnumerateContent(cacheDirectory))
            {
                var relative = Path.GetRelativePath(cacheDirectory, file).Replace('\\', '/');
                total.AppendData(System.Text.Encoding.UTF8.GetBytes(relative + "\n"));
                total.AppendData(System.Text.Encoding.UTF8.GetBytes(
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) + "\n"));
                count++;
            }
            return count == 0 ? null : Convert.ToHexStringLower(total.GetHashAndReset());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string? ReadTreeHash(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, HashFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var value = File.ReadAllText(path).Trim();
            return value.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c)) ? value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // Game content only: the marker and the hash sidecar are Phaket metadata.
    private static IEnumerable<string> EnumerateContent(string cacheDirectory) =>
        System.IO.Directory.GetFiles(cacheDirectory, "*", SearchOption.AllDirectories)
            .Where(f => f != Path.Combine(cacheDirectory, MarkerFileName) && f != Path.Combine(cacheDirectory, HashFileName))
            .OrderBy(f => f, StringComparer.Ordinal);

    public const string HashFileName = ".phaket-vanilla.sha256";

    // Versions/stores become directory names; anything else is rejected.
    private static string Sanitize(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9.\-]*$")
            ? value
            : throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"invalid vanilla cache key: {value}"));
}

// Argument builders for the official tools (no shell, argument-list only).
// Mirrors SUSModder's proven invocation shapes, minus its private server.
// Both tools pin the version by MANIFEST: DepotDownloader with a Steam manifest
// id, legendary with a manifest FILE (the Epic community mirror provides them).
public static class BaseGameToolArguments
{
    public const string SteamAppId = "945360";
    public const string SteamDepotId = "945361";
    public const string EpicManifestMirror = "https://github.com/whichtwix/Data/raw/master/epic/manifests/";

    public static IReadOnlyList<string> DepotDownloader(string targetDirectory, string? manifestId, string? username = null)
    {
        var args = new List<string> { "-app", SteamAppId, "-depot", SteamDepotId, "-dir", targetDirectory };
        // The account name is an argument (the tool never prompts for it); the password
        // still goes to standard input only.
        if (!string.IsNullOrWhiteSpace(username)) { args.Add("-username"); args.Add(username); }
        if (!string.IsNullOrWhiteSpace(manifestId)) { args.Add("-manifest"); args.Add(manifestId); }
        return args;
    }

    public static IReadOnlyList<string> Legendary(string epicAppId, string manifestPath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(epicAppId))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "epic app id required for legendary"));
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "epic manifest file required to pin the version"));
        return ["install", epicAppId, "-y", "--manifest", manifestPath, "--base-path", targetDirectory];
    }
}
