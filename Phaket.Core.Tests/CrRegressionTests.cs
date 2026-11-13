using System.Text;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// Regression tests for the independent code review of PAK-9/12/18/15.
public class CrRegressionTests
{
    private static Dictionary<string, JsonElement> Params(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    [Fact]
    public void DifferentConfigValuesProduceDifferentPlanIds()
    {
        using var fixture = new PlannerFixture();
        Recipe Make(string value) => TestFixtures.AmongUsRecipe() with
        {
            Steps =
            [
                .. TestFixtures.AmongUsRecipe().Steps,
                new RecipeStep
                {
                    Id = "set-config",
                    Use = "config.set",
                    With = new Dictionary<string, JsonElement>
                    {
                        ["path"] = JsonSerializer.SerializeToElement("game://BepInEx/config/mod.cfg"),
                        ["format"] = JsonSerializer.SerializeToElement("keyvalue"),
                        ["key"] = JsonSerializer.SerializeToElement("Setting"),
                        ["value"] = JsonSerializer.SerializeToElement(value),
                    }
                }
            ],
            Requires = new RecipeRequires { Capabilities = [.. OperationCatalog.Capabilities] },
        };
        var planA = OperationPlanner.Plan(Make("a"), fixture.Context);
        var planB = OperationPlanner.Plan(Make("b"), fixture.Context);
        Assert.NotEqual(planA.PlanId, planB.PlanId);
    }

    [Fact]
    public void SymlinkInsideGameRootCannotRedirectWritesOutsideWritableAreas()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Symlink creation requires Linux in this suite.");
        using var fixture = new PlannerFixture();
        Directory.CreateDirectory(Path.Combine(fixture.GameRoot, "Data"));
        Directory.CreateSymbolicLink(Path.Combine(fixture.GameRoot, "BepInEx", "redirect"), Path.Combine(fixture.GameRoot, "Data"));
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [.. recipe.Steps.Take(2), recipe.Steps[2] with
            {
                With = Params(("from", "staging://custom-roles/CustomRoles.dll"), ("to", "game://BepInEx/redirect/payload.dll"))
            }]
        };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, fixture.Context));
        Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
    }

    [Fact]
    public void GitHubAllowlistRejectsUnregisteredRepositories()
    {
        SourcePolicy.AllowGitHubRepository("trusted/mod-repo");
        Assert.True(SourcePolicy.IsAllowedHttpsSource("https://github.com/trusted/mod-repo/releases/download/v1/mod.zip"));
        Assert.False(SourcePolicy.IsAllowedHttpsSource("https://github.com/attacker/mod-repo/releases/download/v1/mod.zip"));
        Assert.False(SourcePolicy.IsAllowedHttpsSource("https://github.com/trusted/mod-repo/archive/v1.zip"));
        Assert.False(SourcePolicy.IsAllowedHttpsSource("https://evil.thunderstore.io/package/mod.zip"));
        Assert.True(SourcePolicy.IsAllowedHttpsSource("https://api.thunderstore.io/package/mod.zip"));
    }

    [Fact]
    public void DeclaredArtifactHashMismatchIsRejectedAtPlanning()
    {
        using var fixture = new PlannerFixture();
        File.WriteAllBytes(Path.Combine(fixture.PackageRoot, "custom-roles.zip"), "tampered"u8.ToArray());
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), fixture.Context));
        Assert.Equal(ErrorCodes.ArtifactHashMismatch, ex.Error.Code);
    }

    [Fact]
    public void LoaderEnsureSkippedByConditionDoesNotSatisfyRequirement()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with { When = new RecipeCondition { Os = ["windows"] } }, .. recipe.Steps.Skip(1)]
        };
        var context = fixture.Context with { Facts = fixture.Context.Facts with { LoaderVersions = null } };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, context));
        Assert.Equal(ErrorCodes.LoaderUnavailable, ex.Error.Code);
    }

    [Fact]
    public void InvalidParameterTypeYieldsValidationErrorNotException()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [.. recipe.Steps.Take(2), recipe.Steps[2] with
            {
                With = new Dictionary<string, JsonElement>
                {
                    ["from"] = JsonSerializer.SerializeToElement(123),
                    ["to"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/CustomRoles.dll"),
                }
            }]
        };
        var errors = RecipeValidator.Validate(recipe, TestFixtures.AmongUs);
        Assert.Contains(errors, e => e.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void StagingSourceWithoutProducerIsRejected()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [recipe.Steps[0], recipe.Steps[2]] // extract removed; copy source not produced
        };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, fixture.Context));
        Assert.Equal(ErrorCodes.ArtifactMissing, ex.Error.Code);
    }

    [Fact]
    public void DuplicateJsonKeysAreRejected()
    {
        var json = RecipeParser.ToJson(TestFixtures.AmongUsRecipe());
        var duplicated = json.Replace("\"schemaVersion\":\"omrl/v0alpha1\"",
            "\"schemaVersion\":\"omrl/v0alpha1\",\"schemaVersion\":\"omrl/v0alpha1\"");
        var ex = Assert.Throws<PhaketException>(() => RecipeParser.ParseJson(duplicated));
        Assert.Contains("duplicate", ex.Error.Parameters["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameVersionConditionWithoutFactFailsWithCode()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with { When = new RecipeCondition { GameVersion = ">=2026.1" } }, .. recipe.Steps.Skip(1)]
        };
        var context = fixture.Context with { Facts = fixture.Context.Facts with { GameVersion = null } };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, context));
        Assert.Equal(ErrorCodes.GameBuildUnavailable, ex.Error.Code);
    }

    [Fact]
    public void ExchangeJsonContainsPlanIdWhileHashExcludesIt()
    {
        using var fixture = new PlannerFixture();
        var plan = OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), fixture.Context);
        var exchange = Encoding.UTF8.GetString(CanonicalJson.SerializeWithId(plan));
        Assert.Contains($"\"planId\":\"{plan.PlanId}\"", exchange);
        Assert.DoesNotContain("planId", Encoding.UTF8.GetString(CanonicalJson.Serialize(plan)));
    }
}
