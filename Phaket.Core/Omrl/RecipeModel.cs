using System.Text.Json;

namespace Phaket.Core.Omrl;

// Typed OMRL alpha model (omrl/v0alpha1). Identifiers and keys are language-neutral;
// only metadata carries localized text. Unknown fields are rejected by the parser.
public sealed record Recipe
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public RecipeMetadata? Metadata { get; init; }
    public required RecipeTarget Target { get; init; }
    public RecipeRequires? Requires { get; init; }
    public IReadOnlyList<RecipeArtifact> Artifacts { get; init; } = [];
    public required IReadOnlyList<RecipeStep> Steps { get; init; }
    public IReadOnlyList<RecipeVerifyStep> Verify { get; init; } = [];
}

public sealed record RecipeMetadata
{
    public IReadOnlyDictionary<string, string>? Title { get; init; }
    public IReadOnlyDictionary<string, string>? Description { get; init; }
}

public sealed record RecipeTarget
{
    public required string Game { get; init; }
    public required IReadOnlyList<string> Systems { get; init; }
    public IReadOnlyList<EditionTarget>? Editions { get; init; }
    public TargetConstraints? Constraints { get; init; }
}

public sealed record EditionTarget
{
    public required string Store { get; init; }
    public IReadOnlyList<string>? Os { get; init; }
}

public sealed record TargetConstraints
{
    public string? GameVersion { get; init; }
}

public sealed record RecipeRequires
{
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<LoaderRequirement> Loaders { get; init; } = [];
}

public sealed record LoaderRequirement
{
    public required string Id { get; init; }
    public string? Version { get; init; }
}

public sealed record RecipeArtifact
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public string? Sha256 { get; init; }
    public long? SizeBytes { get; init; }
    public string Format { get; init; } = "zip";
}

public sealed record RecipeStep
{
    public required string Id { get; init; }
    public required string Use { get; init; }
    public required IReadOnlyDictionary<string, JsonElement> With { get; init; }
    public RecipeCondition? When { get; init; }
}

public sealed record RecipeVerifyStep
{
    public required string Use { get; init; }
    public required IReadOnlyDictionary<string, JsonElement> With { get; init; }
}

// Exactly one node kind must be set; enforced by the semantic validator.
public sealed record RecipeCondition
{
    public IReadOnlyList<RecipeCondition>? All { get; init; }
    public IReadOnlyList<RecipeCondition>? Any { get; init; }
    public RecipeCondition? Not { get; init; }
    public IReadOnlyList<string>? Os { get; init; }
    public string? GameVersion { get; init; }
    public string? FileExists { get; init; }
    public string? Capability { get; init; }
    public string? Store { get; init; }
}
