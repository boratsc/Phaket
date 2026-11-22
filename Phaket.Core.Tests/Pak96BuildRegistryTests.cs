using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-96: the cached build registry turns an installation's build id into the
// canonical version label, and a full package hands the registry manifest to
// game.provision. No network: the catalog cache and artifacts are local.
public sealed class Pak96BuildRegistryTests
{
    private static readonly GameDefinition AmongUs = GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private const string TouUrl = "https://susmodder-cdn.ovh/mods/tou/1.7.3-x86.zip";

    private static JsonElement Build(string versionLabel, string buildId, string[] manifestIds,
        string[] aliases, string arch = "x86")
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = $"among-us-steam-{versionLabel}", gameId = "among-us", store = "steam", versionLabel, buildId,
            depotIds = new[] { "945361" }, manifestIds, branch = "public",
            availabilityStatus = manifestIds.Length == 0 ? "unknown" : "known",
            source = "susmodder", sourceReference = "api.susmodder-cdn.ovh/v2/versions",
            observedAt = "2026-11-20T00:00:00Z", verifiedAt = (string?)null, confidence = 0.9,
            provenance = "susmodder-versions+steamdb", releasedAt = "2026-08-18T00:00:00Z", arch, aliases,
        }));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void BuildIdAndAliasesResolveToTheCanonicalLabel()
    {
        var dir = Directory.CreateTempSubdirectory("pak96-registry-").FullName;
        var path = Path.Combine(dir, "catalog.db");
        try
        {
            using (var store = new CatalogStore(path))
            {
                store.ReplaceAll(JsonSerializer.SerializeToElement(new[]
                {
                    Build("2026.8.18", "24302054", ["9223372036854775807"], ["2026-8-18"]),
                }), 1, null);

                var builds = store.GetBuilds("among-us");
                var build = Assert.Single(builds);
                Assert.Equal(["2026-8-18"], build.Aliases);

                // buildId -> label, including the source's own spelling of the label.
                Assert.Equal("2026.8.18", GameBuildRegistry.VersionForBuildId(builds, "steam", 24302054));
                Assert.Null(GameBuildRegistry.VersionForBuildId(builds, "steam", 11111111));
                Assert.Null(GameBuildRegistry.VersionForBuildId(builds, "epic", 24302054));

                // The dbValue alias and the padded spelling find the same build.
                Assert.NotNull(GameBuildRegistry.ForLabel(builds, "steam", "2026-8-18"));
                Assert.NotNull(GameBuildRegistry.ForLabel(builds, "steam", "2026.08.18"));
                Assert.NotNull(GameBuildRegistry.ForLabel(builds, "steam", "2026.8.18"));
                Assert.Null(GameBuildRegistry.ForLabel(builds, "steam", "2026.9.29"));
            }

            // Aliases survive a reopen of an already populated cache.
            using (var reopened = new CatalogStore(path))
                Assert.Equal(["2026-8-18"], Assert.Single(reopened.GetBuilds("among-us")).Aliases);
        }
        finally { Directory.Delete(dir, true); }
    }

    // W1: a SusModder row can publish the depot manifest without any build id. The
    // installed depot manifest then resolves the version the build id cannot.
    [Fact]
    public void InstalledDepotManifestResolvesABuildWithoutABuildId()
    {
        var dir = Directory.CreateTempSubdirectory("pak96-manifest-").FullName;
        try
        {
            using var store = new CatalogStore(Path.Combine(dir, "catalog.db"));
            store.ReplaceAll(JsonSerializer.SerializeToElement(new[]
            {
                Build("2025.6.10", "", ["1298083356997541927"], ["2025-6-10"]),
            }), 1, null);
            var builds = store.GetBuilds("among-us");

            Assert.Null(GameBuildRegistry.VersionForBuildId(builds, "steam", 11111111));
            Assert.Equal("2025.6.10", GameBuildRegistry.VersionForManifests(builds, "steam", ["1298083356997541927"]));
            Assert.Null(GameBuildRegistry.VersionForManifests(builds, "epic", ["1298083356997541927"]));
            Assert.Null(GameBuildRegistry.VersionForManifests(builds, "steam", []));
            Assert.Null(GameBuildRegistry.VersionForManifests(builds, "steam", ["unknown"]));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static GeneratedRecipe Full(IReadOnlyDictionary<string, string>? manifests) =>
        RecipeGenerator.GenerateCatalogPackage("Town of Us Mira", "1.7.3", "full", TouUrl, new string('a', 64),
            91_654_060, "x86", "2026-8-18",
            ["BepInEx/plugins/TownOfUsMira.dll", "Among Us_Data/sharedassets0.assets"], AmongUs,
            provisionManifests: manifests);

    private static OperationPlan Plan(Recipe recipe, ExecutorFixture fixture, string gameVersion = "2026.8.18") =>
        OperationPlanner.Plan(recipe, new PlannerContext("install-1", "profile-1", fixture.GameRoot,
            fixture.ProfileRoot, fixture.StagingRoot, fixture.PackageRoot, AmongUs,
            new PlanFacts("linux", "steam", gameVersion, OperationCatalog.Capabilities, _ => false, Arch: "x86")));

    [Fact]
    public void KnownManifestIsPassedToTheProvisionStep()
    {
        var recipe = Full(new Dictionary<string, string> { ["steam"] = "9223372036854775807" }).Recipe;

        Assert.Equal("9223372036854775807", recipe.Steps[0].With["manifestId"].GetString());
        Assert.Equal("2026.8.18", recipe.Steps[0].With["version"].GetString());
        Assert.False(recipe.Steps[1].With.ContainsKey("manifestId")); // epic has no published manifest
        Assert.Empty(RecipeValidator.Validate(recipe, AmongUs));

        using var fixture = new ExecutorFixture();
        var provision = Assert.Single(Plan(recipe, fixture).Operations, op => op.Kind == "game.provision");
        Assert.Equal("9223372036854775807", provision.Parameters!["manifestId"]);
        Assert.Equal("2026.8.18", provision.Parameters!["version"]);
    }

    [Fact]
    public void UnknownManifestKeepsProvisioningByVersion()
    {
        var recipe = Full(null).Recipe;
        Assert.False(recipe.Steps[0].With.ContainsKey("manifestId"));

        using var fixture = new ExecutorFixture();
        var provision = Assert.Single(Plan(recipe, fixture).Operations, op => op.Kind == "game.provision");
        Assert.False(provision.Parameters!.ContainsKey("manifestId"));
    }

    [Fact]
    public void FactLabelInTheSourceSpellingStillMatchesTheCanonicalConstraint()
    {
        using var fixture = new ExecutorFixture();
        Assert.NotEmpty(Plan(Full(null).Recipe, fixture, "2026-8-18").Operations);
    }

    [Fact]
    public void FullPackageRefusesADifferentInstalledVersion()
    {
        using var fixture = new ExecutorFixture();
        var error = Assert.Throws<PhaketException>(() => Plan(Full(null).Recipe, fixture, "2026.9.29"));
        Assert.Equal(ErrorCodes.UnsupportedGameVersion, error.Error.Code);
        Assert.Equal("=2026.8.18", error.Error.Parameters["range"]);
    }
}