using System.Security.Cryptography;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;

namespace Phaket.Core.Tests;

public class RealRecipeTests
{
    private static readonly string[] RecipeFiles =
    [
        "valheim-bepinex-loader.yaml",
        "valheim-morevanillabuilds.yaml",
        "among-us-bepinex-loader.yaml",
        "among-us-betterpolus.yaml",
        "among-us-outfit-presets.yaml",
        "among-us-tou-mira-1.7.3.yaml",
    ];

    [Theory]
    [MemberData(nameof(RecipeNames))]
    public void ShippedRealRecipesParseValidateAndPlan(string filename)
    {
        var recipe = RecipeParser.ParseYaml(File.ReadAllText(Path.Combine(RecipeLibrary.Directory, filename)));
        var definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "definitions", "games", $"{recipe.Target.Game}.yaml")));
        Assert.Empty(RecipeSchema.Validate(System.Text.Json.JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement));
        Assert.Empty(RecipeValidator.Validate(recipe, definition));

        var root = Directory.CreateTempSubdirectory("phaket-real-recipe-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            foreach (var path in new[] { game, Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages") })
                Directory.CreateDirectory(path);
            var facts = new PlanFacts("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "5.4.2360" });
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, Path.Combine(root, "profile"),
                Path.Combine(root, "staging"), Path.Combine(root, "packages"), definition, facts));
            Assert.NotEmpty(plan.Operations);
            Assert.All(plan.Operations.Where(o => o.Kind == "file.copy"),
                op => Assert.StartsWith(game + Path.DirectorySeparatorChar, op.TargetPath, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static IEnumerable<object[]> RecipeNames() => RecipeFiles.Select(x => new object[] { x });

    [Fact]
    public void DeclaredArtifactDigestsMatchDownloadedPackagesWhenAvailable()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["valheim-loader-5.4.2360.zip"] = "6bca77b1016acfe0ffaebb6534c84b12ae8d12e5a96abbf59ec6e4526204fc6f",
            ["valheim-loader-5.4.2202.zip"] = "d9c3b16992aa6a02069a921a3e4d613240fe291282bb67fe92261b00c6fabf8c",
            ["jotunn-2.26.1.zip"] = "37a408db8c5eec1345ae345bf97423e201805e21c6ed912923f6eb3fa4c5aec9",
            ["morevanillabuilds-1.1.5.zip"] = "3f498b4b7bb5d621ac48533426b7ff63dbc07b65605cc1ac67f6051674163150",
            ["amongus-loader-6.0.700.zip"] = "88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564",
            ["outfit-presets-1.1.1.zip"] = "46b011e61ff8d5d4d4d7504c6517122df6ce6b148984a5de595ec039b85f10a6",
            ["tabsbuilderapi-1.2.1.zip"] = "f73799520b987384514f88c3899a92d2f3fc895c6dceec60fd0e776b0666aeec",
        };
        var artifactRoot = "/tmp/opencode/phaket-research/artifacts";
        foreach (var (filename, expected) in files)
        {
            var path = Path.Combine(artifactRoot, filename);
            if (File.Exists(path))
                Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        }
    }
}
