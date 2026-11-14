using Phaket.Core.Domain;
using Phaket.Core.Execution;

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
    IReadOnlyList<ResourceOwner>? ExistingOwners = null,
    // Resolves "latest" for game.provision from the build catalog (store -> version
    // label). Null means no catalog: "latest" then fails with GAME_BUILD_UNAVAILABLE.
    Func<string, string?>? LatestGameVersion = null);

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
        var requiresDownload = false;

        foreach (var step in recipe.Steps)
        {
            if (!ConditionEvaluator.Evaluate(step.When, facts)) continue;
            var operation = OperationCatalog.TryGet(step.Use, out var definition) ? definition! : null;
            if (operation is null) throw new PhaketException(ErrorCodes.UnknownOperation, ("operation", step.Use));

            var (sourceRef, sizeBytes, sha256, sourcePath) = ResolveSource(step.Use, step.With, context, artifacts, producedPaths);
            // A remote artifact with no local file yet means the host must download
            // it before execution; a locally present file clears the flag.
            if (sourcePath is null && sourceRef is not null
                && sourceRef.StartsWith("package://", StringComparison.Ordinal)
                && step.Use is "file.copy" or "archive.extract" or "loader.ensure")
                requiresDownload = true;
            var targetPath = ResolveTarget(step.Use, step.With, context);
            var preconditions = new List<string>(operation.Preconditions);
            var parameters = step.With.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? kv.Value.GetString()! : kv.Value.GetRawText());
            if (sourcePath is null && sourceRef is not null && step.Use is "file.copy")
            {
                var producer = producedPaths.FirstOrDefault(kv => sourceRef.StartsWith(kv.Key, StringComparison.Ordinal));
                if (producer.Value is not null) preconditions.Add($"produced-by:{producer.Value}");
            }
            if (step.Use == "game.provision")
            {
                // The base game must match the recipe's pinned version exactly and
                // can only come from official tooling (DepotDownloader / legendary).
                var provisionStore = step.With.GetValueOrDefault("store").GetString() ?? "";
                if (provisionStore != facts.Store)
                    throw new PhaketException(ErrorCodes.ValidationFailed,
                        ("detail", $"game.provision store '{provisionStore}' does not match installation store '{facts.Store}'"));
                var provisionVersion = step.With.GetValueOrDefault("version").GetString() ?? "";
                // "Latest" is resolved ONCE at plan time from the build catalog; the
                // executor only ever sees a concrete version (reproducible plans).
                if (provisionVersion == "latest")
                {
                    provisionVersion = context.LatestGameVersion?.Invoke(provisionStore)
                        ?? throw new PhaketException(ErrorCodes.GameBuildUnavailable);
                    parameters["version"] = provisionVersion;
                }
                sourceRef = $"vanilla://{provisionStore}/{provisionVersion}";
                targetPath = context.GameRoot;
                // A cached vanilla for this exact version means no download is needed.
                var cacheDir = VanillaCache.DirectoryFor(context.PackageRoot, provisionStore, provisionVersion);
                var marker = VanillaCache.ReadMarker(cacheDir);
                var manifestId = step.With.TryGetValue("manifestId", out var manifestElement)
                    && manifestElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? manifestElement.GetString() : null;
                if (marker is null || marker.Store != provisionStore
                    || (manifestId is not null && marker.ManifestId != manifestId)
                    || !Directory.Exists(cacheDir))
                    requiresDownload = true;
            }

            operations.Add(new PlannedOperation(
                step.Id, step.Use, targetPath ?? context.ProfileRoot, sourceRef, sizeBytes, sha256,
                preconditions, operation.Recovery, operation.Recovery,
                IsWrite(step.Use) ? "record-owner" : "none", operation.Permissions,
                parameters));

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
            requiresDownload,
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
        // Writes that target the game root as a DIRECTORY (provision, loader,
        // extract) are tree merges, not file conflicts: per-file sharing is
        // resolved by the ownership ledger at execute time.
        foreach (var group in writes.Where(w => w.Path != gameRoot)
            .GroupBy(w => w.Path, StringComparer.Ordinal).Where(g => g.Count() > 1))
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
                return ($"package://{Path.GetFileName(loaderPath)}", bytes.LongLength, loaderArtifact.Sha256 ?? observed, loaderPath);
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
            // Remote artifacts land in the package root under their artifact id
            // (that is where a host downloader places them); local package://
            // artifacts keep their declared file name.
            var baseName = artifact.Source.StartsWith("package://", StringComparison.Ordinal)
                ? artifact.Source["package://".Length..]
                : artifact.Id + ".zip";
            var filePath = relative.Length == 0
                ? Path.Combine(context.PackageRoot, baseName)
                : PathBoundary.Resolve(context.PackageRoot, Path.Combine(baseName, relative));
            if (File.Exists(filePath))
            {
                var bytes = File.ReadAllBytes(filePath);
                var observed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                if (artifact.Sha256 is { } declared
                    && !string.Equals(declared, observed, StringComparison.OrdinalIgnoreCase))
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", artifact.Id));
                return ($"package://{Path.GetFileName(filePath)}", bytes.LongLength, artifact.Sha256 ?? observed, filePath);
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
        // Root sentinel: archive.extract may target the game root itself; every
        // extracted entry is still checked against writableAreas at write time.
        if (path == ".")
        {
            if (scheme == "game" && use != "archive.extract")
                throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", element.GetString() ?? ""));
            return Path.GetFullPath(root);
        }
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
