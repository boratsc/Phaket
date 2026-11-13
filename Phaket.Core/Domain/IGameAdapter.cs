namespace Phaket.Core.Domain;

// Adapter inputs are snapshots; controlled file access and process isolation belong to the host.
public sealed record InstallationFacts(Installation Installation, IReadOnlyList<string> Versions,
    IReadOnlyList<string> Capabilities);
public sealed record PlatformSnapshot(IReadOnlyList<InstallationFacts> Installations);
public sealed record ModdingMethod(string Id, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Limitations);
public sealed record LaunchSpec(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
public sealed record VerificationResult(bool Succeeded, IReadOnlyList<string> Errors);

public interface IGameAdapter
{
    IReadOnlyList<Installation> DetectInstallations(PlatformSnapshot snapshot);
    IReadOnlyList<string> ProbeVersion(InstallationFacts facts);
    IReadOnlyList<ModdingMethod> SupportedMethods(InstallationFacts facts);
    OperationPlan PlanInstall(Profile profile, InstallationFacts facts);
    Task<ExecutionJournal> ApplyPlan(OperationPlan plan, IApprovedOperations operations, CancellationToken cancellationToken);
    Task<VerificationResult> Verify(InstallationFacts facts, Profile profile, IApprovedOperations operations, CancellationToken cancellationToken);
    Task<ExecutionJournal> Rollback(ExecutionJournal journal, IApprovedOperations operations, CancellationToken cancellationToken);
    LaunchSpec BuildLaunchSpec(Profile profile, InstallationFacts facts);
}

// This broker is provided by the host and must reject operations outside the approved plan.
public interface IApprovedOperations
{
    Task ApplyAsync(string opId, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string opId, CancellationToken cancellationToken);
    Task RollbackAsync(string opId, CancellationToken cancellationToken);
}
