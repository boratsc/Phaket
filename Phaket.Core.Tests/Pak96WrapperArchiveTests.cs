using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-96 gate: SusModder full packages come in two shapes. Some wrap the whole
// game tree in an author/version folder, some are already game-relative. The
// writableAreas include filter matches the path after stripComponents, so the
// wrapper must be stripped or a "successful" install writes no mod files.
public sealed class Pak96WrapperArchiveTests
{
    private static readonly GameDefinition AmongUs = GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private const string Url = "https://susmodder-cdn.ovh/mods/tou/1.7.3-x86.zip";

    private static Recipe Full(params string[] entries) =>
        RecipeGenerator.GenerateCatalogPackage("Town of Us Mira", "1.7.3", "full", Url, new string('a', 64),
            91_654_060, "x86", "2026-8-18", entries, AmongUs).Recipe;

    private static int Strip(Recipe recipe) =>
        recipe.Steps.Single(s => s.Use == "archive.extract").With["stripComponents"].GetInt32();

    [Fact]
    public void SingleWrapperFolderIsStrippedBeforeWritableAreaFilter()
    {
        var recipe = Full(
            "TouMira v1.7.3-x86-steam-itch/BepInEx/plugins/TownOfUsMira.dll",
            "TouMira v1.7.3-x86-steam-itch/Among Us_Data/sharedassets0.assets");
        Assert.Equal(1, Strip(recipe));
    }

    [Fact]
    public void GameRelativeArchiveIsNotStripped()
    {
        var recipe = Full(
            "BepInEx/plugins/TownOfUsMira.dll",
            "winhttp.dll",
            "Among Us_Data/sharedassets0.assets");
        Assert.Equal(0, Strip(recipe));
    }

    [Fact]
    public void WrapperIsStrippedButNonWritableGameFilesStayOut()
    {
        var entries = new[]
        {
            "TouMira v1.7.3-x86-steam-itch/BepInEx/plugins/TownOfUsMira.dll",
            "TouMira v1.7.3-x86-steam-itch/winhttp.dll",
            "TouMira v1.7.3-x86-steam-itch/Among Us_Data/sharedassets0.assets",
        };
        var recipe = Full(entries);
        using var fixture = new ExecutorFixture();
        var archive = Path.Combine(fixture.PackageRoot, "package.zip");
        ExecutorFixture.MakeZip(archive,
            (entries[0], "tou-mira"u8.ToArray()),
            (entries[1], "winhttp"u8.ToArray()),
            (entries[2], "vanilla-asset"u8.ToArray()));

        SafeArchive.Extract(archive, fixture.GameRoot, "package", Strip(recipe), null, AmongUs.WritableAreas);

        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx/plugins/TownOfUsMira.dll")));
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "Among Us_Data/sharedassets0.assets")));
        Assert.False(Directory.Exists(Path.Combine(fixture.GameRoot, "TouMira v1.7.3-x86-steam-itch")));
    }
}