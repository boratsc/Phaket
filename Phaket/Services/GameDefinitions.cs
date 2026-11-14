using Phaket.Core.Omrl;

namespace Phaket.Services;

// Loads the declarative game definitions shipped with Phaket.Core.
public static class GameDefinitions
{
    public static IReadOnlyList<GameDefinition> LoadAll()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "definitions", "games");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal)
            .Select(path => GameDefinitionLoader.LoadYaml(File.ReadAllText(path)))
            .ToArray();
    }
}
