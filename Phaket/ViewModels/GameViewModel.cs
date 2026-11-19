using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using System.Collections.ObjectModel;

namespace Phaket.ViewModels;

// A supported game shown as a library card / game hub. Cover art always present;
// status reflects real local detection. Technical facts live behind "Szczegóły".
public partial class GameCatalogItem : ViewModelBase
{
    public GameCatalogItem(GameDefinition definition, string cover, string hero)
    {
        Definition = definition;
        Cover = cover;
        Hero = hero;
        Installations.CollectionChanged += (_, _) => UpdateAvailability();
    }

    public GameDefinition Definition { get; }
    public string Cover { get; }
    public string Hero { get; }
    public bool HasCover => !string.IsNullOrEmpty(Cover);
    public string Title => Definition.Title?.GetValueOrDefault(LocalizationService.Instance.CurrentLanguage)
        ?? Definition.Title?.GetValueOrDefault("en") ?? Definition.Slug;
    public string SteamAppId => Definition.StoreIds?.GetValueOrDefault("steam") ?? "";
    public bool IsSupported => !Definition.Slug.StartsWith("epic-owned-", StringComparison.Ordinal) && !Definition.Slug.StartsWith("steam-owned-", StringComparison.Ordinal);
    [ObservableProperty] private bool epicOwned;
    [ObservableProperty] private bool steamOwned;
    [ObservableProperty] private string? epicInstallPath;
    public bool EpicInstalled => EpicInstallPath is not null;
    public bool SteamInstalled => Detected is not null && Directory.Exists(Detected.Installation.RootPath);
    public bool CanOpenGame => IsSupported;
    public bool OwnedNotInstalled => (EpicOwned || SteamOwned) && !IsInstalled;
    public bool SupportedOwnedNotInstalled => OwnedNotInstalled && !UnsupportedOwned;
    public bool SteamOwnedNotInstalled => SteamOwned && !SteamInstalled;
    public bool CanInstallSteam => SteamOwnedNotInstalled && IsSupported && !string.IsNullOrWhiteSpace(Definition.MainExecutable);
    public bool ShowSteam => SteamOwned || SteamInstalled;
    public bool EpicOwnedNotInstalled => EpicOwned && !EpicInstalled;
    public bool CanInstallEpic => EpicOwnedNotInstalled && IsSupported && !string.IsNullOrWhiteSpace(Definition.MainExecutable);
    public bool EpicUnsupported => EpicOwned && (!IsSupported || string.IsNullOrWhiteSpace(Definition.MainExecutable));
    public bool UnsupportedOwned => (EpicOwned || SteamOwned) && (!IsSupported || string.IsNullOrWhiteSpace(Definition.MainExecutable));
    public bool NotDetected => !EpicOwned && !SteamOwned && !IsInstalled;

    [ObservableProperty] private SteamInstallation? detected;
    public ObservableCollection<SteamInstallation> Installations { get; } = [];
    public IReadOnlyList<InstallationChoice> AvailableInstallations
    {
        get
        {
            var paths = Installations.AsEnumerable();
            if (Detected is { } detected) paths = paths.Append(detected);
            var choices = paths.Select(i => new InstallationChoice("steam", i.Installation.RootPath, i.Manifest.BuildId))
                .Where(i => Directory.Exists(i.RootPath)).Distinct().ToList();
            if (EpicInstallPath is { } epic && Directory.Exists(epic)) choices.Add(new("epic", epic));
            return choices;
        }
    }
    private InstallationChoice? selectedInstallation;
    public InstallationChoice? SelectedInstallation
    {
        get => AvailableInstallations.FirstOrDefault(i => i == selectedInstallation) ?? AvailableInstallations.FirstOrDefault();
        set
        {
            selectedInstallation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RootPath));
            OnPropertyChanged(nameof(IsSelectedSteam));
            OnPropertyChanged(nameof(InstallationArchitecture));
            OnPropertyChanged(nameof(LauncherLabel));
            OnPropertyChanged(nameof(SelectedBuild));
        }
    }
    public bool IsSelectedSteam => SelectedInstallation?.Store == "steam";
    public string LauncherLabel => SelectedInstallation?.Store switch
    {
        "steam" => "Steam",
        "epic" => "Epic Games",
        _ when SteamOwned => "Steam",
        _ when EpicOwned => "Epic Games",
        _ => LocalizationService.Instance["Library.State.NotDetected"]
    };
    public string SelectedBuild => SelectedInstallation?.BuildId ?? "—";
    [ObservableProperty] private string profileName = "Profil domyślny";
    [ObservableProperty] private int modCount;

    public bool IsInstalled => SteamInstalled || EpicInstalled;
    public string StatusKey => IsInstalled ? "Library.State.Moddable" : "Library.State.NotDetected";
    public string Build => Detected?.Manifest.BuildId ?? "—";
    public string RootPath => SelectedInstallation?.RootPath ?? "—";
    public string InstallationArchitecture => SelectedInstallation is { } installation
        ? InstallationArchProbe.Probe(installation.RootPath, Definition) ?? LocalizationService.Instance["Game.ArchitectureUnknown"]
        : LocalizationService.Instance["Game.ArchitectureUnknown"];

    partial void OnDetectedChanged(SteamInstallation? value)
    {
        OnPropertyChanged(nameof(SteamInstalled));
        UpdateAvailability();
        OnPropertyChanged(nameof(Build));
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(InstallationArchitecture));
    }
    partial void OnEpicOwnedChanged(bool value) => UpdateAvailability();
    partial void OnSteamOwnedChanged(bool value) => UpdateAvailability();
    partial void OnEpicInstallPathChanged(string? value)
    {
        OnPropertyChanged(nameof(EpicInstalled));
        UpdateAvailability();
    }
    private void UpdateAvailability()
    {
        OnPropertyChanged(nameof(AvailableInstallations));
        OnPropertyChanged(nameof(SelectedInstallation));
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(InstallationArchitecture));
        OnPropertyChanged(nameof(IsSelectedSteam));
        OnPropertyChanged(nameof(LauncherLabel));
        OnPropertyChanged(nameof(SelectedBuild));
        OnPropertyChanged(nameof(CanOpenGame));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(OwnedNotInstalled));
        OnPropertyChanged(nameof(SupportedOwnedNotInstalled));
        OnPropertyChanged(nameof(SteamOwnedNotInstalled));
        OnPropertyChanged(nameof(CanInstallSteam));
        OnPropertyChanged(nameof(ShowSteam));
        OnPropertyChanged(nameof(EpicOwnedNotInstalled));
        OnPropertyChanged(nameof(CanInstallEpic));
        OnPropertyChanged(nameof(EpicUnsupported));
        OnPropertyChanged(nameof(UnsupportedOwned));
        OnPropertyChanged(nameof(NotDetected));
        OnPropertyChanged(nameof(StatusKey));
    }
}

public sealed record InstallationChoice(string Store, string RootPath, string? BuildId = null)
{
    public string Label => $"{(Store == "epic" ? "Epic" : "Steam")} — {RootPath}";
}

public partial class GameViewModel : ViewModelBase
{
    private readonly LaunchService launch = new();
    private readonly ProfileStore store = new();
    private readonly Action back;
    private readonly Action openMods;
    private readonly Action openProfiles;
    private readonly Action openBackup;
    private readonly Action openCompatibility;
    private readonly Action openInstall;
    private readonly Action openBuilds;
    private readonly Action openShare;
    private readonly Action openGraph;
    private readonly Action openReport;
    private string? lastBackup;

    public GameViewModel(GameCatalogItem game, Action back,
        Action? openMods = null, Action? openProfiles = null, Action? openBackup = null,
        Action? openCompatibility = null, Action? openInstall = null, Action? openBuilds = null,
        Action? openShare = null, Action? openGraph = null, Action? openReport = null)
    {
        Game = game;
        this.back = back;
        this.openMods = openMods ?? (() => { });
        this.openProfiles = openProfiles ?? (() => { });
        this.openBackup = openBackup ?? (() => { });
        this.openCompatibility = openCompatibility ?? (() => { });
        this.openInstall = openInstall ?? (() => { });
        this.openBuilds = openBuilds ?? (() => { });
        this.openShare = openShare ?? (() => { });
        this.openGraph = openGraph ?? (() => { });
        this.openReport = openReport ?? (() => { });
        var profile = store.Active(game.Definition.Slug);
        Game.ProfileName = profile?.Name ?? "Profil domyślny";
        Game.ModCount = profile?.Mods.Count(m => m.Enabled) ?? 0;
    }

    public GameCatalogItem Game { get; }
    public string Hero => Game.Hero;
    public string Title => Game.Title;
    public string GameSource => Game.LauncherLabel;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private LaunchOptionsPlan? pendingPlan;
    [ObservableProperty] private bool canRollback;

    public string StatusKey => Game.IsInstalled ? "Game.Ready" : "Game.NotDetected";
    public string StatusHint => Game.IsInstalled ? "Game.ReadyHint" : "Game.NotDetectedHint";
    public string PlanSummary => PendingPlan is null ? ""
        : $"{PendingPlan.Current ?? "(brak)"}  →  {PendingPlan.Proposed}";

    [RelayCommand] private void Back() => back();
    [RelayCommand] private void OpenMods() => openMods();
    [RelayCommand] private void OpenProfiles() => openProfiles();
    [RelayCommand] private void OpenBackup() => openBackup();
    [RelayCommand] private void OpenCompatibility() => openCompatibility();
    [RelayCommand] private void OpenInstall() => openInstall();
    [RelayCommand] private void OpenBuilds() => openBuilds();
    [RelayCommand] private void OpenShare() => openShare();
    [RelayCommand] private void OpenGraph() => openGraph();
    [RelayCommand] private void OpenReport() => openReport();

    [RelayCommand]
    private void Play()
    {
        var loc = LocalizationService.Instance;
        if (!Game.IsInstalled)
        {
            StatusMessage = loc["Game.NotDetectedHint"];
            return;
        }
        if (Game.SelectedInstallation?.Store != "steam")
        {
            StatusMessage = loc["Game.EpicLaunchManual"];
            return;
        }
        var ok = launch.LaunchGame(Game.SteamAppId);
        StatusMessage = ok ? loc["Game.Launched"] : loc["Game.LaunchFailed"];
    }

    // Launch options proposed for this game on the current OS; null when the
    // definition declares no requirements (e.g. Windows needs none for Among Us).
    public string? BuildLaunchProposal(string? os = null) =>
        GameLaunchOptions.SteamLaunchOptions(Game.Definition, os ?? (OperatingSystem.IsWindows() ? "windows" : "linux"));

    // Shows a plan for a SINGLE Steam account; nothing is written until accepted.
    // The proposed options come from the declarative game definition (launchRequirements
    // with provenance), never from a hard-coded string in the UI.
    [RelayCommand]
    private void PrepareLaunchOptions()
    {
        var loc = LocalizationService.Instance;
        try
        {
            var root = SteamLaunchOptionsService.FindSteamRoot();
            if (root is null)
            {
                StatusMessage = loc["Game.NoSteam"];
                return;
            }
            var proposed = BuildLaunchProposal();
            if (proposed is null)
            {
                StatusMessage = loc["Game.LaunchOptionsNotNeeded"];
                return;
            }
            var service = new SteamLaunchOptionsService(root);
            PendingPlan = service.BuildPlan(Game.SteamAppId, proposed);
            OnPropertyChanged(nameof(PlanSummary));
            StatusMessage = PendingPlan is null ? loc["Game.NoSteam"] : loc["Game.PlanReady"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: launch options prepare failed: {ex}");
            StatusMessage = loc["Game.LaunchOptionsError"];
        }
    }

    [RelayCommand]
    private void ConfirmLaunchOptions()
    {
        if (PendingPlan is null) return;
        var loc = LocalizationService.Instance;
        try
        {
            var root = SteamLaunchOptionsService.FindSteamRoot();
            if (root is null) return;
            var service = new SteamLaunchOptionsService(root);
            lastBackup = service.ApplyPlan(PendingPlan);
            CanRollback = true;
            PendingPlan = null;
            OnPropertyChanged(nameof(PlanSummary));
            StatusMessage = loc["Game.OptionsApplied"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: launch options apply failed: {ex}");
            StatusMessage = loc["Game.LaunchOptionsError"];
        }
    }

    [RelayCommand]
    private void RollbackLaunchOptions()
    {
        if (lastBackup is null) return;
        var loc = LocalizationService.Instance;
        try
        {
            var root = SteamLaunchOptionsService.FindSteamRoot();
            if (root is null) return;
            new SteamLaunchOptionsService(root).Rollback(lastBackup);
            CanRollback = false;
            lastBackup = null;
            StatusMessage = loc["Game.OptionsRolledBack"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: launch options rollback failed: {ex}");
            StatusMessage = loc["Game.LaunchOptionsError"];
        }
    }
}
