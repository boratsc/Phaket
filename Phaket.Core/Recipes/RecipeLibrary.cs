using Phaket.Core.Omrl;

namespace Phaket.Core.Recipes;

// Recipes shipped with Phaket.Core (product content, not test fixtures).
// Identifiers and versions are language-neutral; only metadata is localized.
public static class RecipeLibrary
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "recipes");

    public static IReadOnlyList<Recipe> LoadAll(string? directory = null)
    {
        var dir = directory ?? Directory;
        if (!System.IO.Directory.Exists(dir)) return [];
        var recipes = new List<Recipe>();
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal))
        {
            // One bad file must not take down the whole library: the install UI
            // loads it synchronously while navigating.
            try { recipes.Add(RecipeParser.ParseYaml(File.ReadAllText(path))); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"phaket: recipe skipped {path}: {ex.Message}"); }
        }
        return recipes;
    }

    public static IReadOnlyList<Recipe> ForGame(string slug) =>
        LoadAll().Where(r => r.Target.Game == slug).ToArray();
}
