using System.Text.Json;
using System.Text.Json.Serialization;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// Resource ownership ledger (spec §8, ADR-017). Resources are game-relative
// file paths, or "path::key" for config keys. The base state is preserved so
// uninstall can recompute from remaining owners instead of naive restore.
public sealed record LedgerOwner(
    string RecipeId, string ProfileId, string OpId, int Layer,
    string? ContentHash = null, string? Value = null);

public sealed record LedgerEntry(
    string Resource,
    string? BaseHash,
    string? BaseBackupRef,
    string? BaseValue,
    bool BaseExisted,
    IReadOnlyList<LedgerOwner> Owners,
    string? CurrentHash,
    string? Format = null,
    bool Shared = false);

public sealed class OwnershipLedger
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public Dictionary<string, LedgerEntry> Entries { get; set; } = new(StringComparer.Ordinal);

    // Directories created by each recipe's extracts/provisions (absolute paths).
    // Uninstall removes them when empty; non-empty (user content) always survives.
    public Dictionary<string, IReadOnlyList<string>> CreatedDirectories { get; set; } = new(StringComparer.Ordinal);

    public void RecordCreatedDirs(string recipeId, IReadOnlyList<string>? dirs)
    {
        if (dirs is null || dirs.Count == 0) return;
        var existing = CreatedDirectories.TryGetValue(recipeId, out var list) ? list : [];
        CreatedDirectories[recipeId] = existing.Union(dirs, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> TakeCreatedDirs(string recipeId) =>
        CreatedDirectories.TryGetValue(recipeId, out var list) ? list : [];

    public void RemoveCreatedDirs(string recipeId) => CreatedDirectories.Remove(recipeId);

    public static OwnershipLedger Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<OwnershipLedger>(File.ReadAllText(path), Options) ?? new OwnershipLedger()
            : new OwnershipLedger();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    public LedgerEntry? Get(string resource) => Entries.TryGetValue(resource, out var entry) ? entry : null;

    public void Set(LedgerEntry entry) => Entries[entry.Resource] = entry;

    public void Remove(string resource) => Entries.Remove(resource);

    public IEnumerable<LedgerEntry> ForRecipe(string recipeId) =>
        Entries.Values.Where(e => e.Owners.Any(o => o.RecipeId == recipeId));

    // PAK-142: plan what uninstalling a recipe may touch. A config key owns no file, an
    // entry another owner keeps has to be recomputed, and a shared file always stays.
    public UninstallPlan PlanUninstall(string recipeId) =>
        new(ForRecipe(recipeId).OrderBy(e => e.Resource, StringComparer.Ordinal)
            .Where(e => e.Owners.Any(o => o.RecipeId == recipeId))
            .Select(e =>
            {
                var remaining = e.Owners.Where(o => o.RecipeId != recipeId).OrderBy(o => o.Layer).ToArray();
                var action = e.Resource.Contains("::", StringComparison.Ordinal) ? UninstallAction.Recompute
                    : remaining.Length > 0 ? UninstallAction.Recompute
                    : e.Shared ? UninstallAction.Keep
                    : e.BaseExisted ? UninstallAction.RestoreBase
                    : UninstallAction.Remove;
                return new PlannedResourceChange(e.Resource, action,
                    [.. e.Owners.Select(o => o.RecipeId)],
                    remaining.Select(o => o.RecipeId).ToArray(),
                    e.Shared ? "shared" : e.BaseExisted ? "restore-base" : "remove");
            })
            .ToArray());

    public IReadOnlyList<string> SoleOwnerResources(string recipeId) =>
        Entries.Values.Where(e => e.Owners.Count == 1 && e.Owners[0].RecipeId == recipeId
                && !e.Shared && !e.Resource.Contains("::", StringComparison.Ordinal))
            .Select(e => e.Resource).OrderBy(r => r, StringComparer.Ordinal).ToArray();
}

public enum UninstallAction { Remove, RestoreBase, Recompute, Keep }

// Removable is what a confirmation may show as "will be deleted"; everything else
// either survives or is recomputed from the remaining owners.
public sealed record PlannedResourceChange(
    string Resource, UninstallAction Action, IReadOnlyList<string> Owners,
    IReadOnlyList<string> RemainingOwners, string Basis)
{
    public bool Removable => Action is UninstallAction.Remove or UninstallAction.RestoreBase;
}

public sealed record UninstallPlan(IReadOnlyList<PlannedResourceChange> Resources);
