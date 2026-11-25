using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-95: archive.extract `include` filters entries after stripComponents without
// weakening zip-slip/traversal protection or the game's writableAreas boundary.
public sealed class Pak95ArchiveIncludeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-include-").FullName;

    private string GameRoot => Path.Combine(root, "game");
    private string PackageRoot => Path.Combine(root, "packages");

    public Pak95ArchiveIncludeTests()
    {
        foreach (var dir in new[] { GameRoot, Path.Combine(root, "profile"), Path.Combine(root, "staging"), PackageRoot })
            Directory.CreateDirectory(dir);
    }

    public void Dispose() => Directory.Delete(root, true);

    private static readonly GameDefinition AmongUs = GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private static void MakeZip(string path, params (string Name, string Text)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
    }

    private string ModZip() => Path.Combine(PackageRoot, "package.zip");

    [Fact]
    public void IncludeKeepsOnlyWritableAreasAndSkipsVanillaFiles()
    {
        MakeZip(ModZip(),
            ("TouMira-1.7.3/Among Us_Data/sharedassets0.assets", "vanilla"),
            ("TouMira-1.7.3/BepInEx/plugins/TownOfUsMira.dll", "mod"),
            ("TouMira-1.7.3/dotnet/coreclr.dll", "runtime"),
            ("TouMira-1.7.3/winhttp.dll", "proxy"),
            ("TouMira-1.7.3/doorstop_config.ini", "doorstop"));
        var include = new[] { "BepInEx/**", "dotnet/**", "winhttp.dll", "doorstop_config.ini" };

        var created = SafeArchive.Extract(ModZip(), GameRoot, "package", stripComponents: 1, include: include);

        Assert.Equal(4, created.Count);
        Assert.True(File.Exists(Path.Combine(GameRoot, "BepInEx", "plugins", "TownOfUsMira.dll")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "dotnet", "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "winhttp.dll")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "doorstop_config.ini")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "Among Us_Data", "sharedassets0.assets")));
    }

    [Fact]
    public void IncludeMatchesAfterStripSoWrapperPrefixesAreNotPartOfThePattern()
    {
        MakeZip(ModZip(), ("TouMira-1.7.3/BepInEx/plugins/Mod.dll", "mod"));

        // stripComponents: 0 -> the wrapper folder is part of the path and `BepInEx/**` does not match.
        Assert.Empty(SafeArchive.PlanWrites(ModZip(), GameRoot, "package", 0, ["BepInEx/**"]));
        // stripComponents: 1 -> strip first, then match; the same pattern now applies.
        Assert.Single(SafeArchive.PlanWrites(ModZip(), GameRoot, "package", 1, ["BepInEx/**"]));
    }

    [Fact]
    public void WithoutIncludeEveryEntryIsExtracted()
    {
        MakeZip(ModZip(), ("BepInEx/plugins/Mod.dll", "mod"), ("Among Us_Data/level.assets", "vanilla"));
        Assert.Equal(2, SafeArchive.PlanWrites(ModZip(), GameRoot, "package").Count);
    }

    // An empty list is the schema minimum (minItems: 1) being bypassed at runtime: it must
    // extract nothing, never mean "no filter".
    [Fact]
    public void EmptyIncludeListExtractsNothing()
    {
        MakeZip(ModZip(), ("BepInEx/plugins/Mod.dll", "mod"), ("Among Us_Data/level.assets", "vanilla"));

        Assert.Empty(SafeArchive.PlanWrites(ModZip(), GameRoot, "package", 0, []));
        Assert.Empty(SafeArchive.PlanDirectories(ModZip(), GameRoot, "package", 0, []));
        Assert.Empty(SafeArchive.Extract(ModZip(), GameRoot, "package", 0, include: []));
        Assert.Empty(Directory.GetFiles(GameRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ZipSlipIsRejectedEvenWhenTheEntryIsNotIncluded()
    {
        MakeZip(ModZip(), ("../evil.dll", "escape"), ("BepInEx/plugins/Ok.dll", "ok"));

        var error = Assert.Throws<PhaketException>(() =>
            SafeArchive.Extract(ModZip(), GameRoot, "package", 0, include: ["BepInEx/**"]));

        Assert.Equal(ErrorCodes.ArchiveUnsafe, error.Error.Code);
        Assert.False(File.Exists(Path.Combine(root, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "BepInEx", "plugins", "Ok.dll")));
    }

    private OperationPlan Plan(Recipe recipe, string? arch = "x86") => OperationPlanner.Plan(recipe,
        new PlannerContext("i", "p", GameRoot, Path.Combine(root, "profile"), Path.Combine(root, "staging"), PackageRoot,
            AmongUs, new PlanFacts("linux", "steam", null, OperationCatalog.Capabilities, _ => false, Arch: arch)));

    private static Recipe ExtractRecipe(GameDefinition game, params string[] include) => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.include",
        Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
        Requires = new RecipeRequires { Capabilities = ["archive.extract"] },
        Artifacts = [new RecipeArtifact { Id = "package", Source = "package://package.zip" }],
        Steps =
        [
            new RecipeStep
            {
                Id = "extract",
                Use = "archive.extract",
                With = new Dictionary<string, JsonElement>
                {
                    ["from"] = JsonSerializer.SerializeToElement("package://package"),
                    ["to"] = JsonSerializer.SerializeToElement("game://."),
                    ["include"] = JsonSerializer.SerializeToElement(include),
                },
            },
        ],
    };

    [Fact]
    public void ExecutorExtractsOnlyIncludedEntries()
    {
        MakeZip(ModZip(), ("BepInEx/plugins/Mod.dll", "mod"), ("Among Us_Data/level.assets", "vanilla"));
        var recipe = ExtractRecipe(AmongUs, "BepInEx/**");
        var plan = Plan(recipe);
        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
        var executor = new PlanExecutor(new PlanExecutionContext(GameRoot, Path.Combine(root, "profile"),
            Path.Combine(root, "staging"), PackageRoot, AmongUs, recipe.Id, "p", Path.Combine(root, "state")));
        var journal = executor.Execute(plan, executor.Approve(plan), ct: TestContext.Current.CancellationToken);

        Assert.Equal(JournalState.Completed, journal.State);
        Assert.True(File.Exists(Path.Combine(GameRoot, "BepInEx", "plugins", "Mod.dll")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "Among Us_Data", "level.assets")));
    }

    [Theory]
    [InlineData("Among Us_Data/**")]
    [InlineData("**/*.dll")]
    [InlineData("../outside.dll")]
    [InlineData("BepInEx/../../outside.dll")]
    public void IncludeOutsideWritableAreasIsRejectedByThePlan(string pattern)
    {
        MakeZip(ModZip(), ("BepInEx/plugins/Mod.dll", "mod"));
        var error = Assert.Throws<PhaketException>(() => Plan(ExtractRecipe(AmongUs, pattern)));
        Assert.Contains(error.Error.Code, new[] { ErrorCodes.PathOutOfBounds, ErrorCodes.ValidationFailed });
    }

    [Fact]
    public void StagingIncludeIsNotBoundToGameWritableAreas()
    {
        MakeZip(ModZip(), ("data/file.bin", "x"));
        var recipe = ExtractRecipe(AmongUs, "data/**") with
        {
            Steps =
            [
                new RecipeStep
                {
                    Id = "extract", Use = "archive.extract",
                    With = new Dictionary<string, JsonElement>
                    {
                        ["from"] = JsonSerializer.SerializeToElement("package://package"),
                        ["to"] = JsonSerializer.SerializeToElement("staging://work"),
                        ["include"] = JsonSerializer.SerializeToElement(new[] { "data/**" }),
                    },
                },
            ],
        };
        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));
        Assert.NotEmpty(Plan(recipe).Operations);
    }
}