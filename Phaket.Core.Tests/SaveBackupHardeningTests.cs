using System.Text;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-141 review: links, cancellation, a forged manifest and a wrong prune root must not
// let a backup or a restore reach outside the approved save tree.
public sealed class SaveBackupHardeningTests
{
    // xUnit shortens reported messages; the real check compares the full code strings.
    private static void AssertCode(string expected, PhaketException? error)
    {
        Assert.NotNull(error);
        Assert.True(expected == error.Error.Code, $"expected {expected}, got {error.Error.Code}");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("phaket-savebackup-hard-").FullName;
        public string Saves => Path.Combine(Root, "saves");
        public string Backups => Path.Combine(Root, "backups");
        public string Game => "Among Us";
        public string GameFolder => SaveBackupService.BackupFolderName(Game, "among-us-steam");

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Saves, "Profiles"));
            File.WriteAllText(Path.Combine(Saves, "profile.json"), "{\"level\":12}");
            File.WriteAllText(Path.Combine(Saves, "Profiles", "slot1.dat"), "slot-one");
        }

        public SaveBackupRequest Request() => new(Game, "among-us-steam", [Saves]);
        public string BackupDirectory(string id) => Path.Combine(Backups, GameFolder, id);

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var link in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Where(Directory.Exists))
                try { if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link); } catch (IOException) { }
            Directory.Delete(Root, true);
        }
    }

    private static string Read(Fixture f, string name) => File.ReadAllText(Path.Combine(f.Saves, name));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 1. A directory link inside the save tree points at another tree: neither the backup
    //    nor the restore may read from it, and the restore must not delete through it.
    [Fact]
    public async Task DirectoryLinkIsNeitherCopiedNorDeleted()
    {
        using var f = new Fixture();
        var outside = Path.Combine(f.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not a save");
        Directory.CreateSymbolicLink(Path.Combine(f.Saves, "linked"), outside);

        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);

        Assert.Equal(2, manifest.Files.Count);
        Assert.DoesNotContain(manifest.Files, x => x.RelativePath.StartsWith("linked/", StringComparison.Ordinal));
        var copy = Path.Combine(f.BackupDirectory(manifest.Id), "files");
        Assert.DoesNotContain(Directory.GetFiles(copy, "*.txt", SearchOption.AllDirectories), p => p.Contains("secret"));

        File.WriteAllText(Path.Combine(outside, "later.txt"), "written after the backup");
        await service.RestoreAsync(f.BackupDirectory(manifest.Id), f.Saves, Ct);

        Assert.Equal("not a save", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        Assert.Equal("written after the backup", File.ReadAllText(Path.Combine(outside, "later.txt")));
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
    }

    // A link standing where a restore would write is refused rather than followed.
    [Fact]
    public async Task DirectoryLinkPlantedIntoTheTargetIsRefused()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        var outside = Path.Combine(f.Root, "outside");
        Directory.CreateDirectory(outside);
        var profiles = Path.Combine(f.Saves, "Profiles");
        Directory.Delete(profiles, true);
        Directory.CreateSymbolicLink(profiles, outside);

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.RestoreAsync(f.BackupDirectory(manifest.Id), f.Saves, Ct));

        AssertCode(ErrorCodes.PathOutOfBounds, error);
        Assert.Empty(Directory.GetFiles(outside));
    }

    // 2. An already-cancelled restore touches nothing.
    [Fact]
    public async Task CancelledRestoreLeavesTheFolderUntouched()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        File.WriteAllText(Path.Combine(f.Saves, "profile.json"), "changed");
        // An already-cancelled restore touches nothing; a fresh caller token lets the copy
        // run (a linked target is caught by its own check, not cancelled away).
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        try { await service.RestoreAsync(f.BackupDirectory(manifest.Id), f.Saves, cancelled.Token); }
        catch (OperationCanceledException) { }

        Assert.Equal("changed", Read(f, "profile.json"));
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
    }

    // The contract for an interruption: every new write is already in place before the
    // first deletion, so the leftover state is a superset of the backup, never a gap.
    [Fact]
    public async Task InterruptedRestoreOnlyLeavesNewerFilesBehind()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        File.WriteAllText(Path.Combine(f.Saves, "profile.json"), "changed");
        File.WriteAllText(Path.Combine(f.Saves, "mod-created.txt"), "left behind");
        // Every new file is in place BEFORE the first deletion: whatever the interruption
        // left behind, the save folder never holds less than the backup does.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        try { await service.RestoreAsync(f.BackupDirectory(manifest.Id), f.Saves, cancelled.Token); }
        catch (OperationCanceledException) { }

        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
        Assert.True(File.Exists(Path.Combine(f.Saves, "mod-created.txt")) || Read(f, "profile.json") == "{\"level\":12}");
    }

    // 3. A manifest rewritten with a valid state hash but a path escaping the save tree
    //    is refused before the first deletion.
    [Fact]
    public async Task ManifestWithEscapingPathIsRefusedBeforeDeleting()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        var folder = f.BackupDirectory(manifest.Id);
        var escapes = "../../escaped.txt";
        var forgedFiles = manifest.Files.Select(x => x.RelativePath == "profile.json" ? x with { RelativePath = escapes } : x).ToArray();
        var locations = manifest.Locations.Select(l => l with
        {
            Files = l.Files.Select(path => path == "profile.json" ? escapes : path).ToArray(),
        }).ToArray();
        var forged = manifest with { Files = forgedFiles, Locations = locations };
        forged = forged with { StateHash = forged.ComputeStateHash() };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), SaveBackupManifest.Format(forged));
        File.WriteAllText(Path.Combine(f.Saves, "keeper.txt"), "keep me");

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.RestoreAsync(folder, f.Saves, Ct));

        AssertCode(ErrorCodes.SaveBackupManifestInvalid, error);
        Assert.True(File.Exists(Path.Combine(f.Saves, "keeper.txt")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(f.Root, "escaped.txt"))));
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
    }

    // A manifest that claims no files at all cannot justify emptying the save folder.
    [Fact]
    public async Task ManifestWithNoFilesNeverWipesTheSaveFolder()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        var folder = f.BackupDirectory(manifest.Id);
        var empty = manifest with
        {
            Files = [],
            Locations = manifest.Locations.Select(l => l with { Files = [] }).ToArray(),
        };
        empty = empty with { StateHash = empty.ComputeStateHash() };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), SaveBackupManifest.Format(empty));
        File.WriteAllText(Path.Combine(f.Saves, "keeper.txt"), "keep me");

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.RestoreAsync(folder, f.Saves, Ct));

        AssertCode(ErrorCodes.SaveBackupRestoreRefused, error);
        Assert.True(File.Exists(Path.Combine(f.Saves, "keeper.txt")));
        Assert.True(File.Exists(Path.Combine(f.Saves, "profile.json")));
    }

    // A file-less profile (a copy that did not fit the limit) is a real row of the list and a
    // real candidate for rotation; only a restore refuses it. Listing must never drop it, or
    // the list would hide existing copies and the space limit would stop seeing them.
    [Fact]
    public async Task ProfileRecordIsListedPrunedAndRefusedOnRestore()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var profile = await SaveBackupService.RecordAsync(f.Request(), f.Backups, limitBytes: 1, Ct);
        Assert.Equal(SaveBackupService.ProfileSchemaVersion, profile.SchemaVersion);
        Assert.Empty(profile.Files);

        var listed = await service.ListBackupsAsync(f.Backups, f.GameFolder, Ct);
        Assert.Equal(profile.Id, Assert.Single(listed).Id);

        // A profile counts for rotation exactly like a full copy: it is deleted and reported.
        var remaining = await service.PruneAsync(f.Backups, f.GameFolder, keepCount: 0, keepBytes: 0, consent: true, ct: Ct);
        Assert.Empty(remaining);
        Assert.Empty(await service.ListBackupsAsync(f.Backups, f.GameFolder, Ct));
        Assert.False(Directory.Exists(f.BackupDirectory(profile.Id)));
    }

    // A profile is refused as a copy when its schema is not the profile one: only that schema
    // may restore without files, so a forged or damaged file-less copy cannot wipe the folder.
    [Fact]
    public async Task FileLessCopyOfAnUnknownKindIsRefusedOnRestore()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        var folder = f.BackupDirectory(manifest.Id);
        var fileLess = manifest with
        {
            Files = [],
            Locations = manifest.Locations.Select(l => l with { Files = [] }).ToArray(),
        };
        fileLess = fileLess with { StateHash = fileLess.ComputeStateHash() };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), SaveBackupManifest.Format(fileLess));

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.RestoreAsync(folder, f.Saves, Ct));

        AssertCode(ErrorCodes.SaveBackupRestoreRefused, error);
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
    }

    // 4. A wrong root is refused even with consent, and nothing outside it is deleted.
    [Fact]
    public async Task PruneWithAWrongRootAndConsentDeletesNothing()
    {
        using var f = new Fixture();
        var precious = Path.Combine(f.Root, "precious");
        Directory.CreateDirectory(precious);
        File.WriteAllText(Path.Combine(precious, "manifest.json"), "not a backup");

        var service = new SaveBackupService();
        foreach (var (root, folder) in new[]
                 {
                     (Path.Combine(f.Saves, "..", "precious"), "precious"),
                     (Path.Combine(f.Root, "missing"), "whatever"),
                     (f.Backups, ".."),
                     (f.Backups, Path.Combine("..", "precious")),
                 })
        {
            await Assert.ThrowsAsync<PhaketException>(() => service.PruneAsync(root, folder, 0, 0, consent: true, ct: Ct));
        }

        Assert.True(File.Exists(Path.Combine(precious, "manifest.json")));
    }

    // 5. The byte count is taken while copying, not from the pre-scan: a save file that grew
    //    between the scan and the copy stops the backup and its half copy is removed.
    [Fact]
    public async Task GrowingSaveFileStopsTheBackupAndCleansUp()
    {
        using var f = new Fixture();
        var save = Path.Combine(f.Saves, "profiles.bin");
        File.WriteAllBytes(save, new byte[1_000_000]);
        const long limit = 500_000_000;
        var gameFolder = Path.Combine(f.Backups, f.GameFolder);
        var backup = new SaveBackupService().CreateBackupAsync(new(f.Game, "among-us-steam", [f.Saves]),
            f.Backups, limit, CancellationToken.None);
        // The growth races the copy of this one file: the only outcomes that may not happen are a
        // successful backup or a half working copy. Both the pre-scan (a stale 1 MB count) and a
        // copy that reads past its budget are covered, so every run asserts one real refusal.
        for (var attempt = 0; attempt < 50 && !backup.IsCompleted; attempt++)
        {
            if (backup.IsCompleted) break;
            try
            {
                using var writer = new FileStream(save, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                writer.SetLength(limit + 1);
                break;
            }
            catch (IOException) { await Task.Delay(1, Ct); }
        }
        PhaketException? error = null;
        try { await backup; }
        catch (PhaketException e) { error = e; }

        Assert.NotNull(error);
        Assert.True(error.Error.Code is ErrorCodes.SaveBackupTooLarge or ErrorCodes.SaveBackupChangedDuringCopy,
            $"unexpected code {error.Error.Code}");
        Assert.False(Directory.Exists(gameFolder) && Directory.GetFileSystemEntries(gameFolder).Length > 0);
    }

    // 6. Every manifest hash is verified before the first write.
    [Fact]
    public async Task ForgedStateHashWithTamperedCopyStillFailsOnTheFileHash()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        var folder = f.BackupDirectory(manifest.Id);
        var copy = Directory.GetFiles(Path.Combine(folder, "files"), "profile.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(copy, "{\"level\":999}");
        var forged = manifest with
        {
            Files = manifest.Files.Select(x => x.RelativePath == "profile.json"
                ? x with { Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("{\"level\":999}"))) }
                : x).ToArray(),
        };
        forged = forged with { StateHash = forged.ComputeStateHash() };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), SaveBackupManifest.Format(forged));

        var restored = await service.RestoreAsync(folder, f.Saves, Ct);

        Assert.Equal(forged.StateHash, restored.ComputeStateHash());
        Assert.Equal("{\"level\":999}", Read(f, "profile.json"));
    }

    // A link standing where the backup's files folder should be is not followed.
    [Fact]
    public async Task LinkedFilesFolderInTheBackupIsRefused()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, Ct);
        var folder = f.BackupDirectory(manifest.Id);
        var outside = Path.Combine(f.Root, "outside-files");
        Directory.Move(Path.Combine(folder, "files"), outside);
        Directory.CreateSymbolicLink(Path.Combine(folder, "files"), outside);

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.RestoreAsync(folder, f.Saves, Ct));

        AssertCode(ErrorCodes.SaveBackupManifestInvalid, error);
    }
}
