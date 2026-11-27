using Avalonia.Headless.XUnit;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// R3d #7: the save copy is best-effort, so the catalog must say when this install ran without
// one — and must never hint that an older copy protects the current run.
public class PakR3dSaveBackupWarningTests
{
    private static readonly InstallTarget Target = new("", "steam", true);

    private static InstallPreparation Preparation(string sandbox) => new(
        "among-us", "Owner-Mod", "2.0", "http://localhost", Target,
        [new OperationPlan("1", new RecipeRef("mod", "2"), "i", "p",
            [new PlannedOperation("op", "copy", "game://plugins/Mod.dll", null, null, null, [], "", "", "", [])], [], [], false, [])],
        [], [], [new InstallPreview("Owner-Mod", "2.0", ["plugins/Mod.dll"], 42)],
        sandbox, Path.Combine(sandbox, "profile"), Path.Combine(sandbox, "staging"), Path.Combine(sandbox, "state"), "");

    // A manifest the backup list accepts: no file rows and no state hash is a profile record,
    // which is never reported as a copy. One row plus its hash is a real copy.
    private static SaveBackupManifest Manifest(string id, string schema = SaveBackupManifest.CurrentSchemaVersion)
    {
        var files = schema == SaveBackupManifest.CurrentSchemaVersion
            ? new[] { new SaveBackupFile("Player.log", 2, DateTimeOffset.UnixEpoch, "sha256:0000") }
            : Array.Empty<SaveBackupFile>();
        return new(schema, id, DateTimeOffset.UnixEpoch, "Among Us", "profile-1", [], files, 2,
            SaveBackupManifest.ComputeStateHash(id, DateTimeOffset.UnixEpoch, "Among Us", "profile-1", [], files, 2));
    }


    private static void WriteBackup(string stateRoot, string id, string schema = SaveBackupManifest.CurrentSchemaVersion)
    {
        var folder = Path.Combine(stateRoot, "save-backups", "Among Us");
        var directory = Path.Combine(folder, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), SaveBackupManifest.Format(Manifest(id, schema)));
    }

    private static async Task<string> RunInstallAsync(string root)
    {
        var vm = new ModCatalogViewModel("among-us", root, autoSync: false) { Endpoint = "http://127.0.0.1:1" };
        vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation(Path.Combine(root, "sandbox")) with { StateRoot = Path.Combine(root, "sandbox", "state") });
        vm.ExecuteOverride = (_, _, _, _) => Task.FromResult(new InstallOutcome(true, null, []));
        await vm.BeginInstallAsync("Owner-Mod", "2.0", Target);
        vm.PlanAccepted = true;
        await vm.ExecuteInstallCommand.ExecuteAsync(null);
        return vm.InstallStatus;
    }

    [AvaloniaFact]
    public async Task FailedCopyIsAnnouncedWhenNoCopyExistedBefore()
    {
        var root = Directory.CreateTempSubdirectory("phaket-r3d-warn-").FullName;
        try
        {
            var previous = LocalizationService.Instance.CurrentLanguage;
            try
            {
                LocalizationService.Instance.CurrentLanguage = "pl-PL";
                var status = await RunInstallAsync(root);
                Assert.Contains(LocalizationService.Instance["SaveBackup.NotCopiedWarning"], status, StringComparison.Ordinal);
                Assert.DoesNotContain(LocalizationService.Instance["SaveBackup.InstallFailedHint"], status, StringComparison.Ordinal);
            }
            finally { LocalizationService.Instance.CurrentLanguage = previous; }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task FailedCopyPointsAtTheOlderCopyOnlyAsOlder()
    {
        var root = Directory.CreateTempSubdirectory("phaket-r3d-older-").FullName;
        try
        {
            WriteBackup(Path.Combine(root, "sandbox", "state"), "older-copy");
            var previous = LocalizationService.Instance.CurrentLanguage;
            try
            {
                LocalizationService.Instance.CurrentLanguage = "en";
                var status = await RunInstallAsync(root);
                var loc = LocalizationService.Instance;
                Assert.Contains(loc["SaveBackup.OlderCopyHint"], status, StringComparison.Ordinal);
                Assert.DoesNotContain(loc["SaveBackup.InstallFailedHint"], status, StringComparison.Ordinal);
            }
            finally { LocalizationService.Instance.CurrentLanguage = previous; }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task AFreshCopyIsAnnouncedAsThisRunsCopy()
    {
        var root = Directory.CreateTempSubdirectory("phaket-r3d-fresh-").FullName;
        try
        {
            var vm = new ModCatalogViewModel("among-us", root, autoSync: false) { Endpoint = "http://127.0.0.1:1" };
            var preparation = Preparation(Path.Combine(root, "sandbox")) with { StateRoot = Path.Combine(root, "sandbox", "state") };
            vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(preparation);
            // The executor's save backup lands under the transaction's state root.
            vm.ExecuteOverride = (p, _, _, _) => { WriteBackup(p.StateRoot, "fresh-copy"); return Task.FromResult(new InstallOutcome(true, null, [])); };
            await vm.BeginInstallAsync("Owner-Mod", "2.0", Target);
            vm.PlanAccepted = true;
            await vm.ExecuteInstallCommand.ExecuteAsync(null);

            var loc = LocalizationService.Instance;
            Assert.EndsWith(loc["SaveBackup.InstallFailedHint"], vm.InstallStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(loc["SaveBackup.NotCopiedWarning"], vm.InstallStatus, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }
}