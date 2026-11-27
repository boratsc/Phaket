using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Mods;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-141: "your game saves" lists the copies, restoring asks first, and deleting old
// copies never happens without the consent box — the service refuses on its own too.
public class SaveBackupsViewModelTests
{
    private static (string Root, string Saves) Fixture()
    {
        var root = Directory.CreateTempSubdirectory("phaket-savebackup-vm-").FullName;
        var saves = Path.Combine(root, "profile", "saveData");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "save.dat"), "save bytes");
        return (root, saves);
    }

    // The service's own layout (<app data>/state/<game>/<root key>/save-backups), so the page
    // finds exactly what an install would have written.
    private static string RootFor(string appDataRoot, string gameId) =>
        Path.Combine(appDataRoot, "state", gameId, "installation-1", "save-backups");

    // Distinct ids so two copies never share a folder; the id carries the timestamp.
    private static async Task<SaveBackupManifest> CreateAsync(string root, string saves, string game = "Among Us")
    {
        var manifest = await new SaveBackupService().CreateBackupAsync(
            new SaveBackupRequest(game, "install-1", [saves]), root, SaveBackupService.DefaultLimitBytes);
        await Task.Delay(5);
        return manifest;
    }

    // The page reads the disk and awaits the service; its continuations belong on the UI thread.
    private static SaveBackupsViewModel Page(string appDataRoot, string gameId) =>
        UiTest.Run(() => Task.FromResult(SaveBackupsViewModel.ForGame(appDataRoot, gameId)));

    private static void Load(SaveBackupsViewModel page) => UiTest.Run(() => page.LoadCommand.ExecuteAsync(null));

    [AvaloniaFact]
    public void ListsEveryCopyNewestFirstWithAFriendlyDate()
    {
        var (root, saves) = Fixture();
        try
        {
            var backupRoot = RootFor(root, "among-us");
            var first = UiTest.Run(() => CreateAsync(backupRoot, saves));
            var second = UiTest.Run(() => CreateAsync(backupRoot, saves));
            var vm = Page(root, "among-us");

            Load(vm);

            Assert.False(vm.IsEmpty);
            Assert.Equal(2, vm.Items.Count);
            Assert.Equal(second.Id, vm.Items[0].Manifest.Id);
            Assert.Equal(first.Id, vm.Items[1].Manifest.Id);
            Assert.Contains("2026", vm.Items[0].DateText);
            Assert.EndsWith(vm.Items[0].SizeText[^2..], "KB");
            Assert.Single(vm.Items[0].Manifest.Files);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void AnEmptyListSaysCopiesAppearAfterInstallingMods()
    {
        var (root, _) = Fixture();
        try
        {
            var vm = Page(root, "among-us");
            Load(vm);
            Assert.True(vm.IsEmpty);
            Assert.False(vm.HasItems);
            Assert.NotEqual("SaveBackup.Empty", LocalizationService.Instance["SaveBackup.Empty"]);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void RestoreAsksFirstAndReplacesTheCurrentSavesOnlyAfterConfirmation()
    {
        var (root, saves) = Fixture();
        try
        {
            var vm = Page(root, "among-us");
            UiTest.Run(() => CreateAsync(RootFor(root, "among-us"), saves));
            Load(vm);

            vm.RestoreCommand.Execute(vm.Items[0]);
            Assert.NotNull(vm.PendingRestore);
            Assert.Contains("zastąpione", vm.RestoreQuestion);
            // Nothing restored yet: the save file still holds what the game wrote.
            File.WriteAllText(Path.Combine(saves, "save.dat"), "changed after the backup");

            UiTest.Run(() => vm.ConfirmRestoreCommand.ExecuteAsync(null));

            Assert.Null(vm.PendingRestore);
            Assert.Equal("save bytes", File.ReadAllText(Path.Combine(saves, "save.dat")));
            Assert.Equal(LocalizationService.Instance["SaveBackup.Restored"], vm.StatusMessage);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void RestoreKeepsTheSavesWhenTheQuestionIsCancelled()
    {
        var (root, saves) = Fixture();
        try
        {
            var vm = Page(root, "among-us");
            UiTest.Run(() => CreateAsync(RootFor(root, "among-us"), saves));
            Load(vm);
            File.WriteAllText(Path.Combine(saves, "save.dat"), "changed");

            vm.RestoreCommand.Execute(vm.Items[0]);
            vm.CancelRestoreCommand.Execute(null);

            Assert.Null(vm.PendingRestore);
            Assert.Equal("changed", File.ReadAllText(Path.Combine(saves, "save.dat")));
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void PruneWithoutConsentNeverReachesTheServiceOrDeletesAnything()
    {
        var (root, saves) = Fixture();
        try
        {
            var backupRoot = RootFor(root, "among-us");
            for (var i = 0; i < 5; i++) UiTest.Run(() => CreateAsync(backupRoot, saves));
            var vm = Page(root, "among-us");
            Load(vm);
            Assert.Equal(5, vm.Items.Count);

            Assert.False(vm.CanPrune);
            vm.PruneCommand.Execute(null);
            Assert.False(vm.IsPruneOpen);
            UiTest.Run(() => vm.PruneConfirmedCommand.ExecuteAsync(null));
            Assert.Empty(vm.StatusMessage);

            // Even a forced confirmation is a no-op while the consent box is unticked.
            vm.IsPruneOpen = true;
            UiTest.Run(() => vm.PruneConfirmedCommand.ExecuteAsync(null));
            var left = UiTest.Run(() => new SaveBackupService().ListBackupsAsync(backupRoot,
                Path.GetFileName(Directory.GetDirectories(backupRoot)[0])));
            Assert.Equal(5, left.Count);
            Assert.Empty(vm.StatusMessage);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void PruneWithConsentKeepsTheThreeNewestCopies()
    {
        var (root, saves) = Fixture();
        try
        {
            var backupRoot = RootFor(root, "among-us");
            for (var i = 0; i < 5; i++) UiTest.Run(() => CreateAsync(backupRoot, saves));
            var vm = Page(root, "among-us");
            Load(vm);

            vm.PruneConsent = true;
            Assert.True(vm.CanPrune);
            vm.PruneCommand.Execute(null);
            Assert.True(vm.IsPruneOpen);
            Assert.Contains("nie da się odzyskać", vm.PruneQuestion);

            UiTest.Run(() => vm.PruneConfirmedCommand.ExecuteAsync(null));

            Assert.True(vm.Items.Count == 3, "count=" + vm.Items.Count + " raw=" + string.Join("|", Directory.GetDirectories(SaveBackupsViewModel.RootsFor(root, "among-us")[0]).SelectMany(Directory.GetDirectories)));
            Assert.Contains("2", vm.StatusMessage);
            Assert.False(vm.PruneConsent);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void ACopyWithoutFilesIsListedWithNoFileCount()
    {
        var (root, _) = Fixture();
        try
        {
            var backupRoot = RootFor(root, "among-us");
            // Over the limit the lifecycle records a profile instead of failing the install:
            // paths only, no file contents.
            var huge = Path.Combine(Directory.CreateTempSubdirectory("phaket-savebackup-huge-").FullName, "save.dat");
            File.WriteAllBytes(huge, new byte[4096]);
            try
            {
                UiTest.Run(() => SaveBackupService.RecordAsync(new SaveBackupRequest("Among Us", "install-1", [huge]),
                    backupRoot, SaveBackupService.DefaultLimitBytes));
            }
            finally { Delete(Path.GetDirectoryName(huge)!); }
            var vm = Page(root, "among-us");

            Load(vm);

            Assert.Single(vm.Items);
            // A copy without file contents still restores as "remove what the recipe wrote":
            // it is listed, and says it holds no files instead of a file count of zero.
            Assert.Empty(vm.Items[0].Manifest.Files);
            Assert.Equal(LocalizationService.Instance["SaveBackup.ProfileOnly"], vm.Items[0].CopiesHint);
            Assert.Equal(LocalizationService.Instance["SaveBackup.ProfileOnly"], vm.Items[0].CopiesHint);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public void TheGamePageReadsTheKeyedStateRootOfItsOwnGame()
    {
        var root = Directory.CreateTempSubdirectory("phaket-savebackup-page-").FullName;
        try
        {
            var expected = RootFor(root, "among-us");
            Directory.CreateDirectory(expected);
            var game = new GameCatalogItem(new GameDefinition("among-us", []), "", "");
            var page = new GameViewModel(game, () => { }, appDataRoot: root);
            Assert.Equal([expected], SaveBackupsViewModel.RootsFor(root, page.Game.Definition.Slug));
            Assert.True(page.SaveBackups.IsEmpty);
        }
        finally { Delete(root); }
    }

    // The game page owns the section; it must render both the empty state and a populated list.
    [AvaloniaFact]
    public void TheGamePageRendersTheSectionWithAndWithoutCopies()
    {
        var root = Directory.CreateTempSubdirectory("phaket-savebackup-render-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "profile", "saveData"));
            File.WriteAllText(Path.Combine(root, "profile", "saveData", "save.dat"), "save bytes");
            var game = new GameCatalogItem(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"), "", "");
            var page = new GameViewModel(game, () => { }, appDataRoot: root);
            var window = new Window { Width = 1280, Height = 800, Content = new Phaket.Views.GameView { DataContext = page } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Save(window, "46-save-backups-empty.png");

            UiTest.Run(() => new SaveBackupService().CreateBackupAsync(
                new SaveBackupRequest("Among Us", "installation-1", [Path.Combine(root, "profile", "saveData")]),
                SaveBackupsViewModel.RootsFor(root, "among-us").FirstOrDefault()
                    ?? Path.Combine(root, "state", "among-us", "installation-1", "save-backups"),
                SaveBackupService.DefaultLimitBytes));
            UiTest.Run(() => page.SaveBackups.LoadCommand.ExecuteAsync(null));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(page.SaveBackups.Items);
            Save(window, "47-save-backups-list.png");

            page.SaveBackups.RestoreCommand.Execute(page.SaveBackups.Items[0]);
            Dispatcher.UIThread.RunJobs();
            Save(window, "48-save-backups-restore-question.png");
            Assert.NotNull(page.SaveBackups.PendingRestore);
            window.Close();
        }
        finally { Delete(root); }
    }

    private static void Save(Window window, string name)
    {
        var dir = RenderArtifacts.Directory;
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(RenderArtifacts.Path(name), PngBitmapEncoderOptions.Default);
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}