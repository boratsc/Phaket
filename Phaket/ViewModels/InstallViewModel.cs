using System.IO.Compression;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class InstallItemViewModel(string key, string number) : ViewModelBase
{
    public string Key { get; } = key;
    public string Number { get; } = number;
    [ObservableProperty] private bool isComplete;
    [ObservableProperty] private bool isActive;
}

// A recipe offered for the current game, with its localized display title.
public sealed class RecipeOption(Recipe recipe, string title)
{
    public Recipe Recipe { get; } = recipe;
    public string Title { get; } = title;
    public override string ToString() => Title;
}

// A selectable game version: "latest" or a concrete catalog build with its
// publisher release date (the backend discovers both from public sources).
public sealed class VersionOption(string value, string label)
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

// Install stepper (mockup 10): recipe -> game version -> dry plan -> execute.
// Real recipes come from the shipped RecipeLibrary; the base game is provisioned
// by official tooling (DepotDownloader/legendary) through ToolBaseGameSource.
// Default target is a SANDBOX; installing into a detected game install requires
// an explicit opt-in checkbox (consent), never implicit.
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
        LoadOptions();
        UpdateSteps();
    }

    private static string AppDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");
    private static string CachePath => Path.Combine(AppDataRoot, "catalog.db");

    public InstallItemViewModel[] Steps { get; } = [new("Install.Step1", "1"), new("Install.Step2", "2"), new("Install.Step3", "3"), new("Install.Step4", "4")];
    public InstallItemViewModel[] Checklist { get; } = [new("Install.VerifyVersion", ""), new("Install.CheckDeps", ""), new("Install.AnalyzeConflicts", ""), new("Install.Prepare", "")];
    public System.Collections.ObjectModel.ObservableCollection<RecipeOption> Recipes { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<VersionOption> Versions { get; } = [];
    [ObservableProperty] private RecipeOption? selectedRecipe;
    [ObservableProperty] private VersionOption? selectedVersion;
    [ObservableProperty] private bool useDetectedInstall;
    [ObservableProperty] private int currentStep = 2;
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private string statusMessage = "";
    public string ProgressPercent => $"{Progress:P0}";
    public bool CanRollback => !IsRunning && journal is not null;
    public bool CanStart => !IsRunning && journal is null && SelectedRecipe is not null && SelectedVersion is not null;
    public bool HasDetectedInstall => game.IsInstalled;
    public string DetectedRoot => game.RootPath;
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressPercent));
    partial void OnCurrentStepChanged(int value) => UpdateSteps();
    partial void OnIsRunningChanged(bool value) { OnPropertyChanged(nameof(CanRollback)); OnPropertyChanged(nameof(CanStart)); }
    partial void OnSelectedRecipeChanged(RecipeOption? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnSelectedVersionChanged(VersionOption? value) => OnPropertyChanged(nameof(CanStart));
    private void UpdateSteps()
    {
        for (var i = 0; i < Steps.Length; i++) { Steps[i].IsComplete = i + 1 < CurrentStep; Steps[i].IsActive = i + 1 == CurrentStep; }
    }

    private void LoadOptions()
    {
        var loc = LocalizationService.Instance;
        try
        {
            foreach (var recipe in RecipeLibrary.ForGame(game.Definition.Slug))
            {
                var title = recipe.Metadata?.Title?.GetValueOrDefault(loc.CurrentLanguage)
                    ?? recipe.Metadata?.Title?.GetValueOrDefault("en") ?? recipe.Id;
                Recipes.Add(new RecipeOption(recipe, $"{title} {recipe.Version}"));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: recipe load failed: {ex}");
            StatusMessage = loc["Install.RecipeLoadError"];
        }
        SelectedRecipe = Recipes.FirstOrDefault();

        Versions.Add(new VersionOption("latest", loc["Install.VersionLatest"]));
        try
        {
            using var store = new CatalogStore(CachePath);
            foreach (var build in store.GetBuilds(game.Definition.Slug, "steam")
                .Where(b => b.AvailabilityStatus is "known" or "verified" or "downloadable")
                .OrderByDescending(b => b.ReleasedAt ?? b.ObservedAt ?? DateTimeOffset.MinValue))
                Versions.Add(new VersionOption(build.VersionLabel,
                    $"{build.VersionLabel} · {loc["Builds.Released"]} {build.ReleasedText}"));
        }
        catch (Exception) { /* offline: only "latest" remains, resolved later */ }
        SelectedVersion = Versions.FirstOrDefault();
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
        if (!CanStart || SelectedRecipe is null || SelectedVersion is null) return;
        CleanupSandbox();
        IsRunning = true; IsDone = false; CurrentStep = 2; Progress = 0; StatusMessage = "";
        foreach (var item in Checklist) { item.IsComplete = false; item.IsActive = false; }
        Checklist[0].IsActive = true;
        cancellation = new CancellationTokenSource();
        // Snapshot consent/target on the UI thread: the worker must never observe
        // a checkbox the user toggled after Start was pressed.
        var detectedRoot = UseDetectedInstall && game.IsInstalled
            ? game.Detected!.Installation.RootPath : null;
        try
        {
            var token = cancellation.Token;
            var recipe = SelectedRecipe.Recipe;
            var store = "steam";
            var versionChoice = SelectedVersion.Value;
            await Task.Run(() =>
            {
                var definition = GameDefinitions.LoadAll().First(g => g.Slug == game.Definition.Slug);
                token.ThrowIfCancellationRequested();

                // Resolve the version choice against the local catalog cache.
                string? resolved = versionChoice;
                using (var catalog = new CatalogStore(CachePath))
                {
                    if (versionChoice == "latest")
                        resolved = catalog.GetLatestBuild(game.Definition.Slug, store)?.VersionLabel;
                    else if (catalog.GetBuilds(game.Definition.Slug, store).All(b => b.VersionLabel != versionChoice))
                        resolved = null; // stale selection after a catalog refresh
                }
                if (resolved is null)
                    throw new PhaketException(ErrorCodes.GameBuildUnavailable);
                var gameVersion = resolved;

                var root = Directory.CreateTempSubdirectory("phaket-install-").FullName;
                sandboxRoot = root;
                var gameRoot = detectedRoot ?? Path.Combine(root, "game");
                profileRoot = Path.Combine(root, "profile");
                var staging = Path.Combine(root, "staging");
                var packages = Path.Combine(AppDataRoot, "packages");
                var state = Path.Combine(AppDataRoot, "state", game.Definition.Slug);
                if (detectedRoot is null)
                {
                    Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
                    File.WriteAllText(Path.Combine(gameRoot, definition.MainExecutable ?? "game.exe"), "sandbox fixture");
                }
                Directory.CreateDirectory(profileRoot); Directory.CreateDirectory(staging); Directory.CreateDirectory(packages); Directory.CreateDirectory(state);

                var facts = new PlanFacts(OperatingSystem.IsWindows() ? "windows" : "linux", store, gameVersion,
                    OperationCatalog.Capabilities,
                    uri => VirtualUri.TryParse(uri, out var scheme, out var path)
                        && scheme == "game" && File.Exists(Path.Combine(gameRoot, path)),
                    new Dictionary<string, string> { ["bepinex"] = "6.0.700" });
                var context = new PlannerContext("install", "install-profile", gameRoot, profileRoot, staging, packages,
                    definition, facts,
                    LatestGameVersion: s =>
                    {
                        using var catalog = new CatalogStore(CachePath);
                        return catalog.GetLatestBuild(game.Definition.Slug, s)?.VersionLabel;
                    });
                var plan = OperationPlanner.Plan(recipe, context);
                token.ThrowIfCancellationRequested();
                executor = new PlanExecutor(new PlanExecutionContext(gameRoot, profileRoot, staging, packages,
                    definition, recipe.Id, "install-profile", state, new ToolBaseGameSource()));
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
        catch (PhaketException ex) { StatusMessage = ex.LocalizedMessage(LocalizationService.Instance.CurrentLanguage); }
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
