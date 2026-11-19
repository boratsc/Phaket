using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class LibraryViewModel
{
    [ObservableProperty] private LegendaryInstallPlan? installPlan;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteGameInstallCommand))] private bool tokenConsent;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteGameInstallCommand))] private bool overwriteConsent;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteGameInstallCommand))] private bool planAccepted;
    [ObservableProperty] private string installLog = "";
    [ObservableProperty] private string installError = "";
    [ObservableProperty] private bool installingGame;

    // Tests drive the folder choice without an OS picker (headless has no portal);
    // the Steam flow takes the same delegate shape.
    internal Func<Task<string?>>? InstallFolderPicker { get; set; }

    // Bound instead of InstallPlan.AppName/TargetPath: a null plan must not produce
    // binding errors or an empty plan card.
    public string InstallPlanAppName => InstallPlan?.AppName ?? "";
    public string InstallPlanTargetPath => InstallPlan?.TargetPath ?? "";
    public bool HasInstallPlan => InstallPlan is not null;
    public bool HasInstallConflicts => InstallPlan?.Conflicts.Count > 0;
    public string InstallConflicts => string.Join(Environment.NewLine, InstallPlan?.Conflicts ?? []);
    public string InstallSize => InstallPlan?.SizeBytes is long size ? $"{size:N0} B" : LocalizationService.Instance["Library.InstallUnknownSize"];

    partial void OnInstallPlanChanged(LegendaryInstallPlan? value)
    {
        OnPropertyChanged(nameof(HasInstallPlan));
        OnPropertyChanged(nameof(InstallPlanAppName));
        OnPropertyChanged(nameof(InstallPlanTargetPath));
        OnPropertyChanged(nameof(HasInstallConflicts));
        OnPropertyChanged(nameof(InstallConflicts));
        OnPropertyChanged(nameof(InstallSize));
        ExecuteGameInstallCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PrepareGameInstallAsync(GameCatalogItem? item)
    {
        if (InstallingGame || item?.CanInstallEpic != true || !epicGames.TryGetValue(item, out var game)) return;
        if (installAdapter is null)
        {
            // Seam-only state: production always has an adapter, and Core now fetches the tool
            // itself, so there is no "tool missing" error code left to report.
            InstallError = LocalizationService.Instance["Library.InstallError.Failed"];
            return;
        }
        InstallPlan = null;
        InstallError = InstallLog = "";
        TokenConsent = OverwriteConsent = PlanAccepted = false;
        string? target;
        try
        {
            target = await (InstallFolderPicker ?? (() => PickInstallFolderAsync(item.Definition.Slug)))();
        }
        // A picker that cannot open used to end as an unobserved task exception: the user
        // saw nothing after clicking Install. Cancellation stays silent and clean.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            || ex is NotSupportedException or InvalidOperationException or UriFormatException)
        {
            InstallError = LocalizationService.Instance["ModInstall.FolderFailed"];
            return;
        }
        if (target is null) return;
        try { InstallPlan = installAdapter.BuildInstallPlan(game, target, item.Definition); }
        catch (Exception ex) { ShowInstallError(ex); }
    }

    private async Task<string?> PickInstallFolderAsync(string slug)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.StorageProvider is not { } storage) throw new InvalidOperationException("storage provider is unavailable");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var suggested = Path.Combine(home, "Games", "Phaket", slug);
        // The start location is a convenience only: an unusable path must not stop the picker.
        IStorageFolder? start = null;
        try { start = await storage.TryGetFolderFromPathAsync(Directory.Exists(suggested) ? suggested : home); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            || ex is NotSupportedException or InvalidOperationException or UriFormatException) { }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Library.InstallChooseFolder"], AllowMultiple = false, SuggestedStartLocation = start
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private bool CanExecuteGameInstall() => InstallPlan is not null && !InstallingGame;

    [RelayCommand(CanExecute = nameof(CanExecuteGameInstall))]
    private async Task ExecuteGameInstallAsync()
    {
        var plan = InstallPlan;
        if (plan is null || !CanExecuteGameInstall()) return;
        var missing = !TokenConsent ? "ConsentRequired" : !PlanAccepted ? "PlanNotAccepted" : HasInstallConflicts && !OverwriteConsent ? "OverwriteRequired" : null;
        if (missing is not null)
        {
            Status = LocalizationService.Instance["Library.InstallError." + missing];
            return;
        }
        if (installAdapter is null) return;
        Status = "";
        InstallingGame = true;
        ExecuteGameInstallCommand.NotifyCanExecuteChanged();
        InstallError = InstallLog = "";
        try
        {
            var progress = new Progress<string>(line =>
            {
                var lines = (InstallLog + line + Environment.NewLine).Split(Environment.NewLine);
                InstallLog = string.Join(Environment.NewLine, lines.TakeLast(101));
            });
            await installAdapter.ExecuteInstallAsync(plan, TokenConsent, PlanAccepted, progress, overwriteConsent: OverwriteConsent);
            InstallPlan = null;
            await ScanAsync();
            Status = LocalizationService.Instance["ModInstall.Success"];
        }
        catch (Exception ex) { ShowInstallError(ex); }
        finally { InstallingGame = false; ExecuteGameInstallCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand]
    private void CancelGameInstall()
    {
        if (!InstallingGame) { InstallPlan = null; InstallError = Status = ""; }
    }

    private void ShowInstallError(Exception ex)
    {
        // Unreachable Epic services read as a network problem, not as a code or a raw exception.
        if (ex is PhaketException { Error.Code: "EPIC_LIBRARY_UNAVAILABLE" } or HttpRequestException or SocketException or TaskCanceledException)
        {
            InstallError = LocalizationService.Instance["Library.EpicUnavailable"];
            return;
        }
        var code = (ex as PhaketException)?.Error.Code;
        var key = code switch
        {
            "CONSENT_REQUIRED" => "ConsentRequired",
            "OVERWRITE_CONSENT_REQUIRED" => "OverwriteRequired", "PLAN_NOT_ACCEPTED" => "PlanNotAccepted",
            "EPIC_TOKEN_INVALID" => "TokenInvalid", "LEGENDARY_INSTALL_FAILED" => "Failed",
            "LEGENDARY_INSTALL_UNVERIFIED" => "Unverified", ErrorCodes.PathOutOfBounds => "PathOutOfBounds",
            _ => "Failed"
        };
        InstallError = LocalizationService.Instance["Library.InstallError." + key];
        if (ex is PhaketException pe && pe.Error.Parameters.TryGetValue("tail", out var tail) && tail.Length > 0) InstallLog = tail;
    }
}
