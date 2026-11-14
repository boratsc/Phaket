using System.Text.Json;
using Json.Schema;

namespace Phaket.Core.Omrl;

// Semantic validation beyond the JSON Schema: catalog membership, capabilities,
// URI rules, write boundaries, version ranges, typed conditions, source policy.
// Collects all errors; never executes anything.
public static class RecipeValidator
{
    private static readonly Dictionary<string, JsonSchema> ParameterSchemas =
        OperationCatalog.All.ToDictionary(o => o.Name, o => JsonSchema.FromText(o.ParameterSchema));

    private static readonly IReadOnlyDictionary<string, (string[] FromSchemes, string[] ToSchemes)> PathRules =
        new Dictionary<string, (string[], string[])>(StringComparer.Ordinal)
        {
            ["directory.ensure"] = ([], ["game", "profile", "staging"]),
            ["file.copy"] = (["package", "staging"], ["game", "profile"]),
            ["file.remove"] = ([], ["game", "profile"]),
            ["file.verify"] = ([], ["game", "profile", "staging", "package"]),
            ["archive.extract"] = (["package", "staging"], ["staging", "game"]),
            ["config.set"] = ([], ["game", "profile"]),
        };

    public static IReadOnlyList<PhaketError> Validate(Recipe recipe, GameDefinition game)
    {
        var errors = new List<PhaketError>();
        void Add(string code, params (string, string)[] p) =>
            errors.Add(new PhaketError(code, p.ToDictionary(x => x.Item1, x => x.Item2)));

        if (recipe.SchemaVersion != "omrl/v0alpha1")
            Add(ErrorCodes.UnsupportedSchemaVersion, ("schemaVersion", recipe.SchemaVersion));
        if (recipe.Target.Game != game.Slug)
            Add(ErrorCodes.ValidationFailed, ("detail", $"target game '{recipe.Target.Game}' does not match definition '{game.Slug}'"));

        if (recipe.Target.Constraints?.GameVersion is { } gameVersion
            && !VersionRange.TryParse([gameVersion], out _))
            Add(ErrorCodes.InvalidVersionRange, ("range", gameVersion));

        var editions = recipe.Target.Editions ?? [];
        var editionKeys = editions.Select(e => e.Store + ":" + string.Join(",", (e.Os ?? []).OrderBy(x => x, StringComparer.Ordinal)));
        if (editionKeys.Distinct(StringComparer.Ordinal).Count() != editions.Count)
            Add(ErrorCodes.AmbiguousTarget, ("reason", "duplicate edition targets"));

        foreach (var loader in recipe.Requires?.Loaders ?? [])
            if (loader.Version is { } v && !VersionRange.TryParse([v], out _))
                Add(ErrorCodes.InvalidVersionRange, ("range", v));

        var declaredCapabilities = new HashSet<string>(recipe.Requires?.Capabilities ?? [], StringComparer.Ordinal);
        foreach (var capability in declaredCapabilities.Where(c => !OperationCatalog.Capabilities.Contains(c)))
            Add(ErrorCodes.ValidationFailed, ("detail", $"unknown capability: {capability}"));

        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in recipe.Artifacts)
        {
            if (!artifactIds.Add(artifact.Id))
                Add(ErrorCodes.ValidationFailed, ("detail", $"duplicate artifact id: {artifact.Id}"));
            if (artifact.Source.StartsWith("package://", StringComparison.Ordinal))
            {
                if (!VirtualUri.TryParse(artifact.Source, out var scheme, out var path)
                    || scheme != "package" || path.Contains('/'))
                    Add(ErrorCodes.InvalidUri, ("uri", artifact.Source));
            }
            else if (artifact.Source.StartsWith("https://", StringComparison.Ordinal))
            {
                if (!SourcePolicy.IsAllowedHttpsSource(artifact.Source))
                    Add(ErrorCodes.ValidationFailed, ("detail", $"https source not on allowlist: {artifact.Source}"));
                if (artifact.Sha256 is null)
                    Add(ErrorCodes.ValidationFailed, ("detail", $"sha256 required for remote artifact: {artifact.Id}"));
            }
            else
            {
                Add(ErrorCodes.InvalidUri, ("uri", artifact.Source));
            }
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in recipe.Steps)
        {
            if (!stepIds.Add(step.Id))
                Add(ErrorCodes.ValidationFailed, ("detail", $"duplicate step id: {step.Id}"));
            ValidateOperation(step.Use, step.With, step.Id, game, artifactIds, declaredCapabilities, Add);
            if (step.When is { } when) ValidateCondition(when, 1, Add);
        }

        foreach (var verify in recipe.Verify)
        {
            if (verify.Use != "file.verify")
                Add(ErrorCodes.ValidationFailed, ("detail", $"verify steps must use file.verify, got: {verify.Use}"));
            ValidateOperation(verify.Use, verify.With, "verify", game, artifactIds, declaredCapabilities, Add);
        }

        return errors;
    }

    private static void ValidateOperation(
        string use, IReadOnlyDictionary<string, JsonElement> with, string stepId,
        GameDefinition game, HashSet<string> artifactIds, HashSet<string> declaredCapabilities,
        Action<string, (string, string)[]> add)
    {
        if (!OperationCatalog.TryGet(use, out var operation) || operation is null)
        {
            add(ErrorCodes.UnknownOperation, [("operation", use)]);
            return;
        }

        foreach (var capability in operation.Capabilities.Where(c => !declaredCapabilities.Contains(c)))
            add(ErrorCodes.MissingCapability, [("operation", use), ("capability", capability)]);

        var element = JsonSerializer.SerializeToElement(with);
        var results = ParameterSchemas[use].Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
            add(ErrorCodes.ValidationFailed, [("detail", $"step '{stepId}' ({use}): invalid parameters")]);
            return; // do not interpret parameters that failed their schema
        }

        if (use == "loader.ensure" && with.TryGetValue("artifact", out var artifactRef)
            && !artifactIds.Contains(artifactRef.GetString() ?? ""))
            add(ErrorCodes.ValidationFailed, [("detail", $"step '{stepId}': undeclared artifact '{artifactRef.GetString()}'")]);

        if (!PathRules.TryGetValue(use, out var rules)) return;

        if (rules.FromSchemes.Length > 0 && with.TryGetValue("from", out var from))
            ValidatePathUri(from.GetString() ?? "", rules.FromSchemes, game, artifactIds, stepId, add, isWrite: false);
        var toKey = use == "directory.ensure" || use == "file.remove" || use == "file.verify" || use == "config.set" ? "path" : "to";
        if (with.TryGetValue(toKey, out var to))
            ValidatePathUri(to.GetString() ?? "", rules.ToSchemes, game, artifactIds, stepId, add,
                isWrite: use != "file.verify",
                // archive.extract targets a DIRECTORY; the game root is a legal
                // target and every extracted entry is still checked against
                // writableAreas at write time.
                allowGameRoot: use == "archive.extract");
    }

    private static void ValidatePathUri(
        string uri, string[] allowedSchemes, GameDefinition game, HashSet<string> artifactIds,
        string stepId, Action<string, (string, string)[]> add, bool isWrite, bool allowGameRoot = false)
    {
        if (!VirtualUri.TryParse(uri, out var scheme, out var path))
        {
            add(ErrorCodes.InvalidUri, [("uri", uri)]);
            return;
        }
        if (!allowedSchemes.Contains(scheme))
        {
            add(ErrorCodes.InvalidUri, [("uri", uri)]);
            return;
        }
        if (scheme == "package")
        {
            var artifactId = path.Split('/')[0];
            if (!artifactIds.Contains(artifactId))
                add(ErrorCodes.ValidationFailed, [("detail", $"step '{stepId}': package URI references undeclared artifact '{artifactId}'")]);
            return;
        }
        if (isWrite && scheme == "game" && !(allowGameRoot && path == ".") && !game.AllowsWrite(path))
            add(ErrorCodes.PathOutOfBounds, [("path", uri)]);
    }

    private static void ValidateCondition(RecipeCondition condition, int depth, Action<string, (string, string)[]> add)
    {
        if (depth > 8)
        {
            add(ErrorCodes.ValidationFailed, [("detail", "condition nesting deeper than 8")]);
            return;
        }
        var kinds = new[]
        {
            condition.All is not null, condition.Any is not null, condition.Not is not null,
            condition.Os is not null, condition.GameVersion is not null, condition.FileExists is not null,
            condition.Capability is not null, condition.Store is not null,
        };
        if (kinds.Count(k => k) != 1)
        {
            add(ErrorCodes.ValidationFailed, [("detail", "condition must set exactly one node kind")]);
            return;
        }
        if (condition.All is { } all) foreach (var c in all) ValidateCondition(c, depth + 1, add);
        if (condition.Any is { } any) foreach (var c in any) ValidateCondition(c, depth + 1, add);
        if (condition.Not is { } not) ValidateCondition(not, depth + 1, add);
        if (condition.GameVersion is { } range && !VersionRange.TryParse([range], out _))
            add(ErrorCodes.InvalidVersionRange, [("range", range)]);
        if (condition.FileExists is { } uri && !VirtualUri.TryParse(uri, out _, out _))
            add(ErrorCodes.InvalidUri, [("uri", uri)]);
        if (condition.Capability is { } capability && !OperationCatalog.Capabilities.Contains(capability))
            add(ErrorCodes.ValidationFailed, [("detail", $"unknown capability in condition: {capability}")]);
    }
}
