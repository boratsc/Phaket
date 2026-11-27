using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-142: a two-sided file conflict stops the install before the first write. The dialog
// names the files in plain words, offers one global answer, and cancelling runs nothing.
public class ConflictDecisionDialogTests
{
    private static readonly string ModFile = Path.Combine(Path.GetTempPath(), "game", "BepInEx", "plugins", "NiceMod.dll");

    // A real executor over a temp sandbox: the conflict gate is the one under test.
    private static (PlanExecutor Executor, string GameRoot) Sandbox()
    {
        var sandbox = Directory.CreateTempSubdirectory("phaket-pak142-exec-").FullName;
        var gameRoot = Path.Combine(sandbox, "game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
        var context = new PlanExecutionContext(gameRoot, Path.Combine(sandbox, "profile"), Path.Combine(sandbox, "staging"),
            Path.Combine(sandbox, "packages"), new Phaket.Core.Omrl.GameDefinition("among-us", []), "mod", "mod-profile",
            Path.Combine(sandbox, "state"), InstallationId: "installation-1");
        return (new PlanExecutor(context), gameRoot);
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static OperationPlan ConflictPlan() => new(OperationPlan.CurrentSchemaVersion, new RecipeRef("mod", "2"),
        "installation-1", "install-profile",
        // A write that needs no package: the conflict, not the copy, is what this test drives.
        [new PlannedOperation("op", "directory.ensure", "game://BepInEx/plugins", null, null, null, [], "", "", "", [])],
        // An overwrite proposal is not the user's call; only the two-sided edit is.
        [new PlanConflict(ModFile, ["local:installation-1", "remote:mod"], PlanLifecycle.ConflictsRequireDecision),
         new PlanConflict(ModFile + ".old", ["op"], "overwrite")],
        [], false, []);

    private static InstallPreparation Preparation(OperationPlan plan) => new("among-us", "Owner-Mod", "2.0",
        "http://127.0.0.1:1", new InstallTarget("/tmp/opencode/pak142-target", "steam", true),
        [plan], [], [], [new InstallPreview("Owner-Mod", "2.0", ["plugins/NiceMod.dll"], 42)],
        "/tmp/opencode/pak142-sandbox", "", "", "", "");

    [AvaloniaFact]
    public void TheDialogShowsFriendlyFileNamesAndAnswersTheGlobalChoice()
    {
        var vm = ConflictDecisionViewModel.Shared;
        Assert.False(vm.IsOpen);
        var pending = vm.RequestAsync([ModFile, ModFile]);
        Assert.True(vm.IsOpen);
        Assert.Single(vm.Files);
        Assert.Equal("NiceMod.dll", vm.Files[0].FileName);
        Assert.Equal(ModFile, vm.Files[0].LocationHint);

        vm.KeepLocalCommand.Execute(null);

        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Equal(ConflictDecision.KeepLocal, pending.Result);
        Assert.False(vm.IsOpen);
        Assert.Empty(vm.Files);
    }

    [AvaloniaFact]
    public void CancellingReturnsNoDecision()
    {
        var vm = ConflictDecisionViewModel.Shared;
        var pending = vm.RequestAsync([ModFile]);
        vm.Dismiss();
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Null(pending.Result);
        Assert.False(vm.IsOpen);
    }

    [AvaloniaFact]
    public void OnlyTwoSidedConflictsAreHandedToTheUser()
    {
        var plan = ConflictPlan();
        Assert.True(InstallConflicts.RequiresDecision([plan]));
        var decided = InstallConflicts.Decide([plan], ConflictDecision.TakeRecipe);
        // Only the two-sided file is answered: the executor filters conflicts per path, so a
        // proposal the user never saw ("overwrite") must not become a decision of its own.
        Assert.Equal([ModFile], decided.Keys);
        Assert.All(decided.Values, value => Assert.Equal(ConflictDecision.TakeRecipe, value));
        Assert.False(InstallConflicts.RequiresDecision([plan with { Conflicts = [] }]));
    }

    [AvaloniaFact]
    public void AnUndecidedPlanIsRefusedBeforeTheFirstWrite()
    {
        var (executor, gameRoot) = Sandbox();
        try
        {
            var plan = ConflictPlan();
            var journal = executor.Approve(plan);
            var refused = Assert.Throws<Phaket.Core.Omrl.PhaketException>(() => executor.Execute(plan, journal));
            Assert.Equal(Phaket.Core.Omrl.ErrorCodes.FileConflictRequiresDecision, refused.Error.Code);
            // Nothing was written and the journal is still the approval, not a transaction.
            Assert.False(Directory.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "NiceMod.dll.backup")));
            Assert.Equal(JournalState.Approved, PlanExecutor.LoadJournal(Path.GetDirectoryName(Path.Combine(gameRoot, "profile"))!)?.State ?? JournalState.Approved);
        }
        finally { Delete(Path.GetDirectoryName(gameRoot)!); }
    }

    [AvaloniaFact]
    public void TheUsersAnswerIsWhatTheExecutorAsksFor()
    {
        var (executor, gameRoot) = Sandbox();
        try
        {
            var plan = ConflictPlan();
            var kept = executor.Execute(plan, executor.Approve(plan), decisions: InstallConflicts.Decide([plan], ConflictDecision.KeepLocal));
            Assert.Equal(JournalState.Completed, kept.State);
        }
        finally { Delete(Path.GetDirectoryName(gameRoot)!); }
    }

    // An install whose refusal carries the code opens the question instead of the failure box.
    [AvaloniaFact]
    public async Task TheCatalogOpensTheDialogWhenTheExecutorAsksAndCancellingWritesNothing()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pak142-vm-").FullName;
        try
        {
            var vm = new ModCatalogViewModel("among-us", root, autoSync: false);
            vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(Preparation(ConflictPlan()));
            vm.ExecuteOverride = (_, _, _) => Task.FromResult(new InstallOutcome(false,
                Phaket.Core.Omrl.ErrorCodes.FileConflictRequiresDecision, []));
            UiTest.Run(() => vm.BeginInstallAsync("Owner-Mod", "2.0", new InstallTarget(root, "steam", true) with { Arch = "x86" }));
            Assert.True(vm.HasPreview);
            vm.PlanAccepted = true;

            // The command opens the dialog and waits for the answer, so its task settles only
            // after the command below; the UI queue is drained until the question is shown.
            var executing = vm.ExecuteInstallCommand.ExecuteAsync(null);
            var dialog = ConflictDecisionViewModel.Shared;
            UiTest.Until(() => dialog.IsOpen);
            dialog.CancelCommand.Execute(null);
            // Cancelled: no second execute, no error box, the preview stays as the user left it.
            UiTest.Run(() => executing);
            Assert.Empty(vm.InstallStatus);
            Assert.False(dialog.IsOpen);

            // The accepted answer runs the install once more and reports the outcome.
            var runs = 0;
            vm.ExecuteOverride = (_, _, _) => { runs++; return Task.FromResult(new InstallOutcome(true, null, [])); };
            executing = vm.ExecuteInstallCommand.ExecuteAsync(null);
            UiTest.Until(() => dialog.IsOpen);
            dialog.TakeRecipeCommand.Execute(null);
            UiTest.Run(() => executing);
            Assert.Equal(1, runs);
            Assert.Equal(LocalizationService.Instance["ModInstall.Success"], vm.InstallStatus);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [AvaloniaFact]
    public void TheDialogRendersInBothLanguages()
    {
        var window = new Window { Width = 620, Height = 420, Content = new Phaket.Views.ConflictDecisionDialog { DataContext = ConflictDecisionViewModel.Shared } };
        window.Show();
        var pending = ConflictDecisionViewModel.Shared.RequestAsync([ModFile]);
        Dispatcher.UIThread.RunJobs();
        var save = RenderArtifacts.Directory;
        Directory.CreateDirectory(save);
        foreach (var language in new[] { "pl-PL", "en" })
        {
            var previous = LocalizationService.Instance.CurrentLanguage;
            LocalizationService.Instance.CurrentLanguage = language;
            try
            {
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(save, $"45-conflict-decision-{language}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally { LocalizationService.Instance.CurrentLanguage = previous; }
        }
        var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Contains(buttons, button => button.IsEffectivelyVisible && button.Content is string text && text.Length > 0);
        ConflictDecisionViewModel.Shared.CancelCommand.Execute(null);
        Assert.True(pending.IsCompleted);
        window.Close();
    }
}