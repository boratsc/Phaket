using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

public sealed record PlanExecutionContext(
    string GameRoot, string ProfileRoot, string StagingRoot, string PackageRoot,
    GameDefinition Game, string RecipeId, string ProfileId);

public enum DivergencePolicy { Fail, Restore, Skip }

// Transactional executor (spec §7, ADR-014): journal persisted before mutations,
// staging/atomic writes where the filesystem allows, ownership-based rollback,
// crash recovery with divergence detection. No ACID promise across the FS.
public sealed class PlanExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly PlanExecutionContext context;
    private readonly string journalPath;
    private readonly string ledgerPath;
    private readonly string backupRoot;

    public PlanExecutor(PlanExecutionContext context)
    {
        this.context = context;
        journalPath = Path.Combine(context.ProfileRoot, "journal.json");
        ledgerPath = Path.Combine(context.ProfileRoot, "ledger.json");
        backupRoot = Path.Combine(context.ProfileRoot, "backups");
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
        if (journal.State is not (JournalState.Approved or JournalState.Interrupted or JournalState.Failed))
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"journal state {journal.State} cannot execute"));
        journal = journal with { State = JournalState.Executing };
        Persist(journal);

        var ledger = OwnershipLedger.Load(ledgerPath);
        var entries = journal.Entries.ToList();
        try
        {
            foreach (var operation in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();
                var existing = entries.FirstOrDefault(e => e.Intent.OpId == operation.OpId);
                if (existing is { Verified: true }) continue; // resume: idempotent skip

                var intent = new OperationIntent(operation.OpId, operation.Kind, operation.TargetPath,
                    operation.Sha256, operation.Parameters?.GetValueOrDefault("key"));
                var entry = existing ?? new JournalEntry(intent, null, false, false);
                if (existing is null) entries.Add(entry);
                Persist(journal with { Entries = entries.ToArray() });

                var (backupRef, created, removedOwner) = Apply(operation, ledger, ct);
                entry = entry with { BackupRef = backupRef, Applied = true, CreatedFiles = created, RemovedOwner = removedOwner };
                entries[entries.FindIndex(e => e.Intent.OpId == operation.OpId)] = entry;
                Persist(journal with { Entries = entries.ToArray() });

                VerifyApplied(operation, ledger);
                entry = entry with { Verified = true };
                entries[entries.FindIndex(e => e.Intent.OpId == operation.OpId)] = entry;
                Persist(journal with { Entries = entries.ToArray() });
            }
            foreach (var verify in plan.Verification)
                VerifyOperation(verify);

            ledger.Save(ledgerPath);
            journal = journal with { State = JournalState.Completed, Entries = entries.ToArray() };
            Persist(journal);
            return journal;
        }
        catch (OperationCanceledException)
        {
            ledger.Save(ledgerPath);
            Persist(journal with { State = JournalState.Interrupted, Entries = entries.ToArray() });
            throw;
        }
        catch
        {
            ledger.Save(ledgerPath);
            Persist(journal with { State = JournalState.Failed, Entries = entries.ToArray() });
            throw;
        }
    }

    public ExecutionJournal Rollback(ExecutionJournal journal, DivergencePolicy policy = DivergencePolicy.Fail)
    {
        if (journal.State == JournalState.RolledBack) return journal;
        var ledger = OwnershipLedger.Load(ledgerPath);
        foreach (var entry in journal.Entries.Reverse())
        {
            if (!entry.Applied) continue;
            var target = entry.Intent.TargetPath;
            var expected = entry.Intent.ExpectedSha256
                ?? (entry.Intent.Kind is "file.copy" or "archive.extract" or "loader.ensure"
                    ? ledger.Get(Relative(target))?.CurrentHash : null);
            if (expected is not null && File.Exists(target)
                && HashFile(target) != expected && policy == DivergencePolicy.Fail)
                throw new PhaketException(ErrorCodes.JournalDivergence, ("path", target));
            if (policy == DivergencePolicy.Skip && File.Exists(target)
                && expected is not null && HashFile(target) != expected)
                continue;

            switch (entry.Intent.Kind)
            {
                case "directory.ensure":
                    if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                        Directory.Delete(target);
                    break;
                case "file.copy":
                    RestoreResource(Relative(target), ledger);
                    break;
                case "archive.extract":
                case "loader.ensure":
                    foreach (var file in entry.CreatedFiles ?? [])
                        RestoreResource(Relative(file), ledger);
                    break;
                case "config.set":
                    RestoreResource(Relative(target) + "::" + entry.Intent.Key, ledger);
                    break;
                case "file.remove":
                    if (entry.BackupRef is { } backup && File.Exists(backup))
                        AtomicCopy(backup, target);
                    if (entry.RemovedOwner is { } owner)
                        AddOwner(Relative(target), owner, ledger, HashFile(target));
                    break;
                case "profile.activate":
                    if (entry.BackupRef is { } profileBackup && File.Exists(profileBackup))
                        AtomicCopy(profileBackup, target);
                    else if (File.Exists(target))
                        File.Delete(target);
                    break;
            }
        }
        ledger.Save(ledgerPath);
        journal = journal with { State = JournalState.RolledBack };
        Persist(journal);
        return journal;
    }

    // PAK-26: uninstall recomputes shared resources from remaining owners.
    public void Uninstall(string recipeId)
    {
        var ledger = OwnershipLedger.Load(ledgerPath);
        var affected = ledger.ForRecipe(recipeId).Select(e => e.Resource).ToArray();
        foreach (var resource in affected)
        {
            var entry = ledger.Get(resource)!;
            var remaining = entry.Owners.Where(o => o.RecipeId != recipeId).ToArray();
            ledger.Set(entry with { Owners = remaining });
            Recompute(resource, ledger);
            if (ledger.Get(resource) is { Owners.Count: 0 } empty && !empty.BaseExisted && empty.BaseValue is null)
                ledger.Remove(resource);
        }
        var ownerBackupDir = Path.Combine(backupRoot, "owners", recipeId);
        if (Directory.Exists(ownerBackupDir)) Directory.Delete(ownerBackupDir, recursive: true);
        ledger.Save(ledgerPath);
    }

    private (string? BackupRef, IReadOnlyList<string>? Created, LedgerOwner? Removed) Apply(
        PlannedOperation operation, OwnershipLedger ledger, CancellationToken ct)
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
                var relative = Relative(target);
                var backup = CaptureBase(relative, target, ledger);
                AtomicCopy(source, target);
                AddOwner(relative, NewOwner(operation.OpId, HashFile(target)), ledger, HashFile(target));
                StoreOwnerContent(relative, target);
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
                    if (target == context.GameRoot && !context.Game.AllowsWrite(relative))
                        throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", relative));
                    CaptureBase(relative, destination, ledger);
                });
                foreach (var file in created)
                {
                    var relative = Relative(file);
                    AddOwner(relative, NewOwner(operation.OpId, HashFile(file)), ledger, HashFile(file));
                    StoreOwnerContent(relative, file);
                }
                return (null, created, null);
            }

            case "loader.ensure":
            {
                // Alpha: a loader artifact is a zip laid out over the game root
                // (doorstop files + BepInEx/**), constrained by writableAreas.
                var artifactId = parameters.GetValueOrDefault("artifact")
                    ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "loader.ensure missing artifact"));
                var source = ResolvePackageArtifact(artifactId);
                var created = SafeArchive.Extract(source, context.GameRoot, artifactId, 0, destination =>
                {
                    var relative = Relative(destination);
                    if (!context.Game.AllowsWrite(relative))
                        throw new PhaketException(ErrorCodes.PathOutOfBounds, ("path", relative));
                    CaptureBase(relative, destination, ledger);
                });
                foreach (var file in created)
                {
                    var relative = Relative(file);
                    AddOwner(relative, NewOwner(operation.OpId, HashFile(file)), ledger, HashFile(file));
                    StoreOwnerContent(relative, file);
                }
                return (null, created, null);
            }

            case "config.set":
            {
                var key = parameters["key"];
                var value = parameters["value"];
                var format = parameters.GetValueOrDefault("format", "keyvalue");
                var resource = Relative(target) + "::" + key;
                var (baseValue, existed) = ReadConfigValue(target, key, format);
                if (ledger.Get(resource) is null)
                    ledger.Set(new LedgerEntry(resource, null, null, baseValue, existed, [], null));
                var entry = ledger.Get(resource)!;
                var layer = entry.Owners.Count == 0 ? 1 : entry.Owners.Max(o => o.Layer) + 1;
                var updated = entry with { Owners = [.. entry.Owners.Where(o => o.RecipeId != context.RecipeId), NewOwner(operation.OpId, null, value) with { Layer = layer }] };
                ledger.Set(updated);
                WriteConfigValue(target, key, value, format);
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
                File.Delete(target);
                Recompute(relative, ledger);
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

    private void VerifyApplied(PlannedOperation operation, OwnershipLedger ledger)
    {
        if (operation.Kind is "file.copy" or "config.set" or "profile.activate")
        {
            if (!File.Exists(operation.TargetPath))
                throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"target missing after apply: {operation.TargetPath}"));
        }
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
            var path = Path.Combine(context.GameRoot, split[0]);
            var format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "keyvalue";
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
        // package://<artifactId>[/<rel>] maps to the artifact store file named
        // after the artifact; the planner already validated the declaration.
        var segments = packagePath.Split('/');
        var storeFile = Path.Combine(context.PackageRoot, segments[0]);
        if (File.Exists(storeFile)) return storeFile;
        var withExtension = storeFile + ".zip";
        if (File.Exists(withExtension)) return withExtension;
        throw new PhaketException(ErrorCodes.ArtifactMissing, ("artifact", segments[0]));
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

    private string ResourcePath(string resource)
    {
        if (resource.StartsWith("staging/", StringComparison.Ordinal))
            return Path.Combine(context.StagingRoot, resource["staging/".Length..]);
        if (resource.StartsWith("profile/", StringComparison.Ordinal))
            return Path.Combine(context.ProfileRoot, resource["profile/".Length..]);
        return Path.Combine(context.GameRoot, resource);
    }

    private static (string? Value, bool Existed) ReadConfigValue(string path, string key, string format)
    {
        if (!File.Exists(path)) return (null, false);
        if (format == "json")
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(key, out var value) ? (value.GetString(), true) : (null, true);
        }
        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx > 0 && line[..idx].Trim() == key) return (line[(idx + 1)..].Trim(), true);
        }
        return (null, true);
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
