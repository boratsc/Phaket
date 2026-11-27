using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;

namespace Phaket.Services;

// PAK-142 host side: a plan with a two-sided file conflict is stopped BEFORE the first
// write when the user has not answered yet (PlanExecutor refuses an undecided plan;
// InstallOrchestrator asks the lifecycle first). This picks the plans that need the user's
// answer, so the UI can ask and then run the same plans through the executor with decisions
// (one answer per conflicted path; unrelated writes still run).
public static class InstallConflicts
{
    public static IReadOnlyList<OperationPlan> RequiringDecision(IEnumerable<OperationPlan> plans) =>
        plans.Where(PlanLifecycle.HasConflict).ToArray();

    public static bool RequiresDecision(IEnumerable<OperationPlan> plans) => plans.Any(PlanLifecycle.HasConflict);

    // One answer for every two-sided conflict of the given plans. Proposals the user was not
    // asked about ("last-wins", "overwrite") stay out: the executor filters only conflicted
    // paths, so an extra key would decide a file the question never showed.
    public static IReadOnlyDictionary<string, ConflictDecision> Decide(IEnumerable<OperationPlan> plans,
        ConflictDecision decision) => plans
        .SelectMany(plan => plan.Conflicts)
        .Where(conflict => conflict.Resolution == PlanLifecycle.ConflictsRequireDecision)
        .Select(conflict => conflict.Path)
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(path => path, _ => decision, StringComparer.Ordinal);
}