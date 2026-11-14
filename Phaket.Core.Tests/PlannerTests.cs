using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public sealed class PlannerFixture : IDisposable
{
    public string Root { get; }
    public string GameRoot { get; }
    public string ProfileRoot { get; }
    public string StagingRoot { get; }
    public string PackageRoot { get; }
    public PlannerContext Context { get; }

    public PlannerFixture(IReadOnlyList<ResourceOwner>? owners = null)
    {
        Root = Directory.CreateTempSubdirectory("phaket-planner-").FullName;
        GameRoot = Path.Combine(Root, "game");
        ProfileRoot = Path.Combine(Root, "profile");
        StagingRoot = Path.Combine(Root, "staging");
        PackageRoot = Path.Combine(Root, "packages");
        Directory.CreateDirectory(Path.Combine(GameRoot, "BepInEx", "plugins"));
        Directory.CreateDirectory(ProfileRoot);
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(PackageRoot);
        File.WriteAllBytes(Path.Combine(PackageRoot, "custom-roles.zip"), []);
        File.WriteAllText(Path.Combine(GameRoot, "winhttp.dll"), "original-loader");

        var facts = new PlanFacts("linux", "steam", "2026.3.5",
            OperationCatalog.Capabilities, _ => false,
            new Dictionary<string, string> { ["bepinex"] = "6.0.0" });
        Context = new PlannerContext("install-1", "profile-1",
            GameRoot, ProfileRoot, StagingRoot, PackageRoot,
            TestFixtures.AmongUs, facts, owners);
    }

    public string[] TreeSnapshot() =>
        Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? p + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))
                : p + ":dir")
            .ToArray();

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class OperationPlannerTests
{
    [Theory]
    [InlineData("different", true)]
    [InlineData("same", false)]
    public void LoaderPlanReportsOnlyDifferentExistingArchiveFiles(string existing, bool conflictExpected)
    {
        using var fixture = new PlannerFixture();
        var archivePath = Path.Combine(fixture.PackageRoot, "loader.zip");
        File.WriteAllText(Path.Combine(fixture.GameRoot, "doorstop_config.ini"), existing == "same" ? "loader" : "user");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("doorstop_config.ini").Open())) writer.Write("loader");
        var recipe = TestFixtures.AmongUsRecipe() with
        {
            Artifacts = [new RecipeArtifact { Id = "mod-bundle", Source = "package://loader.zip" }]
        };
        var plan = OperationPlanner.Plan(recipe, fixture.Context);
        Assert.Equal(conflictExpected, plan.Conflicts.Any(c => c.Resolution == "overwrite"));
        if (!conflictExpected) Assert.DoesNotContain(plan.Warnings, w => w == "loader-overwrite-unknown");
    }

    [Fact]
    public void DryRunProducesPlanWithoutMutatingFilesystem()
    {
        using var fixture = new PlannerFixture();
        var before = fixture.TreeSnapshot();
        var plan = OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), fixture.Context);
        Assert.Equal(before, fixture.TreeSnapshot());
        Assert.Equal(3, plan.Operations.Count);
        Assert.Single(plan.Verification);
        Assert.False(plan.RequiresDownload);
    }

    [Fact]
    public void IdenticalInputYieldsByteIdenticalPlan()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        var plan1 = OperationPlanner.Plan(recipe, fixture.Context);
        var plan2 = OperationPlanner.Plan(recipe, fixture.Context);
        Assert.Equal(
            Encoding.UTF8.GetString(CanonicalJson.Serialize(plan1)),
            Encoding.UTF8.GetString(CanonicalJson.Serialize(plan2)));
        Assert.Equal(plan1.PlanId, plan2.PlanId);
    }

    [Fact]
    public void PlanCarriesResolvedPathsChecksumsAndRollback()
    {
        using var fixture = new PlannerFixture();
        var plan = OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), fixture.Context);
        var copy = plan.Operations.Single(o => o.Kind == "file.copy");
        Assert.Equal(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "CustomRoles.dll"), copy.TargetPath);
        Assert.Contains("produced-by:extract-mod", copy.Preconditions);
        var extract = plan.Operations.Single(o => o.Kind == "archive.extract");
        Assert.NotNull(extract.Sha256);
        Assert.Equal(64, extract.Sha256!.Length);
        Assert.All(plan.Operations, o => Assert.NotEmpty(o.Rollback));
    }

    [Fact]
    public void SymlinkEscapeIsRejectedBeforeOperation()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Symlink creation requires Linux in this suite.");
        using var fixture = new PlannerFixture();
        var outside = Directory.CreateTempSubdirectory("phaket-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(fixture.GameRoot, "escape"), outside);
            var recipe = TestFixtures.AmongUsRecipe();
            recipe = recipe with
            {
                Steps = [.. recipe.Steps.Take(2), recipe.Steps[2] with
                {
                    With = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["from"] = System.Text.Json.JsonSerializer.SerializeToElement("staging://custom-roles/CustomRoles.dll"),
                        ["to"] = System.Text.Json.JsonSerializer.SerializeToElement("game://escape/payload.dll"),
                    }
                }]
            };
            var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, fixture.Context));
            Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void UnsupportedBuildFailsWithCode()
    {
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { GameVersion = "2025.1" } };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), context));
        Assert.Equal(ErrorCodes.UnsupportedGameVersion, ex.Error.Code);
        Assert.Contains("2025.1", ex.LocalizedMessage("pl-PL"));
        Assert.Contains("2025.1", ex.LocalizedMessage("en"));
    }

    [Fact]
    public void MissingBuildFactFailsWithCode()
    {
        using var fixture = new PlannerFixture();
        var context = fixture.Context with { Facts = fixture.Context.Facts with { GameVersion = null } };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), context));
        Assert.Equal(ErrorCodes.GameBuildUnavailable, ex.Error.Code);
    }

    [Fact]
    public void MissingLoaderWithoutEnsureStepFailsWithCode()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Steps = recipe.Steps.Where(s => s.Use != "loader.ensure").ToArray() };
        var context = fixture.Context with { Facts = fixture.Context.Facts with { LoaderVersions = null } };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(recipe, context));
        Assert.Equal(ErrorCodes.LoaderUnavailable, ex.Error.Code);
    }

    [Fact]
    public void TwoWritesToSameResourceAreReportedAsConflict()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        var duplicate = recipe.Steps[2] with { Id = "install-plugin-copy" };
        recipe = recipe with { Steps = [.. recipe.Steps, duplicate] };
        var plan = OperationPlanner.Plan(recipe, fixture.Context);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("last-wins", conflict.Resolution);
        Assert.Contains("install-plugin", conflict.Owners);
        Assert.Contains("install-plugin-copy", conflict.Owners);
    }

    [Fact]
    public void ExistingOwnerIsReportedForRecomputeOnUninstall()
    {
        var target = "BepInEx/plugins/CustomRoles.dll";
        using var fixture = new PlannerFixture();
        var owners = new[] { new ResourceOwner(target, "other.recipe", "profile-1", 1) };
        var context = fixture.Context with { ExistingOwners = owners };
        var plan = OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), context);
        Assert.Contains(plan.Conflicts, c => c.Resolution == "recompute-from-remaining-owners"
            && c.Owners.Contains("other.recipe"));
    }

    [Fact]
    public void SameLayerOwnersAreUnresolvable()
    {
        var target = "BepInEx/plugins/CustomRoles.dll";
        using var fixture = new PlannerFixture();
        var owners = new[]
        {
            new ResourceOwner(target, "recipe.a", "profile-1", 1),
            new ResourceOwner(target, "recipe.b", "profile-1", 1),
        };
        var context = fixture.Context with { ExistingOwners = owners };
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), context));
        Assert.Equal(ErrorCodes.ConflictUnresolvable, ex.Error.Code);
    }

    [Fact]
    public void WhenConditionSkipsStepDeterministically()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with { When = new RecipeCondition { Os = ["windows"] } }, .. recipe.Steps.Skip(1)]
        };
        var plan = OperationPlanner.Plan(recipe, fixture.Context);
        Assert.Equal(2, plan.Operations.Count);
        Assert.DoesNotContain(plan.Operations, o => o.OpId == "ensure-loader");
    }

    [Fact]
    public void HttpsArtifactMarksPlanAsRequiringDownload()
    {
        SourcePolicy.AllowGitHubRepository("org/repo");
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Artifacts = [new RecipeArtifact
            {
                Id = "mod-bundle",
                Source = "https://github.com/org/repo/releases/download/v1/mod.zip",
                Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            }]
        };
        var plan = OperationPlanner.Plan(recipe, fixture.Context);
        Assert.True(plan.RequiresDownload);
    }

    [Fact]
    public void MissingPackageFileFailsWithCode()
    {
        using var fixture = new PlannerFixture();
        File.Delete(Path.Combine(fixture.PackageRoot, "custom-roles.zip"));
        var ex = Assert.Throws<PhaketException>(() => OperationPlanner.Plan(TestFixtures.AmongUsRecipe(), fixture.Context));
        Assert.Equal(ErrorCodes.ArtifactMissing, ex.Error.Code);
    }
}

// PAK-15: locale independence and metadata fallback.
public class LocalizationPlanTests
{
    [Fact]
    public void SameRecipeInPlAndEnYieldsIdenticalPlan()
    {
        using var fixture = new PlannerFixture();
        var baseRecipe = TestFixtures.AmongUsRecipe();
        var pl = baseRecipe with
        {
            Metadata = new RecipeMetadata
            {
                Title = new Dictionary<string, string> { ["pl-PL"] = "Role niestandardowe" },
                Description = new Dictionary<string, string> { ["pl-PL"] = "Opis po polsku." },
            }
        };
        var en = baseRecipe with
        {
            Metadata = new RecipeMetadata
            {
                Title = new Dictionary<string, string> { ["en"] = "Custom Roles" },
                Description = new Dictionary<string, string> { ["en"] = "English description." },
            }
        };
        var planPl = OperationPlanner.Plan(pl, fixture.Context);
        var planEn = OperationPlanner.Plan(en, fixture.Context);
        Assert.Equal(planPl.PlanId, planEn.PlanId);
        Assert.Equal(
            Encoding.UTF8.GetString(CanonicalJson.Serialize(planPl)),
            Encoding.UTF8.GetString(CanonicalJson.Serialize(planEn)));
    }

    [Fact]
    public void MissingTranslationNeverBlocksPlanning()
    {
        using var fixture = new PlannerFixture();
        var recipe = TestFixtures.AmongUsRecipe() with { Metadata = null };
        var plan = OperationPlanner.Plan(recipe, fixture.Context);
        Assert.Equal(3, plan.Operations.Count);
    }

    [Fact]
    public void MetadataFallbackIsDeterministic()
    {
        var both = new Dictionary<string, string> { ["pl-PL"] = "PL", ["en"] = "EN" };
        Assert.Equal("PL", LocalizedText.Resolve(both, "pl-PL"));
        Assert.Equal("EN", LocalizedText.Resolve(both, "en"));
        Assert.Equal("EN", LocalizedText.Resolve(both, "de-DE"));
        var plOnly = new Dictionary<string, string> { ["pl-PL"] = "PL" };
        Assert.Equal("PL", LocalizedText.Resolve(plOnly, "de-DE"));
        var otherOnly = new Dictionary<string, string> { ["fr-FR"] = "FR", ["de-DE"] = "DE" };
        Assert.Equal("DE", LocalizedText.Resolve(otherOnly, "es-ES"));
        Assert.Null(LocalizedText.Resolve(null, "pl-PL"));
        Assert.Null(LocalizedText.Resolve(new Dictionary<string, string>(), "pl-PL"));
    }
}
