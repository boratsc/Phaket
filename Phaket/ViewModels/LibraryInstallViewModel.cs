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

    public bool HasInstallPlan => InstallPlan is not null;
    public bool HasInstallConflicts => InstallPlan?.Conflicts.Count > 0;
    public string InstallConflicts => string.Join(Environment.NewLine, InstallPlan?.Conflicts ?? []);
    public string InstallSize => InstallPlan?.SizeBytes is long size ? $"{size:N0} B" : LocalizationService.Instance["Library.InstallUnknownSize"];

    partial void OnInstallPlanChanged(LegendaryInstallPlan? value)
    {
        OnPropertyChanged(nameof(HasInstallPlan));
        OnPropertyChanged(nameof(HasInstallConflicts));
        OnPropertyChanged(nameof(InstallConflicts));
        OnPropertyChanged(nameof(InstallSize));
        ExecuteGameInstallCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PrepareGameInstallAsync(GameCatalogItem? item)
    {
        if (InstallingGame || item?.CanInstallEpic != true || !epicGames.TryGetValue(item, out var game) || installAdapter is null) return;
        InstallPlan = null;
        InstallError = InstallLog = "";
        TokenConsent = OverwriteConsent = PlanAccepted = false;
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.StorageProvider is not { } storage) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var suggested = Path.Combine(home, "Games", "Phaket", item.Definition.Slug);
        var start = await storage.TryGetFolderFromPathAsync(new Uri(Directory.Exists(suggested) ? suggested : home));
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Library.InstallChooseFolder"], AllowMultiple = false, SuggestedStartLocation = start
        });
        if (folders.Count == 0) return;
        try { InstallPlan = installAdapter.BuildInstallPlan(game, folders[0].Path.LocalPath, item.Definition); }
        catch (Exception ex) { ShowInstallError(ex); }
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
        var code = (ex as PhaketException)?.Error.Code;
        var key = code switch
        {
            "LEGENDARY_NOT_FOUND" => "LegendaryNotFound", "CONSENT_REQUIRED" => "ConsentRequired",
            "OVERWRITE_CONSENT_REQUIRED" => "OverwriteRequired", "PLAN_NOT_ACCEPTED" => "PlanNotAccepted",
            "EPIC_TOKEN_INVALID" => "TokenInvalid", "LEGENDARY_INSTALL_FAILED" => "Failed",
            "LEGENDARY_INSTALL_UNVERIFIED" => "Unverified", ErrorCodes.PathOutOfBounds => "PathOutOfBounds",
            _ => "Failed"
        };
        InstallError = LocalizationService.Instance["Library.InstallError." + key];
        if (ex is PhaketException pe && pe.Error.Parameters.TryGetValue("tail", out var tail) && tail.Length > 0) InstallLog = tail;
    }
}
