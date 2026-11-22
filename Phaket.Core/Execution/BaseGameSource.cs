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

    // Cache lives under the package root, one directory per store+version.
    public static string DirectoryFor(string packageRoot, string store, string version) =>
        Path.Combine(packageRoot, "vanilla", Sanitize(store), Sanitize(version));

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
        var temp = Path.Combine(cacheDirectory, MarkerFileName) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(marker));
        File.Move(temp, Path.Combine(cacheDirectory, MarkerFileName), overwrite: true);
    }

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
