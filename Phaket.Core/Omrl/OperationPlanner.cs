using Phaket.Core.Domain;
using Phaket.Core.Execution;
using System.IO.Compression;
using System.Security.Cryptography;

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
    Func<string, string?>? LatestGameVersion = null,
    // PAK-100: root of the shared vanilla cache; null falls back to VanillaCache.DefaultRoot.
    string? VanillaCacheRoot = null,
    // PAK-142: last agreed state of this installation. Null keeps the plan unchanged.
    InstallBaseline? Baseline = null);

// Expands a validated recipe against concrete installation facts into a dry
// OperationPlan. Never mutates the filesystem; reads are limited to source
// existence/hash checks. Identical input yields a byte-identical plan.
public static class OperationPlanner
{
    // True when the recipe cannot be planned without a concrete game build fact
    // (provision step, version constraint or a gameVersion condition). Mod-only
    // recipes install against a sandbox without any catalog build.
    public static bool RequiresGameVersion(Recipe recipe) =>
        recipe.Target.Constraints?.GameVersion is not null
        || recipe.Steps.Any(s => s.Use == "game.provision" || ConditionUsesGameVersion(s.When));

    private static bool ConditionUsesGameVersion(RecipeCondition? condition) =>
        condition is not null && (condition.GameVersion is not null
            || (condition.All?.Any(ConditionUsesGameVersion) ?? false)
            || (condition.Any?.Any(ConditionUsesGameVersion) ?? false)
            || ConditionUsesGameVersion(condition.Not));

    // PAK-100: without an explicit root the cache is per user, not per mod package.
    private static string CacheRoot(PlannerContext context) => string.IsNullOrWhiteSpace(context.VanillaCacheRoot)
        ? VanillaCache.DefaultRoot : context.VanillaCacheRoot;

    public static OperationPlan Plan(Recipe recipe, PlannerContext context, IReadOnlyDictionary<string, string>? stepOwnership = null)
    {
        var recipeElement = System.Text.Json.JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement;
        var schemaErrors = RecipeSchema.Validate(recipeElement);
        if (schemaErrors.Count > 0) throw ToException(schemaErrors[0]);
        var semanticErrors = RecipeValidator.Validate(recipe, context.Game);
        if (semanticErrors.Count > 0) throw ToException(semanticErrors[0]);

        var facts = context.Facts;
        // Checked BEFORE the top-level condition: without the facts a condition could only
        // evaluate to false, and an empty plan would look like a completed install (PAK-65).
        RequireFacts(recipe.When, facts);
        if (!ConditionEvaluator.Evaluate(recipe.When, facts))
            return new OperationPlan(OperationPlan.CurrentSchemaVersion, new RecipeRef(recipe.Id, recipe.Version),
                context.InstallationId, context.ProfileId, [], [], [], false, []);
        if (!recipe.Target.Systems.Contains(facts.Os, StringComparer.Ordinal))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"os '{facts.Os}' not in target systems"));
        if (recipe.Target.Editions is { Count: > 0 } editions
            && !editions.Any(e => e.Store == facts.Store && (e.Os is null || e.Os.Contains(facts.Os))))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"store '{facts.Store}' not in target editions"));

        if (recipe.Target.Constraints?.GameVersion is { } declaredRange)
        {
            if (facts.GameVersion is null)
                throw new PhaketException(ErrorCodes.GameBuildUnavailable);
            // PAK-96: compare labels like the server does, so a recipe written with the
            // SusModder dbValue ("=2026-8-18") matches the canonical fact ("2026.8.18").
            // A date-shaped but impossible label is a bad fact, not text to compare.
            var gameVersion = GameVersionLabel.Normalize(facts.GameVersion)
                ?? throw new PhaketException(ErrorCodes.InvalidVersion, ("version", facts.GameVersion));
            if (!NumericVersion.TryParse(gameVersion, out var version))
                throw new PhaketException(ErrorCodes.InvalidVersion, ("version", facts.GameVersion));
            var range = GameVersionLabel.NormalizeRange(declaredRange);
            if (!VersionRange.TryParse([range], out var parsed) || !parsed!.Contains(version!))
                throw new PhaketException(ErrorCodes.UnsupportedGameVersion, ("gameVersion", gameVersion), ("range", declaredRange));
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

        // Conditions that need a concrete fact (gameVersion, arch, buildId) must fail loudly
        // instead of silently evaluating false and producing an empty "successful" plan (PAK-65).
        RequireFacts(recipe.When, facts);
        foreach (var step in recipe.Steps) RequireFacts(step.When, facts);

        var artifacts = recipe.Artifacts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var operations = new List<PlannedOperation>();
        var producedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var writes = new List<(string Path, string OpId)>();
        var requiresDownload = false;

        // R2c: an artifact's declared arch applies to EVERY step that consumes it, not only to
        // loader.ensure. A hard mismatch refuses the plan; an unknown installation arch only
        // warns, because the mod may still work and refusing would block a first install.
        var archUnknown = false;
        foreach (var step in recipe.Steps)
        {
            if (!ConditionEvaluator.Evaluate(step.When, facts)) continue;
            var consumed = ConsumedArtifact(step, artifacts);
            if (consumed is not null)
            {
                if (step.Use == "loader.ensure" && facts.Arch != consumed.Arch)
                    throw new PhaketException("LOADER_ARCH_UNAVAILABLE", ("arch", facts.Arch ?? "unknown"));
                if (step.Use != "loader.ensure")
                {
                    if (facts.Arch is null) archUnknown = true;
                    else if (!string.Equals(facts.Arch, consumed.Arch, StringComparison.Ordinal))
                        throw new PhaketException(ErrorCodes.ArchMismatch,
                            ("artifact", consumed.Id), ("artifactArch", consumed.Arch!), ("arch", facts.Arch));
                }
            }
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
                // A cached vanilla for this game+store+version means no download is needed.
                var cacheDir = VanillaCache.DirectoryFor(CacheRoot(context),
                    context.Game.Slug, provisionStore, provisionVersion);
                var marker = VanillaCache.ReadMarker(cacheDir);
                var manifestId = step.With.TryGetValue("manifestId", out var manifestElement)
                    && manifestElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? manifestElement.GetString() : context.Facts.ManifestId;
                // The executor provisions by manifest; carry the resolved id in the op.
                if (manifestId is not null) parameters["manifestId"] = manifestId;
                // PAK-100: a hash mismatch in the shared cache reads as "not cached", so the plan
                // still asks for the download that will replace it (same check as the executor).
                if (marker is null || marker.Store != provisionStore || marker.Version != provisionVersion
                    || (manifestId is not null && marker.ManifestId != manifestId)
                    || VanillaCache.ReadTreeHash(cacheDir) is not { } recorded
                    || recorded != VanillaCache.ComputeTreeHash(cacheDir)
                    || !Directory.Exists(cacheDir))
                    requiresDownload = true;
            }

            operations.Add(new PlannedOperation(
                step.Id, step.Use, targetPath ?? context.ProfileRoot, sourceRef, sizeBytes, sha256,
                preconditions, operation.Recovery, operation.Recovery,
                IsWrite(step.Use) ? "record-owner" : "none", operation.Permissions,
                parameters, stepOwnership?.TryGetValue(step.Id, out var ownership) == true && ownership == "shared"));

            if (targetPath is not null)
            {
                if (IsWrite(step.Use)) writes.Add((targetPath, step.Id));
                if (step.Use == "archive.extract" && step.With.TryGetValue("to", out var toUri))
                    producedPaths[toUri.GetString() ?? ""] = step.Id;
            }
        }

        var conflicts = DetectConflicts(writes, context.ExistingOwners ?? [], context.GameRoot).ToList();
        var warnings = ExecutableWarnings(operations).ToList();
        if (archUnknown) warnings.Add("ARCH_UNKNOWN");
        if (recipe.Artifacts.Any(a => a.Sha256 is null && SourcePolicy.IsGitHubAssetUrl(a.Source)))
            warnings.Add("GITHUB_HASH_UNVERIFIED");
        foreach (var operation in operations.Where(o => o.Kind == "loader.ensure"))
        {
            if (operation.SourceRef is null || !operation.SourceRef.StartsWith("package://", StringComparison.Ordinal))
            {
                warnings.Add("loader-overwrite-unknown");
                continue;
            }
            var archive = Path.Combine(context.PackageRoot, operation.SourceRef["package://".Length..]);
            if (!File.Exists(archive)) { warnings.Add("loader-overwrite-unknown"); continue; }
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var entry in zip.Entries.Where(e => !e.FullName.Replace('\\', '/').EndsWith('/')))
                {
                    var destination = PathBoundary.Resolve(context.GameRoot, entry.FullName);
                    if (!File.Exists(destination)) continue;
                    using var stream = entry.Open();
                    var incoming = SHA256.HashData(stream);
                    if (!incoming.SequenceEqual(SHA256.HashData(File.ReadAllBytes(destination))))
                        conflicts.Add(new PlanConflict(destination, [operation.OpId], "overwrite"));
                }
            }
            catch (InvalidDataException) { warnings.Add("loader-overwrite-unknown"); }
        }

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

        // PAK-142: a two-sided edit is the user's decision. The write STAYS in the plan, so a
        // "use the mod's version" answer has something to run; the executor skips or runs it
        // per conflict path. Cutting every write instead would block unrelated files too.
        if (context.Baseline is { } baseline)
        {
            var compared = BaselineSync.Compare(operations, baseline, context.GameRoot);
            foreach (var conflict in compared.Where(c => c.RequiresUserDecision))
                conflicts.Add(new PlanConflict(conflict.Path,
                    [.. (conflict.Local is null ? [] : new[] { "local:" + context.InstallationId }),
                        .. (conflict.Remote is null ? [] : new[] { "remote:" + recipe.Id })],
                    "requires-user-decision"));
        }

        return new OperationPlan(
            OperationPlan.CurrentSchemaVersion,
            new RecipeRef(recipe.Id, recipe.Version),
            context.InstallationId, context.ProfileId,
            operations, conflicts,
            warnings,
            requiresDownload,
            verification);
    }

    // The artifact a step takes its bytes from, when that artifact declares an arch:
    // loader.ensure through "artifact", file.copy/archive.extract through "from".
    private static RecipeArtifact? ConsumedArtifact(
        RecipeStep step, Dictionary<string, RecipeArtifact> artifacts)
    {
        var key = step.Use switch { "loader.ensure" => "artifact", "file.copy" or "archive.extract" => "from", _ => null };
        if (key is null || !step.With.TryGetValue(key, out var element)
            || element.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        var value = element.GetString() ?? "";
        if (key == "from")
        {
            var (scheme, path) = VirtualUri.Parse(value);
            if (scheme != "package") return null;
            value = path.Split('/')[0];
        }
        return artifacts.TryGetValue(value, out var artifact) && artifact.Arch is not null ? artifact : null;
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

    private static void RequireFacts(RecipeCondition? condition, PlanFacts facts)
    {
        if (condition is null) return;
        if (condition.GameVersion is not null)
        {
            if (facts.GameVersion is null)
                throw new PhaketException(ErrorCodes.GameBuildUnavailable);
            if (GameVersionLabel.Normalize(facts.GameVersion) is not { } label || !NumericVersion.TryParse(label, out _))
                throw new PhaketException(ErrorCodes.InvalidVersion, ("version", facts.GameVersion));
        }
        if (condition.Arch is not null && facts.Arch is null)
            throw new PhaketException(ErrorCodes.InstallFactsMissing, ("fact", "arch"));
        if (condition.BuildId is not null && facts.BuildId is null)
            throw new PhaketException(ErrorCodes.InstallFactsMissing, ("fact", "buildId"));
        if (condition.All is { } all) foreach (var c in all) RequireFacts(c, facts);
        // Alternatives (PAK-66): a branch that already matches makes the others irrelevant, and a
        // branch ruled out by the facts we do have states nothing we lack. Only a branch that
        // could still match once an unknown fact is filled in makes that fact required.
        if (condition.Any is { } any && !any.Any(c => ConditionEvaluator.EvaluateOrNull(c, facts) == true))
            foreach (var c in any.Where(branch => CouldMatch(branch, facts))) RequireFacts(c, facts);
        if (condition.Not is { } not) RequireFacts(not, facts);
    }

    // Not disprovable from the known facts: unknown arch/buildId/version leaves a branch open,
    // so the fact behind it has to be supplied before the plan can be called a plain mismatch.
    private static bool CouldMatch(RecipeCondition? condition, PlanFacts facts)
    {
        if (condition is null) return true;
        if (condition.Arch is not null || condition.BuildId is not null || condition.GameVersion is not null)
            return ConditionEvaluator.EvaluateOrNull(condition, facts) != false;
        if (condition.All is { } all) return all.All(c => CouldMatch(c, facts));
        if (condition.Any is { } any) return any.Any(c => CouldMatch(c, facts));
        return ConditionEvaluator.EvaluateOrNull(condition, facts) != false;
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
                : artifact.Id + (artifact.Format == "zip" ? ".zip" : "");
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
