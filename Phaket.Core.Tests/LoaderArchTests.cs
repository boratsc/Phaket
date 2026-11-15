using System.Text.Json;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class LoaderArchTests
{
    [Fact]
    public void LoaderLayoutSelectsArchitectureAndKeepsLegacyVariants()
    {
        var game = TestFixtures.AmongUs with { LoaderLayouts = [
            new LoaderLayout("bepinex", "x86", []), new LoaderLayout("bepinex", "x64", [])] };
        Assert.Equal("x64", game.Loader("bepinex", "x64")!.Variant);
        Assert.Equal("x86", game.Loader("bepinex")!.Variant);
        Assert.Null(game.Loader("bepinex", "arm64"));
        game = game with { LoaderLayouts = [new LoaderLayout("bepinex", "il2cpp", [])] };
        Assert.Equal("il2cpp", game.Loader("bepinex", "x64")!.Variant);
    }

    private static Recipe RecipeWithArch(string? arch)
    {
        var recipe = TestFixtures.AmongUsRecipe();
        return recipe with { Artifacts = [recipe.Artifacts[0] with { Arch = arch }] };
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("x86", true)]
    [InlineData("x64", true)]
    [InlineData("arm64", false)]
    public void SchemaAndSemanticValidatorAgree(string? arch, bool valid)
    {
        var recipe = RecipeWithArch(arch);
        var errors = RecipeSchema.Validate(JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement);
        Assert.Equal(arch != "arm64", errors.Count == 0);
        Assert.Equal(valid, RecipeValidator.Validate(recipe, TestFixtures.AmongUs).Count == 0);
    }

    [Theory]
    [InlineData("x86", "x86", false)]
    [InlineData("x64", "x64", false)]
    public void PlannerAcceptsMatchingArch(string loaderArch, string? factsArch, bool warning)
    {
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = factsArch } };
        var plan = OperationPlanner.Plan(RecipeWithArch(loaderArch), context);
        Assert.Equal(warning, plan.Warnings.Contains("ARCH_UNKNOWN"));
    }

    [Fact]
    public void PlannerRejectsUnknownArchForMarkedLoader()
    {
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = null } };
        var error = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(RecipeWithArch("x86"), context));
        Assert.Equal("LOADER_ARCH_UNAVAILABLE", error.Error.Code);
    }

    [Fact]
    public void ArchitectureConditionIsValidatedAndEvaluated()
    {
        using var fixture = new PlannerFixture();
        var condition = new RecipeCondition { Arch = "x64" };
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Steps = [recipe.Steps[0] with { When = condition }, .. recipe.Steps.Skip(1)] };
        Assert.Empty(RecipeSchema.Validate(JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement));
        Assert.Empty(RecipeValidator.Validate(recipe, TestFixtures.AmongUs));
        Assert.True(ConditionEvaluator.Evaluate(condition, fixture.Context.Facts with { Arch = "x64" }));
        Assert.False(ConditionEvaluator.Evaluate(condition, fixture.Context.Facts with { Arch = "x86" }));
        Assert.False(ConditionEvaluator.Evaluate(condition, fixture.Context.Facts with { Arch = null }));
    }

    [Fact]
    public void PlannerRejectsMismatchedArch()
    {
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = "x64" } };
        var error = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(RecipeWithArch("x86"), context));
        Assert.Equal("LOADER_ARCH_UNAVAILABLE", error.Error.Code);
    }
}
