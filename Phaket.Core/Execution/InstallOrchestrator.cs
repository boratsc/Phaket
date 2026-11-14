using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

public sealed record InstallProgress(
    int StepIndex, int StepCount, string OpId, string Kind, string Phase, double Fraction);

// Drives a dry-run plan through the transactional executor step by step,
// reporting progress for the install stepper UI (mockup 05). Cancellation
// leaves an interrupted journal that can be resumed or rolled back.
public sealed class InstallOrchestrator
{
    private readonly PlanExecutor executor;

    public InstallOrchestrator(PlanExecutor executor) => this.executor = executor;

    public ExecutionJournal Run(OperationPlan plan, IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Hold the installation lock across approval, every operation and
        // completion: a concurrent install/rollback must not interleave.
        using var gate = executor.LockInstallation();
        var journal = executor.Approve(plan);
        var count = plan.Operations.Count;
        try
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = plan.Operations[i];
                progress?.Report(new InstallProgress(i, count, operation.OpId, operation.Kind, "running", (double)i / count));
                journal = executor.ExecuteOperation(plan, journal, operation.OpId);
                progress?.Report(new InstallProgress(i, count, operation.OpId, operation.Kind, "done", (double)(i + 1) / count));
            }
        }
        catch (OperationCanceledException)
        {
            executor.Interrupt(journal);
            throw;
        }
        journal = executor.Complete(plan, journal);
        progress?.Report(new InstallProgress(count, count, "", "verify", "done", 1.0));
        return journal;
    }
}
