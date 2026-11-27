using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-142: a plan built against the agreed baseline asks instead of overwriting.
// File.copy-only recipe, so the comparison never depends on tree writes.
public sealed class Pak142BaselinePlanTests : IDisposable
{
    private const string ModPath = "BepInEx/plugins/Mod.dll";
    private readonly string root;
    private readonly string gameRoot;
    private readonly string packageRoot;

    public Pak142BaselinePlanTests()
    {
        root = Directory.CreateTempSubdirectory("phaket-pak142-").FullName;
        gameRoot = Path.Combine(root, "game");
        packageRoot = Path.Combine(root, "packages");
        foreach (var dir in new[] { Path.Combine(gameRoot, "BepInEx", "plugins"), Path.Combine(root, "profile"),
                     Path.Combine(root, "staging"), packageRoot })
            Directory.CreateDirectory(dir);
    }

    private string ModFile => Path.Combine(gameRoot, ModPath.Replace('/', Path.DirectorySeparatorChar));

    private PlannerContext Context(InstallBaseline? baseline) => new(
        "install-1", "profile-1", gameRoot, Path.Combine(root, "profile"), Path.Combine(root, "staging"), packageRoot,
        TestFixtures.AmongUs,
        new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false,
            new Dictionary<string, string> { ["bepinex"] = "6.0.0" }, Arch: "x86"),
        Baseline: baseline);

    private Recipe Recipe(string content)
    {
        File.WriteAllText(Path.Combine(packageRoot, "mod.dll"), content);
        return new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.recipe",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["file.copy"] },
            Artifacts = [new RecipeArtifact { Id = "mod-bundle", Source = "package://mod.dll" }],
            Steps =
            [
                new RecipeStep
                {
                    Id = "copy",
                    Use = "file.copy",
                    With = new Dictionary<string, JsonElement>
                    {
                        ["from"] = JsonSerializer.SerializeToElement("package://mod-bundle"),
                        ["to"] = JsonSerializer.SerializeToElement("game://" + ModPath),
                    },
                },
            ],
        };
    }

    [Fact]
    public void TwoSidedEditBecomesAConflictButTheWriteStaysForTheUsersAnswer()
    {
        File.WriteAllText(ModFile, "content-v1");
        var baseline = InstallBaseline.Capture("install-1", gameRoot);
        File.WriteAllText(ModFile, "user-edit");

        var plan = OperationPlanner.Plan(Recipe("content-v2"), Context(baseline));

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ModPath, conflict.Path);
        Assert.Equal("requires-user-decision", conflict.Resolution);
        Assert.Contains("local:install-1", conflict.Owners);
        Assert.Contains("remote:test.recipe", conflict.Owners);
        // The write stays: "use the mod's version" needs something to run, and the executor
        // skips or runs it per decided path. Nothing is written by planning.
        Assert.Contains(plan.Operations, o => o.Kind == "file.copy" && o.TargetPath == ModFile);
        Assert.Equal("user-edit", File.ReadAllText(ModFile));
    }

    [Fact]
    public void LocalEditWithAnUnchangedRemoteInstallsWithoutAConflict()
    {
        File.WriteAllText(ModFile, "content-v1");
        var baseline = InstallBaseline.Capture("install-1", gameRoot);
        File.WriteAllText(ModFile, "user-edit");

        var plan = OperationPlanner.Plan(Recipe("content-v1"), Context(baseline));

        Assert.Empty(plan.Conflicts);
        Assert.Contains(plan.Operations, o => o.Kind == "file.copy");
    }

    [Fact]
    public void WithoutABaselineThePlanIsUnchanged()
    {
        File.WriteAllText(ModFile, "content-v1");

        var plan = OperationPlanner.Plan(Recipe("content-v2"), Context(null));

        Assert.Empty(plan.Conflicts);
        Assert.Contains(plan.Operations, o => o.Kind == "file.copy");
    }

    [Fact]
    public void ConflictCodeHasPolishAndEnglishMessages()
    {
        Assert.True(ErrorMessages.TryGetTemplate(ErrorCodes.FileConflictRequiresDecision, "pl-PL", out _));
        Assert.True(ErrorMessages.TryGetTemplate(ErrorCodes.FileConflictRequiresDecision, "en", out _));
        Assert.True(ErrorMessages.TryGetTemplate(ErrorCodes.BaselineInvalid, "pl-PL", out _));
        var message = ErrorMessages.Format(ErrorCodes.FileConflictRequiresDecision, "pl-PL",
            new Dictionary<string, string> { ["path"] = ModPath });
        Assert.Contains(ModPath, message);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}