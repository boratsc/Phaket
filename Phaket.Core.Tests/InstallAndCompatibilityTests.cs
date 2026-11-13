using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public class InstallOrchestratorTests
{
    [Fact]
    public void RunExecutesPlanAndReportsProgress()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var orchestrator = new InstallOrchestrator(executor);

        var progress = new List<InstallProgress>();
        var syncProgress = new SyncProgress(progress);
        var journal = orchestrator.Run(plan, syncProgress);

        Assert.Equal(JournalState.Completed, journal.State);
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "CustomRoles.dll")));
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1].Fraction);
    }

    private sealed class SyncProgress : IProgress<InstallProgress>
    {
        private readonly List<InstallProgress> list;
        public SyncProgress(List<InstallProgress> list) => this.list = list;
        public void Report(InstallProgress value) => list.Add(value);
    }

    [Fact]
    public void CancelLeavesResumableJournal()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var orchestrator = new InstallOrchestrator(executor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => orchestrator.Run(plan, null, cts.Token));
        var journal = PlanExecutor.LoadJournal(fixture.ProfileRoot)!;
        Assert.Equal(JournalState.Interrupted, journal.State);
        // Resume completes.
        var resumed = orchestrator.Run(plan);
        Assert.Equal(JournalState.Completed, resumed.State);
    }
}

public class CompatibilityAnalyzerTests
{
    [Fact]
    public void SeparatesRuntimeFromModCompatibility()
    {
        var report = CompatibilityAnalyzer.Analyze("among-us", null,
            new[] { ("ModA", "1.0", true), ("ModB", "2.0", true) });
        Assert.Null(report.Runtime); // no cache -> no ProtonDB claim
        Assert.Equal(2, report.Mods.Count);
        Assert.True(report.AllModsCompatible);
    }

    [Fact]
    public void UnknownGameHasNoRuntimeData()
    {
        var report = CompatibilityAnalyzer.Analyze("unknown-game", null, []);
        Assert.Null(report.Runtime);
        Assert.True(report.AllModsCompatible);
    }
}
