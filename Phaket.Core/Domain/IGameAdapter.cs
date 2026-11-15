using Phaket.Core.Omrl;

namespace Phaket.Core.Domain;

// Adapter inputs are snapshots; controlled file access and process isolation belong to the host.
public sealed record InstallationFacts(Installation Installation, IReadOnlyList<string> Versions,
    IReadOnlyList<string> Capabilities, string? Arch = null);
public sealed record PlatformSnapshot(IReadOnlyList<InstallationFacts> Installations);
public sealed record ModdingMethod(string Id, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Limitations);
public sealed record LaunchSpec(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
public sealed record VerificationResult(bool Succeeded, IReadOnlyList<string> Errors);

// The host owns execution: adapters plan and orchestrate, the trusted executor
// applies approved operations. No unrestricted filesystem access for adapters.
public interface IPlanExecutorHost
{
    ExecutionJournal Approve(OperationPlan plan);
    ExecutionJournal ExecuteOperation(OperationPlan plan, ExecutionJournal journal, string opId);
    ExecutionJournal Complete(OperationPlan plan, ExecutionJournal journal);
    ExecutionJournal Rollback(ExecutionJournal journal);
    VerificationResult VerifyPlan(OperationPlan plan);
}

public interface IGameAdapter
{
    IReadOnlyList<Installation> DetectInstallations(PlatformSnapshot snapshot);
    IReadOnlyList<string> ProbeVersion(InstallationFacts facts);
    IReadOnlyList<ModdingMethod> SupportedMethods(InstallationFacts facts);
    OperationPlan PlanInstall(Profile profile, InstallationFacts facts);
    Task<ExecutionJournal> ApplyPlan(OperationPlan plan, IPlanExecutorHost host, CancellationToken cancellationToken);
    Task<VerificationResult> Verify(InstallationFacts facts, Profile profile, IPlanExecutorHost host, CancellationToken cancellationToken);
    Task<ExecutionJournal> Rollback(ExecutionJournal journal, IPlanExecutorHost host, CancellationToken cancellationToken);
    LaunchSpec BuildLaunchSpec(Profile profile, InstallationFacts facts);
}
