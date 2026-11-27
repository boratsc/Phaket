using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// R2c: an artifact's declared arch is enforced for every step that consumes it, not only for
// loader.ensure. A hard mismatch refuses the plan; an unknown installation arch only warns.
public sealed class ArtifactArchPlanTests
{
    // The recipe exactly as shipped, so the fixture's x86 install matches it.
    private static Recipe Recipe() => TestFixtures.AmongUsRecipe();

    // The shipped steps without loader.ensure: isolates archive.extract/file.copy from the
    // loader's own (pre-existing) arch refusal.
    private static Recipe WithoutLoader() => Recipe() with { Steps = [.. Recipe().Steps.Skip(1)] };

    private static Recipe ExtractStep() => Recipe() with { Steps = [Recipe().Steps[1]] };

    // Steps hold JsonElement values; building one needs a document to own the element.
    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Json(Dictionary<string, object> values) =>
        values.ToDictionary(kv => kv.Key, kv => System.Text.Json.JsonSerializer.SerializeToElement(kv.Value));

    private static Recipe CopyStep()
    {
        // The dll is produced by the bundle extract, so the copy's artifact dependency is the
        // same mod-bundle whose arch applies (this is the shipped recipe's shape).
        var steps = Recipe().Steps;
        return Recipe() with { Steps = [steps[1], steps[2]] };
    }

    [Theory]
    [InlineData("x86", "x64")] // artifact built x86, installation x64
    [InlineData("x64", "x86")] // artifact built x64, installation x86
    public void ArchiveExtractRefusesMismatchedArtifactArch(string artifactArch, string installArch)
    {
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = installArch } };
        // The artifact alone decides, even when the consuming step has no when.arch.
        var recipe = ExtractStep() with { Artifacts = [Recipe().Artifacts[0] with { Arch = artifactArch }] };
        var error = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, context));
        Assert.Equal(ErrorCodes.ArchMismatch, error.Error.Code);
        Assert.Equal("mod-bundle", error.Error.Parameters["artifact"]);
        Assert.Equal(artifactArch, error.Error.Parameters["artifactArch"]);
        Assert.Equal(installArch, error.Error.Parameters["arch"]);
        // Readable in both product languages, naming both sides.
        Assert.Contains(artifactArch, error.LocalizedMessage("pl-PL"));
        Assert.Contains(installArch, error.LocalizedMessage("en"));
    }

    [Theory]
    [InlineData("x86", "x64")]
    [InlineData("x64", "x86")]
    public void FileCopyRefusesMismatchedArtifactArch(string artifactArch, string installArch)
    {
        using var fixture = new PlannerFixture();
        var steps = Recipe().Steps;
        // The dll is copied from the bundle's own entry: a plain file, so no earlier extract is
        // needed and no producer-staging lookup hides the artifact check.
        var recipe = Recipe() with
        {
            Artifacts = [Recipe().Artifacts[0] with { Arch = artifactArch }],
            Steps = [steps[2] with { With = Json(new Dictionary<string, object>
                { ["from"] = "package://mod-bundle/CustomRoles.dll", ["to"] = "game://BepInEx/plugins/CustomRoles.dll" }) }],
        };
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = installArch } };
        var error = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, context));
        Assert.Equal(ErrorCodes.ArchMismatch, error.Error.Code);
        Assert.Equal("mod-bundle", error.Error.Parameters["artifact"]);
    }

    [Theory]
    [InlineData("archive.extract")]
    [InlineData("file.copy")]
    public void UnknownInstallationArchPlansWithTheArchUnknownWarning(string use)
    {
        using var fixture = new PlannerFixture();
        var recipe = use == "archive.extract" ? ExtractStep() : CopyStep();
        Directory.CreateDirectory(Path.Combine(fixture.StagingRoot, "custom-roles"));
        File.WriteAllText(Path.Combine(fixture.StagingRoot, "custom-roles", "CustomRoles.dll"), "dll");
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = null } };

        var plan = OperationPlanner.Plan(recipe, context);

        Assert.Contains("ARCH_UNKNOWN", plan.Warnings);
        // The write stays planned: an unknown arch is a warning, not a refusal.
        Assert.Equal(use == "archive.extract" ? "extract-mod" : "install-plugin", plan.Operations[^1].OpId);
    }

    [Fact]
    public void MatchingArchPlansWithoutTheWarning()
    {
        using var fixture = new PlannerFixture();
        var plan = OperationPlanner.Plan(WithoutLoader(), fixture.Context);
        Assert.DoesNotContain("ARCH_UNKNOWN", plan.Warnings);
        Assert.Equal(2, plan.Operations.Count);
    }

    [Fact]
    public void ArtifactWithoutDeclaredArchNeverWarnsOrRefuses()
    {
        using var fixture = new PlannerFixture();
        var recipe = WithoutLoader() with { Artifacts = [Recipe().Artifacts[0] with { Arch = null }] };
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = "x64" } };

        var plan = OperationPlanner.Plan(recipe, context);

        Assert.DoesNotContain("ARCH_UNKNOWN", plan.Warnings);
        Assert.Equal(2, plan.Operations.Count);
    }

    [Fact]
    public void LoaderEnsureKeepsItsOwnRefusalCode()
    {
        // No regression: the loader path still reports LOADER_ARCH_UNAVAILABLE, not ARCH_MISMATCH.
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { Arch = "x64" } };
        var error = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(Recipe(), context));
        Assert.Equal("LOADER_ARCH_UNAVAILABLE", error.Error.Code);
    }
}