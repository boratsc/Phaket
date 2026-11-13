using System.Text.Json;
using System.Text.Json.Serialization;
using Phaket.Core.Omrl;

namespace Phaket.Core.Omrl;

// Declarative loader layout: differences between games are data, never Core code.
public sealed record LoaderLayout(string Id, string Variant, IReadOnlyList<string> EntryFiles);

// Minimal declarative game definition (spec §6.3). Full schema: game-definition/v0alpha1.
public sealed record GameDefinition(
    string Slug,
    IReadOnlyList<string> WritableAreas,
    IReadOnlyDictionary<string, string>? StoreIds = null,
    IReadOnlyList<LoaderLayout>? LoaderLayouts = null,
    string? DetectionStrategy = null,
    IReadOnlyDictionary<string, string>? Title = null,
    string? MainExecutable = null)
{
    public bool AllowsWrite(string relativePath) =>
        WritableAreas.Any(pattern => Glob.IsMatch(pattern, relativePath));

    public LoaderLayout? Loader(string id) =>
        LoaderLayouts?.FirstOrDefault(layout => layout.Id == id);
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
            dto.Detection?.Strategy, dto.Title, dto.MainExecutable);
    }
}

public static class SourcePolicy
{
    // PoC allowlist (spec §9): concrete GitHub repositories (owner/repo) and the
    // Thunderstore API host only. Redirects beyond the allowlist are forbidden.
    private static readonly HashSet<string> GitHubRepos = new(StringComparer.OrdinalIgnoreCase);

    public static void AllowGitHubRepository(string ownerAndRepo) => GitHubRepos.Add(ownerAndRepo);

    public static bool IsAllowedHttpsSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https") return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "^/([^/]+/[^/]+)/releases/download/");
            return match.Success && GitHubRepos.Contains(match.Groups[1].Value);
        }
        return uri.Host.Equals("api.thunderstore.io", StringComparison.OrdinalIgnoreCase);
    }
}
