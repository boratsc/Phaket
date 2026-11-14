using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Domain;

namespace Phaket.Core.Tests;

public sealed class RecipeGeneratorTests
{
    [Fact]
    public void RoutesEntriesAndMarksConfigShared()
    {
        var game = GameDefinitionLoader.LoadYaml(File.ReadAllText(Definition("among-us")));
        var generated = RecipeGenerator.Generate("owner-Mod", "1.2.3", "https://thunderstore.io/package/download/owner/Mod/1.2.3/", new string('a', 64),
            ["plugins/a.dll", "patchers/b.dll", "CORE/c.dll", "monomod/d.dll", "CONFIG/settings.cfg", "loose.dll", "README.md", "manifest.json"], game);
        var targets = generated.Recipe.Steps.Where(s => s.Use == "file.copy").Select(s => s.With["to"].GetString()).ToArray();
        Assert.Contains("game://BepInEx/plugins/owner-Mod/a.dll", targets);
        Assert.Contains("game://BepInEx/patchers/owner-Mod/b.dll", targets);
        Assert.Contains("game://BepInEx/CORE/owner-Mod/c.dll", targets);
        Assert.Contains("game://BepInEx/monomod/owner-Mod/d.dll", targets);
        Assert.Contains("game://BepInEx/config/settings.cfg", targets);
        Assert.Contains("game://BepInEx/plugins/owner-Mod/loose.dll", targets);
        Assert.Contains("game://BepInEx/plugins/owner-Mod/README.md", targets);
        Assert.Contains("game://BepInEx/plugins/owner-Mod/manifest.json", targets);
        Assert.Equal("shared", Assert.Single(generated.Ownership).Ownership);
        Assert.Equal("thunderstore.owner.mod", generated.Recipe.Id);
        Assert.Equal("owner-Mod", generated.Recipe.Metadata.Title["en"]);
        Assert.Empty(RecipeValidator.Validate(generated.Recipe, game).Select(e => (e.Code, string.Join(";", e.Parameters.Select(p => p.Key + "=" + p.Value)))));
    }

    [Theory]
    [InlineData("among-us")]
    [InlineData("valheim")]
    public void LoaderPackGeneratesValidEnsure(string slug)
    {
        var game = GameDefinitionLoader.LoadYaml(File.ReadAllText(Definition(slug)));
        var generated = RecipeGenerator.Generate("BepInEx-BepInExPack_Game", "6.0.700", "https://thunderstore.io/package/download/BepInEx/BepInExPack_Game/6.0.700/", new string('a', 64), ["BepInExPack_Game/winhttp.dll"], game);
        Assert.Equal("loader.ensure", Assert.Single(generated.Recipe.Steps).Use);
        Assert.Empty(RecipeValidator.Validate(generated.Recipe, game));
    }

    [Theory]
    [InlineData("among-us")]
    [InlineData("valheim")]
    public void RoundTripThroughValidatorAndPlanner(string slug)
    {
        var game = GameDefinitionLoader.LoadYaml(File.ReadAllText(Definition(slug)));
        foreach (var (name, entries, expected) in new[]
        {
            ("owner-Mod", new[] { "plugins/A.dll", "config/x.cfg", "B.dll" }, new[] { "BepInEx/plugins/owner-Mod/A.dll", "BepInEx/config/x.cfg", "BepInEx/plugins/owner-Mod/B.dll" }),
            ("BepInEx-BepInExPack_Game", new[] { "BepInEx/core/core.dll", "winhttp.dll" }, new[] { "BepInEx/core/BepInEx-BepInExPack_Game/core.dll" })
        })
        {
            if (name.StartsWith("BepInEx", StringComparison.Ordinal)) continue;
            var generated = RecipeGenerator.Generate(name, "1.2.3", "https://thunderstore.io/package/download/owner/Mod/1.2.3/", new string('a', 64), entries, game);
            Assert.Empty(RecipeValidator.Validate(generated.Recipe, game));
            using var fixture = new PlannerFixture();
            var context = new PlannerContext("install-1", "profile-1", fixture.GameRoot, fixture.ProfileRoot,
                fixture.StagingRoot, fixture.PackageRoot,
                slug == "among-us" ? TestFixtures.AmongUs : TestFixtures.Valheim,
                new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false,
                    new Dictionary<string, string> { ["bepinex"] = "6.0.0" }));
            Directory.CreateDirectory(Path.Combine(fixture.StagingRoot, "package", "plugins"));
            Directory.CreateDirectory(Path.Combine(fixture.StagingRoot, "package", "config"));
            File.WriteAllText(Path.Combine(fixture.StagingRoot, "package", "plugins", "A.dll"), "a");
            File.WriteAllText(Path.Combine(fixture.StagingRoot, "package", "config", "x.cfg"), "x");
            File.WriteAllText(Path.Combine(fixture.StagingRoot, "package", "B.dll"), "b");
            var plan = OperationPlanner.Plan(generated.Recipe, context);
            var paths = plan.Operations.Select(o => o.TargetPath).ToArray();
            foreach (var target in expected) Assert.Contains(paths, p => p.EndsWith(target.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DeterministicUnderEntryPermutation()
    {
        var game = GameDefinitionLoader.LoadYaml(File.ReadAllText(Definition("among-us")));
        var first = RecipeGenerator.Generate("owner-Mod", "1.2.3", "https://thunderstore.io/package/download/owner/Mod/1.2.3/", new string('a', 64), ["plugins/A.dll", "config/x.cfg", "B.dll"], game).Recipe;
        var second = RecipeGenerator.Generate("owner-Mod", "1.2.3", "https://thunderstore.io/package/download/owner/Mod/1.2.3/", new string('a', 64), ["B.dll", "config/x.cfg", "plugins/A.dll"], game).Recipe;
        Assert.Equal(first.Steps.Select(s => s.Use + "|" + string.Join("|", s.With.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value))),
            second.Steps.Select(s => s.Use + "|" + string.Join("|", s.With.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value))));
    }

    [Fact]
    public void ExtractPrecedesSortedCopiesAndGeneratedRecipePlansWithLooseFiles()
    {
        var game = GameDefinitionLoader.LoadYaml(File.ReadAllText(Definition("among-us")));
        var generated = RecipeGenerator.Generate("owner-Mod", "1.2.3", "https://thunderstore.io/package/download/owner/Mod/1.2.3/", new string('a', 64),
            ["plugins/Mod.dll", "README.md", "CHANGELOG.md"], game);
        Assert.Equal(new[] { "archive.extract", "file.copy", "file.copy", "file.copy" }, generated.Recipe.Steps.Select(s => s.Use));
        Assert.Equal(generated.Recipe.Steps.Skip(1).Select(s => s.With["to"].GetString()).Order(StringComparer.Ordinal),
            generated.Recipe.Steps.Skip(1).Select(s => s.With["to"].GetString()));

        using var fixture = new PlannerFixture();
        var context = new PlannerContext("install-1", "profile-1", fixture.GameRoot, fixture.ProfileRoot,
            fixture.StagingRoot, fixture.PackageRoot, TestFixtures.AmongUs,
            new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.0" }));
        Directory.CreateDirectory(Path.Combine(fixture.StagingRoot, "package", "plugins"));
        File.WriteAllText(Path.Combine(fixture.StagingRoot, "package", "CHANGELOG.md"), "change");
        File.WriteAllText(Path.Combine(fixture.StagingRoot, "package", "README.md"), "readme");
        File.WriteAllText(Path.Combine(fixture.StagingRoot, "package", "plugins", "Mod.dll"), "mod");
        Assert.NotEmpty(OperationPlanner.Plan(generated.Recipe, context).Operations);
    }

    [Fact]
    public void MissingSha256ForRemoteArtifactIsRejected()
    {
        var game = GameDefinitionLoader.LoadYaml(File.ReadAllText(Definition("among-us")));
        var exception = Assert.Throws<ArgumentException>(() => RecipeGenerator.Generate("owner-Mod", "1.2.3", "https://thunderstore.io/package/download/owner/Mod/1.2.3/", null, ["A.dll"], game));
        Assert.Equal("sha256", exception.ParamName);
    }

    private static string Definition(string slug) => Path.Combine(AppContext.BaseDirectory, "definitions", "games", slug + ".yaml");
}
