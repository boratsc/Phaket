using System.Security.Cryptography;
using System.Text;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution.SaveBackup;

// PAK-141: what must happen before a plan may touch the game tree. An unaccepted
// plan writes nothing, and a save backup over the limit becomes a profile record.
public sealed record PlanApproval(bool Accepted, bool SaveBackupIsProfile = false)
{
    public static PlanApproval Accept { get; } = new(true);
    public static PlanApproval Refuse { get; } = new(false);
    public static PlanApproval ProfileOnTooLarge { get; } = new(true, SaveBackupIsProfile: true);
}

// The save backup one transaction captured, and where it lives.
public sealed record SaveBackupRef(SaveBackupManifest Manifest, string Directory);

// The minimal host seam an install loop needs: PlanExecutor provides it through
// InstallOrchestrator, so a save backup still happens when the loop calls
// Approve/ExecuteOperation itself (the ModInstallService path).
public interface IPlanLifecycle
{
    PlanApproval Begin(OperationPlan plan);
    Task FinishAsync(OperationPlan plan, ExecutionJournal journal, CancellationToken ct = default);
}

// Ties the pieces PAK-141/142 already provide into the install lifecycle: a save
// backup before the first write, the user's decision for a file conflict, and the
// next agreed baseline after completion. Kept out of PlanExecutor so the host stays
// the one place that decides what is approved.
public sealed class PlanLifecycle : IPlanLifecycle
{
    // shortcut: no settings seam yet, so 2 GB of saves; move it to settings when
    // BackupView exposes a limit. Over it the copy is skipped for a profile record.
    public const long DefaultSaveBackupLimitBytes = 2L * 1024 * 1024 * 1024;

    public const string ConflictsRequireDecision = "requires-user-decision";

    private readonly PlanExecutionContext context;
    private readonly SaveBackupService backups;

    public PlanLifecycle(PlanExecutionContext context, SaveBackupService? backups = null)
    {
        this.context = context;
        this.backups = backups ?? new SaveBackupService();
    }

    public SaveBackupRef? Last { get; private set; }

    public PlanApproval Begin(OperationPlan plan) =>
        BeginAsync(plan, null, CancellationToken.None).GetAwaiter().GetResult().Approval;

    // Only the requires-user-decision conflicts gate approval; HasConflict is the one check.
    public async Task<(PlanApproval Approval, SaveBackupRef? Backup)> BeginAsync(OperationPlan plan,
        long? limitBytes = null, CancellationToken ct = default, bool waitsForDecision = true)
    {
        // An undecided two-sided edit waits for the user: nothing is copied and nothing is
        // written. A user-approved answer is not a block — the executor applies it per path.
        if (waitsForDecision && HasConflict(plan)) return (PlanApproval.Refuse, null);
        var request = SaveBackupRequest();
        if (request is null) return (PlanApproval.Accept, null);

        var limit = limitBytes is > 0 ? limitBytes.Value : DefaultSaveBackupLimitBytes;
        var root = SaveBackupRoot(context);
        try
        {
            return (PlanApproval.Accept, Last = Store(request, root,
                await backups.CreateBackupAsync(request, root, limit, ct).ConfigureAwait(false)));
        }
        catch (PhaketException e) when (e.Error.Code == ErrorCodes.SaveBackupTooLarge)
        {
            // Never block an install on disk space: keep paths/mtimes, so a restore can
            // remove only files the recipe creates.
            return (PlanApproval.ProfileOnTooLarge, Last = Store(request, root,
                await SaveBackupService.RecordAsync(request, root, limit, ct).ConfigureAwait(false)));
        }
    }

    public Task FinishAsync(OperationPlan plan, ExecutionJournal journal, CancellationToken ct = default)
    {
        if (journal.State != JournalState.Completed) return Task.CompletedTask;
        try
        {
            InstallBaseline.Capture(plan.InstallationRef, context.GameRoot).Save(BaselinePath(context));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PhaketException)
        {
            // The install already succeeded; a missing baseline only means the next
            // plan compares against an empty one, never a failed (or rolled back) install.
            System.Diagnostics.Trace.TraceWarning("phaket: baseline capture failed for {0}: {1}", context.GameRoot, e.Message);
        }
        return Task.CompletedTask;
    }

    public static bool HasConflict(OperationPlan plan) =>
        plan.Conflicts.Any(c => c.Resolution == ConflictsRequireDecision);

    public static string BaselinePath(PlanExecutionContext context) =>
        InstallBaseline.DefaultPath(context.StateRoot ?? context.ProfileRoot);

    public static string SaveBackupRoot(PlanExecutionContext context) =>
        Path.Combine(context.StateRoot ?? context.ProfileRoot, "save-backups");

    private static SaveBackupRef Store(SaveBackupRequest request, string root, SaveBackupManifest manifest) =>
        new(manifest, Path.Combine(root, SaveBackupService.BackupFolderName(request.GameId, request.InstallationId), manifest.Id));

    // Distinct existing save locations; null when the definition has none (behaviour
    // before PAK-141) or none of them exists yet.
    public SaveBackupRequest? SaveBackupRequest()
    {
        var locations = (context.Game.SavePaths ?? [])
            .Select(path => ResolveSavePath(path, context.ProfileRoot))
            .Where(path => path is not null && Directory.Exists(path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(path => path!)
            .ToArray();
        if (locations.Length == 0) return null;
        var title = context.Game.Title?.GetValueOrDefault("en") is { Length: > 0 } english ? english : context.Game.Slug;
        // The state root is per installation and per store root; the folder key adds the
        // profile so two profiles of one installation never share a backup folder.
        var stateKey = context.StateRoot ?? context.ProfileRoot;
        return new SaveBackupRequest(title, $"{context.ProfileId}-{StableKey(stateKey)}", locations);
    }

    // A definition path: "profile://..." under the profile root, otherwise absolute.
    // Anything else is not a path grant and is ignored.
    public static string? ResolveSavePath(string path, string profileRoot)
    {
        if (!VirtualUri.TryParse(path, out var scheme, out var rest))
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        if (scheme != "profile") return null;
        try
        {
            return PathBoundary.Resolve(profileRoot, rest == "." ? "" : rest);
        }
        catch (PhaketException) { return null; }
    }

    private static string StableKey(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
}