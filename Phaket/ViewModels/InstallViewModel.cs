using System.IO.Compression;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Execution;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class InstallItemViewModel(string key, string number) : ViewModelBase
{
    public string Key { get; } = key;
    public string Number { get; } = number;
    [ObservableProperty] private bool isComplete;
    [ObservableProperty] private bool isActive;
}

public partial class InstallViewModel : ViewModelBase
{
    private readonly GameCatalogItem game;
    private readonly Action back;
    private CancellationTokenSource? cancellation;
    private PlanExecutor? executor;
    private string? profileRoot;
    private string? sandboxRoot;
    private ExecutionJournal? journal;

    public InstallViewModel(GameCatalogItem game, Action back)
    {
        this.game = game;
        this.back = back;
        UpdateSteps();
    }

    public InstallItemViewModel[] Steps { get; } = [new("Install.Step1", "1"), new("Install.Step2", "2"), new("Install.Step3", "3"), new("Install.Step4", "4")];
    public InstallItemViewModel[] Checklist { get; } = [new("Install.VerifyVersion", ""), new("Install.CheckDeps", ""), new("Install.AnalyzeConflicts", ""), new("Install.Prepare", "")];
    [ObservableProperty] private int currentStep = 2;
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private string statusMessage = "";
    public string ProgressPercent => $"{Progress:P0}";
    public bool CanRollback => !IsRunning && journal is not null;
    public bool CanStart => !IsRunning && journal is null;
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressPercent));
    partial void OnCurrentStepChanged(int value) => UpdateSteps();
    partial void OnIsRunningChanged(bool value) { OnPropertyChanged(nameof(CanRollback)); OnPropertyChanged(nameof(CanStart)); }
    private void UpdateSteps()
    {
        for (var i = 0; i < Steps.Length; i++) { Steps[i].IsComplete = i + 1 < CurrentStep; Steps[i].IsActive = i + 1 == CurrentStep; }
    }

    [RelayCommand] private void Back() { if (!IsRunning) { CleanupSandbox(); back(); } }
    [RelayCommand] private void Cancel() { if (IsRunning) cancellation?.Cancel(); else { CleanupSandbox(); back(); } }

    private void CleanupSandbox()
    {
        if (sandboxRoot is null) return;
        try { if (Directory.Exists(sandboxRoot)) Directory.Delete(sandboxRoot, recursive: true); }
        catch { /* best effort: temp sandbox cleanup */ }
        sandboxRoot = null; profileRoot = null; executor = null; journal = null;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart) return;
        CleanupSandbox();
        IsRunning = true; IsDone = false; CurrentStep = 2; Progress = 0; StatusMessage = "";
        foreach (var item in Checklist) { item.IsComplete = false; item.IsActive = false; }
        Checklist[0].IsActive = true;
        cancellation = new CancellationTokenSource();
        try
        {
            var token = cancellation.Token;
            await Task.Run(() =>
            {
                var definition = GameDefinitions.LoadAll().First(g => g.Slug == game.Definition.Slug);
                token.ThrowIfCancellationRequested();
                var root = Directory.CreateTempSubdirectory("phaket-install-").FullName;
                sandboxRoot = root;
                var gameRoot = Path.Combine(root, "game");
                profileRoot = Path.Combine(root, "profile");
                var staging = Path.Combine(root, "staging");
                var packages = Path.Combine(root, "packages");
                Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
                Directory.CreateDirectory(profileRoot); Directory.CreateDirectory(staging); Directory.CreateDirectory(packages);
                using (var zip = ZipFile.Open(Path.Combine(packages, "sample.zip"), ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("sample.txt").Open())) writer.Write("Phaket sandbox fixture");
                static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] args) => args.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
                var recipe = new Recipe
                {
                    SchemaVersion = "omrl/v0alpha1", Id = "sandbox.sample", Version = "1.0.0",
                    Target = new RecipeTarget { Game = definition.Slug, Systems = ["linux", "windows"] },
                    Requires = new RecipeRequires { Capabilities = ["archive.extract", "file.copy"] },
                    Artifacts = [new RecipeArtifact { Id = "sample", Source = "package://sample.zip" }],
                    Steps = [new RecipeStep { Id = "extract", Use = "archive.extract", With = Args(("from", "package://sample"), ("to", "staging://sample")) },
                        new RecipeStep { Id = "copy", Use = "file.copy", With = Args(("from", "staging://sample/sample.txt"), ("to", "game://BepInEx/plugins/sample.txt")) }]
                };
                var facts = new PlanFacts(OperatingSystem.IsWindows() ? "windows" : "linux", "steam", null, OperationCatalog.Capabilities, _ => false);
                var plan = OperationPlanner.Plan(recipe, new PlannerContext("sandbox", "sandbox", gameRoot, profileRoot, staging, packages, definition, facts));
                token.ThrowIfCancellationRequested();
                executor = new PlanExecutor(new PlanExecutionContext(gameRoot, profileRoot, staging, packages, definition, recipe.Id, "sandbox", Path.Combine(root, "state")));
                var reporter = new Progress<InstallProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    if (!IsRunning) return;
                    CurrentStep = 3; Progress = p.Fraction;
                    foreach (var item in Checklist) { item.IsComplete = true; item.IsActive = false; }
                }));
                journal = new InstallOrchestrator(executor).Run(plan, reporter, token);
            }, token);
            Progress = 1; CurrentStep = 4; IsDone = true;
            foreach (var item in Checklist) { item.IsComplete = true; item.IsActive = false; }
            StatusMessage = LocalizationService.Instance["Install.Done"];
        }
        catch (OperationCanceledException) { StatusMessage = LocalizationService.Instance["Install.Cancel"]; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally
        {
            try { if (journal is null && profileRoot is not null) journal = PlanExecutor.LoadJournal(profileRoot); }
            catch (Exception ex) { StatusMessage = ex.Message; }
            foreach (var item in Checklist) item.IsActive = false;
            cancellation.Dispose(); cancellation = null; IsRunning = false;
            OnPropertyChanged(nameof(CanRollback)); OnPropertyChanged(nameof(CanStart));
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (!CanRollback || executor is null || journal is null) return;
        IsRunning = true;
        try
        {
            await Task.Run(() => executor.Rollback(journal));
            journal = null; IsDone = false; CurrentStep = 2; Progress = 0; StatusMessage = "";
            foreach (var item in Checklist) { item.IsComplete = false; item.IsActive = false; }
            CleanupSandbox();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsRunning = false; OnPropertyChanged(nameof(CanRollback)); OnPropertyChanged(nameof(CanStart)); }
    }
}
