using System.Text.Json;
using System.Text.Json.Serialization;
using Phaket.Core.Omrl;

namespace Phaket.Core.Omrl;

// Declarative loader layout: differences between games are data, never Core code.
public sealed record LoaderLayout(string Id, string Variant, IReadOnlyList<string> EntryFiles);

// Launch requirements are DATA with provenance, never code or a shell string.
// Kind mirrors Launch.LaunchRequirement: "dll-override" | "env" | "game-arg".
// Status: "required" | "recommended" | "optional". Os null/empty = every system.
public sealed record GameLaunchRequirement(
    string Kind, string Name, string Value, string Status,
    IReadOnlyList<string>? Os = null, string? Source = null, string? ObservedAt = null);

// Minimal declarative game definition (spec §6.3). Full schema: game-definition/v0alpha1.
public sealed record GameDefinition(
    string Slug,
    IReadOnlyList<string> WritableAreas,
    IReadOnlyDictionary<string, string>? StoreIds = null,
    IReadOnlyList<LoaderLayout>? LoaderLayouts = null,
    string? DetectionStrategy = null,
    IReadOnlyDictionary<string, string>? Title = null,
    string? MainExecutable = null,
    IReadOnlyList<GameLaunchRequirement>? LaunchRequirements = null,
    // Operating systems the shipped game binary actually runs on. Null = every
    // system the definition is used on. Among Us ships a Windows-only executable,
    // so under Proton on Linux its depot artifact is still "windows".
    IReadOnlyList<string>? Platforms = null,
    // Architecture a fresh sandbox/install is assumed to be when nothing was probed
    // or chosen: the CURRENT retail build, so the default loader pack matches it.
    string? DefaultArch = null)
{
    public bool AllowsWrite(string relativePath) =>
        WritableAreas.Any(pattern => Glob.IsMatch(pattern, relativePath));

    // OS to download/install the artifact for: the game's own platform, never the host's.
    public string ArtifactOs(string hostOs) =>
        Platforms is { Count: > 0 } systems && !systems.Contains(hostOs, StringComparer.Ordinal)
            ? systems[0] : hostOs;

    public LoaderLayout? Loader(string id, string? arch = null)
    {
        var layouts = LoaderLayouts?.Where(layout => layout.Id == id).ToArray() ?? [];
        if (arch is null) return layouts.FirstOrDefault();
        return layouts.FirstOrDefault(layout => layout.Variant == arch)
            ?? layouts.FirstOrDefault(layout => layout.Variant is not ("x86" or "x64"));
    }

    public IReadOnlyList<GameLaunchRequirement> LaunchRequirementsFor(string os) =>
        (LaunchRequirements ?? [])
            .Where(r => r.Os is not { Count: > 0 } systems || systems.Contains(os, StringComparer.Ordinal))
            .ToArray();
}

// Builds launcher-facing strings from declarative requirements. Values that could
// escape their quotes or inject shell code are rejected instead of sanitized away
// (same rule as Launch.LaunchTranslator): unsafe data needs manual review.
public static class GameLaunchOptions
{
    public static string? SteamLaunchOptions(GameDefinition definition, string os, bool includeOptional = false)
    {
        var parts = new List<string>();
        foreach (var requirement in definition.LaunchRequirementsFor(os))
        {
            if (requirement.Status == "optional" && !includeOptional) continue;
            if (requirement.Kind == "game-arg") { parts.Add(Validated(requirement).Value); continue; }
            var pair = requirement.Kind == "dll-override"
                ? $"{requirement.Name}={requirement.Value}"
                : requirement.Value;
            var name = requirement.Kind == "dll-override" ? "WINEDLLOVERRIDES" : Validated(requirement).Name;
            parts.Add($"{name}=\"{pair}\"");
        }
        if (parts.Count == 0) return null;
        return string.Join(" ", parts) + " %command%";
    }

    private static GameLaunchRequirement Validated(GameLaunchRequirement requirement)
    {
        var nameOk = System.Text.RegularExpressions.Regex.IsMatch(requirement.Name, @"^[A-Za-z_][A-Za-z0-9_.]*$");
        if (!nameOk)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"invalid launch requirement name: {requirement.Name}"));
        if (requirement.Value.Any(char.IsControl)
            || requirement.Value.Any(c => "\"$`\\;|&<>()".Contains(c)))
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", $"launch requirement {requirement.Name} contains shell metacharacters and needs manual review"));
        return requirement;
    }
}

public static class GameDefinitionLoader
{
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record DefinitionDto
    {
        public required string SchemaVersion { get; init; }
        public required string Slug { get; init; }
        public required IReadOnlyList<string> WritableAreas { get; init; }
        public IReadOnlyDictionary<string, string>? StoreIds { get; init; }
        public IReadOnlyList<LoaderLayout>? LoaderLayouts { get; init; }
        public DetectionDto? Detection { get; init; }
        public IReadOnlyDictionary<string, string>? Title { get; init; }
        public string? MainExecutable { get; init; }
        public IReadOnlyList<GameLaunchRequirement>? LaunchRequirements { get; init; }
        public IReadOnlyList<string>? Platforms { get; init; }
        public string? DefaultArch { get; init; }
    }

    private sealed record DetectionDto
    {
        public required string Strategy { get; init; }
    }

    public static GameDefinition LoadYaml(string yaml)
    {
        var element = RecipeParser.YamlToJsonElement(yaml);
        var schemaErrors = GameDefinitionSchema.Validate(element);
        if (schemaErrors.Count > 0)
            throw new PhaketException(schemaErrors[0].Code, schemaErrors[0].Parameters.Select(kv => (kv.Key, kv.Value)).ToArray());
        var dto = element.Deserialize<DefinitionDto>(StrictOptions)
            ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "empty game definition"));
        if (dto.SchemaVersion != "game-definition/v0alpha1")
            throw new PhaketException(ErrorCodes.UnsupportedSchemaVersion, ("schemaVersion", dto.SchemaVersion));
        return new GameDefinition(dto.Slug, dto.WritableAreas, dto.StoreIds, dto.LoaderLayouts,
            dto.Detection?.Strategy, dto.Title, dto.MainExecutable, dto.LaunchRequirements,
            dto.Platforms, dto.DefaultArch);
    }
}

public static class SourcePolicy
{
    // PoC allowlist (spec §9): concrete GitHub repositories (owner/repo) and the
    // Thunderstore download host. Redirects beyond the allowlist are forbidden.
    // Defaults cover the repositories of the recipes shipped with Phaket; hosts
    // may extend the set at runtime (SourcePolicy.AllowGitHubRepository).
    private static readonly HashSet<string> GitHubRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        "AU-Avengers/TOU-Mira",   // Town of Us Mira release packs
        "BepInEx/BepInEx",        // BepInEx 6 IL2CPP builds (Epic/x64 variant)
    };

    // Thunderstore: the canonical download entry point is
    //   https://thunderstore.io/package/download/<owner>/<name>/<version>/
    // which 302-redirects to a CDN object. Both the download URL and the resolved
    // ccdn.thunderstore.io object are allowed; anything else is rejected.

    public static void AllowGitHubRepository(string ownerAndRepo) => GitHubRepos.Add(ownerAndRepo);

    public static bool IsAllowedHttpsSource(string source, string? sha256 = null)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https") return false;
        if (uri.Host.Equals("builds.bepinex.dev", StringComparison.OrdinalIgnoreCase))
            return sha256 is not null;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "^/([^/]+/[^/]+)/releases/download/");
            return match.Success && GitHubRepos.Contains(match.Groups[1].Value);
        }
        if (uri.Host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.IsMatch(
                uri.AbsolutePath, "^/package/download/[^/]+/[^/]+/[^/]+/$");
        if (uri.Host.Equals("ccdn.thunderstore.io", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/live/repository/packages/", StringComparison.Ordinal);
        return uri.Host.Equals("api.thunderstore.io", StringComparison.OrdinalIgnoreCase);
    }
}
