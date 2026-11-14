using System.Security.Cryptography;
using System.Text;
using Phaket.Core.Domain;

namespace Phaket.Core.Profiles;

// Immutable profile manifest + lockfile (spec §8.5, ADR-035). The manifest
// declares selected recipes/artifacts; the lockfile freezes concrete versions
// and content hashes. No internal mod settings are stored (approved exclusion).
public sealed record ProfileManifest(
    string SchemaVersion,
    string ProfileId,
    string GameId,
    string EditionId,
    IReadOnlyList<RecipeRef> Recipes,
    IReadOnlyList<ManifestArtifact> Artifacts)
{
    public const string CurrentSchemaVersion = "profile-manifest/v1";
}

public sealed record ManifestArtifact(string Id, string Source, string? Sha256, long? SizeBytes);

public sealed record LockedRecipe(string Id, string Version, string RecipeHash);

public sealed record ProfileLockfile(
    string SchemaVersion,
    string ProfileId,
    string ManifestHash,
    IReadOnlyList<LockedRecipe> Recipes,
    IReadOnlyList<ManifestArtifact> Artifacts,
    string? GameBuildRef)
{
    public const string CurrentSchemaVersion = "profile-lockfile/v1";
    [System.Text.Json.Serialization.JsonIgnore]
    public string LockHash => CanonicalJson.Hash(this);
}

public static class ProfileLockfileBuilder
{
    // Deterministic: recipes/artifacts sorted by id; hashes over canonical content.
    public static ProfileLockfile Build(ProfileManifest manifest, Func<RecipeRef, string> recipeContent, string? gameBuildRef = null)
    {
        var locked = manifest.Recipes
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new LockedRecipe(r.Id, r.Version,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recipeContent(r))))))
            .ToArray();
        var artifacts = manifest.Artifacts.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
        return new ProfileLockfile(ProfileLockfile.CurrentSchemaVersion, manifest.ProfileId,
            CanonicalJson.Hash(manifest), locked, artifacts, gameBuildRef);
    }
}

public sealed record ProfileDiff(
    IReadOnlyList<RecipeRef> Added,
    IReadOnlyList<RecipeRef> Removed,
    IReadOnlyList<(RecipeRef Old, RecipeRef New)> Changed,
    IReadOnlyList<string> ChangedArtifacts);

public static class ProfileDiffComputer
{
    // Deterministic upgrade/downgrade diff between two lockfiles.
    public static ProfileDiff Compare(ProfileLockfile older, ProfileLockfile newer)
    {
        var oldRecipes = older.Recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newRecipes = newer.Recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var added = newRecipes.Keys.Except(oldRecipes.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new RecipeRef(id, newRecipes[id].Version)).ToArray();
        var removed = oldRecipes.Keys.Except(newRecipes.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new RecipeRef(id, oldRecipes[id].Version)).ToArray();
        var changed = oldRecipes.Keys.Intersect(newRecipes.Keys, StringComparer.Ordinal)
            .Where(id => oldRecipes[id].Version != newRecipes[id].Version || oldRecipes[id].RecipeHash != newRecipes[id].RecipeHash)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => (new RecipeRef(id, oldRecipes[id].Version), new RecipeRef(id, newRecipes[id].Version)))
            .ToArray();
        var oldArtifacts = older.Artifacts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var changedArtifacts = newer.Artifacts
            .Where(a => !oldArtifacts.TryGetValue(a.Id, out var old) || old.Sha256 != a.Sha256 || old.Source != a.Source)
            .Select(a => a.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return new ProfileDiff(added, removed, changed, changedArtifacts);
    }
}
