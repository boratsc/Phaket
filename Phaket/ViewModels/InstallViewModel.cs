using System.IO.Compression;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Catalog;
using Phaket.Core.Execution;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
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
public sealed class VersionOption(string value, string label, string? arch = null)
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    // Architecture the catalog observed for this build: a fact, not a guess.
    public string? Arch { get; } = arch;
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
    private readonly string appDataRoot;
    private readonly IPackageTransport? transport;
    private string? installedGameRoot;
    private string? installedRecipeId;

    public InstallViewModel(GameCatalogItem game, Action back)
        : this(game, back, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket")) { }

    internal InstallViewModel(GameCatalogItem game, Action back, string appDataRoot, IPackageTransport? transport = null)
    {
        this.appDataRoot = appDataRoot;
        this.transport = transport;
        this.game = game;
        this.back = back;
        LoadOptions();
        UpdateSteps();
    }

    private string CachePath => Path.Combine(appDataRoot, "catalog.db");
    private InstallRegistry Registry => new(Path.Combine(appDataRoot, "installs.json"));

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
                    $"{build.VersionLabel} · {loc["Builds.Released"]} {build.ReleasedText}", build.Arch));
        }
        catch (Exception) { /* offline: only "latest" remains, resolved later */ }
        SelectedVersion = Versions.FirstOrDefault();
    }

    [RelayCommand] private void Back() { if (!IsRunning && (IsDone || CleanupFailedInstall())) back(); }
    [RelayCommand] private void Cancel() { if (IsRunning) cancellation?.Cancel(); else if (IsDone || CleanupFailedInstall()) back(); }

    private bool CleanupFailedInstall()
    {
        try { if (executor is not null && journal is not null) executor.Rollback(journal); }
        catch (Exception ex) { StatusMessage = ex.Message; return false; }
        CleanupSandbox();
        return true;
    }

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
        if (!CleanupFailedInstall()) return;
        IsRunning = true; IsDone = false; CurrentStep = 2; Progress = 0; StatusMessage = "";
        foreach (var item in Checklist) { item.IsComplete = false; item.IsActive = false; }
        Checklist[0].IsActive = true;
        cancellation = new CancellationTokenSource();
        // Snapshot consent/target on the UI thread: the worker must never observe
        // a checkbox the user toggled after Start was pressed.
        var installation = UseDetectedInstall ? game.SelectedInstallation : null;
        var detectedRoot = installation?.RootPath;
        try
        {
            var token = cancellation.Token;
            var recipe = SelectedRecipe.Recipe;
            var store = installation?.Store ?? "steam";
            var versionChoice = SelectedVersion.Value;
            var usedVersionFallback = false;
            await Task.Run(async () =>
            {
                var definition = GameDefinitions.LoadAll().First(g => g.Slug == game.Definition.Slug);
                token.ThrowIfCancellationRequested();

                // Resolve the version choice against the local catalog cache.
                CatalogBuild? selectedBuild;
                using (var catalog = new CatalogStore(CachePath))
                {
                    selectedBuild = versionChoice == "latest"
                        ? catalog.GetLatestBuild(game.Definition.Slug, store)
                        : catalog.GetBuilds(game.Definition.Slug, store).FirstOrDefault(b => b.VersionLabel == versionChoice);
                }
                // PAK-66: the facts a recipe conditions on must all come from the SAME build.
                // A catalog build is provably the installed game only when both know a build id
                // and it matches, so an explicit choice that cannot be matched to the detected
                // installation is refused instead of lending it a version and a manifest. With
                // "latest" nothing is claimed about the installed game at all: the installation
                // then supplies every fact and an unknown version is refused by the planner.
                var installedBuild = ParseBuildId(installation?.BuildId);
                var sameAsInstalled = selectedBuild is not null && installedBuild is not null
                    && ParseBuildId(selectedBuild.BuildId) == installedBuild;
                if (detectedRoot is not null && versionChoice != "latest" && selectedBuild is not null && !sameAsInstalled)
                    throw new PhaketException(ErrorCodes.InstallFactsMissing, ("fact", "buildId"));
                var evidence = detectedRoot is null ? selectedBuild : sameAsInstalled ? selectedBuild : null;
                var resolved = evidence?.VersionLabel;
                if (resolved is null && OperationPlanner.RequiresGameVersion(recipe))
                    throw new PhaketException(ErrorCodes.GameBuildUnavailable);
                // Mod-only recipes do not inspect the version; this is a neutral planning fact.
                usedVersionFallback = resolved is null;
                var gameVersion = resolved ?? "0.0.0";

                var root = Directory.CreateTempSubdirectory("phaket-install-").FullName;
                sandboxRoot = root;
                var gameRoot = detectedRoot ?? Path.Combine(root, "game");
                profileRoot = Path.Combine(root, "profile");
                var staging = Path.Combine(root, "staging");
                var packages = Path.Combine(appDataRoot, "packages");
                // Ownership belongs to an installation, not every copy of the same game.
                var rootId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot))));
                var state = Path.Combine(appDataRoot, "state", game.Definition.Slug, rootId);
                if (detectedRoot is null)
                {
                    Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
                    File.WriteAllText(Path.Combine(gameRoot, definition.MainExecutable ?? "game.exe"), "sandbox fixture");
                }
                Directory.CreateDirectory(profileRoot); Directory.CreateDirectory(staging); Directory.CreateDirectory(packages); Directory.CreateDirectory(state);

                // PAK-65/66: conditions need facts, and every fact comes from ONE build.
                // A detected installation is read from its PE header — never guessed from the
                // catalog. A sandbox takes the architecture of the build the user actually
                // selected; the definition default is only the last resort when no build is known.
                // A recipe conditioning on a fact nobody supplied is refused, never silently
                // planned empty.
                var arch = detectedRoot is not null
                    ? InstallationArchProbe.Probe(detectedRoot, definition)
                    : evidence?.Arch ?? definition.DefaultArch;
                var facts = new PlanFacts(HostOs(), store, gameVersion,
                    OperationCatalog.Capabilities,
                    uri => VirtualUri.TryParse(uri, out var scheme, out var path)
                        && scheme == "game" && File.Exists(Path.Combine(gameRoot, path)),
                    new Dictionary<string, string> { ["bepinex"] = "6.0.700" },
                    evidence?.ManifestIds.FirstOrDefault(), arch,
                    installedBuild ?? ParseBuildId(evidence?.BuildId));
                var context = new PlannerContext("install", "install-profile", gameRoot, profileRoot, staging, packages,
                    definition, facts,
                    LatestGameVersion: s =>
                    {
                        using var catalog = new CatalogStore(CachePath);
                        return catalog.GetLatestBuild(game.Definition.Slug, s)?.VersionLabel;
                    });
                var plan = OperationPlanner.Plan(recipe, context);
                // Nothing matched: run no tool and register nothing (PAK-65).
                if (plan.Operations.Count == 0) throw new PhaketException(ErrorCodes.PlanEmpty);
                token.ThrowIfCancellationRequested();
                if (plan.RequiresDownload)
                    await PackageDownloader.EnsureArtifactsAsync(recipe, packages, transport ?? new HttpPackageTransport(),
                        new Progress<ArtifactDownloadProgress>(p => Dispatcher.UIThread.Post(() =>
                        { if (IsRunning) StatusMessage = LocalizationService.Instance["Install.Downloading"] + " " + p.ArtifactId; })), token);
                executor = new PlanExecutor(new PlanExecutionContext(gameRoot, profileRoot, staging, packages,
                    definition, recipe.Id, "install-profile", state, new ToolBaseGameSource()));
                var reporter = new Progress<InstallProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    if (!IsRunning) return;
                    CurrentStep = 3; Progress = p.Fraction;
                    foreach (var item in Checklist) { item.IsComplete = true; item.IsActive = false; }
                }));
                journal = new InstallOrchestrator(executor).Run(plan, reporter, token);
                installedGameRoot = gameRoot; installedRecipeId = recipe.Id;
                Registry.Add(new InstalledRecipe(recipe.Id, recipe.Version, definition.Slug, store, gameRoot, profileRoot,
                    staging, state, detectedRoot is null ? root : null, DateTimeOffset.UtcNow));
            }, token);
            Progress = 1; CurrentStep = 4; IsDone = true;
            foreach (var item in Checklist) { item.IsComplete = true; item.IsActive = false; }
            StatusMessage = LocalizationService.Instance[usedVersionFallback ? "Install.SandboxVersionHint" : "Install.Done"];
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

    private static string HostOs() => OperatingSystem.IsWindows() ? "windows" : "linux";

    // Steam build ids are decimal; an unparsable or missing one is simply no fact.
    static long? ParseBuildId(string? buildId) =>
        long.TryParse(buildId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (!CanRollback || executor is null || journal is null) return;
        IsRunning = true;
        try
        {
            await Task.Run(() => executor.Rollback(journal));
            if (installedRecipeId is not null && installedGameRoot is not null) Registry.Remove(installedRecipeId, installedGameRoot);
            journal = null; IsDone = false; CurrentStep = 2; Progress = 0; StatusMessage = "";
            foreach (var item in Checklist) { item.IsComplete = false; item.IsActive = false; }
            CleanupSandbox();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsRunning = false; OnPropertyChanged(nameof(CanRollback)); OnPropertyChanged(nameof(CanStart)); }
    }
}
