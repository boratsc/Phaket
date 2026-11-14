namespace Phaket.Core.Domain;

public sealed record Game(string GameId);
public sealed record GameEdition(string EditionId, string GameId, string Store);
public sealed record Installation(string InstallationId, string EditionId, string RootPath);
public sealed record Profile(string ProfileId, string InstallationId, IReadOnlyList<RecipeRef> Recipes);
public sealed record RecipeRef(string Id, string Version);

public sealed record PlannedOperation(
    string OpId, string Kind, string TargetPath, string? SourceRef, long? SizeBytes,
    string? Sha256, IReadOnlyList<string> Preconditions, string BackupStrategy,
    string Rollback, string OwnershipEffect, IReadOnlyList<string> RequiredPermissions,
    IReadOnlyDictionary<string, string>? Parameters = null, bool Shared = false);
public sealed record PlanConflict(string Path, IReadOnlyList<string> Owners, string Resolution);

public sealed record OperationPlan(
    string SchemaVersion, RecipeRef RecipeRef, string InstallationRef, string ProfileRef,
    IReadOnlyList<PlannedOperation> Operations, IReadOnlyList<PlanConflict> Conflicts,
    IReadOnlyList<string> Warnings, bool RequiresDownload, IReadOnlyList<PlannedOperation> Verification)
{
    public const string CurrentSchemaVersion = "operation-plan/v1";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PlanId => CanonicalJson.Sha256(this);
}

public enum JournalState { Planned, Approved, Executing, Completed, Failed, RolledBack, Interrupted }
public sealed record OperationIntent(string OpId, string Kind, string TargetPath, string? ExpectedSha256, string? Key = null);
public sealed record JournalEntry(OperationIntent Intent, string? BackupRef, bool Applied, bool Verified,
    IReadOnlyList<string>? CreatedFiles = null, Phaket.Core.Execution.LedgerOwner? RemovedOwner = null,
    IReadOnlyList<string>? CreatedDirs = null);
public sealed record ExecutionJournal(string SchemaVersion, string TransactionId, string PlanId,
    JournalState State, IReadOnlyList<JournalEntry> Entries)
{
    public const string CurrentSchemaVersion = "execution-journal/v1";
}
