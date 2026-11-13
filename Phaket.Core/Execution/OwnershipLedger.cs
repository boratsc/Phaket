using System.Text.Json;
using System.Text.Json.Serialization;

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
    string? CurrentHash);

public sealed class OwnershipLedger
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public Dictionary<string, LedgerEntry> Entries { get; set; } = new(StringComparer.Ordinal);

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
}
