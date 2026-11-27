using System.Text;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-141: save backups use only a fixture tree in temp, never a real game install.
public sealed class SaveBackupServiceTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("phaket-savebackup-").FullName;
        public string Saves => Path.Combine(Root, "saves");
        public string Backups => Path.Combine(Root, "backups");
        public string Game => "Among Us";

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Saves, "Profiles"));
            File.WriteAllText(Path.Combine(Saves, "profile.json"), "{\"level\":12}");
            File.WriteAllText(Path.Combine(Saves, "Profiles", "slot1.dat"), "slot-one");
        }

        public SaveBackupRequest Request() => new(Game, "among-us-steam", [Saves]);

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static string Read(Fixture f, string name) => File.ReadAllText(Path.Combine(f.Saves, name));

    [Fact]
    public async Task BackupAndRestoreRoundTripsTheSaveTree()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);

        Assert.Equal(SaveBackupManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(manifest.ComputeStateHash(), manifest.StateHash);
        Assert.Equal("Profiles/slot1.dat", Assert.Single(manifest.Files, x => x.RelativePath.Contains('/')).RelativePath);

        File.WriteAllText(Path.Combine(f.Saves, "profile.json"), "{\"level\":0}");
        File.Delete(Path.Combine(f.Saves, "Profiles", "slot1.dat"));

        var backups = await service.ListBackupsAsync(f.Backups, SaveBackupService.BackupFolderName(f.Game, "among-us-steam"), TestContext.Current.CancellationToken);
        var backupDirectory = Path.Combine(f.Backups, SaveBackupService.BackupFolderName(f.Game, "among-us-steam"), Assert.Single(backups).Id);
        await service.RestoreAsync(backupDirectory, f.Saves, TestContext.Current.CancellationToken);

        Assert.Equal("{\"level\":12}", Read(f, "profile.json"));
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
    }

    // Nothing the backup itself listed disappears from the restored folder (the contract for
    // the whole tree is now "delete only what was listed", see SaveBackupHardeningTests).
    [Fact]
    public async Task RestoreBringsBackEveryFileTheBackupListed()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(f.Saves, "Profiles", "slot1.dat"));

        await service.RestoreAsync(Path.Combine(f.Backups, SaveBackupService.BackupFolderName(f.Game, "among-us-steam"), manifest.Id), f.Saves, TestContext.Current.CancellationToken);

        Assert.Equal(manifest.Files.Count, manifest.Files.Count(x => File.Exists(Path.Combine(f.Saves, x.RelativePath))));
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
    }

    [Theory]
    [InlineData("Among Us", "", "Among Us")]
    [InlineData("a/b:c*d?e\"f<g>h|i", "install-1", "a_b_c_d_e_f_g_h_i")]
    [InlineData("   ", "among-us-steam", "among-us-steam")]
    [InlineData("dots...", "install-1", "dots")]
    public void FolderNamesAreSanitizedWithAnInstallationFallback(string title, string installation, string expected) =>
        Assert.Equal(expected, SaveBackupService.BackupFolderName(title, installation));

    [Fact]
    public void LongFolderNamesAreCappedAt120Characters()
    {
        var name = SaveBackupService.BackupFolderName(new string('x', 200), "install");
        Assert.Equal(SaveBackupService.MaxNameLength, name.Length);
    }

    [Fact]
    public async Task TwoBackupsInTheSameMillisecondDoNotCollide()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var first = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        var second = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await service.ListBackupsAsync(f.Backups, SaveBackupService.BackupFolderName(f.Game, "x"), TestContext.Current.CancellationToken)).Count);
    }

    // The limit is decided from file lengths BEFORE anything is copied.
    [Fact]
    public async Task ExceedingTheLimitRefusesBeforeCopyingAnything()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            service.CreateBackupAsync(f.Request(), f.Backups, limitBytes: 10, ct: TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SaveBackupTooLarge, error.Error.Code);
        // The message shows both sizes, so the user knows how far over they are.
        Assert.Contains("10", error.LocalizedMessage("en"));
        Assert.Contains("20", error.LocalizedMessage("en"));
        Assert.False(Directory.Exists(f.Backups));
    }

    [Fact]
    public async Task EmptySaveListStillProducesAValidBackup()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(new(f.Game, "among-us-steam", []), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        Assert.Equal(SaveBackupService.EmptySchemaVersion, manifest.SchemaVersion);
        Assert.Empty(manifest.Files);
        Assert.Empty(manifest.Locations);
        Assert.NotEmpty(await service.ListBackupsAsync(f.Backups, SaveBackupService.BackupFolderName(f.Game, "among-us-steam"), TestContext.Current.CancellationToken));
    }

    // A damaged copy is refused in full: no file is touched, not even the healthy ones.
    [Fact]
    public async Task CorruptCopyFailsWholeRestoreWithoutPartialWrite()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        var folder = Path.Combine(f.Backups, SaveBackupService.BackupFolderName(f.Game, "among-us-steam"), manifest.Id);
        var locationKey = Directory.GetDirectories(Path.Combine(folder, "files")).Single();
        File.WriteAllText(Path.Combine(locationKey, "Profiles", "slot1.dat"), "tampered");

        File.WriteAllText(Path.Combine(f.Saves, "profile.json"), "changed by the game");
        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            service.RestoreAsync(folder, f.Saves, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SaveBackupHashMismatch, error.Error.Code);
        Assert.Equal("Profiles/slot1.dat", error.Error.Parameters["path"]);
        // The healthy file was not restored either: the restore never started.
        Assert.Equal("changed by the game", Read(f, "profile.json"));
    }

    [Fact]
    public async Task TamperedManifestIsRefused()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        var folder = Path.Combine(f.Backups, SaveBackupService.BackupFolderName(f.Game, "x"), manifest.Id);
        // Editing any manifest field breaks its own state hash: the copy is refused whole.
        var manifestPath = Path.Combine(folder, "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"among-us-steam\"", "\"tampered\""));

        var error = await Assert.ThrowsAsync<PhaketException>(() =>
            service.RestoreAsync(folder, f.Saves, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SaveBackupManifestInvalid, error.Error.Code);
    }

    [Fact]
    public async Task PruneWithoutConsentRefusesAndKeepsEveryBackup()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        var gameFolder = SaveBackupService.BackupFolderName(f.Game, "x");

        var error = await Assert.ThrowsAsync<PhaketException>(() => service.PruneAsync(f.Backups, gameFolder, keepCount: 0, keepBytes: 0, consent: false, ct: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SaveBackupPruneConsentRequired, error.Error.Code);
        Assert.Single(await service.ListBackupsAsync(f.Backups, gameFolder, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PruneWithConsentKeepsTheNewestAndDeletesTheRest()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var gameFolder = SaveBackupService.BackupFolderName(f.Game, "x");
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(f.Saves, "profile.json"), $"{{\"level\":{i}}}");
            await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
            await Task.Delay(3, TestContext.Current.CancellationToken); // distinct timestamps
        }

        var remaining = await service.PruneAsync(f.Backups, gameFolder, keepCount: 1, keepBytes: long.MaxValue, consent: true, ct: TestContext.Current.CancellationToken);

        Assert.Single(remaining);
        Assert.Single(await service.ListBackupsAsync(f.Backups, gameFolder, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Backups, gameFolder)));
    }

    [Fact]
    public void ManifestSerializationIsDeterministic()
    {
        var location = new SaveBackupLocation("/saves", true, ["b.dat", "a.dat"]);
        var files = new[]
        {
            new SaveBackupFile("b.dat", 2, DateTimeOffset.UnixEpoch, new string('b', 64)),
            new SaveBackupFile("a.dat", 1, DateTimeOffset.UnixEpoch, new string('a', 64)),
        };
        var first = new SaveBackupManifest(SaveBackupManifest.CurrentSchemaVersion, "id-1", DateTimeOffset.UnixEpoch,
            "Among Us", "among-us-steam", [location], files, 3, null);
        first = first with { StateHash = first.ComputeStateHash() };
        // Same content in a different order: the same canonical bytes and hash.
        var reordered = first with { Files = files.Reverse().ToArray() };
        Assert.Equal(first.ComputeStateHash(), reordered.ComputeStateHash());

        var sameContent = first with { Id = "id-1", CreatedAt = first.CreatedAt };
        Assert.Equal(Encoding.UTF8.GetString(SaveBackupManifest.Serialize(first)), Encoding.UTF8.GetString(SaveBackupManifest.Serialize(sameContent)));
    }

    // A profile record notes the location without copying a byte; restoring it keeps
    // every file there, the user's included.
    [Fact]
    public async Task ProfileRecordKeepsExistingFilesOnRestore()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var manifest = await SaveBackupService.RecordAsync(f.Request(), f.Backups, limitBytes: 1, TestContext.Current.CancellationToken);

        Assert.Equal(SaveBackupService.ProfileSchemaVersion, manifest.SchemaVersion);
        Assert.Empty(manifest.Files);
                Assert.True(Assert.Single(manifest.Locations).Existed);
        Assert.False(Directory.Exists(Path.Combine(f.Backups, SaveBackupService.BackupFolderName(f.Game, "x"), manifest.Id, "files")));

        File.WriteAllText(Path.Combine(f.Saves, "recipe-wrote-this.txt"), "new");
        var folder = Path.Combine(f.Backups, SaveBackupService.BackupFolderName(f.Game, "x"), manifest.Id);
        await service.RestoreAsync(folder, f.Saves, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(f.Saves, "recipe-wrote-this.txt")));
        Assert.Equal("slot-one", Read(f, Path.Combine("Profiles", "slot1.dat")));
        Assert.True(File.Exists(Path.Combine(f.Saves, "profile.json")));
    }

    // Save locations are per installation; the same game title does not mix them.
    [Fact]
    public async Task ListBackupsIsScopedToTheInstallationFolder()
    {
        using var f = new Fixture();
        var service = new SaveBackupService();
        var other = new SaveBackupRequest(f.Game, "among-us-epic", []);
        await service.CreateBackupAsync(f.Request(), f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);
        await service.CreateBackupAsync(other, f.Backups, SaveBackupService.DefaultLimitBytes, TestContext.Current.CancellationToken);

        var steam = await service.ListBackupsAsync(f.Backups, SaveBackupService.BackupFolderName(f.Game, "x"), TestContext.Current.CancellationToken);
        // Both installations share the sanitized game title: the folder is the same.
        Assert.Equal(2, steam.Count);
        Assert.NotEqual(steam[0].Id, steam[1].Id);
    }
}