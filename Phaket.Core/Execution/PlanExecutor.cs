using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// StateRoot is INSTALLATION-level (shared by all profiles modifying one game):
// the ownership ledger and content backups live there. The journal is per
// profile transaction. Hosts must pass a durable StateRoot per installation.
public sealed record PlanExecutionContext(
    string GameRoot, string ProfileRoot, string StagingRoot, string PackageRoot,
    GameDefinition Game, string RecipeId, string ProfileId, string? StateRoot = null,
    IBaseGameSource? BaseGameSource = null,
    // PAK-100: root of the ONE vanilla cache shared by every package of a game+version;
    // null falls back to LocalApplicationData (VanillaCache.DefaultRoot).
    string? VanillaCacheRoot = null,
    // PAK-142: the installation this plan belongs to, used to key the baseline. Null falls
    // back to ProfileId, which is a profile, not an installation: pass the real one.
    string? InstallationId = null);

public enum DivergencePolicy { Fail, Restore, Skip }

// PAK-141/142: the user's answer for one two-sided file conflict. KeepLocal leaves
// the file as the user has it (the recipe's write for that path is skipped);
// TakeRecipe accepts the recipe's version. Until every conflict in a plan is decided,
// Execute refuses before the first write.
public enum ConflictDecision { KeepLocal, TakeRecipe }

// Transactional executor (spec §7, ADR-014): base state and intent persisted
// BEFORE mutations, atomic writes, ownership-based rollback, crash recovery
// with divergence detection. No ACID promise across the filesystem.
public sealed class PlanExecutor : IPlanExecutorHost, IPlanLifecycle
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly PlanExecutionContext context;
    private readonly string stateRoot;
    private readonly string journalPath;
    private readonly string ledgerPath;
    private readonly string backupRoot;
    private readonly Action<string>? faultHook;
    private PlanLifecycle? lifecycle;

    public PlanExecutor(PlanExecutionContext context, Action<string>? faultHook = null)
    {
        this.context = context;
        this.faultHook = faultHook;
        stateRoot = context.StateRoot ?? context.ProfileRoot;
        journalPath = Path.Combine(context.ProfileRoot, "journal.json");
        ledgerPath = Path.Combine(stateRoot, "ledger.json");
        backupRoot = Path.Combine(stateRoot, "backups");
    }

    // The installation a plan belongs to. The host knows it best (context), then the plan itself;
    // the profile id is only the last resort for a context built before PAK-142.
    public string InstallationIdFor(OperationPlan plan) =>
        context.InstallationId ?? (plan.InstallationRef == context.ProfileId ? context.ProfileId : plan.InstallationRef);

    // Save backup before the first write. InstallOrchestrator asks through IPlanLifecycle;
    // Execute asks the same instance directly, so a host that skips Approve is covered too.
    PlanApproval IPlanLifecycle.Begin(OperationPlan plan) => Lifecycle.Begin(plan);

    // The orchestrator's entry: the save backup still runs, and the user's answer (null when the
    // dialog was cancelled) says whether an undecided conflict may block the transaction.
    internal PlanApproval BeginWithAnswer(OperationPlan plan, IReadOnlyDictionary<string, ConflictDecision>? decisions,
        CancellationToken ct) =>
        Lifecycle.BeginAsync(plan, ct: ct, waitsForDecision: decisions is null).GetAwaiter().GetResult().Approval;

    public static ExecutionJournal? LoadJournal(string profileRoot)
    {
        var path = Path.Combine(profileRoot, "journal.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ExecutionJournal>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    public ExecutionJournal Approve(OperationPlan plan)
    {
        if (plan.Operations.Count == 0)
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "Cannot execute an empty plan."));
        var journal = new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"), plan.PlanId, JournalState.Approved, []);
        Persist(journal);
        return journal;
    }

    public ExecutionJournal Execute(OperationPlan plan, ExecutionJournal journal, CancellationToken ct = default,
        IReadOnlyDictionary<string, ConflictDecision>? decisions = null)
    {
        if (journal.State is not (JournalState.Approved or JournalState.Interrupted or JournalState.Failed or JournalState.Executing))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"journal state {journal.State} cannot execute"));
        using var gate = LockInstallation();
        var executionStarted = journal.State == JournalState.Executing;
        try
        {
            // Save backup before the FIRST mutation; a journal write is not a game-tree change,
            // so a refused plan leaves the journal Approved. The conflict gate is ResolveConflicts.
            if (journal.State != JournalState.Executing) Lifecycle.Begin(plan);
            var operations = ResolveConflicts(plan, journal, decisions);
            // A journal with entries is a resume: run every remaining operation. ExecuteOperation
            // skips only verified entries, so Complete cannot claim a write it never made.
            journal = journal with { State = JournalState.Executing };
            Persist(journal);
            executionStarted = true;
            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();
                journal = ExecuteOperation(plan, journal, operation.OpId);
            }
            // The plan's own verification steps carry the recipe's hash: a file the user kept
            // would fail it, so those steps are checked for existence only.
            return Complete(PlanWithKeptVerification(plan, decisions), journal, decisions);
        }
        catch (OperationCanceledException)
        {
            // Reload the latest persisted journal: ExecuteOperation may have
            // recorded the current operation's intent/backup before failing.
            if (executionStarted)
            {
                var latest = LoadJournal(context.ProfileRoot) ?? journal;
                Persist(latest with { State = JournalState.Interrupted });
            }
            throw;
        }
        catch
        {
            if (executionStarted)
            {
                var latest = LoadJournal(context.ProfileRoot) ?? journal;
                Persist(latest with { State = JournalState.Failed });
            }
            throw;
        }
    }

    public ExecutionJournal Complete(OperationPlan plan, ExecutionJournal journal)
    {
        foreach (var verify in plan.Verification)
            VerifyOperation(verify);
        journal = journal with { State = JournalState.Completed };
        Persist(journal);
        return journal;
    }

    // Completes the transaction and records the new agreed state (baseline). The
    // baseline is the executor's own post-condition, not a host concern, so an install
    // driven step by step gets it too. A conflict left undecided writes no baseline.
    public ExecutionJournal Complete(OperationPlan plan, ExecutionJournal journal,
        IReadOnlyDictionary<string, ConflictDecision>? decisions)
    {
        var completed = Complete(plan, journal);
        if (PlanLifecycle.HasConflict(plan)
            && plan.Conflicts.Any(c => !(decisions?.ContainsKey(c.Path) ?? false))) return completed;
        SaveBaseline(plan.InstallationRef);
        return completed;
    }

    // After an uninstall, or a rollback that returned the tree to its base state, the
    // agreed state is the tree as it is now; nothing is written when the game is gone.
    // Also the orchestrator's post-step: it verifies only executed operations and then records
    // the same baseline a one-shot Execute would (see Complete).
    internal void SaveBaselineAfter(OperationPlan plan) => SaveBaseline(plan.InstallationRef);

    private void SaveBaseline(string? planInstallationId = null)
    {
        if (!Directory.Exists(context.GameRoot)) return;
        var installationId = planInstallationId ?? context.InstallationId ?? context.ProfileId;
        try
        {
            InstallBaseline.Capture(installationId, context.GameRoot).Save(PlanLifecycle.BaselinePath(context));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PhaketException)
        {
            System.Diagnostics.Trace.TraceWarning("phaket: baseline capture failed for {0}: {1}", context.GameRoot, e.Message);
        }
    }

    // Every requires-user-decision conflict needs an answer before the first write. The plan
    // is filtered PER PATH: a "keep local" file skips the writes touching it (and only those),
    // a "take recipe" file runs them; writes of unrelated files always run. Internal so a
    // host that drives operations itself (InstallOrchestrator) applies the same answer.
    internal IReadOnlyList<PlannedOperation> ResolveConflicts(OperationPlan plan, ExecutionJournal journal,
        IReadOnlyDictionary<string, ConflictDecision>? decisions)
    {
        if (!PlanLifecycle.HasConflict(plan)) return plan.Operations;
        var open = plan.Conflicts
            .Where(c => c.Resolution == PlanLifecycle.ConflictsRequireDecision
                && decisions?.ContainsKey(c.Path) != true)
            .Select(c => c.Path).ToArray();
        if (open.Length > 0)
            throw new PhaketException(ErrorCodes.FileConflictRequiresDecision, ("path", open[0]));

        // A resumed transaction already wrote part of the tree; its per-path answer is not in
        // the journal, so a path whose write this transaction already made and then ran cannot
        // be re-answered ("both sides edited" answers "use the mod's version"), and a file the
        // user kept has no write to reverse: that contradiction is refused before anything runs.
        if (journal.Entries.Count > 0)
        {
            foreach (var conflict in plan.Conflicts.Where(c => c.Resolution == PlanLifecycle.ConflictsRequireDecision))
            {
                if (decisions!.TryGetValue(conflict.Path, out var answer) && answer == ConflictDecision.TakeRecipe
                    && WrittenBy(plan, journal, conflict.Path))
                    throw new PhaketException(ErrorCodes.FileConflictRequiresDecision, ("path", conflict.Path));
            }
        }

        // Resume included: a kept file skips only the writes touching it. Reporting an empty
        // list here let a resume Complete with the rest unwritten (R3d #3). A plan written
        // before the resume (its first run) legitimately carries no journal yet.
        var kept = KeptPaths(plan, decisions);
        // A plan written before the resume (its first run) has an empty journal: nothing to check.
        var nothingSkipped = plan.Operations.Where(o => !TouchesKept(o, kept)).ToArray();
        return nothingSkipped;
    }

    // True when this journal says the transaction's write of the path succeeded.
    private bool WrittenBy(OperationPlan plan, ExecutionJournal journal, string path)
    {
        var full = Path.GetFullPath(Path.Combine(context.GameRoot, path));
        var opIds = plan.Operations.Where(o => Matches(o)).Select(o => o.OpId).ToHashSet(StringComparer.Ordinal);
        return journal.Entries.Any(entry => entry.Applied
            && string.Equals(Path.GetFullPath(entry.Intent.TargetPath), full, StringComparison.Ordinal)
            || entry.Applied && entry.Intent.Kind is "archive.extract" or "loader.ensure" or "game.provision"
                && opIds.Contains(entry.Intent.OpId)
                && (entry.CreatedFiles ?? []).Any(file => string.Equals(Path.GetFullPath(file), full, StringComparison.Ordinal)));

        bool Matches(PlannedOperation operation) =>
            string.Equals(BaselineComparison.RelativeToGame(operation.TargetPath, context.GameRoot), path, StringComparison.Ordinal)
            || Omrl.BaselineSync.OperationTouches(operation, [path], context.GameRoot,
                operation.Kind is "archive.extract" or "loader.ensure" ? PlanArchiveWrites(operation) : null);
    }

    // The paths a "keep mine" answer protects: the user's bytes must survive every write.
    private static IReadOnlyList<string> KeptPaths(OperationPlan plan,
        IReadOnlyDictionary<string, ConflictDecision>? decisions) =>
        plan.Conflicts.Where(c => c.Resolution == PlanLifecycle.ConflictsRequireDecision
                && decisions!.TryGetValue(c.Path, out var answer) && answer == ConflictDecision.KeepLocal)
            .Select(c => c.Path).ToArray();

    private bool TouchesKept(PlannedOperation operation, IReadOnlyList<string> kept) =>
        Omrl.BaselineSync.OperationTouches(operation, kept, context.GameRoot,
            operation.Kind is "archive.extract" or "loader.ensure" ? PlanArchiveWrites(operation) : null);

    // The verification a plan really leaves: a kept file cannot carry the recipe's hash as its
    // post-condition — the user's file is the answer, so only its existence is checked.
    internal OperationPlan PlanWithKeptVerification(OperationPlan plan,
        IReadOnlyDictionary<string, ConflictDecision>? decisions)
    {
        var kept = KeptPaths(plan, decisions);
        return kept.Count == 0 ? plan : plan with
        {
            Verification = [.. plan.Verification.Select(step =>
                Omrl.BaselineSync.OperationTouches(step, kept, context.GameRoot, plannedFiles: null)
                    ? step with { Sha256 = null } : step)],
        };
    }

    public ExecutionJournal Interrupt(ExecutionJournal journal)
    {
        journal = journal with { State = JournalState.Interrupted };
        Persist(journal);
        return journal;
    }

    // Executes a single approved operation; the host broker drives this per op.
    public ExecutionJournal ExecuteOperation(OperationPlan plan, ExecutionJournal journal, string opId)
    {
        if (journal.State is not (JournalState.Approved or JournalState.Interrupted or JournalState.Failed or JournalState.Executing))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"journal state {journal.State} cannot execute"));
        var operation = plan.Operations.FirstOrDefault(o => o.OpId == opId)
            // A kept file's step is still verified by the plan (its hash is the recipe's), so a
            // "keep mine" answer must not leave a failed verification in the journal.
            ?? plan.Verification.FirstOrDefault(v => v.OpId == opId)
            ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"plan has no operation {opId}"));
        var entries = journal.Entries.ToList();
        var existing = entries.FirstOrDefault(e => e.Intent.OpId == opId);
        if (existing is { Verified: true }) return journal; // resume: idempotent skip

        var ledger = OwnershipLedger.Load(ledgerPath);
        journal = journal with { State = JournalState.Executing };
        try
        {
            // The vanilla cache must exist BEFORE the intent is journaled: the
            // journal then carries the exact write list a rollback can undo.
            if (operation.Kind == "game.provision" && existing is null)
                EnsureProvisionCache(operation.Parameters ?? new Dictionary<string, string>());
            var intent = new OperationIntent(operation.OpId, operation.Kind, operation.TargetPath,
                operation.Sha256, ProvisionLedgerKey(operation));
            var entry = existing ?? new JournalEntry(intent, null, false, false);

            // For extracts, journal the full planned write list BEFORE any write
            // so a partial failure is still recoverable. Created directories are
            // journaled too: they are new game-tree state and must be cleaned up.
            if (operation.Kind is "file.copy" or "archive.extract" or "loader.ensure" or "game.provision" or "directory.ensure" && existing is null)
                entry = entry with
                {
                    CreatedFiles = operation.Kind switch
                    {
                        "game.provision" => PlanProvisionWrites(operation),
                        "archive.extract" or "loader.ensure" => PlanArchiveWrites(operation),
                        _ => null,
                    },
                    CreatedDirs = operation.Kind switch
                    {
                        "game.provision" => PlanProvisionDirs(operation),
                        "archive.extract" or "loader.ensure" => PlanArchiveDirs(operation),
                        "file.copy" => PlanMissingParentDirs(operation.TargetPath),
                        "directory.ensure" => PlanMissingDirectory(operation.TargetPath),
                        _ => null,
                    },
                };

            if (existing is null) entries.Add(entry);
            Persist(journal with { Entries = entries.ToArray() });

            var (backupRef, created, removedOwner) = Apply(operation, ledger);
            entry = entry with { BackupRef = backupRef, Applied = true, CreatedFiles = created ?? entry.CreatedFiles, RemovedOwner = removedOwner };
            ledger.RecordCreatedDirs(context.RecipeId, entry.CreatedDirs);
            entries[entries.FindIndex(e => e.Intent.OpId == opId)] = entry;
            Persist(journal with { Entries = entries.ToArray() });

            VerifyApplied(operation);
            entry = entry with { Verified = true };
            entries[entries.FindIndex(e => e.Intent.OpId == opId)] = entry;
            ledger.Save(ledgerPath);
            journal = journal with { Entries = entries.ToArray() };
            Persist(journal);
            return journal;
        }
        catch
        {
            ledger.Save(ledgerPath);
            Persist(journal with { State = JournalState.Failed, Entries = entries.ToArray() });
            throw;
        }
    }

    public VerificationResult VerifyPlan(OperationPlan plan)
    {
        var errors = new List<string>();
        foreach (var verify in plan.Verification)
        {
            try { VerifyOperation(verify); }
            catch (PhaketException ex) { errors.Add($"{ex.Error.Code}:{verify.TargetPath}"); }
        }
        return new VerificationResult(errors.Count == 0, errors);
    }

    ExecutionJournal IPlanExecutorHost.Rollback(ExecutionJournal journal) => Rollback(journal);

    // InstallOrchestrator asks here; Execute calls the same instance directly.
    Task IPlanLifecycle.FinishAsync(OperationPlan plan, ExecutionJournal journal, CancellationToken ct) =>
        Lifecycle.FinishAsync(plan, journal, ct);

    private PlanLifecycle Lifecycle => lifecycle ??= new PlanLifecycle(context);

    public ExecutionJournal Rollback(ExecutionJournal journal, DivergencePolicy policy = DivergencePolicy.Fail)
    {
        if (journal.State == JournalState.RolledBack) return journal;
        using var gate = LockInstallation();
        var ledger = OwnershipLedger.Load(ledgerPath);
        foreach (var entry in journal.Entries.Reverse())
        {
            var target = entry.Intent.TargetPath;
            var expected = entry.Intent.ExpectedSha256
                ?? (entry.Intent.Kind is "file.copy" or "archive.extract" or "loader.ensure"
                    ? ledger.Get(Relative(target))?.CurrentHash : null);
            if (entry.Applied && expected is not null && File.Exists(target)
                && HashFile(target) != expected && policy == DivergencePolicy.Fail)
                throw new PhaketException(ErrorCodes.JournalDivergence, ("path", target));
            if (policy == DivergencePolicy.Skip && File.Exists(target)
                && expected is not null && HashFile(target) != expected)
                continue;

            switch (entry.Intent.Kind)
            {
                case "directory.ensure":
                    // Only remove directories this transaction actually created.
                    if (entry.CreatedFiles is { } createdDirs && createdDirs.Contains(target)
                        && Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                        Directory.Delete(target);
                    break;
                case "file.copy":
                    if (entry.Applied) RestoreResource(Relative(target), ledger);
                    break;
                case "archive.extract":
                case "loader.ensure":
                case "game.provision":
                    // Covers partial extracts: planned writes are journaled up front.
                    foreach (var file in entry.CreatedFiles ?? [])
                        RestoreResource(Relative(file), ledger);
                    // Remove directories this transaction created, deepest first,
                    // only when empty (never touches pre-existing or user content).
                    foreach (var dir in (entry.CreatedDirs ?? []).Reverse())
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    break;
                case "config.set":
                    if (entry.Applied) RestoreResource(Relative(target) + "::" + entry.Intent.Key, ledger);
                    break;
                case "file.remove":
                    if (!entry.Applied) break;
                    if (entry.BackupRef is { } backup && File.Exists(backup))
                        AtomicCopy(backup, target);
                    if (entry.RemovedOwner is { } owner)
                        AddOwner(Relative(target), owner, ledger, File.Exists(target) ? HashFile(target) : null);
                    break;
                case "profile.activate":
                    if (!entry.Applied) break;
                    if (entry.BackupRef is { } profileBackup && File.Exists(profileBackup))
                        AtomicCopy(profileBackup, target);
                    else if (File.Exists(target))
                        File.Delete(target);
                    break;
            }
            ledger.Save(ledgerPath);
        }
        journal = journal with { State = JournalState.RolledBack };
        Persist(journal);
        // Rollback returned the tree to its base state: that is the next agreed state.
        SaveBaseline();
        return journal;
    }

    // PAK-26: uninstall recomputes shared resources from remaining owners.
    // Divergent user edits are detected and require an explicit policy.
    public void Uninstall(string recipeId, DivergencePolicy policy = DivergencePolicy.Fail)
    {
        using var gate = LockInstallation();
        var ledger = OwnershipLedger.Load(ledgerPath);
        var affected = ledger.ForRecipe(recipeId).Select(e => e.Resource).ToArray();
        foreach (var resource in affected)
        {
            var entry = ledger.Get(resource)!;
            if (!resource.Contains("::", StringComparison.Ordinal))
            {
                var path = ResourcePath(resource);
                if (!entry.Shared && entry.CurrentHash is { } expected && File.Exists(path)
                    && HashFile(path) != expected && policy == DivergencePolicy.Fail)
                    throw new PhaketException(ErrorCodes.JournalDivergence, ("path", path));
                if (!entry.Shared && policy == DivergencePolicy.Skip && File.Exists(path)
                    && entry.CurrentHash is { } expected2 && HashFile(path) != expected2)
                    continue;
            }
            else if (policy == DivergencePolicy.Fail && entry.CurrentHash is { } cfgExpected)
            {
                // Config keys: detect user edits to the whole file before recompute.
                var cfgPath = ResourcePath(resource.Split("::", 2)[0]);
                if (File.Exists(cfgPath) && HashFile(cfgPath) != cfgExpected)
                    throw new PhaketException(ErrorCodes.JournalDivergence, ("path", cfgPath));
            }
            var remaining = entry.Owners.Where(o => o.RecipeId != recipeId).ToArray();
            ledger.Set(entry with { Owners = remaining });
            Recompute(resource, ledger);
            ledger.Save(ledgerPath);
            if (ledger.Get(resource) is { Owners.Count: 0 } empty && (empty.Shared || !empty.BaseExisted && empty.BaseValue is null))
            {
                ledger.Remove(resource);
                ledger.Save(ledgerPath);
            }
        }
        // Directories this recipe created are removed when empty (deepest first);
        // anything the user put inside survives.
        foreach (var dir in ledger.TakeCreatedDirs(recipeId).OrderByDescending(d => d.Length))
            if (IsWithinAllowedInstallArea(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        ledger.RemoveCreatedDirs(recipeId);
        ledger.Save(ledgerPath);
        // Owner content is removed only AFTER the ledger no longer references it.
        var ownerBackupDir = Path.Combine(backupRoot, "owners", recipeId);
        if (Directory.Exists(ownerBackupDir)) Directory.Delete(ownerBackupDir, recursive: true);
        // The next agreed state is the tree as uninstall left it.
        SaveBaseline();
        Persist(new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"), $"uninstall:{recipeId}", JournalState.Completed, []));
    }

    private bool IsWithinAllowedInstallArea(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (PathBoundary.IsWithin(Path.GetFullPath(context.GameRoot), full))
            return context.Game.AllowsWrite(realRelative(full));
        return PathBoundary.IsWithin(Path.GetFullPath(context.ProfileRoot), full);
    }

    private IReadOnlyList<string> PlanArchiveWrites(PlannedOperation operation)
    {
        var source = operation.Kind == "loader.ensure"
            ? (operation.SourceRef is { Length: > 0 } r ? ResolveSource(r) : ResolvePackageArtifact(operation.Parameters?["artifact"] ?? ""))
            : ResolveSource(operation.SourceRef!);
        var targetRoot = operation.Kind == "loader.ensure" ? context.GameRoot : operation.TargetPath;
        var strip = operation.Parameters?.TryGetValue("stripComponents", out var s) == true ? int.Parse(s) : 0;
        return SafeArchive.PlanWrites(source, targetRoot, operation.SourceRef ?? "loader", strip, IncludePatterns(operation));
    }

    private IReadOnlyList<string>? PlanArchiveDirs(PlannedOperation operation)
    {
        // A missing staging source is produced by an earlier extract: no local
        // archive to inspect, so no directories can be planned from it.
        string source;
        try
        {
            source = operation.Kind == "loader.ensure"
                ? (operation.SourceRef is { Length: > 0 } r ? ResolveSource(r) : ResolvePackageArtifact(operation.Parameters?["artifact"] ?? ""))
                : ResolveSource(operation.SourceRef!);
        }
        catch (PhaketException) { return null; }
        if (!File.Exists(source)) return null;
        var targetRoot = operation.Kind == "loader.ensure" ? context.GameRoot : operation.TargetPath;
        var strip = operation.Parameters?.TryGetValue("stripComponents", out var s) == true ? int.Parse(s) : 0;
        return SafeArchive.PlanDirectories(source, targetRoot, operation.SourceRef ?? "loader", strip, IncludePatterns(operation));
    }

    private static IReadOnlyList<string>? IncludePatterns(PlannedOperation operation)
    {
        if (operation.Parameters?.TryGetValue("include", out var raw) != true || raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw);
        }
        catch (JsonException)
        {
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "archive.extract include must be a list of path globs"));
        }
    }

    private IReadOnlyList<string> PlanMissingParentDirs(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return parent is null ? [] : PlanMissingDirectory(parent);
    }

    private static IReadOnlyList<string> PlanMissingDirectory(string path)
    {
        var missing = new List<string>();
        for (var current = Path.GetFullPath(path); !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
        {
            missing.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
        }
        missing.Reverse();
        return missing;
    }

    // game.provision: the shared vanilla cache directory for this game+store+version.
    private string ProvisionCacheDir(PlannedOperation operation)
    {
        var parameters = operation.Parameters ?? new Dictionary<string, string>();
        var store = parameters.GetValueOrDefault("store") ?? "";
        var version = parameters.GetValueOrDefault("version") ?? "";
        return VanillaCache.DirectoryFor(CacheRoot, context.Game.Slug, store, version);
    }

    // PAK-100: without an explicit root the cache is per user, not per mod package.
    private string CacheRoot => string.IsNullOrWhiteSpace(context.VanillaCacheRoot)
        ? VanillaCache.DefaultRoot : context.VanillaCacheRoot;

    // The key under which the provisioned entry is journaled. Leaving the cache means dropping
    // the vanilla:// source of every game.provision entry — never the vanilla files themselves
    // (a rollback must never delete the user's base game).
    private string ProvisionLedgerKey(PlannedOperation operation) =>
        operation.Kind == "game.provision" ? "" : operation.SourceRef ?? "";

    // Fetches the exact pinned vanilla into the shared per-version cache when missing.
    // Sources are official tooling only (DepotDownloader / legendary) via the host
    // runner — never a private mirror, never the user's Steam library.
    private void EnsureProvisionCache(IReadOnlyDictionary<string, string> parameters)
    {
        var store = parameters.GetValueOrDefault("store")
            ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "game.provision missing store"));
        var version = parameters.GetValueOrDefault("version")
            ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "game.provision missing version"));
        var manifestId = parameters.GetValueOrDefault("manifestId");
        var cacheDir = VanillaCache.DirectoryFor(CacheRoot, context.Game.Slug, store, version);
        // Two parallel installs of the same game+version fetch once; the second waits and
        // then finds the promoted cache instead of downloading its own copy.
        using var gate = VanillaCache.Lock(VanillaCache.LockPath(CacheRoot, context.Game.Slug, store, version));
        if (HasCachedVanilla(cacheDir, store, version, manifestId)) return;
        if (context.BaseGameSource is null)
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", $"vanilla {store} {version}"));
        // The tool fills a staging directory; only a completed fetch is promoted into the
        // cache, so a failed or killed download never leaves a tree another install trusts.
        var staging = VanillaCache.StagingDirectoryFor(CacheRoot, context.Game.Slug, store, version);
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            // The runner downloads the tool and may ask the user for Steam credentials, so it
            // is async; the executor itself is synchronous and hosts run it off the UI thread
            // (ModInstallService executes inside Task.Run), which keeps the dialog responsive.
            var source = context.BaseGameSource;
            var prepared = Task.Run(() => source.TryPrepareAsync(store, version, manifestId, staging), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!prepared.Success)
                throw new PhaketException(prepared.ErrorCode ?? ErrorCodes.ArtifactMissing,
                    (prepared.Parameters ?? new Dictionary<string, string>()).Select(p => (p.Key, p.Value)).ToArray());
            VanillaCache.WriteMarker(staging, new VanillaCacheMarker(store, version, manifestId,
                "host-runner", DateTimeOffset.UtcNow));
            VanillaCache.Promote(staging, cacheDir);
        }
        finally
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        }
    }

    // Exact store+version (and manifest when pinned) — the marker check is unchanged by PAK-100,
    // plus the content hash recorded at fetch time.
    private static bool HasCachedVanilla(string cacheDir, string store, string version, string? manifestId)
    {
        if (!System.IO.Directory.Exists(cacheDir)) return false;
        if (VanillaCache.ReadMarker(cacheDir) is not { } marker
            || marker.Store != store || marker.Version != version || (manifestId is not null && marker.ManifestId != manifestId))
            return false;
        return VanillaCache.ReadTreeHash(cacheDir) is { } recorded && recorded == VanillaCache.ComputeTreeHash(cacheDir);
    }

    private IReadOnlyList<string>? PlanProvisionWrites(PlannedOperation operation)
    {
        var cacheDir = ProvisionCacheDir(operation);
        if (!Directory.Exists(cacheDir)) return null; // fetched at execute time
        return EnumerateVanilla(cacheDir);
    }

    private IReadOnlyList<string>? PlanProvisionDirs(PlannedOperation operation)
    {
        var cacheDir = ProvisionCacheDir(operation);
        if (!Directory.Exists(cacheDir)) return null;
        return NewDirectoriesUnder(cacheDir, context.GameRoot);
    }

    // Copies cached vanilla into the game root, capturing base state per file.
    // The cache marker and hash sidecar are excluded: they are Phaket metadata, not game content.
    private IReadOnlyList<string> CopyVanilla(string cacheDir, OwnershipLedger ledger)
    {
        var created = new List<string>();
        foreach (var file in EnumerateVanilla(cacheDir))
        {
            var relative = file[(Path.GetFullPath(cacheDir).Length + 1)..].Replace('\\', '/');
            var target = Path.Combine(context.GameRoot, relative);
            CaptureBase(relative, target, ledger);
            // Base-game files are intentionally outside writableAreas (recipes must
            // not write them); the boundary here is the per-version vanilla cache,
            // which EnumerateVanilla never leaves. No RevalidateWrite on purpose.
            // PAK-146: reflink where the filesystem supports it, full copy otherwise.
            PackageMaterializer.CopyOne(file, target);
            created.Add(target);
        }
        ledger.Save(ledgerPath);
        return created;
    }

    private static IReadOnlyList<string> EnumerateVanilla(string cacheDir) =>
        Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not (VanillaCache.MarkerFileName or VanillaCache.HashFileName))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> NewDirectoriesUnder(string sourceDir, string targetRoot)
    {
        var sourceFull = Path.GetFullPath(sourceDir);
        var targetFull = Path.GetFullPath(targetRoot);
        return Directory.GetDirectories(sourceFull, "*", SearchOption.AllDirectories)
            .Select(d => Path.Combine(targetFull, d[(sourceFull.Length + 1)..].Replace('\\', '/')))
            .Where(d => !Directory.Exists(d))
            .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal)
            .ToArray();
    }

    private (string? BackupRef, IReadOnlyList<string>? Created, LedgerOwner? Removed) Apply(
        PlannedOperation operation, OwnershipLedger ledger)
    {
        var target = operation.TargetPath;
        var parameters = operation.Parameters ?? new Dictionary<string, string>();
        switch (operation.Kind)
        {
            case "directory.ensure":
                if (Directory.Exists(target)) return (null, null, null);
                Directory.CreateDirectory(target);
                return (null, new[] { target }, null);

            case "file.copy":
            {
                var source = ResolveSource(operation.SourceRef!);
                if (operation.Sha256 is { } expected && HashFile(source) != expected)
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", operation.SourceRef!));
                var overwrite = parameters.GetValueOrDefault("overwrite", "fail");
                if (File.Exists(target) && overwrite != "replace" && HashFile(target) != HashFile(source))
                    throw new PhaketException(ErrorCodes.ValidationFailed,
                        ("detail", $"target exists and overwrite policy is '{overwrite}': {target}"));
                var relative = Relative(target);
                var backup = CaptureBase(relative, target, ledger);
                RevalidateWrite(target);
                faultHook?.Invoke("before-file-write");
                if (!File.Exists(target) || HashFile(target) != HashFile(source))
                    AtomicCopy(source, target);
                faultHook?.Invoke("after-file-write");
                AddOwner(relative, NewOwner(operation.OpId, HashFile(target)), ledger, HashFile(target));
                if (operation.Shared) ledger.Set(ledger.Get(relative)! with { Shared = true });
                StoreOwnerContent(relative, target);
                ledger.Save(ledgerPath);
                return (backup, null, null);
            }

            case "archive.extract":
            {
                var source = ResolveSource(operation.SourceRef!);
                if (operation.Sha256 is { } expected && HashFile(source) != expected)
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", operation.SourceRef!));
                var strip = parameters.TryGetValue("stripComponents", out var s) ? int.Parse(s) : 0;
                var created = SafeArchive.Extract(source, target, operation.SourceRef!, strip, destination =>
                {
                    var relative = Relative(destination);
                    if (PathBoundary.IsWithin(Path.GetFullPath(context.GameRoot), Path.GetFullPath(destination))
                        && !context.Game.AllowsWrite(realRelative(destination)))
                        throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", relative));
                    CaptureBase(relative, destination, ledger);
                    ledger.Save(ledgerPath);
                }, IncludePatterns(operation));
                foreach (var file in created)
                {
                    var relative = Relative(file);
                    AddOwner(relative, NewOwner(operation.OpId, HashFile(file)), ledger, HashFile(file));
                    StoreOwnerContent(relative, file);
                }
                ledger.Save(ledgerPath);
                return (null, created, null);
            }

            case "loader.ensure":
            {
                var artifactId = parameters.GetValueOrDefault("artifact")
                    ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "loader.ensure missing artifact"));
                var source = operation.SourceRef is { Length: > 0 } srcRef
                    ? ResolveSource(srcRef)
                    : ResolvePackageArtifact(artifactId);
                if (operation.Sha256 is { } expected && HashFile(source) != expected)
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", artifactId));
                // Real loader packages (e.g. Thunderstore BepInExPack_AmongUs.zip) wrap
                // their contents in a single folder; strip it so entry files land at the
                // game root, matching the official manual-install instructions.
                var strip = parameters.TryGetValue("stripComponents", out var sc) ? int.Parse(sc) : 0;
                var created = SafeArchive.Extract(source, context.GameRoot, artifactId, strip, destination =>
                {
                    var relative = realRelative(destination);
                    if (!context.Game.AllowsWrite(relative))
                        throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", relative));
                    CaptureBase(Relative(destination), destination, ledger);
                    ledger.Save(ledgerPath);
                });
                foreach (var file in created)
                {
                    var relative = Relative(file);
                    AddOwner(relative, NewOwner(operation.OpId, HashFile(file)), ledger, HashFile(file));
                    StoreOwnerContent(relative, file);
                }
                var layout = context.Game.Loader(parameters.GetValueOrDefault("id") ?? "");
                if (layout is not null)
                    foreach (var entryFile in layout.EntryFiles)
                        if (!File.Exists(Path.Combine(context.GameRoot, entryFile)))
                            throw new PhaketException(ErrorCodes.LoaderUnavailable,
                                ("loader", layout.Id), ("range", "entry-files-missing"));
                ledger.Save(ledgerPath);
                return (null, created, null);
            }

            case "config.set":
            {
                var key = parameters["key"];
                var value = parameters["value"];
                var format = parameters.GetValueOrDefault("format", "keyvalue");
                var resource = Relative(target) + "::" + key;
                var (baseValue, keyExisted) = ReadConfigValue(target, key, format);
                if (ledger.Get(resource) is null)
                    ledger.Set(new LedgerEntry(resource, null, null, baseValue, keyExisted, [], null) { Format = format });
                ledger.Save(ledgerPath);
                var entry = ledger.Get(resource)!;
                var layer = entry.Owners.Count == 0 ? 1 : entry.Owners.Max(o => o.Layer) + 1;
                var updated = entry with { Owners = [.. entry.Owners.Where(o => o.RecipeId != context.RecipeId), NewOwner(operation.OpId, null, value) with { Layer = layer }] };
                ledger.Set(updated);
                RevalidateWrite(target);
                faultHook?.Invoke("before-config-write");
                WriteConfigValue(target, key, value, format);
                ledger.Set(ledger.Get(resource)! with { CurrentHash = File.Exists(target) ? HashFile(target) : null });
                ledger.Save(ledgerPath);
                return (null, null, null);
            }

            case "file.remove":
            {
                var relative = Relative(target);
                var entry = ledger.Get(relative);
                var owner = entry?.Owners.FirstOrDefault(o => o.RecipeId == context.RecipeId);
                if (owner is null)
                    throw new PhaketException(ErrorCodes.OwnershipViolation, ("path", target));
                var backup = Path.Combine(backupRoot, "removed", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup);
                ledger.Set(entry! with { Owners = entry!.Owners.Where(o => o != owner).ToArray() });
                ledger.Save(ledgerPath);
                File.Delete(target);
                Recompute(relative, ledger);
                ledger.Save(ledgerPath);
                return (backup, null, owner);
            }

            case "profile.activate":
            {
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = Path.Combine(backupRoot, "profile", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, JsonSerializer.Serialize(new { profileId = parameters["profile"] }, JsonOptions));
                return (backup, null, null);
            }

            case "game.provision":
            {
                EnsureProvisionCache(parameters);
                var created = CopyVanilla(ProvisionCacheDir(operation), ledger);
                return (null, created, null);
            }

            case "file.verify":
                VerifyOperation(operation);
                return (null, null, null);

            default:
                throw new PhaketException(ErrorCodes.UnknownOperation, ("operation", operation.Kind));
        }
    }

    private void VerifyApplied(PlannedOperation operation)
    {
        if (operation.Kind is "file.copy" or "config.set" or "profile.activate" && !File.Exists(operation.TargetPath))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"target missing after apply: {operation.TargetPath}"));
    }

    private void VerifyOperation(PlannedOperation operation)
    {
        if (!File.Exists(operation.TargetPath))
            throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", operation.TargetPath));
        if (operation.Sha256 is { } expected && HashFile(operation.TargetPath) != expected)
            throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", operation.TargetPath));
    }

    private void RestoreResource(string resource, OwnershipLedger ledger)
    {
        var entry = ledger.Get(resource);
        if (entry is null) return;
        ledger.Set(entry with { Owners = entry.Owners.Where(o => o.RecipeId != context.RecipeId).ToArray() });
        Recompute(resource, ledger);
    }

    // Recomputes the target state from base + remaining owners (last layer wins).
    private void Recompute(string resource, OwnershipLedger ledger)
    {
        var entry = ledger.Get(resource);
        if (entry is null) return;
        var remaining = entry.Owners.OrderBy(o => o.Layer).ToArray();

        if (entry.Shared && !resource.Contains("::", StringComparison.Ordinal))
        {
            var sharedPath = ResourcePath(resource);
            ledger.Set(entry with { CurrentHash = File.Exists(sharedPath) ? HashFile(sharedPath) : null });
            return;
        }

        if (resource.Contains("::", StringComparison.Ordinal))
        {
            var split = resource.Split("::", 2);
            var path = ResourcePath(split[0]);
            var format = entry.Format ?? (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "keyvalue");
            if (remaining.Length > 0)
                WriteConfigValue(path, split[1], remaining[^1].Value ?? "", format);
            else if (entry.BaseExisted)
                WriteConfigValue(path, split[1], entry.BaseValue ?? "", format);
            else
                RemoveConfigKey(path, split[1], format);
            ledger.Set(entry with { CurrentHash = File.Exists(path) ? HashFile(path) : null });
            return;
        }

        var target = ResourcePath(resource);
        if (remaining.Length > 0)
        {
            var ownerContent = OwnerContentPath(remaining[^1].RecipeId, resource);
            if (!File.Exists(ownerContent))
                throw new PhaketException(ErrorCodes.ConflictUnresolvable, ("path", resource), ("owners", "missing owner content"));
            AtomicCopy(ownerContent, target);
        }
        else if (entry.BaseExisted && entry.BaseBackupRef is { } baseBackup && File.Exists(baseBackup))
        {
            AtomicCopy(baseBackup, target);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
        ledger.Set(entry with { CurrentHash = File.Exists(target) ? HashFile(target) : null });
    }

    private string? CaptureBase(string relative, string target, OwnershipLedger ledger)
    {
        if (ledger.Get(relative) is { } existing) return existing.BaseBackupRef;
        string? backup = null;
        var existed = File.Exists(target);
        if (existed)
        {
            backup = Path.Combine(backupRoot, "base", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup, overwrite: true);
        }
        ledger.Set(new LedgerEntry(relative, existed ? HashFile(target) : null, backup, null, existed, [], null));
        ledger.Save(ledgerPath); // base is durable BEFORE the mutation
        return backup;
    }

    private void AddOwner(string relative, LedgerOwner owner, OwnershipLedger ledger, string? currentHash)
    {
        var entry = ledger.Get(relative)
            ?? new LedgerEntry(relative, null, null, null, false, [], null);
        var layer = entry.Owners.Count == 0 ? 1 : entry.Owners.Max(o => o.Layer) + 1;
        ledger.Set(entry with
        {
            Owners = [.. entry.Owners.Where(o => o.RecipeId != owner.RecipeId), owner with { Layer = layer }],
            CurrentHash = currentHash,
        });
    }

    private LedgerOwner NewOwner(string opId, string? contentHash, string? value = null) =>
        new(context.RecipeId, context.ProfileId, opId, 0, contentHash, value);

    private void StoreOwnerContent(string relative, string target)
    {
        var path = OwnerContentPath(context.RecipeId, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(target, path, overwrite: true);
    }

    private string OwnerContentPath(string recipeId, string relative) =>
        Path.Combine(backupRoot, "owners", recipeId, relative);

    private string ResolveSource(string sourceRef)
    {
        var (scheme, path) = VirtualUri.Parse(sourceRef);
        return scheme switch
        {
            "staging" => PathBoundary.Resolve(context.StagingRoot, path),
            "package" => ResolvePackagePath(path),
            _ => throw new PhaketException(ErrorCodes.InvalidUri, ("uri", sourceRef)),
        };
    }

    private string ResolvePackageArtifact(string artifactId) => ResolvePackagePath(artifactId);

    private string ResolvePackagePath(string packagePath)
    {
        var segments = packagePath.Split('/');
        var storeFile = Path.Combine(context.PackageRoot, segments[0]);
        if (File.Exists(storeFile)) return storeFile;
        var withExtension = storeFile + ".zip";
        if (File.Exists(withExtension)) return withExtension;
        throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", segments[0]));
    }

    // Re-checks write boundaries immediately before a mutation (spec §3):
    // narrows the plan->execute symlink-swap window. Residual TOCTOU between
    // this check and the write is a documented PoC limitation (no openat-style
    // dirfd operations yet).
    private void RevalidateWrite(string target)
    {
        var gameRoot = Path.GetFullPath(context.GameRoot);
        var full = Path.GetFullPath(target);
        if (!PathBoundary.IsWithin(gameRoot, full)) return; // profile/staging handled by their own roots
        var relative = Relative(target);
        if (!context.Game.AllowsWrite(relative))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", target));
        var real = PathBoundary.Resolve(gameRoot, relative);
        var realRelative = real.StartsWith(Path.GetFullPath(gameRoot), StringComparison.Ordinal)
            ? real[Path.GetFullPath(gameRoot).Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/')
            : relative;
        if (!context.Game.AllowsWrite(realRelative))
            throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", target));
    }

    private string Relative(string absolute)
    {
        var full = Path.GetFullPath(absolute);
        var gameRoot = Path.GetFullPath(context.GameRoot);
        if (PathBoundary.IsWithin(gameRoot, full))
            return full[gameRoot.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
        var stagingRoot = Path.GetFullPath(context.StagingRoot);
        if (PathBoundary.IsWithin(stagingRoot, full))
            return "staging/" + full[stagingRoot.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
        var profileRoot = Path.GetFullPath(context.ProfileRoot);
        if (PathBoundary.IsWithin(profileRoot, full))
            return "profile/" + full[profileRoot.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
        return full.Replace(':', '_').Replace('\\', '/');
    }

    // Real (symlink-resolved) path relative to the real game root.
    private string realRelative(string absolute)
    {
        var realRoot = PathBoundary.Resolve(context.GameRoot, ".");
        var real = PathBoundary.Resolve(context.GameRoot, Relative(absolute));
        return real.StartsWith(realRoot, StringComparison.Ordinal)
            ? real[realRoot.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/')
            : Relative(absolute);
    }

    private string ResourcePath(string resource)
    {
        if (resource.StartsWith("staging/", StringComparison.Ordinal))
            return Path.Combine(context.StagingRoot, resource["staging/".Length..]);
        if (resource.StartsWith("profile/", StringComparison.Ordinal))
            return Path.Combine(context.ProfileRoot, resource["profile/".Length..]);
        return Path.Combine(context.GameRoot, resource);
    }

    private static (string? Value, bool KeyExisted) ReadConfigValue(string path, string key, string format)
    {
        if (!File.Exists(path)) return (null, false);
        if (format == "json")
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(key, out var value) ? (value.GetString(), true) : (null, false);
        }
        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx > 0 && line[..idx].Trim() == key) return (line[(idx + 1)..].Trim(), true);
        }
        return (null, false);
    }

    private static void WriteConfigValue(string path, string key, string value, string format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (format == "json")
        {
            var map = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? []
                : [];
            map[key] = JsonSerializer.SerializeToElement(value);
            AtomicWrite(path, JsonSerializer.Serialize(map, JsonOptions));
            return;
        }
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var idx = lines[i].IndexOf('=');
            if (idx > 0 && lines[i][..idx].Trim() == key)
            {
                lines[i] = $"{key} = {value}";
                replaced = true;
            }
        }
        if (!replaced) lines.Add($"{key} = {value}");
        AtomicWrite(path, string.Join('\n', lines) + "\n");
    }

    private static void RemoveConfigKey(string path, string key, string format)
    {
        if (!File.Exists(path)) return;
        if (format == "json")
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? [];
            map.Remove(key);
            AtomicWrite(path, JsonSerializer.Serialize(map, JsonOptions));
            return;
        }
        var lines = File.ReadAllLines(path)
            .Where(line => !(line.IndexOf('=') is var idx && idx > 0 && line[..idx].Trim() == key))
            .ToArray();
        AtomicWrite(path, string.Join('\n', lines) + "\n");
    }

    private static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".phaket-tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + ".phaket-tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    // Serializes transactions per installation (single-machine PoC guard).
    // Public so the orchestrator can hold it across the whole transaction.
    public FileStream LockInstallation()
    {
        Directory.CreateDirectory(stateRoot);
        var path = Path.Combine(stateRoot, ".lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new PhaketException(ErrorCodes.ValidationFailed,
                ("detail", "another transaction is active for this installation"));
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private void Persist(ExecutionJournal journal)
    {
        Directory.CreateDirectory(context.ProfileRoot);
        var temp = journalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal, JsonOptions));
        File.Move(temp, journalPath, overwrite: true);
    }
}
