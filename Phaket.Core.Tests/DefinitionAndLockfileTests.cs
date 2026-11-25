using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Profiles;

namespace Phaket.Core.Tests;

// PAK-19: declarative definitions + identical engine pipeline for both games.
public class DefinitionTests
{
    private static GameDefinition Load(string name) =>
        GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions", "games", name)));

    [Fact]
    public void AmongUsDefinitionLoadsAndValidates()
    {
        var definition = Load("among-us.yaml");
        Assert.Equal("among-us", definition.Slug);
        Assert.Equal("945360", definition.StoreIds!["steam"]);
        Assert.Equal("il2cpp", definition.Loader("bepinex")!.Variant);
        Assert.True(definition.AllowsWrite("BepInEx/plugins/mod.dll"));
        Assert.False(definition.AllowsWrite("Among Us_Data/resources.assets"));
    }

    [Fact]
    public void ValheimDefinitionLoadsAndValidates()
    {
        var definition = Load("valheim.yaml");
        Assert.Equal("valheim", definition.Slug);
        Assert.Equal("892970", definition.StoreIds!["steam"]);
        Assert.Equal("mono", definition.Loader("bepinex")!.Variant);
    }

    // PAK-119: a game's mod sources are data in the definition, not a map in consumer code.
    [Fact]
    public void DefinitionsExposeModSources()
    {
        var valheim = Load("valheim.yaml").Sources!;
        Assert.Equal("valheim", valheim.Thunderstore);
        Assert.Equal(new NexusSource("valheim", 3667), valheim.Nexus);

        var amongUs = Load("among-us.yaml").Sources!;
        Assert.Equal("among-us", amongUs.Thunderstore);
        Assert.Equal(new NexusSource("amongus", 9589), amongUs.Nexus);
    }

    [Fact]
    public void NexusSourceRequiresDomainAndGameId()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "valheim.yaml"))
            .Replace("gameId: 3667\n", "");
        Assert.Throws<PhaketException>(() => GameDefinitionLoader.LoadYaml(yaml));
    }

    [Fact]
    public void UnknownDefinitionFieldIsRejected()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml"));
        Assert.Throws<PhaketException>(() => GameDefinitionLoader.LoadYaml(yaml + "\nextra: 1\n"));
    }

    // Runs the SAME engine pipeline for a game; only the definition differs.
    private static void RunFullFlow(string definitionFile, string slug, string coreDll, string gameExe)
    {
        var definition = Load(definitionFile);
        var root = Directory.CreateTempSubdirectory($"phaket-{slug}-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            var profile = Path.Combine(root, "profile");
            var staging = Path.Combine(root, "staging");
            var packages = Path.Combine(root, "packages");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(packages);
            File.WriteAllText(Path.Combine(game, gameExe), "game-executable");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "original-loader");
            ExecutorFixture.MakeZip(Path.Combine(packages, "loader-bundle.zip"),
                ("winhttp.dll", "new-loader"u8.ToArray()),
                ($"BepInEx/core/{coreDll}", "loader-core"u8.ToArray()));
            ExecutorFixture.MakeZip(Path.Combine(packages, "mod-bundle.zip"),
                ("TestMod.dll", "test-mod"u8.ToArray()));

            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1",
                Id = $"test.{slug}",
                Version = "1.0.0",
                Target = new RecipeTarget { Game = slug, Systems = ["windows", "linux"] },
                Requires = new RecipeRequires
                {
                    Capabilities = ["loader.ensure", "archive.extract", "file.copy", "file.verify"],
                    Loaders = [new LoaderRequirement { Id = "bepinex", Version = ">=5.0" }],
                },
                Artifacts =
                [
                    new RecipeArtifact { Id = "loader-bundle", Source = "package://loader-bundle.zip", Arch = "x86" },
                    new RecipeArtifact { Id = "mod-bundle", Source = "package://mod-bundle.zip" },
                ],
                Steps =
                [
                    new RecipeStep { Id = "ensure-loader", Use = "loader.ensure",
                        With = ExecutorFixture.Params(("id", "bepinex"), ("artifact", "loader-bundle")) },
                    new RecipeStep { Id = "extract", Use = "archive.extract",
                        With = ExecutorFixture.Params(("from", "package://mod-bundle"), ("to", "staging://mod")) },
                    new RecipeStep { Id = "copy", Use = "file.copy",
                        With = ExecutorFixture.Params(("from", "staging://mod/TestMod.dll"), ("to", "game://BepInEx/plugins/TestMod.dll")) },
                ],
                Verify =
                [
                    new RecipeVerifyStep { Use = "file.verify",
                        With = ExecutorFixture.Params(("path", "game://BepInEx/plugins/TestMod.dll")) },
                ],
            };

            var facts = new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false, Arch: "x86");
            var plannerContext = new PlannerContext("install-1", "profile-1",
                game, profile, staging, packages, definition, facts);
            var plan = OperationPlanner.Plan(recipe, plannerContext);
            Assert.Equal(3, plan.Operations.Count);

            var executorContext = new PlanExecutionContext(game, profile, staging, packages,
                definition, recipe.Id, "profile-1", state);
            var executor = new PlanExecutor(executorContext);
            var journal = executor.Execute(plan, executor.Approve(plan));
            Assert.Equal(JournalState.Completed, journal.State);
            Assert.True(executor.VerifyPlan(plan).Succeeded);
            Assert.Equal("test-mod", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "TestMod.dll")));

            executor.Uninstall(recipe.Id);
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "plugins", "TestMod.dll")));
            Assert.Equal("original-loader", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
            Assert.Equal("game-executable", File.ReadAllText(Path.Combine(game, gameExe)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AmongUsFullFixtureFlow() =>
        RunFullFlow("among-us.yaml", "among-us", "BepInEx.Unity.IL2CPP.dll", "Among Us.exe");

    [Fact]
    public void ValheimFullFixtureFlow() =>
        RunFullFlow("valheim.yaml", "valheim", "BepInEx.dll", "valheim.exe");

    [Fact]
    public void LoaderLayoutEntryFilesAreEnforced()
    {
        var definition = Load("among-us.yaml");
        var root = Directory.CreateTempSubdirectory("phaket-layout-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            // Loader zip missing the IL2CPP core entry file.
            ExecutorFixture.MakeZip(Path.Combine(root, "loader-bundle.zip"), ("winhttp.dll", "x"u8.ToArray()));
            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1",
                Id = "test.layout",
                Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
                Requires = new RecipeRequires { Capabilities = ["loader.ensure"] },
                Artifacts = [new RecipeArtifact { Id = "loader-bundle", Source = "package://loader-bundle.zip", Arch = "x86" }],
                Steps = [new RecipeStep { Id = "ensure", Use = "loader.ensure",
                    With = ExecutorFixture.Params(("id", "bepinex"), ("artifact", "loader-bundle")) }],
            };
            var facts = new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false, Arch: "x86");
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game,
                Path.Combine(root, "profile"), Path.Combine(root, "staging"), root, definition, facts));
            var executor = new PlanExecutor(new PlanExecutionContext(game,
                Path.Combine(root, "profile"), Path.Combine(root, "staging"), root, definition, "test.layout", "p",
                Path.Combine(root, "state")));
            var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
            Assert.Equal(ErrorCodes.LoaderUnavailable, ex.Error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AdapterPlansDryAndExecutesThroughHost()
    {
        var definition = Load("among-us.yaml");
        using var fixture = new ExecutorFixture();
        var adapter = new DefinitionGameAdapter(definition, "linux", "steam",
            fixture.GameRoot, fixture.ProfileRoot, fixture.StagingRoot, fixture.PackageRoot,
            _ => fixture.FullRecipe());
        var installation = new Installation("install-1", "among-us-steam", fixture.GameRoot);
        var facts = new InstallationFacts(installation, ["2026.3.5"], [.. OperationCatalog.Capabilities], "x86");
        var profile = new Profile("profile-1", installation.InstallationId, [new RecipeRef("test.recipe", "1.0.0")]);

        var methods = adapter.SupportedMethods(facts);
        Assert.Contains(methods, m => m.Id == "bepinex-il2cpp");

        var before = fixture.TreeSnapshot();
        var plan = adapter.PlanInstall(profile, facts);
        Assert.Equal(before, fixture.TreeSnapshot()); // dry

        var host = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var journal = await adapter.ApplyPlan(plan, host, CancellationToken.None);
        Assert.Equal(JournalState.Completed, journal.State);
        Assert.True((await adapter.Verify(facts, profile, host, CancellationToken.None)).Succeeded);
        Assert.Equal("Among Us.exe", adapter.BuildLaunchSpec(profile, facts).Executable);
    }
}

// PAK-22: immutable manifests + lockfiles, deterministic update/downgrade diff.
public class LockfileTests
{
    private static ProfileManifest Manifest(string version, string artifactHash) => new(
        ProfileManifest.CurrentSchemaVersion, "profile-1", "among-us", "among-us-steam",
        [new RecipeRef("test.recipe", version)],
        [new ManifestArtifact("mod-bundle", "package://mod-bundle.zip", artifactHash, 100)]);

    [Fact]
    public void IdenticalManifestYieldsIdenticalLockfile()
    {
        var a = ProfileLockfileBuilder.Build(Manifest("1.0.0", "aaa"), _ => "recipe-content");
        var b = ProfileLockfileBuilder.Build(Manifest("1.0.0", "aaa"), _ => "recipe-content");
        Assert.Equal(a.LockHash, b.LockHash);
        Assert.Equal(a.ManifestHash, b.ManifestHash);
    }

    [Fact]
    public void RecipeContentChangeChangesLockHash()
    {
        var a = ProfileLockfileBuilder.Build(Manifest("1.0.0", "aaa"), _ => "content-v1");
        var b = ProfileLockfileBuilder.Build(Manifest("1.0.0", "aaa"), _ => "content-v2");
        Assert.NotEqual(a.LockHash, b.LockHash);
    }

    [Fact]
    public void UpgradeDiffIsDeterministic()
    {
        var old = ProfileLockfileBuilder.Build(Manifest("1.0.0", "aaa"), _ => "content-v1");
        var newer = ProfileLockfileBuilder.Build(
            new ProfileManifest(ProfileManifest.CurrentSchemaVersion, "profile-1", "among-us", "among-us-steam",
                [new RecipeRef("test.recipe", "1.1.0"), new RecipeRef("test.extra", "1.0.0")],
                [new ManifestArtifact("mod-bundle", "package://mod-bundle.zip", "bbb", 100)]),
            reference => reference.Id == "test.recipe" ? "content-v2" : "extra-content");
        var diff = ProfileDiffComputer.Compare(old, newer);
        Assert.Single(diff.Added);
        Assert.Equal("test.extra", diff.Added[0].Id);
        Assert.Empty(diff.Removed);
        Assert.Single(diff.Changed);
        Assert.Equal("1.0.0", diff.Changed[0].Old.Version);
        Assert.Equal("1.1.0", diff.Changed[0].New.Version);
        Assert.Equal(["mod-bundle"], diff.ChangedArtifacts);
        // Downgrade is the symmetric inverse.
        var downgrade = ProfileDiffComputer.Compare(newer, old);
        Assert.Single(downgrade.Removed);
        Assert.Equal("test.extra", downgrade.Removed[0].Id);
    }

    [Fact]
    public void LockfileRoundTripsThroughJson()
    {
        var lockfile = ProfileLockfileBuilder.Build(Manifest("1.0.0", "aaa"), _ => "content");
        var json = System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(lockfile));
        var restored = System.Text.Json.JsonSerializer.Deserialize<ProfileLockfile>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
        Assert.Equal(lockfile.LockHash, restored.LockHash);
    }
}
