using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;

namespace Phaket.Services;

// PAK-141: before an install the user must know their saves were read and copied, and
// where. The lifecycle's Last sits on the executor inside InstallOrchestrator, so the host
// reads the copy off disk: the newest backup under the transaction's state root.
public static class SaveBackupMessages
{
    // Null when this transaction took no copy (no save paths, or a profile record only).
    public static SaveBackupRef? LastFor(string gameRoot, string stateRoot) =>
        Newest(Path.Combine(stateRoot, "save-backups"), gameRoot);

    // R3d #7: the save copy is best-effort, so a run that took no copy must say so instead
    // of letting the user believe the previous copy protects this install. before/after are the
    // newest copy around the run: a new one (other id) is this run's copy, an unchanged one was
    // made by an earlier run and is only ever named as older.
    public static string NewCopyHint(SaveBackupRef? before, SaveBackupRef? after) =>
        after is not null && (before is null || after.Manifest.Id != before.Manifest.Id)
            ? " " + LocalizationService.Instance["SaveBackup.InstallFailedHint"]
            : " " + (before is null
                ? LocalizationService.Instance["SaveBackup.NotCopiedWarning"]
                : LocalizationService.Instance["SaveBackup.OlderCopyHint"]);

    // The newest copy under backupRoot for this game: the restore hint only names a
    // copy the "your game saves" list will actually show. A profile record (over the limit,
    // no file contents) is not that copy and never counts as one.
    public static SaveBackupRef? Newest(string backupRoot, string gameRoot)
    {
        try
        {
            if (!Directory.Exists(backupRoot)) return null;
            return Directory.GetDirectories(backupRoot)
                .SelectMany(Directory.GetDirectories)
                .Select(directory => (Directory: directory, Manifest: NewestManifest(Path.Combine(directory, "manifest.json"))))
                .Where(backup => backup.Manifest is { SchemaVersion: SaveBackupManifest.CurrentSchemaVersion }
                    && string.Equals(backup.Manifest.GameId, SaveBackupService.BackupFolderName(backup.Manifest.GameId, backup.Manifest.InstallationId), StringComparison.Ordinal))
                .OrderByDescending(backup => backup.Manifest!.CreatedAt)
                .Select(backup => new SaveBackupRef(backup.Manifest!, backup.Directory))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Null for a missing or damaged manifest: a folder that cannot be read is skipped.
    public static SaveBackupManifest? NewestManifest(string manifestPath)
    {
        try { return SaveBackupManifest.Parse(File.ReadAllText(manifestPath)); }
        catch (Exception e) when (e is System.Text.Json.JsonException or Phaket.Core.Omrl.PhaketException
            or FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null; // a damaged folder is skipped, never reported as a restorable copy
        }
    }
}