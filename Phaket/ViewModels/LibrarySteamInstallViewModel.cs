using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Catalog;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class LibraryViewModel
{
    private readonly DepotDownloaderInstallAdapter depotAdapter = new();
    [ObservableProperty] private SteamInstallFlowViewModel? steamInstall;
    [ObservableProperty] private bool confirmSessionCleanup;
    [ObservableProperty] private string sessionCleanupStatus = "";
    private IReadOnlyList<string> sessionPaths = [];

    [RelayCommand]
    private async Task OpenSteamInstallAsync(GameCatalogItem? item)
    {
        if (SteamInstall?.Installing == true || item?.CanInstallSteam != true || !steamGames.TryGetValue(item, out var game)) return;
        async Task<string?> Pick()
        {
            var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window?.StorageProvider is not { } storage) return null;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = LocalizationService.Instance["Library.InstallChooseFolder"], AllowMultiple = false });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }
        Task<IReadOnlyList<BuildOption>> Builds() => Task.Run<IReadOnlyList<BuildOption>>(() =>
        {
            using var store = new CatalogStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "catalog.db"));
            return store.GetBuilds(item.Definition.Slug).Where(b => b.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && b.DepotIds.Count == 1 && b.ManifestIds.Count == 1)
                .Select(b => new BuildOption($"{b.VersionLabel} · {b.Branch} · {b.BuildId} · {b.Arch ?? "?"}", b.DepotIds[0], b.ManifestIds[0], Arch: b.Arch, Branch: b.Branch, BuildId: b.BuildId)).ToArray();
        });
        var flow = new SteamInstallFlowViewModel(game, item.Definition, depotAdapter, Pick, Builds, credentialStore: steamCredentials);
        flow.OnInstalled = () => _ = ScanCommand.ExecuteAsync(null);
        SteamInstall = flow;
        await flow.OpenCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void CloseSteamInstall() { if (SteamInstall?.Installing != true) SteamInstall = null; }

    [RelayCommand]
    private void ListDepotSessions()
    {
        try
        {
            var stores = depotAdapter.ListSessionStores();
            sessionPaths = stores.Paths.ToArray();
            ConfirmSessionCleanup = sessionPaths.Count > 0;
            SessionCleanupStatus = sessionPaths.Count == 0 ? string.Format(LocalizationService.Instance["SteamInstall.SessionNone"], stores.Root) : string.Join(Environment.NewLine, sessionPaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SessionCleanupStatus = LocalizationService.Instance["SteamInstall.Error.Failed"]; }
    }

    [RelayCommand]
    private void ConfirmDepotSessionCleanup()
    {
        if (!ConfirmSessionCleanup) return;
        try
        {
            var results = depotAdapter.CleanupSessionStores(sessionPaths, path => sessionPaths.Contains(path, StringComparer.Ordinal));
            SessionCleanupStatus = results.Count == 0 ? LocalizationService.Instance["SteamInstall.SessionRemoved"] : LocalizationService.Instance["SteamInstall.Error.Failed"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SessionCleanupStatus = LocalizationService.Instance["SteamInstall.Error.Failed"]; }
        finally { ConfirmSessionCleanup = false; sessionPaths = []; }
    }

    [RelayCommand]
    private void CancelDepotSessionCleanup() { ConfirmSessionCleanup = false; sessionPaths = []; }
}
