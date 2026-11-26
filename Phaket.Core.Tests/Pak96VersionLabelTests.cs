using Phaket.Core.Domain;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-96: game version labels are compared in the server's canonical form
// ("Y.M.D", no leading zeros). A recipe written with the SusModder dbValue
// ("=2026-8-18") must match the planner fact the build registry returns.
public sealed class Pak96VersionLabelTests
{
    private static readonly GameDefinition AmongUs = GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private const string ModUrl = "https://susmodder-cdn.ovh/mods/submerged/1.0.0.zip";

    [Theory]
    [InlineData("2026-8-18", "2026.8.18")]
    [InlineData("2026.8.18", "2026.8.18")]
    [InlineData("2026.08.18", "2026.8.18")]
    [InlineData(" 2026-08-9 ", "2026.8.9")]
    [InlineData("18.0.0", "18.0.0")]
    [InlineData("ToU", "ToU")]
    [InlineData("latest", "latest")]
    public void DateLabelsAreCanonicalAndOtherLabelsStayAsWritten(string input, string expected) =>
        Assert.Equal(expected, GameVersionLabel.Normalize(input));

    [Theory]
    [InlineData("2026-99-99")]
    [InlineData("2026-13-1")]
    [InlineData("2026-2-30")]
    [InlineData("1999-1-1")]
    [InlineData("2026-8-0")]
    public void ImpossibleCalendarDatesAreRejectedNotGuessed(string input)
    {
        Assert.Null(GameVersionLabel.Canonical(input));
        Assert.Null(GameVersionLabel.Normalize(input));
    }

    [Theory]
    [InlineData("=2026-8-18", "=2026.8.18")]
    [InlineData(">=2026.08.18", ">=2026.8.18")]
    [InlineData("*", "*")]
    [InlineData("=18.0.0", "=18.0.0")]
    [InlineData("=2026-99-99", "=2026-99-99")] // rejected later by the range parser, never rewritten
    public void RangeOperatorsKeepTheirMeaning(string input, string expected) =>
        Assert.Equal(expected, GameVersionLabel.NormalizeRange(input));

    [Fact]
    public void DbValueAndCanonicalLabelAreTheSameBuild()
    {
        Assert.True(GameVersionLabel.Matches("2026-8-18", "2026.8.18"));
        Assert.False(GameVersionLabel.Matches("2026-8-18", "2026.9.29"));
    }

    [Fact]
    public void ConditionOnLegacyLabelMatchesCanonicalFact()
    {
        var facts = new PlanFacts("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false);
        Assert.True(ConditionEvaluator.Evaluate(new RecipeCondition { GameVersion = "=2026-8-18" }, facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { GameVersion = "=2026.9.29" }, facts));
    }

    // PAK-98: the validator normalized the constraint but not the `when` condition, so a
    // condition written with the dbValue form failed while the same text in constraints passed.
    [Fact]
    public void LegacyDashedConditionValidatesLikeTheConstraint()
    {
        var generated = Dll("2026-8-18");
        var recipe = generated with
        {
            Target = generated.Target with { Constraints = new TargetConstraints { GameVersion = "=2026-8-18" } },
            When = new RecipeCondition { GameVersion = "=2026-8-18" },
        };

        Assert.DoesNotContain(ErrorCodes.InvalidVersionRange,
            RecipeValidator.Validate(recipe, AmongUs).Select(e => e.Code));

        using var fixture = new ExecutorFixture();
        Assert.NotEmpty(Plan(recipe, "2026.8.18", fixture).Operations);
        // The step condition alone rules the step out — an empty plan, not a version error.
        Assert.Empty(Plan(recipe, "2026.9.29", fixture).Operations);

        // The condition range itself is what must not be rejected: an impossible date in it still is.
        Assert.DoesNotContain(ErrorCodes.InvalidVersionRange,
            RecipeValidator.Validate(recipe with { When = new RecipeCondition { GameVersion = "=2026.9.29" } }, AmongUs)
                .Select(e => e.Code));
        Assert.Contains(ErrorCodes.InvalidVersionRange,
            RecipeValidator.Validate(recipe with { When = new RecipeCondition { GameVersion = "=2026-99-99" } }, AmongUs)
                .Select(e => e.Code));
    }

    private static Recipe Dll(string amongVersion) =>
        RecipeGenerator.GenerateCatalogPackage("Submerged", "1.0.0", "dll", ModUrl, new string('a', 64), 1024,
            "x86", amongVersion, ["BepInEx/plugins/Submerged/Submerged.dll"], AmongUs).Recipe;

    private static OperationPlan Plan(Recipe recipe, string gameVersion, ExecutorFixture fixture) =>
        OperationPlanner.Plan(recipe, new PlannerContext("i", "p", fixture.GameRoot, fixture.ProfileRoot,
            fixture.StagingRoot, fixture.PackageRoot, AmongUs,
            new PlanFacts("linux", "steam", gameVersion, OperationCatalog.Capabilities, _ => false, Arch: "x86")));

    [Fact]
    public void GeneratorWritesTheCanonicalConstraint()
    {
        Assert.Equal("=2026.8.18", Dll("2026-8-18").Target.Constraints?.GameVersion);
        Assert.Equal("=2026.8.18", Dll("2026.08.18").Target.Constraints?.GameVersion);
    }

    [Fact]
    public void LegacyDashedConstraintStillMatchesTheCanonicalFact()
    {
        var generated = Dll("2026-8-18");
        var recipe = generated with
        {
            Target = generated.Target with { Constraints = new TargetConstraints { GameVersion = "=2026-8-18" } },
        };
        using var fixture = new ExecutorFixture();
        Assert.NotEmpty(Plan(recipe, "2026.8.18", fixture).Operations);

        var error = Assert.Throws<PhaketException>(() => Plan(recipe, "2026.9.29", fixture));
        Assert.Equal(ErrorCodes.UnsupportedGameVersion, error.Error.Code);
        Assert.Equal("2026.9.29", error.Error.Parameters["gameVersion"]);
    }

    [Theory]
    [InlineData("2026-99-99")]
    [InlineData("2026-13-1")]
    public void ImpossibleDateInAModIsRefused(string amongVersion)
    {
        var error = Assert.Throws<PhaketException>(() => Dll(amongVersion));
        Assert.Equal(ErrorCodes.InvalidVersion, error.Error.Code);
    }

    // A date-shaped but impossible installation fact used to fall back to the raw text
    // ("2026-99-99") and evaluate as "does not match" instead of refusing.
    [Fact]
    public void ImpossibleDateAsAConditionFactIsRefused()
    {
        var facts = new PlanFacts("linux", "steam", "2026-99-99", OperationCatalog.Capabilities, _ => false);
        var error = Assert.Throws<PhaketException>(() =>
            ConditionEvaluator.Evaluate(new RecipeCondition { GameVersion = "=2026-8-18" }, facts));
        Assert.Equal(ErrorCodes.InvalidVersion, error.Error.Code);
        Assert.Equal("2026-99-99", error.Error.Parameters["version"]);
    }

    // In an `any` branch the other fact already ruled the branch out, so RequireFacts used to
    // skip it and the impossible date silently dropped the step instead of refusing the plan.
    [Fact]
    public void ImpossibleDateInAnAlternativeBranchIsRefusedInsteadOfDroppingTheStep()
    {
        var generated = Dll("2026-8-18");
        var recipe = generated with
        {
            Target = generated.Target with { Constraints = null },
            Steps = [generated.Steps[0] with
            {
                When = new RecipeCondition
                {
                    Any =
                    [
                        new RecipeCondition { Store = "epic" },
                        new RecipeCondition { GameVersion = "=2026.8.18" },
                    ],
                },
            }],
        };
        using var fixture = new ExecutorFixture();

        var error = Assert.Throws<PhaketException>(() => Plan(recipe, "2026-99-99", fixture));

        Assert.Equal(ErrorCodes.InvalidVersion, error.Error.Code);
        Assert.Equal("2026-99-99", error.Error.Parameters["version"]);
    }
}