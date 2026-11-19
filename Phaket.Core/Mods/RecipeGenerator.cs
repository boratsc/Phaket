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
        string? sha256, IEnumerable<string> archiveEntries, GameDefinition game, string? arch = null)
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

        var recipeId = "thunderstore." + Regex.Replace(fullName.ToLowerInvariant().Replace('-', '.'), "[^a-z0-9.\\-]", "-");
        return GenerateArchive(fullName, version, downloadUrl, sha256, null, archiveEntries, game, arch, recipeId,
            fullName.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase));
    }

    // Generates the same safe OMRL install plan for a pinned GitHub or local Nexus ZIP.
    public static GeneratedRecipe GenerateModpackPackage(string source, string name, string version,
        string artifactSource, string? sha256, long? sizeBytes, IEnumerable<string> archiveEntries,
        GameDefinition game, string recipeId, string? repository = null, string? tag = null, string? assetName = null)
    {
        if (source is not ("github" or "nexus") || string.IsNullOrWhiteSpace(name) || name.Length > 160
            || string.IsNullOrWhiteSpace(version) || version.Length > 100 || string.IsNullOrWhiteSpace(recipeId)
            || game.Slug is not ("among-us" or "valheim") || sizeBytes is <= 0
            || sha256 is not null && !Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$"))
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", source), ("detail", "invalid package metadata"));
        if (source == "github" && (!SourcePolicy.IsAllowedHttpsSource(artifactSource, sha256)
            || !SourcePolicy.IsAllowedGitHubReleaseAsset(artifactSource, repository ?? "", tag ?? "", assetName ?? "")))
            throw new PhaketException(ErrorCodes.ModSourceHostNotAllowed,
                ("host", Uri.TryCreate(artifactSource, UriKind.Absolute, out var uri) ? uri.Host : "invalid"));
        if (source == "nexus" && artifactSource != "package://package.zip")
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", source), ("detail", "Nexus archive must be staged locally"));

        var safeName = Regex.Replace(name.Trim(), @"[^A-Za-z0-9._-]", "_").Trim('.', '_');
        if (safeName.Length == 0)
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", source), ("detail", "mod name has no safe path characters"));
        return GenerateArchive(safeName, ToRecipeVersion(version), artifactSource, sha256, sizeBytes, archiveEntries, game, null, recipeId, false);
    }

    internal static string ToRecipeVersion(string pinnedVersion)
    {
        var candidate = pinnedVersion.StartsWith('v') || pinnedVersion.StartsWith('V') ? pinnedVersion[1..] : pinnedVersion;
        if (Regex.IsMatch(candidate, @"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.\-]*)?$")) return candidate;
        var suffix = Regex.Replace(pinnedVersion.ToLowerInvariant(), @"[^0-9a-z-]+", "-").Trim('-', '.');
        if (suffix.Length > 80) suffix = suffix[..80];
        return "0.0.0-" + (suffix.Length == 0 ? "pinned" : suffix);
    }

    private static GeneratedRecipe GenerateArchive(string fullName, string version, string artifactSource,
        string? sha256, long? sizeBytes, IEnumerable<string> archiveEntries, GameDefinition game,
        string? arch, string recipeId, bool loader)
    {
        var entries = archiveEntries.Select(NormalizeEntry).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).ToArray();
        const string artifactId = "package";
        var artifact = new RecipeArtifact { Id = artifactId, Source = artifactSource, Sha256 = sha256, SizeBytes = sizeBytes };
        if (loader)
        {
            if (game.Slug == "valheim" && arch == "x86")
                throw new PhaketException("LOADER_ARCH_UNAVAILABLE", ("arch", arch));
            artifact = game.Slug == "valheim"
                ? artifact with { Arch = "x64", Source = "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2360/", Sha256 = "6bca77b1016acfe0ffaebb6534c84b12ae8d12e5a96abbf59ec6e4526204fc6f" }
                : arch == "x64"
                    ? artifact with { Arch = "x64", Source = "https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip", Sha256 = "5b63d7d0e92a2448ccd13b738ff95ff1cc1026c6d718555d4df8e6c824953ac4" }
                    : artifact with { Arch = "x86", Source = "https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/", Sha256 = "88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564" };
        }

        var steps = new List<RecipeStep>();
        var ownership = new List<GeneratedRecipeStepOwnership>();
        if (!loader)
        {
            steps.Add(Step("extract", "archive.extract", new { from = "package://package", to = "staging://package" }));
            var index = 0;
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
            steps.Add(Step("extract", "loader.ensure", new Dictionary<string, object?> { ["id"] = layout.Id, ["artifact"] = artifactId,
                ["version"] = game.Slug == "valheim" ? "5.4.2360" : arch == "x64" ? "6.0.0-be.793" : "6.0.700", ["stripComponents"] = game.Slug == "among-us" && arch == "x64" ? 0 : 1 }));
        }
        if (!loader)
        {
            var extract = steps[0];
            steps = [extract, .. steps.Skip(1).OrderBy(Target, StringComparer.Ordinal)];
        }

        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1", Id = recipeId, Version = version,
            Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = fullName, ["en"] = fullName } },
            Target = new RecipeTarget { Game = game.Slug, Systems = ["windows", "linux"],
                Editions = game.StoreIds?.Keys.Select(store => new EditionTarget { Store = store }).ToArray() ?? [] },
            Requires = new RecipeRequires { Capabilities = steps.Select(s => OperationCatalog.TryGet(s.Use, out var op) ? op!.Capabilities : []).SelectMany(x => x).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() },
            Artifacts = [artifact], Steps = steps
        };
        return new(recipe, ownership);

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
