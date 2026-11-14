using System.Text.Json;
using System.Text.RegularExpressions;
using Phaket.Core.Omrl;

namespace Phaket.Core.Mods;

public sealed record GeneratedRecipeStepOwnership(string StepId, string Ownership);

public sealed record GeneratedRecipe(Recipe Recipe, IReadOnlyList<GeneratedRecipeStepOwnership> Ownership);

public static class RecipeGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static GeneratedRecipe Generate(string fullName, string version, string downloadUrl,
        string? sha256, IEnumerable<string> archiveEntries, GameDefinition game)
    {
        if (string.IsNullOrWhiteSpace(fullName) || !Regex.IsMatch(fullName, @"^[A-Za-z0-9_]+-[A-Za-z0-9_]+$"))
            throw new ArgumentException("Expected Thunderstore owner-Name.", nameof(fullName));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Version is required.", nameof(version));
        if (game.Slug is not ("among-us" or "valheim")) throw new ArgumentException("Unsupported game.", nameof(game));
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("An HTTPS download URL is required.", nameof(downloadUrl));
        if (sha256 is not null && !Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$"))
            throw new ArgumentException("Invalid SHA-256.", nameof(sha256));
        if (sha256 is null) throw new ArgumentException("Remote package requires SHA-256.", nameof(sha256));

        var loader = fullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase);
        var entries = archiveEntries.Select(NormalizeEntry).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).ToArray();
        var recipeId = "thunderstore." + Regex.Replace(fullName.ToLowerInvariant().Replace('-', '.'), "[^a-z0-9.\\-]", "-");
        var artifactId = "package";
        var artifact = new RecipeArtifact { Id = artifactId, Source = downloadUrl, Sha256 = sha256 };
        var steps = new List<RecipeStep>();
        var ownership = new List<GeneratedRecipeStepOwnership>();
        var index = 0;
        if (!loader)
        {
            steps.Add(Step("extract", "archive.extract", new { from = "package://package", to = "staging://package" }));
            foreach (var entry in entries)
            {
                var target = Route(entry, fullName);
                var stepId = $"copy-{index++:D5}";
                steps.Add(Step(stepId, "file.copy", new { from = "staging://package/" + entry, to = "game://" + target }));
                if (target.StartsWith("BepInEx/config/", StringComparison.Ordinal)) ownership.Add(new(stepId, "shared"));
            }
        }
        else
        {
            var layout = game.Loader("bepinex") ?? throw new ArgumentException("Game has no BepInEx layout.", nameof(game));
            Add("loader.ensure", new Dictionary<string, object?> { ["id"] = layout.Id, ["artifact"] = artifactId,
                ["version"] = version, ["stripComponents"] = 1 });
        }
        if (!loader)
        {
            var extract = steps[0];
            var copySteps = steps.Skip(1).OrderBy(Target, StringComparer.Ordinal);
            steps = [extract, .. copySteps];
        }

        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1", Id = recipeId, Version = version,
            Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = fullName, ["en"] = fullName } },
            Target = new RecipeTarget { Game = game.Slug, Systems = ["windows", "linux"],
                Editions = [new EditionTarget { Store = "steam" }] },
            Requires = new RecipeRequires { Capabilities = steps.Select(s => OperationCatalog.TryGet(s.Use, out var op) ? op!.Capabilities : []).SelectMany(x => x).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() },
            Artifacts = [artifact], Steps = steps
        };
        return new(recipe, ownership);

        void Add(string use, Dictionary<string, object?> values) => steps.Add(Step("extract", use, values));
        static string Target(RecipeStep step) => step.With.TryGetValue("to", out var to) ? to.GetString()! : "game://.";
    }

    private static RecipeStep Step(string id, string use, object values) => new()
    {
        Id = id, Use = use,
        With = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values, JsonOptions), JsonOptions)!
    };

    private static string? NormalizeEntry(string value)
    {
        var path = value.Replace('\\', '/');
        if (path.EndsWith('/')) return null;
        if (path.Length == 0 || path.Split('/').Any(part => part is "" or "." or "..") || path.Contains(':') || path.Any(char.IsControl))
            throw new ArgumentException("Archive entry has an unsafe path.", nameof(value));
        return path;
    }

    private static string Route(string entry, string fullName)
    {
        var separator = entry.IndexOf('/');
        var first = separator < 0 ? entry : entry[..separator];
        var rest = separator < 0 ? "" : entry[(separator + 1)..];
        if (first.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || first.Equals("README.md", StringComparison.OrdinalIgnoreCase)
            || first.Equals("icon.png", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/plugins/{fullName}/{entry}";
        if (first.Equals("config", StringComparison.OrdinalIgnoreCase)) return "BepInEx/config/" + rest;
        if (new[] { "plugins", "patchers", "core", "monomod" }.Contains(first, StringComparer.OrdinalIgnoreCase))
            return $"BepInEx/{first}/{fullName}/{rest}";
        return $"BepInEx/plugins/{fullName}/{entry}";
    }
}
