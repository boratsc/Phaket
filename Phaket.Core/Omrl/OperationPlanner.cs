using Phaket.Core.Domain;

namespace Phaket.Core.Omrl;

// Existing resource ownership supplied from the local ledger (PAK-26).
public sealed record ResourceOwner(string Path, string RecipeId, string ProfileId, int Layer);

public sealed record PlannerContext(
    string InstallationId,
    string ProfileId,
    string GameRoot,
    string ProfileRoot,
    string StagingRoot,
    string PackageRoot,
    GameDefinition Game,
    PlanFacts Facts,
    IReadOnlyList<ResourceOwner>? ExistingOwners = null);

// Expands a validated recipe against concrete installation facts into a dry
// OperationPlan. Never mutates the filesystem; reads are limited to source
// existence/hash checks. Identical input yields a byte-identical plan.
public static class OperationPlanner
{
    public static OperationPlan Plan(Recipe recipe, PlannerContext context)
    {
        var recipeElement = System.Text.Json.JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement;
        var schemaErrors = RecipeSchema.Validate(recipeElement);
        if (schemaErrors.Count > 0) throw ToException(schemaErrors[0]);
        var semanticErrors = RecipeValidator.Validate(recipe, context.Game);
        if (semanticErrors.Count > 0) throw ToException(semanticErrors[0]);

        var facts = context.Facts;
        if (!recipe.Target.Systems.Contains(facts.Os, StringComparer.Ordinal))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"os '{facts.Os}' not in target systems"));
        if (recipe.Target.Editions is { Count: > 0 } editions
            && !editions.Any(e => e.Store == facts.Store && (e.Os is null || e.Os.Contains(facts.Os))))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"store '{facts.Store}' not in target editions"));

        if (recipe.Target.Constraints?.GameVersion is { } range)
        {
            if (facts.GameVersion is null)
                throw new PhaketException(ErrorCodes.GameBuildUnavailable);
            if (!NumericVersion.TryParse(facts.GameVersion, out var version))
                throw new PhaketException(ErrorCodes.InvalidVersion, ("version", facts.GameVersion));
            if (!VersionRange.TryParse([range], out var parsed) || !parsed!.Contains(version!))
                throw new PhaketException(ErrorCodes.UnsupportedGameVersion, ("gameVersion", facts.GameVersion), ("range", range));
        }

        foreach (var loader in recipe.Requires?.Loaders ?? [])
        {
            var satisfied = facts.LoaderVersions is { } versions
                && versions.TryGetValue(loader.Id, out var installed)
                && NumericVersion.TryParse(installed, out var installedVersion)
                && (loader.Version is null || (VersionRange.TryParse([loader.Version], out var loaderRange) && loaderRange!.Contains(installedVersion!)));
            var willBeInstalled = recipe.Steps.Any(s => s.Use == "loader.ensure"
                && s.With.TryGetValue("id", out var id) && id.GetString() == loader.Id
                && ConditionEvaluator.Evaluate(s.When, facts)
                && LoaderVersionCompatible(s, loader.Version));
            if (!satisfied && !willBeInstalled)
                throw new PhaketException(ErrorCodes.LoaderUnavailable, ("loader", loader.Id), ("range", loader.Version ?? "*"));
        }

        // A gameVersion condition requires a known, valid build fact (spec §5).
        foreach (var step in recipe.Steps)
            RequireVersionFact(step.When, facts);

        var artifacts = recipe.Artifacts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var operations = new List<PlannedOperation>();
        var producedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var writes = new List<(string Path, string OpId)>();

        foreach (var step in recipe.Steps)
        {
            if (!ConditionEvaluator.Evaluate(step.When, facts)) continue;
            var operation = OperationCatalog.TryGet(step.Use, out var definition) ? definition! : null;
            if (operation is null) throw new PhaketException(ErrorCodes.UnknownOperation, ("operation", step.Use));

            var (sourceRef, sizeBytes, sha256, sourcePath) = ResolveSource(step.Use, step.With, context, artifacts, producedPaths);
            var targetPath = ResolveTarget(step.Use, step.With, context);
            var preconditions = new List<string>(operation.Preconditions);
            if (sourcePath is null && sourceRef is not null && step.Use is "file.copy")
            {
                var producer = producedPaths.FirstOrDefault(kv => sourceRef.StartsWith(kv.Key, StringComparison.Ordinal));
                if (producer.Value is not null) preconditions.Add($"produced-by:{producer.Value}");
            }

            operations.Add(new PlannedOperation(
                step.Id, step.Use, targetPath ?? context.ProfileRoot, sourceRef, sizeBytes, sha256,
                preconditions, operation.Recovery, operation.Recovery,
                IsWrite(step.Use) ? "record-owner" : "none", operation.Permissions,
                step.With.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? kv.Value.GetString()! : kv.Value.GetRawText())));

            if (targetPath is not null)
            {
                if (IsWrite(step.Use)) writes.Add((targetPath, step.Id));
                if (step.Use == "archive.extract" && step.With.TryGetValue("to", out var toUri))
                    producedPaths[toUri.GetString() ?? ""] = step.Id;
            }
        }

        var conflicts = DetectConflicts(writes, context.ExistingOwners ?? [], context.GameRoot);

        var verification = new List<PlannedOperation>();
        foreach (var verify in recipe.Verify)
        {
            var target = ResolveTarget(verify.Use, verify.With, context);
            verify.With.TryGetValue("sha256", out var expectedHash);
            verification.Add(new PlannedOperation(
                $"verify-{verification.Count}", verify.Use, target ?? context.GameRoot, null, null,
                expectedHash.ValueKind == System.Text.Json.JsonValueKind.String ? expectedHash.GetString() : null,
                ["installed"], "none", "none", "none", ["read"]));
        }

        return new OperationPlan(
            OperationPlan.CurrentSchemaVersion,
            new RecipeRef(recipe.Id, recipe.Version),
            context.InstallationId, context.ProfileId,
            operations, conflicts,
            ExecutableWarnings(operations),
            recipe.Artifacts.Any(a => a.Source.StartsWith("https://", StringComparison.Ordinal)),
            verification);
    }

    private static bool LoaderVersionCompatible(RecipeStep step, string? requiredRange)
    {
        if (requiredRange is null) return true;
        if (!step.With.TryGetValue("version", out var declared)
            || declared.ValueKind != System.Text.Json.JsonValueKind.String) return true;
        return NumericVersion.TryParse(declared.GetString(), out var version)
            && VersionRange.TryParse([requiredRange], out var range)
            && range!.Contains(version!);
    }

    private static void RequireVersionFact(RecipeCondition? condition, PlanFacts facts)
    {
        if (condition is null) return;
        if (condition.GameVersion is not null)
        {
            if (facts.GameVersion is null)
                throw new PhaketException(ErrorCodes.GameBuildUnavailable);
            if (!NumericVersion.TryParse(facts.GameVersion, out _))
                throw new PhaketException(ErrorCodes.InvalidVersion, ("version", facts.GameVersion));
        }
        if (condition.All is { } all) foreach (var c in all) RequireVersionFact(c, facts);
        if (condition.Any is { } any) foreach (var c in any) RequireVersionFact(c, facts);
        if (condition.Not is { } not) RequireVersionFact(not, facts);
    }

    private static bool IsWrite(string use) => use is "directory.ensure" or "file.copy" or "file.remove"
        or "archive.extract" or "config.set" or "loader.ensure" or "profile.activate";

    private static IReadOnlyList<string> ExecutableWarnings(IReadOnlyList<PlannedOperation> operations) =>
        operations.Where(o => o.Kind is "file.copy" or "archive.extract"
                && o.TargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(o => $"executable-library:{o.TargetPath}")
            .ToArray();

    private static IReadOnlyList<PlanConflict> DetectConflicts(
        List<(string Path, string OpId)> writes, IReadOnlyList<ResourceOwner> existing, string gameRoot)
    {
        var conflicts = new List<PlanConflict>();
        foreach (var group in writes.GroupBy(w => w.Path, StringComparer.Ordinal).Where(g => g.Count() > 1))
            conflicts.Add(new PlanConflict(group.Key, group.Select(w => w.OpId).ToArray(), "last-wins"));
        foreach (var write in writes)
        {
            var owners = existing.Where(o =>
                (Path.IsPathRooted(o.Path) ? o.Path : Path.GetFullPath(Path.Combine(gameRoot, o.Path))) == write.Path).ToArray();
            if (owners.Length == 0) continue;
            if (owners.Select(o => o.Layer).Distinct().Count() != owners.Length)
                throw new PhaketException(ErrorCodes.ConflictUnresolvable,
                    ("path", write.Path), ("owners", string.Join(",", owners.Select(o => o.RecipeId))));
            conflicts.Add(new PlanConflict(write.Path,
                [.. owners.Select(o => o.RecipeId), write.OpId], "recompute-from-remaining-owners"));
        }
        return conflicts;
    }

    private static (string? SourceRef, long? Size, string? Sha256, string? Path) ResolveSource(
        string use, IReadOnlyDictionary<string, System.Text.Json.JsonElement> with,
        PlannerContext context, Dictionary<string, RecipeArtifact> artifacts,
        Dictionary<string, string> producedPaths)
    {
        if (use == "loader.ensure")
        {
            if (!with.TryGetValue("artifact", out var artifactElement)
                || !artifacts.TryGetValue(artifactElement.GetString() ?? "", out var loaderArtifact))
                return (null, null, null, null);
            var fileName = loaderArtifact.Source.StartsWith("package://", StringComparison.Ordinal)
                ? loaderArtifact.Source["package://".Length..] : loaderArtifact.Id;
            var loaderPath = Path.Combine(context.PackageRoot, fileName);
            if (!File.Exists(loaderPath) && File.Exists(loaderPath + ".zip")) loaderPath += ".zip";
            if (File.Exists(loaderPath))
            {
                var bytes = File.ReadAllBytes(loaderPath);
                var observed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                if (loaderArtifact.Sha256 is { } declared
                    && !string.Equals(declared, observed, StringComparison.OrdinalIgnoreCase))
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", loaderArtifact.Id));
                return ($"package://{loaderArtifact.Id}", bytes.LongLength, loaderArtifact.Sha256 ?? observed, loaderPath);
            }
            if (loaderArtifact.Source.StartsWith("https://", StringComparison.Ordinal))
                return ($"package://{loaderArtifact.Id}", loaderArtifact.SizeBytes, loaderArtifact.Sha256, null);
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", loaderArtifact.Id));
        }
        if (use is not ("file.copy" or "archive.extract") || !with.TryGetValue("from", out var fromElement))
            return (null, null, null, null);
        var from = fromElement.GetString() ?? "";
        var (scheme, path) = VirtualUri.Parse(from);
        if (scheme == "package")
        {
            var segments = path.Split('/');
            var artifact = artifacts[segments[0]];
            var relative = string.Join('/', segments.Skip(1));
            var filePath = relative.Length == 0
                ? Path.Combine(context.PackageRoot, artifact.Source["package://".Length..])
                : PathBoundary.Resolve(context.PackageRoot, Path.Combine(artifact.Source["package://".Length..], relative));
            if (File.Exists(filePath))
            {
                var bytes = File.ReadAllBytes(filePath);
                var observed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                if (artifact.Sha256 is { } declared
                    && !string.Equals(declared, observed, StringComparison.OrdinalIgnoreCase))
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", artifact.Id));
                return (from, bytes.LongLength, artifact.Sha256 ?? observed, filePath);
            }
            if (artifact.Source.StartsWith("https://", StringComparison.Ordinal))
                return (from, artifact.SizeBytes, artifact.Sha256, null);
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", artifact.Id));
        }
        var stagingPath = PathBoundary.Resolve(context.StagingRoot, path);
        if (File.Exists(stagingPath))
        {
            var bytes = File.ReadAllBytes(stagingPath);
            return (from, bytes.LongLength, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), stagingPath);
        }
        // Missing staging sources must be produced by an earlier extract step.
        var producer = producedPaths.Keys.FirstOrDefault(prefix =>
            from == prefix || from.StartsWith(prefix + "/", StringComparison.Ordinal));
        if (producer is null)
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", from));
        return (from, null, null, null);
    }

    private static string? ResolveTarget(
        string use, IReadOnlyDictionary<string, System.Text.Json.JsonElement> with, PlannerContext context)
    {
        var key = use is "directory.ensure" or "file.remove" or "file.verify" or "config.set" ? "path" : "to";
        if (use is "loader.ensure") return context.GameRoot;
        if (use is "profile.activate") return Path.Combine(context.ProfileRoot, "active-profile.json");
        if (!with.TryGetValue(key, out var element)) return null;
        var (scheme, path) = VirtualUri.Parse(element.GetString() ?? "");
        var root = scheme switch
        {
            "game" => context.GameRoot,
            "profile" => context.ProfileRoot,
            "staging" => context.StagingRoot,
            "package" => context.PackageRoot,
            _ => throw new PhaketException(ErrorCodes.InvalidUri, ("uri", element.GetString() ?? "")),
        };
        var resolved = PathBoundary.Resolve(root, path);
        if (scheme == "game" && IsWrite(use))
        {
            if (!context.Game.AllowsWrite(path))
                throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", element.GetString() ?? ""));
            // Re-check the real (symlink-resolved) location against writableAreas.
            var realRoot = PathBoundary.Resolve(context.GameRoot, ".");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolved.StartsWith(realRoot + Path.DirectorySeparatorChar, comparison)
                && !string.Equals(resolved, realRoot, comparison))
                throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", element.GetString() ?? ""));
            var realRelative = resolved[realRoot.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
            if (!context.Game.AllowsWrite(realRelative))
                throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", element.GetString() ?? ""));
        }
        return resolved;
    }

    private static PhaketException ToException(PhaketError error) =>
        new(error.Code, error.Parameters.Select(kv => (kv.Key, kv.Value)).ToArray());
}
