using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-142 integration: the baseline is loaded by the adapter, written after Complete,
// refreshed after uninstall, and a two-sided edit refuses Execute until the user decides.
public sealed class IntegrationBaselineTests : IDisposable
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ModPath = "BepInEx/plugins/Mod.dll";
    private const string RecipeId = "test.recipe";
    private readonly string root;
    private readonly string gameRoot;
    private readonly string profileRoot;
    private readonly string stagingRoot;
    private readonly string packageRoot;
    private readonly string stateRoot;

    public IntegrationBaselineTests()
    {
        root = Directory.CreateTempSubdirectory("phaket-int-baseline-").FullName;
        gameRoot = Path.Combine(root, "game");
        profileRoot = Path.Combine(root, "profile");
        stagingRoot = Path.Combine(root, "staging");
        packageRoot = Path.Combine(root, "packages");
        stateRoot = Path.Combine(root, "state");
        foreach (var dir in new[] { Path.Combine(gameRoot, "BepInEx", "plugins"), profileRoot, stagingRoot, packageRoot, stateRoot })
            Directory.CreateDirectory(dir);
    }

    private string ModFile => Path.Combine(gameRoot, ModPath.Replace('/', Path.DirectorySeparatorChar));
    private string BaselinePath => InstallBaseline.DefaultPath(stateRoot);

    private Recipe Recipe(string content, string overwrite = "replace")
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
                        ["overwrite"] = JsonSerializer.SerializeToElement(overwrite),
                    },
                },
            ],
        };
    }

    private PlanExecutionContext Context(GameDefinition definition) =>
        new(gameRoot, profileRoot, stagingRoot, packageRoot, definition, RecipeId, "profile-1", stateRoot,
            InstallationId: "install-1");

    private PlannerContext PlannerContext(InstallBaseline? baseline) => new(
        "install-1", "profile-1", gameRoot, profileRoot, stagingRoot, packageRoot,
        TestFixtures.AmongUs,
        new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false,
            new Dictionary<string, string> { ["bepinex"] = "6.0.0" }, Arch: "x86"),
        Baseline: baseline);

    [Fact]
    public void ExecuteWritesTheBaselineAfterComplete()
    {
        var recipe = Recipe("content-v1");
        var context = Context(TestFixtures.AmongUs);
        var executor = new PlanExecutor(context);
        var journal = executor.Execute(OperationPlanner.Plan(recipe, PlannerContext(null)), executor.Approve(OperationPlanner.Plan(recipe, PlannerContext(null))), Ct);

        Assert.Equal(JournalState.Completed, journal.State);
        Assert.True(File.Exists(BaselinePath));
        var baseline = InstallBaseline.Load(BaselinePath, "install-1");
        Assert.True(baseline.Files.ContainsKey(ModPath));
        Assert.Equal(InstallBaseline.LocalState(ModFile)!.Sha256, baseline.Files[ModPath].Sha256);
    }

    [Fact]
    public void OrchestratorWritesTheBaselineAndSecondPlanSeesIt()
    {
        var first = OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(null));
        new InstallOrchestrator(new PlanExecutor(Context(TestFixtures.AmongUs))).Run(first, cancellationToken: Ct);
        Assert.True(File.Exists(BaselinePath));
        var baseline = InstallBaseline.Load(BaselinePath, "install-1");
        Assert.True(baseline.Files.ContainsKey(ModPath));
        Assert.Equal(InstallBaseline.LocalState(ModFile)!.Sha256, baseline.Files[ModPath].Sha256);

        // A local edit and the same recipe version: no conflict, a normal write.
        File.WriteAllText(ModFile, "user-edit");
        var adapter = new DefinitionGameAdapter(TestFixtures.AmongUs, "linux", "steam",
            gameRoot, profileRoot, stagingRoot, packageRoot, _ => Recipe("content-v1"), stateRoot);
        var plan = adapter.PlanInstall(Profile(), Facts());
        Assert.Empty(plan.Conflicts);
        Assert.Contains(plan.Operations, o => o.Kind == "file.copy");
    }

    [Fact]
    public void TwoSidedConflictRefusesExecuteUntilARecordedDecision()
    {
        var first = OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(null));
        new InstallOrchestrator(new PlanExecutor(Context(TestFixtures.AmongUs))).Run(first, cancellationToken: Ct);

        // User edits the file and the new recipe version changes it too.
        File.WriteAllText(ModFile, "user-edit");
        var adapter = new DefinitionGameAdapter(TestFixtures.AmongUs, "linux", "steam",
            gameRoot, profileRoot, stagingRoot, packageRoot, _ => Recipe("content-v2", overwrite: "fail"), stateRoot);
        var planned = adapter.PlanInstall(Profile(), Facts());
        var conflict = Assert.Single(planned.Conflicts);
        Assert.Equal("requires-user-decision", conflict.Resolution);
        Assert.DoesNotContain(planned.Operations, o => o.Kind is "file.copy" or "file.remove" or "file.verify");
        // Approve refuses an empty plan, so the host cannot start a conflict at all; the
        // executor gate is checked on a runnable no-op plan carrying the same conflict.
        var conflictPlan = planned with { Operations = [new PlannedOperation("op1", "directory.ensure", Path.Combine(gameRoot, "BepInEx"), null, null, null, [], "none", "none", "none", ["write"])] };

        // Refused before the first write: journal stays untouched, the user file is intact.
        var executor = new PlanExecutor(Context(TestFixtures.AmongUs));
        var journal = new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion, "t1", conflictPlan.PlanId,
            JournalState.Approved, []);
        var error = Assert.Throws<PhaketException>(() => executor.Execute(conflictPlan, journal, Ct));
        Assert.Equal(ErrorCodes.FileConflictRequiresDecision, error.Error.Code);
        Assert.Equal("user-edit", File.ReadAllText(ModFile));

        // The user keeps their version: nothing is written, everything completes.
        var kept = executor.Execute(conflictPlan, journal, Ct, new Dictionary<string, ConflictDecision>
        {
            [ModPath] = ConflictDecision.KeepLocal,
        });
        Assert.Equal(JournalState.Completed, kept.State);
        Assert.Equal("user-edit", File.ReadAllText(ModFile));
        Assert.DoesNotContain(kept.Entries, e => e.Intent.Kind == "file.copy" && e.Applied);
    }

    [Fact]
    public void TakeRecipeDecisionWritesTheRecipeVersion()
    {
        new InstallOrchestrator(new PlanExecutor(Context(TestFixtures.AmongUs)))
            .Run(OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(null)), cancellationToken: Ct);
        // The user edited the file after the agreed state: the plan would overwrite it.
        File.WriteAllText(ModFile, "user-edit");
        var plan = OperationPlanner.Plan(Recipe("content-v2"), PlannerContext(null));
        Assert.Contains(plan.Operations, o => o.Kind == "file.copy");

        var executor = new PlanExecutor(Context(TestFixtures.AmongUs));
        var journal = executor.Execute(plan, executor.Approve(plan), Ct);

        Assert.Equal(JournalState.Completed, journal.State);
        Assert.Equal("content-v2", File.ReadAllText(ModFile));
        // The new agreed state is recorded after the write.
        var updated = InstallBaseline.Load(BaselinePath, "install-1");
        Assert.Equal(InstallBaseline.LocalState(ModFile)!.Sha256, updated.Files[ModPath].Sha256);
    }

    [Fact]
    public void UninstallRefreshesTheBaseline()
    {
        var context = Context(TestFixtures.AmongUs);
        var executor = new PlanExecutor(context);
        var recipe = Recipe("content-v1");
        var plan = OperationPlanner.Plan(recipe, PlannerContext(null));
        executor.Execute(plan, executor.Approve(plan), Ct);
        Assert.True(InstallBaseline.Load(BaselinePath, "install-1").Files.ContainsKey(ModPath));

        // Uninstall takes the RECIPE id; the one that owns the file is the planner's, not
        // the context's profile id.
        executor.Uninstall(RecipeId);

        Assert.False(File.Exists(ModFile));
        // The refreshed baseline no longer describes a file the uninstall removed.
        Assert.DoesNotContain(ModPath, InstallBaseline.Load(BaselinePath, "install-1").Files.Keys);
    }

    [Fact]
    public void AdapterWithoutStateRootKeepsThePlanUnchanged()
    {
        File.WriteAllText(ModFile, "content-v1");
        var adapter = new DefinitionGameAdapter(TestFixtures.AmongUs, "linux", "steam",
            gameRoot, profileRoot, stagingRoot, packageRoot, _ => Recipe("content-v2"));
        var plan = adapter.PlanInstall(Profile(), Facts());
        Assert.Empty(plan.Conflicts);
        Assert.Contains(plan.Operations, o => o.Kind == "file.copy");
    }

    [Fact]
    public void CaptureSkipsUnreadableFilesInsteadOfFailing()
    {
        File.WriteAllText(ModFile, "content-v1");
        File.WriteAllText(Path.Combine(gameRoot, "winhttp.dll"), "loader");
        var baseline = InstallBaseline.Capture("install-1", gameRoot);
        Assert.Equal(2, baseline.Files.Count);
        Assert.True(InstallBaseline.SameEntries(baseline,
            InstallBaseline.Create("install-1", baseline.Files)));
    }

    private Profile Profile() => new("profile-1", "install-1", [new RecipeRef("test.recipe", "1.0.0")]);

    private InstallationFacts Facts() => new(
        new Installation("install-1", "among-us-steam", gameRoot),
        ["2026.3.5"], OperationCatalog.Capabilities.ToArray(), "x86");

    public void Dispose() => Directory.Delete(root, recursive: true);
}