using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phaket.Core.Omrl;

namespace Phaket.Core.Omrl;

// Declarative loader layout: differences between games are data, never Core code.
public sealed record LoaderLayout(string Id, string Variant, IReadOnlyList<string> EntryFiles);

// Mod-source identity per game (spec: sources in the definition, never Core code).
// Thunderstore is the community slug; Nexus keeps the domain name and numeric game id
// so no consumer has to translate one into the other.
public sealed record NexusSource(string Domain, int GameId);
public sealed record GameSources(string? Thunderstore = null, NexusSource? Nexus = null);

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
    string? DefaultArch = null,
    // Where the game's mods come from (Thunderstore community, Nexus identity).
    GameSources? Sources = null,
    // PAK-141: save locations back up before a recipe writes. A "profile://" entry is
    // relative to the user's profile root (a store prefix such as "steam/" belongs in
    // the definition); anything else is an absolute path. Empty = no save backup.
    IReadOnlyList<string>? SavePaths = null)
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
        public GameSources? Sources { get; init; }
        public IReadOnlyList<string>? SavePaths { get; init; }
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
            dto.Platforms, dto.DefaultArch, dto.Sources, dto.SavePaths);
    }
}

public static class SourcePolicy
{
    private static readonly object GitHubGate = new();
    private static readonly HashSet<string> GitHubRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        "AU-Avengers/TOU-Mira",
        "BepInEx/BepInEx",
    };

    // Test seam: in a test process a flow resolves GitHub URLs against the entries it registered
    // itself, so a probe repository added by one test is not handed to a test running beside it,
    // and the process-wide list stays untouched. `set` receives the flow's entries (null when it
    // registered none) and returns the new ones; production never installs the hook.
    private static Action<Func<ImmutableHashSet<string>?, ImmutableHashSet<string>?>>? githubReposHook;

    internal static void UseIsolatedGitHubRepos(Action<Func<ImmutableHashSet<string>?, ImmutableHashSet<string>?>> set)
    {
#if DEBUG
        githubReposHook = set;
#endif
        set(_ => []);
    }

    private static ImmutableHashSet<string>? CurrentGitHubRepos()
    {
        ImmutableHashSet<string>? isolated = null;
        if (githubReposHook is not null) githubReposHook(repos => { isolated = repos; return repos; });
        else lock (GitHubGate) isolated = [.. GitHubRepos];
        return isolated;
    }

    // allow: false lets a test drop its own probe entry instead of leaving it for other tests.
    public static void AllowGitHubRepository(string ownerAndRepo, bool allow = true)
    {
        if (!IsValidGitHubRepository(ownerAndRepo))
            throw new ArgumentException("Expected a GitHub owner/repository pair.", nameof(ownerAndRepo));
        if (githubReposHook is null)
        {
            lock (GitHubGate)
            {
                if (allow) GitHubRepos.Add(ownerAndRepo);
                else GitHubRepos.Remove(ownerAndRepo);
            }
            return;
        }
        githubReposHook(repos => allow ? (repos ?? []).Add(ownerAndRepo) : (repos ?? []).Remove(ownerAndRepo));
    }

    // Catalog artifacts come from the trusted server and are pinned by sha256.
    // Registering the GitHub release repository makes the URL pass the same
    // allowlist the Thunderstore/GitHub modpack paths use.
    public static void AllowModArtifact(string source, string? sha256)
    {
        if (sha256 is null || !Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps) return;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return;
        var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "^/([^/]+/[^/]+)/releases/download/");
        if (match.Success && IsValidGitHubRepository(match.Groups[1].Value))
            AllowGitHubRepository(match.Groups[1].Value);
    }

    public static bool IsValidGitHubRepository(string? ownerAndRepo) => ownerAndRepo is not null
        && System.Text.RegularExpressions.Regex.IsMatch(ownerAndRepo,
            @"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsGitHubAssetUrl(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0 && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedGitHubReleaseAsset(string source, string ownerAndRepo, string tag, string assetName)
    {
        if (!IsValidGitHubRepository(ownerAndRepo) || string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(assetName)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsGitHubAssetUrl(source)) return false;
        if (CurrentGitHubRepos() is not { } allowed || !allowed.Contains(ownerAndRepo)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 6
            && string.Equals(parts[0] + "/" + parts[1], ownerAndRepo, StringComparison.OrdinalIgnoreCase)
            && parts[2] == "releases" && parts[3] == "download"
            && Uri.UnescapeDataString(parts[4]) == tag
            && string.Equals(Uri.UnescapeDataString(parts[5]), assetName, StringComparison.Ordinal);
    }

    // Thunderstore's download URL redirects to ccdn.thunderstore.io. GitHub release
    // links may redirect to their asset CDN; every hop is checked by HttpPackageTransport.
    public static bool IsAllowedHttpsSource(string source, string? sha256 = null)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.Host.Equals("builds.bepinex.dev", StringComparison.OrdinalIgnoreCase)) return sha256 is not null;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "^/([^/]+/[^/]+)/releases/download/");
            if (!match.Success) return false;
            return CurrentGitHubRepos()?.Contains(match.Groups[1].Value) is true;
        }
        if (uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
        // SusModder CDN hosts (SUSModder 3.x API/CDN). Accepted only with a declared
        // sha256: the checksum, not the host, is the integrity anchor.
        if (uri.Host.Equals("susmodder-cdn.ovh", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("api.susmodder-cdn.ovh", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("susmodder.app", StringComparison.OrdinalIgnoreCase)
            // susmodder-cdn.ovh redirects large artifacts to this CDN host.
            || uri.Host.Equals("cdn.susmodder.app", StringComparison.OrdinalIgnoreCase))
            return sha256 is not null;
        if (uri.Host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, "^/package/download/[^/]+/[^/]+/[^/]+/$");
        if (uri.Host.Equals("ccdn.thunderstore.io", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/live/repository/packages/", StringComparison.Ordinal);
        return uri.Host.Equals("api.thunderstore.io", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedTransportSource(string source) => IsAllowedHttpsSource(source)
        || Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && (uri.Host.Equals("builds.bepinex.dev", StringComparison.OrdinalIgnoreCase)
                // Integrity is enforced by the declared sha256 in PackageDownloader.
                || uri.Host.Equals("susmodder-cdn.ovh", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("api.susmodder-cdn.ovh", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("susmodder.app", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("cdn.susmodder.app", StringComparison.OrdinalIgnoreCase));
}
