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

    // SusModder catalog packages (PAK-95). `full` archives contain a whole modified
// game, so vanilla is provisioned from the pinned build and only the game
// definition's writableAreas are taken from the archive. `dll` addons behave
// like Thunderstore plugins. The caller has already selected the artifact that
// matches the installation's arch/store (see ModArtifactSelector).
    public static GeneratedRecipe GenerateCatalogPackage(string fullName, string version, string packaging,
        string artifactSource, string? sha256, long? sizeBytes, string? arch, string? amongVersion,
        IEnumerable<string> archiveEntries, GameDefinition game, string? assetName = null,
        IReadOnlyDictionary<string, string>? provisionManifests = null, IReadOnlyList<string>? stores = null)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 160)
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "susmodder"), ("detail", "invalid mod name"));
        if (packaging is not ("full" or "dll"))
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "susmodder"), ("detail", $"unknown packaging '{packaging}'"));
        if (packaging == "full" && game.Slug != "among-us")
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "susmodder"), ("detail", "full packages are Among Us only"));
        if (game.Slug is not ("among-us" or "valheim"))
            throw new ArgumentException("Unsupported game.", nameof(game));
        if (!Uri.TryCreate(artifactSource, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("An HTTPS download URL is required.", nameof(artifactSource));
        if (sha256 is null) throw new ArgumentException("Remote package requires SHA-256.", nameof(sha256));
        if (!Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$")) throw new ArgumentException("Invalid SHA-256.", nameof(sha256));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Version is required.", nameof(version));

        var name = Regex.Replace(fullName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (name.Length == 0) name = "mod";
        var recipeId = "susmodder." + name;
        var recipeVersion = ToRecipeVersion(version);
        var pinnedVersion = NormalizeGameVersion(amongVersion);
        var targetArch = arch ?? game.DefaultArch;
        SourcePolicy.AllowModArtifact(artifactSource, sha256);
        // Editions follow the artifact the selector picked (SusModder publishes per arch AND
        // store, e.g. itch x64); the arch table is only the fallback for artifacts that do
        // not declare their stores.
        var editions = EditionStores(stores, arch);

        return packaging == "full"
            ? GenerateFullCatalog(fullName, recipeId, recipeVersion, artifactSource, sha256, sizeBytes, targetArch, pinnedVersion, game,
                archiveEntries, provisionManifests, editions)
            : GenerateDllCatalog(fullName, name, recipeId, recipeVersion, artifactSource, sha256, sizeBytes,
                arch, pinnedVersion, archiveEntries, game, assetName, editions);
    }

    private static GeneratedRecipe GenerateFullCatalog(string fullName, string recipeId, string version,
        string artifactSource, string sha256, long? sizeBytes, string? arch, string? amongVersion, GameDefinition game,
        IEnumerable<string> archiveEntries, IReadOnlyDictionary<string, string>? provisionManifests = null,
        IReadOnlyList<string>? editions = null)
    {
        // A full package must be pinned to a concrete vanilla build: Phaket
        // provisions that exact game version and merges only writableAreas.
        if (amongVersion is null)
            throw new PhaketException(ErrorCodes.GameBuildUnavailable, ("detail", "full package without a pinned game version"));
        var loader = LoaderPackage(game, arch);
        var artifact = new RecipeArtifact
        {
            Id = "package", Source = artifactSource, Sha256 = sha256, SizeBytes = sizeBytes, Arch = arch,
        };
        var steps = new List<RecipeStep>();
        foreach (var store in new[] { "steam", "epic" })
        {
            var provision = new Dictionary<string, object?> { ["store"] = store, ["version"] = amongVersion };
            // PAK-96: a build the registry publishes a manifest for is provisioned by
            // that exact manifest; without one the store still provisions by version.
            if (provisionManifests?.GetValueOrDefault(store) is { Length: > 0 } manifestId)
                provision["manifestId"] = manifestId;
            steps.Add(Step($"provision-vanilla-{store}", "game.provision", provision) with
            {
                When = new RecipeCondition { Store = store },
            });
        }
        steps.Add(Step("ensure-bepinex", "loader.ensure", new Dictionary<string, object?>
        {
            ["id"] = "bepinex", ["artifact"] = loader.Artifact.Id, ["version"] = loader.RequirementVersion,
            ["stripComponents"] = loader.StripComponents,
        }));
        steps.Add(Step("install-mod-tree", "archive.extract", new Dictionary<string, object?>
        {
            ["from"] = "package://package", ["to"] = "game://.",
            ["include"] = game.WritableAreas,
            // PAK-96: SusModder full packages often wrap the game tree in one
            // folder ("TouMira v1.7.3-.../BepInEx/..."); without stripping it the
            // writableAreas filter matches nothing and the install silently writes
            // no mod files. Flat packages (BepInEx at the root) stay untouched.
            ["stripComponents"] = WrapperStrip(archiveEntries, game.WritableAreas),
        }));
        var editionStores = EditionStores(editions, arch);
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1", Id = recipeId, Version = version,
            Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = fullName, ["en"] = fullName } },
            Target = new RecipeTarget
            {
                Game = game.Slug, Systems = ["windows", "linux"],
                Editions = editionStores.Select(store => new EditionTarget { Store = store }).ToArray(),
                Constraints = new TargetConstraints { GameVersion = "=" + amongVersion },
            },
            Requires = new RecipeRequires
            {
                Capabilities = Capabilities(steps).Union(["file.verify"], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Loaders = [new LoaderRequirement { Id = "bepinex", Version = ">=6.0" }],
            },
            Artifacts = [artifact, loader.Artifact],
            Steps = steps,
            Verify = [new RecipeVerifyStep { Use = "file.verify", With = Values(new { path = "game://winhttp.dll" }) }],
        };
        return new(recipe, []);
    }

    private static GeneratedRecipe GenerateDllCatalog(string fullName, string safeName, string recipeId, string version,
        string artifactSource, string sha256, long? sizeBytes, string? arch, string? amongVersion,
        IEnumerable<string> archiveEntries, GameDefinition game, string? assetName = null,
        IReadOnlyList<string>? editions = null)
    {
        // A dll version may publish the bare plugin file instead of a ZIP; there is
        // nothing to extract, so it is copied straight into the plugins folder.
        if (DllCandidate(artifactSource, assetName) is { } dllName)
            return GenerateSingleDll(fullName, recipeId, version, artifactSource, sha256, sizeBytes, arch,
                amongVersion, SafeDllName(dllName), game, editions);

        var entries = archiveEntries.Select(NormalizeEntry).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).ToArray();
        if (entries.Length == 0)
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "susmodder"), ("detail", "ZIP contains no files"));
        var targets = EditionStores(editions, arch).Select(store => new EditionTarget { Store = store }).ToArray();
        var constraints = amongVersion is null ? null : new TargetConstraints { GameVersion = "=" + amongVersion };
        var artifact = new RecipeArtifact { Id = "package", Source = artifactSource, Sha256 = sha256, SizeBytes = sizeBytes, Arch = arch };

        // Addon ZIPs ship either game-relative BepInEx paths or Thunderstore-style
        // plugins/config folders; both end up under the game's writable areas.
        if (entries.Any(entry => entry.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)))
        {
            var steps = new[]
            {
                Step("install-mod-tree", "archive.extract", new Dictionary<string, object?>
                {
                    ["from"] = "package://package", ["to"] = "game://.", ["include"] = new[] { "BepInEx/**" },
                }),
            };
            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1", Id = recipeId, Version = version,
                Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = fullName, ["en"] = fullName } },
                Target = new RecipeTarget { Game = game.Slug, Systems = ["windows", "linux"], Editions = targets, Constraints = constraints },
                Requires = new RecipeRequires { Capabilities = Capabilities(steps) },
                Artifacts = [artifact], Steps = steps,
            };
            return new(recipe, []);
        }

        var generated = GenerateArchive(safeName, version, artifactSource, sha256, sizeBytes, entries, game, arch, recipeId, loader: false);
        return new(generated.Recipe with
        {
            Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = fullName, ["en"] = fullName } },
            Target = generated.Recipe.Target with { Editions = targets, Constraints = constraints },
        }, generated.Ownership);
    }

    // A `dll` catalog version whose artifact is the bare plugin file, not a ZIP.
    public static bool IsSingleDllArtifact(string packaging, string artifactSource, string? assetName)
    {
        if (packaging != "dll" || DllCandidate(artifactSource, assetName) is not { } name) return false;
        SafeDllName(name); // refuses traversal/path names before anything is downloaded
        return true;
    }

    private static string? DllCandidate(string artifactSource, string? assetName)
    {
        var name = string.IsNullOrWhiteSpace(assetName)
            ? Uri.TryCreate(artifactSource, UriKind.Absolute, out var uri)
                ? Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]) : null
            : assetName.Trim();
        return name?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true ? name : null;
    }

    private static readonly HashSet<string> WindowsDeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })),
        StringComparer.OrdinalIgnoreCase);

    // The catalog is network data: the plugin name becomes a game path, so it must
    // be one plain file name (no separators, no traversal, no Windows device name).
    private static string SafeDllName(string candidate)
    {
        if (!Regex.IsMatch(candidate, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.dll$", RegexOptions.IgnoreCase)
            || WindowsDeviceNames.Contains(candidate.Split('.')[0]))
            throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", "susmodder"),
                ("detail", $"unsafe dll file name '{candidate}'"));
        return candidate;
    }

    private static GeneratedRecipe GenerateSingleDll(string fullName, string recipeId, string version,
        string artifactSource, string sha256, long? sizeBytes, string? arch, string? amongVersion, string dllName,
        GameDefinition game, IReadOnlyList<string>? editions = null)
    {
        var target = "BepInEx/plugins/" + dllName;
        var steps = new[]
        {
            Step("copy-dll", "file.copy", new Dictionary<string, object?>
            {
                ["from"] = "package://package", ["to"] = "game://" + target,
            }),
        };
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1", Id = recipeId, Version = version,
            Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = fullName, ["en"] = fullName } },
            Target = new RecipeTarget
            {
                Game = game.Slug, Systems = ["windows", "linux"],
                Editions = EditionStores(editions, arch).Select(store => new EditionTarget { Store = store }).ToArray(),
                Constraints = amongVersion is null ? null : new TargetConstraints { GameVersion = "=" + amongVersion },
            },
            Requires = new RecipeRequires
            {
                Capabilities = Capabilities(steps).Union(["file.verify"], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            },
            Artifacts =
            [
                new RecipeArtifact
                {
                    Id = "package", Source = artifactSource, Sha256 = sha256, SizeBytes = sizeBytes, Arch = arch,
                    Format = "dll",
                },
            ],
            Steps = steps,
            Verify = [new RecipeVerifyStep { Use = "file.verify", With = Values(new { path = "game://" + target, sha256 }) }],
        };
        return new(recipe, []);
    }

    private static string[] EditionStores(IReadOnlyList<string>? stores, string? arch) =>
        stores is { Count: > 0 } ? stores.Distinct(StringComparer.Ordinal).ToArray() : EditionsFor(arch);

    private static string[] EditionsFor(string? arch) => arch switch
    {
        "x86" => ["steam", "epic", "itch"],
        _ => ["steam", "epic", "msstore"],
    };

    private static IReadOnlyList<string> Capabilities(IEnumerable<RecipeStep> steps) =>
        steps.Select(step => OperationCatalog.TryGet(step.Use, out var operation) ? operation!.Capabilities : [])
            .SelectMany(x => x).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    // "ToU" means the addon follows the base mod, not the game version: no version binding.
    // PAK-96: labels follow the server's canonical rule — "2026-8-18", "2026.08.18" and
    // "2026.8.18" are the same build; impossible dates and non-numeric text are refused.
    public static string? NormalizeGameVersion(string? amongVersion)
    {
        if (string.IsNullOrWhiteSpace(amongVersion) || amongVersion.Equals("ToU", StringComparison.OrdinalIgnoreCase)) return null;
        var label = GameVersionLabel.Normalize(amongVersion.Trim().TrimStart('v', 'V'));
        if (label is null || !Regex.IsMatch(label, @"^[0-9]+(\.[0-9]+)*$"))
            throw new PhaketException(ErrorCodes.InvalidVersion, ("version", amongVersion));
        return label;
    }

    // Version is the loader.ensure parameter; RequirementVersion is numeric-dot so
// `requires.loaders` version ranges can actually match it.
    private sealed record LoaderSelection(RecipeArtifact Artifact, string Version, string RequirementVersion, int StripComponents);

    private static LoaderSelection LoaderPackage(GameDefinition game, string? arch)
    {
        // Among Us ships an IL2CPP BepInEx build; the x86 Thunderstore pack wraps
        // its files in one folder, the x64 bepinex.dev build does not.
        return game.Slug switch
        {
            "valheim" when arch == "x86" => throw new PhaketException("LOADER_ARCH_UNAVAILABLE", ("arch", arch)),
            "valheim" => new(new RecipeArtifact
            {
                Id = "loader", Arch = "x64",
                Source = "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2360/",
                Sha256 = "6bca77b1016acfe0ffaebb6534c84b12ae8d12e5a96abbf59ec6e4526204fc6f",
            }, "5.4.2360", "5.4.2360", 1),
            _ when arch == "x64" => new(new RecipeArtifact
            {
                Id = "loader", Arch = "x64",
                Source = "https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip",
                Sha256 = "5b63d7d0e92a2448ccd13b738ff95ff1cc1026c6d718555d4df8e6c824953ac4",
            }, "6.0.0-be.793", "6.0.0", 0),
            _ => new(new RecipeArtifact
            {
                Id = "loader", Arch = "x86",
                Source = "https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/",
                Sha256 = "88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564",
            }, "6.0.700", "6.0.700", 1),
        };
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
            var package = LoaderPackage(game, arch);
            artifact = artifact with
            {
                Arch = package.Artifact.Arch, Source = package.Artifact.Source, Sha256 = package.Artifact.Sha256,
            };
        }

        var steps = new List<RecipeStep>();
        var ownership = new List<GeneratedRecipeStepOwnership>();
        if (!loader)
        {
            steps.Add(Step("extract", "archive.extract", new { from = "package://package", to = "staging://package" }));
            var index = 0;
            foreach (var entry in entries)
            {
                var target = Route(entry, fullName, game);
                var stepId = $"copy-{index++:D5}";
                steps.Add(Step(stepId, "file.copy", new { from = "staging://package/" + entry, to = "game://" + target }));
                if (target.StartsWith("BepInEx/config/", StringComparison.Ordinal)) ownership.Add(new(stepId, "shared"));
            }
        }
        else
        {
            var layout = game.Loader("bepinex") ?? throw new ArgumentException("Game has no BepInEx layout.", nameof(game));
            var package = LoaderPackage(game, arch);
            steps.Add(Step("extract", "loader.ensure", new Dictionary<string, object?> { ["id"] = layout.Id, ["artifact"] = artifactId,
                ["version"] = package.Version, ["stripComponents"] = package.StripComponents }));
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
        Id = id, Use = use, With = Values(values)
    };

    private static Dictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values, JsonOptions), JsonOptions)!;

    // A full archive whose files all live under one folder that is not itself a
    // game-relative writable area is a wrapper (author/version folder), so the
    // include filter must see the path after that folder.
    private static int WrapperStrip(IEnumerable<string> entries, IReadOnlyList<string> writableAreas)
    {
        var files = entries.Select(NormalizeEntry).Where(x => x is not null).Select(x => x!).ToArray();
        if (files.Length == 0) return 0;
        var roots = files.Select(f => f.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1) return 0;
        var root = roots[0];
        // Every entry must be inside the folder; a file named like the root means flat layout.
        if (files.Any(f => f == root || !f.Contains('/'))) return 0;
        var topAreas = writableAreas.Where(area => area.Contains('/'))
            .Select(area => area.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return topAreas.Contains(root) ? 0 : 1;
    }

    private static string? NormalizeEntry(string value)
    {
        var path = value.Replace('\\', '/');
        if (path.EndsWith('/')) return null;
        if (path.Length == 0 || path.Split('/').Any(part => part is "" or "." or "..") || path.Contains(':') || path.Any(char.IsControl))
            throw new ArgumentException("Archive entry has an unsafe path.", nameof(value));
        return path;
    }

    private static string Route(string entry, string fullName, GameDefinition game)
    {
        var separator = entry.IndexOf('/');
        var first = separator < 0 ? entry : entry[..separator];
        var rest = separator < 0 ? "" : entry[(separator + 1)..];
        // PAK-125: a game-relative archive (a loader or a mod already laid out for
        // the game) keeps its paths; nesting it under plugins/<package>/ breaks it.
        // The loader folder name is fixed so Linux gets BepInEx, not the archive's case.
        if (separator > 0 && first.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) return "BepInEx/" + rest;
        if (first.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || first.Equals("README.md", StringComparison.OrdinalIgnoreCase)
            || first.Equals("icon.png", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/plugins/{fullName}/{entry}";
        if (first.Equals("config", StringComparison.OrdinalIgnoreCase)) return "BepInEx/config/" + rest;
        if (new[] { "plugins", "patchers", "core", "monomod" }.Contains(first, StringComparer.OrdinalIgnoreCase))
            return $"BepInEx/{first}/{fullName}/{rest}";
        // Loader root files (winhttp.dll, doorstop_config.ini, doorstop_libs/…)
        // belong at the game root, as the definition's writable areas declare.
        if (game.AllowsWrite(entry)) return entry;
        return $"BepInEx/plugins/{fullName}/{entry}";
    }
}
