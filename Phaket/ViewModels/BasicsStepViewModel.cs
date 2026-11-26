using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-136: one detected game on the summary page. Read-only: the row carries what detection
// found (title, platform), never an install path.
public sealed record DetectedGame(string Title, string Platform);

// PAK-136: the last wizard step. Two read-mostly sections only — the update channel (through
// the very same UpdateService settings.json key the Settings page writes) and a read-only list
// of what local detection already found installed.
public sealed partial class BasicsStepViewModel : ObservableObject, IOnboardingStep, IDisposable
{
    // A scan reads a few directories; past this it settles for what it has, so the wizard
    // never waits on a slow or stuck disk.
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    private readonly string settingsPath;
    private readonly UpdateService updates;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DetectedGame>>> scan;
    private readonly PropertyChangedEventHandler languageChanged;
    // The saved channel as the wizard saw it on activation: an unchanged choice saves nothing.
    private UpdateChannel preset;
    private bool betaSelected;
    private CancellationTokenSource? scanCancellation;
    private int scanVersion;

    // Real run: Steam app manifests and Epic's installed.json, both read-only.
    public BasicsStepViewModel(string settingsPath)
        : this(settingsPath, new UpdateService(settingsPath), DefaultScan) { }

    // Test seam: a fake channel source and a fake scan, so no real library is ever read.
    internal BasicsStepViewModel(string settingsPath, UpdateService updates,
        Func<CancellationToken, Task<IReadOnlyList<DetectedGame>>> scan)
    {
        this.settingsPath = settingsPath;
        this.updates = updates;
        this.scan = scan;
        preset = updates.Channel;
        betaSelected = preset == UpdateChannel.Beta;
        languageChanged = (_, _) => NotifyLabels();
        LocalizationService.Instance.PropertyChanged += languageChanged;
    }

    public string StepId => "basics";
    public string Title => LocalizationService.Instance["Onboarding.Basics.Title"];
    public string Description => LocalizationService.Instance["Onboarding.Basics.Description"];

    public string ChannelTitle => LocalizationService.Instance["Onboarding.Basics.Channel.Title"];
    public string StableText => LocalizationService.Instance["Settings.Updates.Channel.Stable"];
    public string BetaText => LocalizationService.Instance["Settings.Updates.Channel.Beta"];
    public string ChannelHint => LocalizationService.Instance["Onboarding.Basics.Channel.Hint"];
    public string GamesTitle => LocalizationService.Instance["Onboarding.Basics.Games.Title"];

    // Radios mirror the choice; the command below is what records it (same split as Settings).
    public bool IsStableChannel => !betaSelected;
    public bool IsBetaChannel => betaSelected;

    // A click on the active radio still runs the command, which is what an explicit choice is.
    [RelayCommand]
    private void SelectChannel(string? channel)
    {
        if (channel is not ("stable" or "beta")) return;
        System.Diagnostics.Trace.WriteLine("DBG select " + channel);
        betaSelected = channel == "beta";
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsBetaChannel));
    }

    public IReadOnlyList<DetectedGame> Games { get; private set; } = [];

    [ObservableProperty] private bool isScanning = true;
    [ObservableProperty] private bool scanFailed;

    public bool HasGames => Games.Count > 0;
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ScanStatus));
    partial void OnScanFailedChanged(bool value) => OnPropertyChanged(nameof(ScanStatus));
    public string? ScanStatus => IsScanning
        ? LocalizationService.Instance["Onboarding.Basics.Games.Scanning"]
        : HasGames ? null : ScanFailed
            ? LocalizationService.Instance["Onboarding.Basics.Games.Error"]
            : LocalizationService.Instance["Onboarding.Basics.Games.Empty"];

    // Activating the page starts the scan; navigation never awaits it.
    public void StartScan()
    {
        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        var cancellation = scanCancellation = new CancellationTokenSource();
        var version = ++scanVersion;
        IsScanning = true;
        ScanFailed = false;
        _ = RunScanAsync(cancellation, version);
    }

    private async Task RunScanAsync(CancellationTokenSource cancellation, int version)
    {
        IReadOnlyList<DetectedGame> found;
        bool failed = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(ScanTimeout);
            // Detection reads directories; a closed catalogue never blocks the UI thread.
            found = await Task.Run(() => scan(timeout.Token), timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            // Files that vanish, unreadable dirs or the timeout: the page says "nothing found".
            found = [];
            failed = true;
        }
        if (version != scanVersion) return;
        SetGames(found, failed);
    }

    // Test seam: what a finished scan would show, without a fake detector.
    internal void ShowScanResult(IReadOnlyList<DetectedGame> games, bool failed)
    {
        scanCancellation?.Cancel();
        scanVersion++;
        SetGames(games, failed);
    }

    private void SetGames(IReadOnlyList<DetectedGame> games, bool failed)
    {
        Games = games;
        IsScanning = false;
        ScanFailed = failed;
        OnPropertyChanged(nameof(Games));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(ScanStatus));
    }

    // Next/Finish: only a channel the user actually changed is written, through UpdateService,
    // so the wizard and Settings never disagree about settings.json.
    public void Save()
    {
        var chosen = betaSelected ? UpdateChannel.Beta : UpdateChannel.Stable;
        if (chosen == preset) return;
        updates.SetChannel(chosen);
        preset = chosen;
    }

    // Restarting re-reads the saved channel and drops a late scan of the previous wizard state.
    public void Reset()
    {
        scanCancellation?.Cancel();
        scanVersion++;
        preset = UpdateService.LoadChannel(settingsPath);
        betaSelected = preset == UpdateChannel.Beta;
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsBetaChannel));
        Games = [];
        IsScanning = false;
        ScanFailed = false;
        OnPropertyChanged(nameof(Games));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(ScanStatus));
        StartScan();
    }

    private void NotifyLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ChannelTitle));
        OnPropertyChanged(nameof(StableText));
        OnPropertyChanged(nameof(BetaText));
        OnPropertyChanged(nameof(ChannelHint));
        OnPropertyChanged(nameof(GamesTitle));
        OnPropertyChanged(nameof(ScanStatus));
    }

    // Titles come from what the stores recorded; only the local, read-only install checks run.
    private static async Task<IReadOnlyList<DetectedGame>> DefaultScan(CancellationToken cancellationToken)
    {
        var definitions = GameDefinitions.LoadAll();
        var os = OperatingSystem.IsWindows() ? "windows" : "linux";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<DetectedGame>();
        foreach (var installation in new SteamAdapter(definitions).DetectInstallations(os, home))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (installation.Availability is GameAvailability.Installed or GameAvailability.Moddable)
                found.Add(new DetectedGame(installation.Manifest.Name.Length > 0 ? installation.Manifest.Name : installation.Manifest.AppId,
                    LocalizationService.Instance["Onboarding.Basics.Platform.Steam"]));
        }
        try
        {
            var epic = new LegendaryAdapter(definitions).DetectInstalled(LegendaryAdapter.DefaultConfigDir(home));
            foreach (var game in epic)
                found.Add(new DetectedGame(game.Title, LocalizationService.Instance["Onboarding.Basics.Platform.Epic"]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return found;
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= languageChanged;
        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        scanCancellation = null;
    }
}