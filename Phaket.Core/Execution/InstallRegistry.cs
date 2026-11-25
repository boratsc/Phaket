using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Core.Execution;

// A completed recipe installation, remembered so the host can launch or
// uninstall it later. The ownership ledger (StateRoot) holds the file-level
// truth; this record only locates the installation.
public sealed record InstalledRecipe(
    string RecipeId, string RecipeVersion, string GameSlug, string Store,
    string GameRoot, string ProfileRoot, string StagingRoot, string StateRoot,
    string? SandboxRoot, DateTimeOffset InstalledAt, string ModFullName = "", string ModVersion = "",
    // "unverified" when the catalog declared no sha256 for the package (Thunderstore v1 API
    // does not publish them); null means the declared hash was verified.
    string? ArtifactIntegrity = null);

public sealed class InstallRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string path;
    public InstallRegistry(string filePath) => path = filePath;

    public IReadOnlyList<InstalledRecipe> All()
    {
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<InstalledRecipe>>(File.ReadAllText(path), Options) ?? []; }
        catch { return []; } // unreadable registry: show nothing, never overwrite on read
    }

    public IReadOnlyList<InstalledRecipe> ForGame(string gameSlug) =>
        All().Where(e => e.GameSlug == gameSlug).ToArray();

    public void Add(InstalledRecipe entry)
    {
        var entries = All().Where(e => !(e.RecipeId == entry.RecipeId && e.GameRoot == entry.GameRoot)).ToList();
        entries.Add(entry);
        Save(entries);
    }

    public void Remove(string recipeId, string gameRoot)
    {
        var entries = All().Where(e => !(e.RecipeId == recipeId && e.GameRoot == gameRoot)).ToList();
        Save(entries);
    }

    private void Save(List<InstalledRecipe> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
        File.Move(temp, path, overwrite: true);
    }
}
