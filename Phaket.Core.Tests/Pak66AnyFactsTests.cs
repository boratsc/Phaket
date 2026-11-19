using System.Text.Json;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-66 (review of the facts wave): RequireFacts also has to look inside `any`. An alternative
// that could still match once an unknown fact is supplied must be reported as a missing fact,
// while a branch that already matches — or one disproved by the facts we do have — must not be.
public sealed class AnyBranchMissingFactsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("phaket-pak66-any-").FullName;

    private static Recipe Guarded(RecipeCondition when) => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.any-guarded",
        Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
        When = when,
        Requires = new RecipeRequires { Capabilities = ["file.copy"] },
        Artifacts = [new RecipeArtifact { Id = "bundle", Source = "package://bundle.zip" }],
        Steps = [new RecipeStep { Id = "copy", Use = "file.copy", With = new Dictionary<string, JsonElement>
        {
            ["from"] = JsonSerializer.SerializeToElement("package://bundle"),
            ["to"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/Mod.dll"),
        } }],
    };

    private PlannerContext Context(PlanFacts facts)
    {
        var game = Path.Combine(root, "game");
        foreach (var dir in new[] { game, "profile", "staging", "packages" })
            Directory.CreateDirectory(Path.Combine(root, dir));
        File.WriteAllBytes(Path.Combine(root, "packages", "bundle.zip"), []);
        return new PlannerContext("i", "p", game, Path.Combine(root, "profile"),
            Path.Combine(root, "staging"), Path.Combine(root, "packages"),
            TestFixtures.AmongUs, facts);
    }

    private static PlanFacts Facts(string? arch, long? build) => new("linux", "steam", "2026.3.5",
        OperationCatalog.Capabilities, _ => false, Arch: arch, BuildId: build);

    private static RecipeCondition Branch(params RecipeCondition[] conditions) =>
        new() { Any = conditions };

    [Fact]
    public void OnlyPlausibleAnyBranchWithUnknownFactIsRefusedAsMissingFacts()
    {
        // x86 is disproved by the known arch; the x64 branch is still open and needs a build id.
        var when = Branch(new RecipeCondition { Arch = "x86" },
            new RecipeCondition { All = [new RecipeCondition { Arch = "x64" }, new RecipeCondition { BuildId = ">=25134893" }] });
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(Guarded(when), Context(Facts("x64", null))));
        Assert.Equal(ErrorCodes.InstallFactsMissing, ex.Error.Code);
        Assert.Equal("buildId", ex.Error.Parameters["fact"]);
    }

    [Fact]
    public void AnyBranchThatAlreadyMatchesNeedsNoUnknownFact()
    {
        // The first branch matches, so the unknown buildId behind the second one is irrelevant.
        var when = Branch(new RecipeCondition { Arch = "x64" },
            new RecipeCondition { All = [new RecipeCondition { Arch = "x86" }, new RecipeCondition { BuildId = ">=25134893" }] });
        var plan = OperationPlanner.Plan(Guarded(when), Context(Facts("x64", null)));
        Assert.NotEmpty(plan.Operations);
    }

    [Fact]
    public void AnyBranchDisprovedByKnownFactsIsAPlainMismatch()
    {
        // Both branches are decided by the facts we have: no missing fact is claimed, and the
        // recipe simply does not match this installation.
        var when = Branch(new RecipeCondition { Arch = "x86" },
            new RecipeCondition { All = [new RecipeCondition { Arch = "x64" }, new RecipeCondition { BuildId = "<=100" }] });
        var plan = OperationPlanner.Plan(Guarded(when), Context(Facts("x64", 25134893)));
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void AnyBranchWithUnknownArchAndBuildIdReportsTheFirstMissingFact()
    {
        var when = Branch(new RecipeCondition { Arch = "x86" },
            new RecipeCondition { All = [new RecipeCondition { Arch = "x64" }, new RecipeCondition { BuildId = ">=25134893" }] });
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(Guarded(when), Context(Facts(null, null))));
        Assert.Equal(ErrorCodes.InstallFactsMissing, ex.Error.Code);
        Assert.Contains(ex.Error.Parameters["fact"], new[] { "arch", "buildId" });
    }

    [Fact]
    public void AnyStillEvaluatesNormallyWhenFactsAreKnown()
    {
        var when = Branch(new RecipeCondition { Arch = "x86" }, new RecipeCondition { Arch = "x64" });
        Assert.NotEmpty(OperationPlanner.Plan(Guarded(when), Context(Facts("x86", 1))).Operations);
        Assert.NotEmpty(OperationPlanner.Plan(Guarded(when), Context(Facts("x64", 1))).Operations);
    }

    [Fact]
    public void StepConditionAnyFollowsTheSameRule()
    {
        // The refusal also covers a step-level `any`, not only the recipe gate.
        var recipe = Guarded(new RecipeCondition()) with { When = null };
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with { When = Branch(new RecipeCondition { Arch = "x86" },
                new RecipeCondition { All = [new RecipeCondition { Arch = "x64" }, new RecipeCondition { BuildId = ">=25134893" }] }) }],
        };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, Context(Facts("x64", null))));
        Assert.Equal(ErrorCodes.InstallFactsMissing, ex.Error.Code);
    }

    [Fact]
    public void NegatedUnknownArchRequiresTheFact()
    {
        var when = Branch(new RecipeCondition { Not = new RecipeCondition { Arch = "x64" } });
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(Guarded(when), Context(Facts(null, 1))));
        Assert.Equal(ErrorCodes.InstallFactsMissing, ex.Error.Code);
        Assert.Equal("arch", ex.Error.Parameters["fact"]);
    }

    [Fact]
    public void NegatedKnownArchCanMatch()
    {
        var when = Branch(new RecipeCondition { Not = new RecipeCondition { Arch = "x64" } });
        Assert.NotEmpty(OperationPlanner.Plan(Guarded(when), Context(Facts("arm64", 1))).Operations);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
