using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;

namespace Phaket.Services;

// PAK-142 host side: a plan with a two-sided file conflict is stopped BEFORE the first
// write (PlanExecutor refuses an undecided plan; InstallOrchestrator asks the lifecycle
// first). This picks the plans that need the user's answer, so the UI can ask and then
// run the same plans through the executor with decisions.
public static class InstallConflicts
{
    public static IReadOnlyList<OperationPlan> RequiringDecision(IEnumerable<OperationPlan> plans) =>
        plans.Where(PlanLifecycle.HasConflict).ToArray();

    public static bool RequiresDecision(IEnumerable<OperationPlan> plans) => plans.Any(PlanLifecycle.HasConflict);

    // One answer for every open conflict of the given plans. Every conflict of a plan that
    // has one is answered: the executor looks each up, and a proposal the user was shown
    // together with it ("last-wins", "overwrite") must not become a missing-key throw.
    public static IReadOnlyDictionary<string, ConflictDecision> Decide(IEnumerable<OperationPlan> plans,
        ConflictDecision decision) => plans
        .Where(PlanLifecycle.HasConflict)
        .SelectMany(plan => plan.Conflicts)
        .Select(conflict => conflict.Path)
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(path => path, _ => decision, StringComparer.Ordinal);
}