using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-95: recipe generator for SusModder `full` (whole modified game) and `dll` addons.
public sealed class Pak95SusModderRecipeTests
{
    private static readonly GameDefinition AmongUs = GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private const string TouUrl = "https://github.com/AU-Avengers/TOU-Mira/releases/download/1.7.3/TouMira.v1.7.3-x86-steam-itch.zip";
    private const string X64Url = "https://github.com/AU-Avengers/TOU-Mira/releases/download/1.7.3/TouMira.v1.7.3-x64-epic-msstore.zip";

    private static GeneratedRecipe Full(string arch, string amongVersion, string url = TouUrl) =>
        RecipeGenerator.GenerateCatalogPackage("Town of Us Mira", "1.7.3", "full", url, new string('a', 64), 91_654_060,
            arch, amongVersion, ["BepInEx/plugins/TownOfUsMira.dll", "Among Us_Data/sharedassets0.assets"], AmongUs);

    private static OperationPlan Plan(Recipe recipe, string? arch, string store, string? gameVersion)
    {
        using var fixture = new ExecutorFixture();
        return OperationPlanner.Plan(recipe, new PlannerContext(
            "install-1", "profile-1", fixture.GameRoot, fixture.ProfileRoot, fixture.StagingRoot, fixture.PackageRoot,
            AmongUs, new PlanFacts("linux", store, gameVersion, OperationCatalog.Capabilities, _ => false,
                Arch: arch)));
    }

    [Fact]
    public void FullPackageProvisionsPinnedVanillaThenExtractsOnlyWritableAreas()
    {
        var generated = Full("x86", "2026-8-18");
        var recipe = generated.Recipe;

        Assert.Equal("susmodder.town-of-us-mira", recipe.Id);
        Assert.Equal("1.7.3", recipe.Version);
        Assert.Equal("=2026.8.18", recipe.Target.Constraints?.GameVersion);
        Assert.Equal(["epic", "itch", "steam"], recipe.Target.Editions!.Select(e => e.Store).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["game.provision", "game.provision", "loader.ensure", "archive.extract"],
            recipe.Steps.Select(s => s.Use));
        Assert.Equal("steam", recipe.Steps[0].With["store"].GetString());
        Assert.Equal("2026.8.18", recipe.Steps[0].With["version"].GetString());
        Assert.Equal("epic", recipe.Steps[1].With["store"].GetString());
        Assert.Equal("x86", recipe.Artifacts.Single(a => a.Id == "loader").Arch);

        var include = recipe.Steps[3].With["include"].EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(AmongUs.WritableAreas.ToArray(), include);
        Assert.Contains("winhttp.dll", include);
        Assert.DoesNotContain(include, pattern => pattern.Contains("_Data", StringComparison.Ordinal));

        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
    }

    [Fact]
    public void FullPackagePlanForTownOfUsMira173X86()
    {
        var plan = Plan(Full("x86", "2026-8-18").Recipe, "x86", "steam", "2026.8.18");

        Assert.Contains(plan.Operations, op => op.Kind == "game.provision");
        Assert.Contains(plan.Operations, op => op.Kind == "loader.ensure");
        Assert.Contains(plan.Operations, op => op.Kind == "archive.extract" && op.Parameters!.ContainsKey("include"));
        Assert.True(plan.RequiresDownload);
        Assert.Contains(plan.Verification, op => op.TargetPath.EndsWith("winhttp.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void FullPackageX64IsBoundToItsArchitectureAndNewerBuild()
    {
        var recipe = Full("x64", "2026-9-29", X64Url).Recipe;
        Assert.Equal(["epic", "msstore", "steam"], recipe.Target.Editions!.Select(e => e.Store).Order(StringComparer.Ordinal));
        Assert.Equal("=2026.9.29", recipe.Target.Constraints?.GameVersion);
        Assert.Equal("x64", recipe.Artifacts.Single(a => a.Id == "loader").Arch);
        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));

        var x86Recipe = Full("x86", "2026-9-29").Recipe;
        var error = Assert.Throws<PhaketException>(() => Plan(x86Recipe, "x64", "steam", "2026.9.29"));
        Assert.Equal("LOADER_ARCH_UNAVAILABLE", error.Error.Code);
    }

    [Fact]
    public void FullPackageX64PlanForTownOfUsMira173UsesTheEpicX64Variant()
    {
        var recipe = Full("x64", "2026-8-18", X64Url).Recipe;
        Assert.Equal("=2026.8.18", recipe.Target.Constraints?.GameVersion);
        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
        Assert.NotEmpty(Plan(recipe, "x64", "steam", "2026.8.18").Operations);
    }

    [Fact]
    public void FullPackageWithoutPinnedGameVersionIsRefused()
    {
        var error = Assert.Throws<PhaketException>(() => Full("x64", "ToU"));
        Assert.Equal(ErrorCodes.GameBuildUnavailable, error.Error.Code);
    }

    [Theory]
    [InlineData("2026-9-29", "2026.9.29")]
    [InlineData("2026.8.18", "2026.8.18")]
    public void GameVersionLabelsUseNumericDots(string label, string expected) =>
        Assert.Equal(expected, RecipeGenerator.NormalizeGameVersion(label));

    [Theory]
    [InlineData("ToU")]
    [InlineData(null)]
    public void ToUMarkerOrMissingVersionIsNoBinding(string? label) =>
        Assert.Null(RecipeGenerator.NormalizeGameVersion(label));

    [Fact]
    public void GameRelativeDllZipExtractsBepInExOnlyAndHasNoVersionBinding()
    {
        var generated = RecipeGenerator.GenerateCatalogPackage("Submerged", "1.0.0", "dll",
            "https://thunderstore.io/package/download/Test/Submerged/1.0.0/", new string('b', 64), 1024,
            "x86", "ToU", ["BepInEx/plugins/Submerged/Submerged.dll"], AmongUs);
        var recipe = generated.Recipe;

        Assert.Null(recipe.Target.Constraints);
        Assert.Equal(["epic", "itch", "steam"], recipe.Target.Editions!.Select(e => e.Store).Order(StringComparer.Ordinal));
        var step = Assert.Single(recipe.Steps);
        Assert.Equal("archive.extract", step.Use);
        Assert.Equal("game://.", step.With["to"].GetString());
        Assert.Equal(["BepInEx/**"], step.With["include"].EnumerateArray().Select(x => x.GetString()));

        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
        Assert.NotEmpty(Plan(recipe, "x86", "steam", null).Operations);
    }

    [Fact]
    public void ThunderstoreShapedDllStillRoutesIntoPlugins()
    {
        var recipe = RecipeGenerator.GenerateCatalogPackage("LevelImposter", "1.2.3", "dll",
            "https://thunderstore.io/package/download/Test/LevelImposter/1.2.3/", new string('c', 64), 2048,
            "x64", "2026-9-29", ["plugins/LevelImposter.dll", "config/levelimposter.cfg"], AmongUs).Recipe;

        Assert.Equal("=2026.9.29", recipe.Target.Constraints?.GameVersion);
        var copies = recipe.Steps.Where(s => s.Use == "file.copy")
            .Select(s => s.With["to"].GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("game://BepInEx/plugins/levelimposter/LevelImposter.dll", copies);
        Assert.Contains("game://BepInEx/config/levelimposter.cfg", copies);
        Assert.DoesNotContain("game://BepInEx/plugins/levelimposter/plugins", string.Join(";", copies));

        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
        Assert.NotEmpty(Plan(recipe, "x64", "steam", "2026.9.29").Operations);
    }

    // W7: the fixed arch->editions table cut off real installs (itch x64). When the
    // selected artifact declares its stores, those are the editions.
    [Fact]
    public void EditionsFollowTheSelectedArtifactStores()
    {
        var version = new ThunderstoreVersion("1.0.0", "Sus-Submerged-1.0.0", X64Url, 2048, DateTimeOffset.UnixEpoch,
            [], true, [new ModArtifact(X64Url, "x64", ["steam", "itch"], new string('b', 64), 2048, "susmodder-cdn", "mod.zip")],
            "dll", "ToU", "x64", "susmodder", null);

        var selected = ModArtifactSelector.Select(version, "Submerged", "x64", "itch");
        var recipe = RecipeGenerator.GenerateCatalogPackage("Submerged", "1.0.0", "dll", selected.Url, selected.Sha256,
            selected.SizeBytes, selected.Arch, "ToU", ["BepInEx/plugins/Submerged/Submerged.dll"], AmongUs,
            selected.AssetName, null, selected.Stores).Recipe;

        Assert.Equal(["itch", "steam"], recipe.Target.Editions!.Select(e => e.Store).Order(StringComparer.Ordinal));
        Assert.NotEmpty(Plan(recipe, "x64", "itch", null).Operations);
    }
}
