using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Launch;

// Typed launch-strategy domain (spec §7.13.3, ADR-043/045/046). One semantic
// requirement set translates to Steam / Heroic / Legendary forms. External
// report content (ProtonDB) is DATA with provenance — never executed.
public enum RequirementStatus { Required, Recommended, Optional, Unsupported }
public enum LauncherKind { Steam, Heroic, Legendary }
public enum RuntimeKind { Proton, GEProton, Wine, Native }

public sealed record RuntimeInstallation(string Id, RuntimeKind Kind, string Version, string Path);

public sealed record LaunchProvenance(
    string Source, string SourceReference, DateTimeOffset ObservedAt, string Revision, string VerificationLevel);

// Kind: "env" | "dll-override" | "game-arg" | "wrapper"
public sealed record LaunchRequirement(
    string Kind, string Name, string Value, RequirementStatus Status, LaunchProvenance Provenance);

public sealed record LaunchScope(
    string GameEdition, string? GameBuild, string Os, LauncherKind Launcher,
    RuntimeKind Runtime, string? RuntimeVersion, string? ModLoader, string? LoaderVersion);

public sealed record GameLaunchStrategy(
    string Id, int Revision, LaunchScope Scope, IReadOnlyList<LaunchRequirement> Requirements);

public static class BepInExLaunchStrategy
{
    public static GameLaunchStrategy Create(string gameId, string os, LauncherKind launcher,
        string profilePath, LaunchProvenance provenance) =>
        new($"{gameId}-bepinex-{os}", 1,
            new LaunchScope(gameId, null, os, launcher,
                os == "linux" && gameId == "valheim" ? RuntimeKind.Native : RuntimeKind.Proton,
                null, "bepinex", gameId == "among-us" ? "6.0.700" : "5.4.2360"),
            Requirements(gameId, os, profilePath, provenance));

    private static IReadOnlyList<LaunchRequirement> Requirements(string gameId, string os,
        string profilePath, LaunchProvenance provenance)
    {
        if (os == "linux" && gameId == "valheim")
            return [
                new("env", "DOORSTOP_ENABLED", "1", RequirementStatus.Required, provenance),
                new("env", "DOORSTOP_TARGET_ASSEMBLY", $"{profilePath}/BepInEx/core/BepInEx.Preloader.dll", RequirementStatus.Required, provenance),
                new("env", "LD_LIBRARY_PATH", $"{profilePath}/doorstop_libs:$LD_LIBRARY_PATH", RequirementStatus.Required, provenance),
                new("env", "LD_PRELOAD", "libdoorstop_x64.so", RequirementStatus.Required, provenance),
            ];
        if (os == "windows" && gameId == "valheim")
            return [
                new("dll-override", "winhttp.dll", "n,b", RequirementStatus.Required, provenance),
                new("env", "DOORSTOP_TARGET_ASSEMBLY", $"{profilePath}/BepInEx/core/BepInEx.Preloader.dll", RequirementStatus.Required, provenance),
            ];
        if (os == "windows" && gameId == "among-us")
            return [new("dll-override", "winhttp.dll", "n,b", RequirementStatus.Required, provenance)];
        if (gameId == "among-us")
            return [new("dll-override", "winhttp.dll", "n,b", RequirementStatus.Required, provenance)];
        return [];
    }
}

public sealed record ExistingLaunchSettings(
    string? SteamLaunchOptions, IReadOnlyDictionary<string, string> Environment);

public sealed record LaunchPlanTranslation(
    LauncherKind Launcher,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Arguments,
    string? SteamLaunchOptions,
    string CopyInstruction);

public sealed record LaunchConflict(string Name, string ExistingValue, string ProposedValue);

public sealed record LaunchPlan(
    string StrategyId, int StrategyRevision, LaunchScope Scope,
    IReadOnlyList<LaunchPlanTranslation> Translations,
    IReadOnlyList<LaunchConflict> Conflicts,
    bool RequiresAcceptance);

public sealed record AppliedLaunchConfig(
    LauncherKind Launcher,
    IReadOnlyDictionary<string, string> PreviousEnvironment,
    string? PreviousSteamLaunchOptions,
    DateTimeOffset AppliedAt);

// Evidence kinds are distinct: process start != gameplay != mods working (ADR-046).
public sealed record LaunchEvidence(string Kind, string Detail, DateTimeOffset ObservedAt);

public static class LaunchTranslator
{
    public static LaunchPlan Plan(
        GameLaunchStrategy strategy, LauncherKind launcher,
        ExistingLaunchSettings existing, RuntimeInstallation? runtime)
    {
        if (runtime is not null && strategy.Scope.RuntimeVersion is { } required
            && !string.Equals(required, runtime.Version, StringComparison.Ordinal))
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"runtime version mismatch: strategy requires {required}, installed {runtime.Version}. No verified alternative is available."));

        var conflicts = new List<LaunchConflict>();
        var environment = new Dictionary<string, string>(existing.Environment, StringComparer.Ordinal);
        var arguments = new List<string>();
        var steamParts = new List<string>();
        var steamArgs = new List<string>();

        foreach (var requirement in strategy.Requirements.Where(r => r.Status != RequirementStatus.Unsupported))
        {
            ValidateRequirement(requirement, launcher);
            switch (requirement.Kind)
            {
                case "dll-override":
                {
                    var steamValue = $"{requirement.Name}={requirement.Value}";
                    var wineValue = $"{Path.GetFileNameWithoutExtension(requirement.Name)}={requirement.Value}";
                    var proposed = launcher == LauncherKind.Steam ? steamValue : wineValue;
                    if (existing.Environment.TryGetValue("WINEDLLOVERRIDES", out var current))
                    {
                        if (current != proposed)
                            conflicts.Add(new LaunchConflict("WINEDLLOVERRIDES", current, proposed));
                        // Existing user value is preserved; never silently replaced,
                        // and not re-emitted into Steam options on conflict.
                    }
                    else
                    {
                        environment["WINEDLLOVERRIDES"] = proposed;
                        if (launcher == LauncherKind.Steam)
                            steamParts.Add($"WINEDLLOVERRIDES=\"{steamValue}\"");
                    }
                    break;
                }
                case "env":
                    if (existing.Environment.TryGetValue(requirement.Name, out var currentEnv) && currentEnv != requirement.Value)
                    {
                        conflicts.Add(new LaunchConflict(requirement.Name, currentEnv, requirement.Value));
                    }
                    else
                    {
                        environment[requirement.Name] = requirement.Value;
                        if (launcher == LauncherKind.Steam)
                            steamParts.Add($"{requirement.Name}=\"{requirement.Value}\"");
                    }
                    break;
                case "game-arg":
                    arguments.Add(requirement.Value);
                    if (launcher == LauncherKind.Steam)
                        steamArgs.Add(requirement.Value);
                    break;
                case "wrapper":
                    arguments.Add(requirement.Value);
                    break;
                default:
                    throw new PhaketException(ErrorCodes.ValidationFailed,
                        ("detail", $"unknown requirement kind: {requirement.Kind}"));
            }
        }

        string? steamOptions = null;
        if (launcher == LauncherKind.Steam)
        {
            // Steam requires %command%. Preserve the user's existing options,
            // including game arguments that appear AFTER %command%.
            var existingOptions = existing.SteamLaunchOptions ?? "";
            var idx = existingOptions.IndexOf("%command%", StringComparison.Ordinal);
            var prefix = idx >= 0 ? existingOptions[..idx].TrimEnd() : existingOptions.TrimEnd();
            var suffix = idx >= 0 ? existingOptions[(idx + "%command%".Length)..].Trim() : "";
            var merged = string.Join(" ", new[] { prefix }.Concat(steamParts).Where(p => p.Length > 0));
            // Game arguments go AFTER %command%: before it they would be shell
            // code, not arguments to the game executable.
            var tail = string.Join(" ", new[] { suffix }.Concat(steamArgs).Where(p => p.Length > 0));
            steamOptions = (merged + " %command%" + (tail.Length > 0 ? " " + tail : "")).TrimStart();
        }

        var translation = new LaunchPlanTranslation(
            launcher, environment, arguments, steamOptions,
            BuildCopyInstruction(launcher, environment, steamOptions));
        return new LaunchPlan(strategy.Id, strategy.Revision, strategy.Scope,
            [translation], conflicts, RequiresAcceptance: true);
    }

    // Untrusted values (e.g. ProtonDB hints) must never become shell code.
    // Names are restricted to identifier syntax; Steam-bound values reject
    // shell metacharacters so a copied launch option cannot escape its quotes
    // or trigger command substitution. Rejected hints require manual review.
    private static void ValidateRequirement(LaunchRequirement requirement, LauncherKind launcher)
    {
        var nameOk = requirement.Kind == "dll-override"
            ? System.Text.RegularExpressions.Regex.IsMatch(requirement.Name, @"^[A-Za-z_][A-Za-z0-9_.]*$")
            : System.Text.RegularExpressions.Regex.IsMatch(requirement.Name, @"^[A-Za-z_][A-Za-z0-9_]*$");
        if (requirement.Kind is "env" or "dll-override" && !nameOk)
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"invalid requirement name: {requirement.Name}"));
        if (requirement.Value.Any(char.IsControl))
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"requirement {requirement.Name} contains control characters"));
        if (launcher == LauncherKind.Steam && requirement.Value.Any(c => "\"`\\;|&<>()".Contains(c)))
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"requirement {requirement.Name} contains shell metacharacters and needs manual review"));
        if (launcher == LauncherKind.Steam && requirement.Kind == "game-arg" && requirement.Value.Any(char.IsWhiteSpace))
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"requirement {requirement.Name} contains whitespace and needs manual review"));
    }

    // Fallback when the launcher has no safe write API (ADR-045): the user
    // copies the instruction; Phaket never patches launcher files blindly.
    private static string BuildCopyInstruction(
        LauncherKind launcher, IReadOnlyDictionary<string, string> environment, string? steamOptions) =>
        launcher switch
        {
            LauncherKind.Steam => $"Steam → Properties → Launch Options: {steamOptions}",
            LauncherKind.Heroic => $"Heroic → Settings → Environment: {string.Join(", ", environment.Select(kv => $"{kv.Key}={kv.Value}"))} (no %command%)",
            LauncherKind.Legendary => $"Legendary → game config env: {string.Join(", ", environment.Select(kv => $"{kv.Key}={kv.Value}"))} (no %command%)",
            _ => "",
        };
}

// Phaket-managed settings store with rollback. This is NOT a launcher file
// patcher: real launcher integration uses the copy-instruction fallback unless
// a safe API exists. The store demonstrates apply/rollback semantics on fixtures.
public sealed class LaunchConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed record StoreState(Dictionary<string, string> Environment, string? SteamLaunchOptions);

    public AppliedLaunchConfig Apply(LaunchPlan plan, LauncherKind launcher, string storePath, bool accepted)
    {
        if (!accepted || !plan.RequiresAcceptance)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "launch plan requires explicit acceptance"));
        var translation = plan.Translations.Single(t => t.Launcher == launcher);
        var previous = Load(storePath);
        var applied = new AppliedLaunchConfig(launcher,
            new Dictionary<string, string>(previous.Environment, StringComparer.Ordinal),
            previous.SteamLaunchOptions, DateTimeOffset.UtcNow);
        Save(storePath, new StoreState(
            new Dictionary<string, string>(translation.Environment, StringComparer.Ordinal),
            translation.SteamLaunchOptions));
        File.WriteAllText(storePath + ".applied.json", JsonSerializer.Serialize(applied, Options));
        return applied;
    }

    public void Rollback(string storePath)
    {
        var appliedPath = storePath + ".applied.json";
        if (!File.Exists(appliedPath)) return;
        var applied = JsonSerializer.Deserialize<AppliedLaunchConfig>(File.ReadAllText(appliedPath), Options)!;
        Save(storePath, new StoreState(
            new Dictionary<string, string>(applied.PreviousEnvironment, StringComparer.Ordinal),
            applied.PreviousSteamLaunchOptions));
        File.Delete(appliedPath);
    }

    private static StoreState Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<StoreState>(File.ReadAllText(path), Options)!
            : new StoreState([], null);

    private static void Save(string path, StoreState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, path, overwrite: true);
    }
}
