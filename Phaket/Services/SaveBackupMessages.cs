using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;

namespace Phaket.Services;

// PAK-141: after a failed install the user must know their saves were copied first, and
// where. The lifecycle's Last sits on the executor inside InstallOrchestrator, so the host
// reads the copy off disk: the newest backup under the transaction's state root.
public static class SaveBackupMessages
{
    // Null when this transaction took no copy (no save paths, or a profile record only).
    public static SaveBackupRef? LastFor(string gameRoot, string stateRoot) =>
        Newest(Path.Combine(stateRoot, "save-backups"), gameRoot);

    // The newest full copy under backupRoot for this game: the restore hint only names a
    // copy the "your game saves" list will actually show, i.e. one with file contents.
    public static SaveBackupRef? Newest(string backupRoot, string gameRoot)
    {
        try
        {
            if (!Directory.Exists(backupRoot)) return null;
            return Directory.GetDirectories(backupRoot)
                .SelectMany(Directory.GetDirectories)
                .Select(directory => (Directory: directory, Manifest: NewestManifest(Path.Combine(directory, "manifest.json"))))
                .Where(backup => backup.Manifest is { SchemaVersion: SaveBackupManifest.CurrentSchemaVersion }
                    && string.Equals(backup.Manifest.GameId, Path.GetFileName(gameRoot), StringComparison.Ordinal))
                .OrderByDescending(backup => backup.Manifest!.CreatedAt)
                .Select(backup => new SaveBackupRef(backup.Manifest!, backup.Directory))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Suffix(SaveBackupRef? backup) => backup is null
        ? ""
        : " " + LocalizationService.Instance["SaveBackup.InstallFailedHint"];

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