using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// StateRoot is INSTALLATION-level (shared by all profiles modifying one game):
// the ownership ledger and content backups live there. The journal is per
// profile transaction. Hosts must pass a durable StateRoot per installation.
public sealed record PlanExecutionContext(
    string GameRoot, string ProfileRoot, string StagingRoot, string PackageRoot,
    GameDefinition Game, string RecipeId, string ProfileId, string? StateRoot = null);

public enum DivergencePolicy { Fail, Restore, Skip }

// Transactional executor (spec §7, ADR-014): base state and intent persisted
// BEFORE mutations, atomic writes, ownership-based rollback, crash recovery
// with divergence detection. No ACID promise across the filesystem.
public sealed class PlanExecutor : IPlanExecutorHost
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

    public PlanExecutor(PlanExecutionContext context, Action<string>? faultHook = null)
    {
        this.context = context;
        this.faultHook = faultHook;
        stateRoot = context.StateRoot ?? context.ProfileRoot;
        journalPath = Path.Combine(context.ProfileRoot, "journal.json");
        ledgerPath = Path.Combine(stateRoot, "ledger.json");
        backupRoot = Path.Combine(stateRoot, "backups");
    }

    public static ExecutionJournal? LoadJournal(string profileRoot)
    {
        var path = Path.Combine(profileRoot, "journal.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ExecutionJournal>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    public ExecutionJournal Approve(OperationPlan plan)
    {
        var journal = new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"), plan.PlanId, JournalState.Approved, []);
        Persist(journal);
        return journal;
    }

    public ExecutionJournal Execute(OperationPlan plan, ExecutionJournal journal, CancellationToken ct = default)
    {
        if (journal.State is not (JournalState.Approved or JournalState.Interrupted or JournalState.Failed or JournalState.Executing))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"journal state {journal.State} cannot execute"));
        using var gate = LockInstallation();
        try
        {
            journal = journal with { State = JournalState.Executing };
            Persist(journal);
            foreach (var operation in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();
                journal = ExecuteOperation(plan, journal, operation.OpId);
            }
            return Complete(plan, journal);
        }
        catch (OperationCanceledException)
        {
            // Reload the latest persisted journal: ExecuteOperation may have
            // recorded the current operation's intent/backup before failing.
            var latest = LoadJournal(context.ProfileRoot) ?? journal;
            Persist(latest with { State = JournalState.Interrupted });
            throw;
        }
        catch
        {
            var latest = LoadJournal(context.ProfileRoot) ?? journal;
            Persist(latest with { State = JournalState.Failed });
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

    // Executes a single approved operation; the host broker drives this per op.
    public ExecutionJournal ExecuteOperation(OperationPlan plan, ExecutionJournal journal, string opId)
    {
        if (journal.State is not (JournalState.Approved or JournalState.Interrupted or JournalState.Failed or JournalState.Executing))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"journal state {journal.State} cannot execute"));
        var operation = plan.Operations.First(o => o.OpId == opId);
        var entries = journal.Entries.ToList();
        var existing = entries.FirstOrDefault(e => e.Intent.OpId == opId);
        if (existing is { Verified: true }) return journal; // resume: idempotent skip

        var ledger = OwnershipLedger.Load(ledgerPath);
        journal = journal with { State = JournalState.Executing };
        try
        {
            var intent = new OperationIntent(operation.OpId, operation.Kind, operation.TargetPath,
                operation.Sha256, operation.Parameters?.GetValueOrDefault("key"));
            var entry = existing ?? new JournalEntry(intent, null, false, false);

            // For extracts, journal the full planned write list BEFORE any write
            // so a partial failure is still recoverable.
            if (operation.Kind is "archive.extract" or "loader.ensure" && existing is null)
                entry = entry with { CreatedFiles = PlanArchiveWrites(operation) };

            if (existing is null) entries.Add(entry);
            Persist(journal with { Entries = entries.ToArray() });

            var (backupRef, created, removedOwner) = Apply(operation, ledger);
            entry = entry with { BackupRef = backupRef, Applied = true, CreatedFiles = created ?? entry.CreatedFiles, RemovedOwner = removedOwner };
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
                    // Covers partial extracts: planned writes are journaled up front.
                    foreach (var file in entry.CreatedFiles ?? [])
                        RestoreResource(Relative(file), ledger);
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
                if (entry.CurrentHash is { } expected && File.Exists(path)
                    && HashFile(path) != expected && policy == DivergencePolicy.Fail)
                    throw new PhaketException(ErrorCodes.JournalDivergence, ("path", path));
                if (policy == DivergencePolicy.Skip && File.Exists(path)
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
            if (ledger.Get(resource) is { Owners.Count: 0 } empty && !empty.BaseExisted && empty.BaseValue is null)
            {
                ledger.Remove(resource);
                ledger.Save(ledgerPath);
            }
        }
        // Owner content is removed only AFTER the ledger no longer references it.
        var ownerBackupDir = Path.Combine(backupRoot, "owners", recipeId);
        if (Directory.Exists(ownerBackupDir)) Directory.Delete(ownerBackupDir, recursive: true);
        Persist(new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"), $"uninstall:{recipeId}", JournalState.Completed, []));
    }

    private IReadOnlyList<string> PlanArchiveWrites(PlannedOperation operation)
    {
        var source = operation.Kind == "loader.ensure"
            ? (operation.SourceRef is { Length: > 0 } r ? ResolveSource(r) : ResolvePackageArtifact(operation.Parameters?["artifact"] ?? ""))
            : ResolveSource(operation.SourceRef!);
        var targetRoot = operation.Kind == "loader.ensure" ? context.GameRoot : operation.TargetPath;
        var strip = operation.Parameters?.TryGetValue("stripComponents", out var s) == true ? int.Parse(s) : 0;
        return SafeArchive.PlanWrites(source, targetRoot, operation.SourceRef ?? "loader", strip);
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
                });
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
                var created = SafeArchive.Extract(source, context.GameRoot, artifactId, 0, destination =>
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
    private FileStream LockInstallation()
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
