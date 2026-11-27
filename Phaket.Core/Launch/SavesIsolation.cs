using Phaket.Core.Omrl;

namespace Phaket.Core.Launch;

// PAK-146 (model B): can a package's saves/config land inside the package tree, or do they
// stay in the user's shared profile? Only a verified "yes" is parallel-safe; anything else
// is reported as shared instead of pretending. Data, not code: per OS, in the definition.
//
// "env" means the game honours the redirected environment variables. That is true of Unity
// on Linux (~/.config/unity3d via HOME/XDG_CONFIG_HOME) but NOT on Windows: Unity asks
// SHGetKnownFolderPath for LocalLow, which no environment variable moves. Redirecting a
// user profile is not an option, so a Unity title on Windows is Shared.
public sealed record SavesIsolationMode(string Mode, IReadOnlyList<string>? Env = null, string? Reason = null)
{
    public const string EnvRedirect = "env";
    public const string Shared = "shared";
    public const string Unknown = "unknown";

    public static SavesIsolationMode UnknownMode { get; } = new(Unknown, Reason: "saves-isolation-undetermined");
}

// Saves isolation is only known for the OSes the definition names; an unnamed OS is Unknown.
public sealed record SavesIsolation(IReadOnlyDictionary<string, SavesIsolationMode>? Os = null)
{
    public SavesIsolationMode For(string os) =>
        Os?.GetValueOrDefault(os) ?? SavesIsolationMode.UnknownMode;
}

// What one launch gets, and whether it is safe to say "parallel with vanilla".
public sealed record SavesIsolationPlan(
    string Os, string Mode, IReadOnlyDictionary<string, string> Environment, string? Reason)
{
    public bool SavesShared => Mode != SavesIsolationMode.EnvRedirect;

    public static SavesIsolationPlan Shared(string os, string reason) => new(os, SavesIsolationMode.Shared, Empty, reason);

    public static SavesIsolationPlan Unknown(string os) => Shared(os, "saves-isolation-undetermined");

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public static class SavesIsolationPlanner
{
    // Inside the package tree, so removing the package removes its saves with it.
    public const string HomeDirectoryName = ".phaket-home";

    // Empty environment / shared reason for a definition without an explicit mode: the
    // conservative answer, never an unverified promise of isolation.
    public static SavesIsolationPlan WithoutIsolation(string os) => SavesIsolationPlan.Unknown(os);

    // An EnvRedirect mode only takes effect for a package tree (isPackage); vanilla and
    // launcher-handled launches (steam://) keep the user's real profile untouched.
    public static SavesIsolationPlan Plan(string os, SavesIsolationMode mode, string? packageRoot)
    {
        if (mode.Mode != SavesIsolationMode.EnvRedirect)
            return mode.Mode == SavesIsolationMode.Shared
                ? SavesIsolationPlan.Shared(os, mode.Reason ?? "saves-shared")
                : SavesIsolationPlan.Unknown(os);
        if (string.IsNullOrWhiteSpace(packageRoot) || mode.Env is not { Count: > 0 } keys)
            return SavesIsolationPlan.Shared(os, "package-tree-missing");
        var home = Path.Combine(Path.GetFullPath(packageRoot), HomeDirectoryName);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!IsSafeName(key)) return SavesIsolationPlan.Shared(os, "saves-isolation-invalid-key");
            environment[key] = Path.Combine(home, DirectoryFor(key));
        }
        return new SavesIsolationPlan(os, SavesIsolationMode.EnvRedirect, environment, null);
    }

    // Windows: APPDATA/LOCALAPPDATA (roaming/local app data). HOME and every XDG_* share "config":
    // on Linux the saves themselves live under .config (Unity's ~/.config/unity3d), while
    // .local/share and the cache hold nothing a seed or a launch needs to preserve. Keeping
    // three directories would duplicate every seeded save three times.
    public static string DirectoryFor(string envKey) =>
        envKey is "APPDATA" or "LOCALAPPDATA" ? "data" : "config";

    // The variables a definition may redirect: a fixed set, so a catalog entry can never
    // point PATH or another executable lookup variable at a directory it controls.
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME",
        "APPDATA", "LOCALAPPDATA",
    };

    public static bool IsSafeName(string key) => AllowedKeys.Contains(key);
}