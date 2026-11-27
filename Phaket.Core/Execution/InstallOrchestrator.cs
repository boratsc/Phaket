using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Mods;
using Phaket.Core.Execution.SaveBackup;

namespace Phaket.Core.Execution;

public sealed record InstallProgress(
    int StepIndex, int StepCount, string OpId, string Kind, string Phase, double Fraction);
public sealed record ReceiptContext(string GameId, string? Store, string? ModFullName);

// Drives a dry-run plan through the transactional executor step by step,
// reporting progress for the install stepper UI (mockup 05). Cancellation
// leaves an interrupted journal that can be resumed or rolled back.
public sealed class InstallOrchestrator
{
    private readonly PlanExecutor executor;
    private readonly ReceiptSender? receipts;
    private readonly IPlanLifecycle? lifecycle;

    public InstallOrchestrator(PlanExecutor executor, ReceiptSender? receipts = null)
    {
        this.executor = executor;
        this.receipts = receipts;
        lifecycle = executor as IPlanLifecycle;
    }

    public ExecutionJournal Run(OperationPlan plan, IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default, ReceiptContext? receiptContext = null,
        IReadOnlyDictionary<string, ConflictDecision>? decisions = null)
    {
        // Hold the installation lock across approval, every operation and
        // completion: a concurrent install/rollback must not interleave.
        using var gate = executor.LockInstallation();
        // PAK-141: the save backup happens before Approve; a plan whose conflicts the user
        // has not answered throws here, before anything is written.
        if (lifecycle is PlanLifecycle full)
            full.BeginAsync(plan, ct: cancellationToken,
                waitsForDecision: decisions is null ? plan.Conflicts.Any(c => c.Resolution == PlanLifecycle.ConflictsRequireDecision) : false)
                .GetAwaiter().GetResult();
        else
            lifecycle?.Begin(plan);
        // The baseline is keyed by the installation, which only the plan carries.
        plan = plan with { InstallationRef = executor.InstallationIdFor(plan) };
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
        catch (Exception ex)
        {
            if (receipts is not null) _ = receipts.SendAsync(receiptContext?.GameId ?? "", receiptContext?.ModFullName ?? plan.RecipeRef.Id, plan.RecipeRef.Version, receiptContext?.Store, "failed", (ex as PhaketException)?.Error.Code ?? ex.GetType().Name, cancellationToken);
            throw;
        }
        // Complete(plan, journal, decisions) also records the new agreed baseline.
        journal = executor.Complete(plan, journal, decisions);
        if (receipts is not null) _ = receipts.SendAsync(receiptContext?.GameId ?? "", receiptContext?.ModFullName ?? plan.RecipeRef.Id, plan.RecipeRef.Version, receiptContext?.Store, "installed", cancellationToken: cancellationToken);
        progress?.Report(new InstallProgress(count, count, "", "verify", "done", 1.0));
        return journal;
    }

    public void Uninstall(string recipeId, DivergencePolicy policy = DivergencePolicy.Fail,
        ReceiptContext? receiptContext = null, CancellationToken cancellationToken = default)
    {
        try
        {
            executor.Uninstall(recipeId, policy);
            if (receipts is not null) _ = receipts.SendAsync(receiptContext?.GameId ?? "", receiptContext?.ModFullName ?? recipeId, "", receiptContext?.Store, "uninstalled", cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            if (receipts is not null) _ = receipts.SendAsync(receiptContext?.GameId ?? "", receiptContext?.ModFullName ?? recipeId, "", receiptContext?.Store, "failed", (ex as PhaketException)?.Error.Code ?? ex.GetType().Name, cancellationToken);
            throw;
        }
    }
}
