using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.ViewModels;

// One folder of a game under <app data>/state/<game>/<installation> — the same keyed
// state roots the installers write (PAK-141: save backups live there).
internal static class SaveBackupRoots
{
    public static IReadOnlyList<string> ForGame(string appDataRoot, string gameId)
    {
        var gameState = Path.Combine(appDataRoot, "state", gameId);
        if (!Directory.Exists(gameState)) return [];
        return Directory.GetDirectories(gameState).Select(root => Path.Combine(root, "save-backups")).ToArray();
    }
}

// "Your game saves" (PAK-141): the copies Phaket made before an install touched the game.
// Restoring asks first because it replaces the current saves; deleting old copies needs
// its own consent box. Empty here simply means no mod has been installed yet.
public partial class SaveBackupsViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<string>> roots;
    private readonly SaveBackupService service;

    // The page's own game. The lifecycle records the game's display title as the copy's game,
    // so one page is one game: a copy is mine when its title sanitizes to one of mine. Both
    // product languages are allowed, so switching language never hides a copy.
    private readonly string? gameId;
    private readonly HashSet<string> labels = new(StringComparer.Ordinal);

    public SaveBackupsViewModel(Func<IReadOnlyList<string>> roots, string? gameId = null, SaveBackupService? service = null)
    {
        this.roots = roots;
        this.gameId = gameId;
        this.service = service ?? new SaveBackupService();
        if (gameId is null) return;
        if (GameDefinitions.LoadAll().FirstOrDefault(definition => definition.Slug == gameId) is not { } definition) return;
        foreach (var title in (definition.Title?.Values ?? []).Append(definition.Slug))
            labels.Add(SaveBackupService.BackupFolderName(title, ""));
    }

    // The per-game page: the game's keyed state roots (the same ones the installers key by
    // game root), so every backup Phaket made for this game is listed.
    public static SaveBackupsViewModel ForGame(string appDataRoot, string gameId, SaveBackupService? service = null) =>
        new(() => SaveBackupRoots.ForGame(appDataRoot, gameId), gameId, service);

    // The copy's game title, sanitized the way the backup folder itself is; the folder is
    // never rebuilt from the slug, so a renamed folder still belongs to the game it names.
    private bool Mine(SaveBackupManifest manifest) =>
        gameId is null
        || labels.Contains(SaveBackupService.BackupFolderName(manifest.GameId, manifest.InstallationId));

    // Rebuilt and raised once per load: a copy count is small and the list is read-only.
    [ObservableProperty] private IReadOnlyList<SaveBackupItem> items = [];
    // <app data>/state/<game>/<root key>/save-backups for every installation of this game.
    public static IReadOnlyList<string> RootsFor(string appDataRoot, string gameId) =>
        SaveBackupRoots.ForGame(appDataRoot, gameId);
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "";
    // Restoring replaces the current saves; the question is shown before the call.
    [ObservableProperty] private SaveBackupItem? pendingRestore;
    [ObservableProperty] private bool pruneConsent;
    // Deletion needs both: the box ticked AND the confirmation dialog accepted, so a stray
    // click on a list row can never remove a copy.
    [ObservableProperty] private bool isPruneOpen;
    [ObservableProperty] private string pruneQuestion = "";
    // Without copies there is nothing to refresh and nothing to delete: the header button and
    // the prune box stay out of the way, and the game page keeps its own actions.
    public bool HasCopies => !IsEmpty;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public bool HasItems => Items.Count > 0;
    public bool CanPrune => !IsBusy && PruneConsent && Items.Count > 0;
    public string RestoreQuestion => PendingRestore is null ? ""
        : string.Format(LocalizationService.Instance["SaveBackup.RestoreQuestion"], PendingRestore.DateText);
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnPendingRestoreChanged(SaveBackupItem? value) => OnPropertyChanged(nameof(RestoreQuestion));
    partial void OnPruneConsentChanged(bool value) => OnPropertyChanged(nameof(CanPrune));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanPrune));

    private void Sync()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasCopies));
        OnPropertyChanged(nameof(CanPrune));
        IsEmpty = Items.Count == 0;
    }

    // Every copy under the game's state roots, each folder listed by its own name.
    private IEnumerable<(string Root, string Folder)> Folders() => roots().Distinct(StringComparer.Ordinal)
        .Where(Directory.Exists)
        .SelectMany(root => Directory.GetDirectories(root).Select(folder => (Root: root, Folder: Path.GetFileName(folder))))
        .Where(folder => !folder.Folder.StartsWith(".", StringComparison.Ordinal));

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var found = new List<SaveBackupItem>();
            foreach (var (root, folder) in Folders())
                foreach (var manifest in await service.ListBackupsAsync(root, folder).ConfigureAwait(true))
                    if (Mine(manifest)) found.Add(new SaveBackupItem(manifest, Path.Combine(root, folder, manifest.Id)));
            Items = found.OrderByDescending(item => item.CreatedAt).ToArray();
            Sync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("phaket: save backup list failed: {0}", ex.Message);
            StatusMessage = LocalizationService.Instance["SaveBackup.LoadFailed"];
        }
        finally { IsBusy = false; }
    }

    // Step 1: ask. Nothing is restored until ConfirmRestore.
    [RelayCommand]
    private void Restore(SaveBackupItem? item)
    {
        if (item is null || IsBusy) return;
        StatusMessage = "";
        PendingRestore = item;
    }

    [RelayCommand]
    private void CancelRestore() => PendingRestore = null;

    // Step 2: the backup is verified in full before the first write, so a damaged copy
    // never leaves a half-restored save folder (SaveBackupService.RestoreAsync).
    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (PendingRestore is not { } item || IsBusy) return;
        PendingRestore = null;
        var locations = item.Manifest.Locations.Select(location => location.Location).ToArray();
        if (locations.Length == 0)
        {
            StatusMessage = LocalizationService.Instance["SaveBackup.NothingToRestore"];
            return;
        }
        IsBusy = true;
        try
        {
            foreach (var location in locations) await service.RestoreAsync(item.Directory, location).ConfigureAwait(true);
            StatusMessage = LocalizationService.Instance["SaveBackup.Restored"];
        }
        catch (PhaketException ex)
        {
            StatusMessage = ex.LocalizedMessage(LocalizationService.Instance.CurrentLanguage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = LocalizationService.Instance["SaveBackup.RestoreFailed"];
        }
        finally { IsBusy = false; }
    }

    // Keeps the three newest copies per folder and caps each folder at the service's default
    // limit. The click opens the confirmation; only its confirm calls the service.
    [RelayCommand]
    private void Prune()
    {
        if (!CanPrune) return;
        PruneQuestion = string.Format(LocalizationService.Instance["SaveBackup.PruneQuestion"], Items.Count);
        IsPruneOpen = true;
    }

    [RelayCommand] private void CancelPrune() => IsPruneOpen = false;

    [RelayCommand]
    private async Task PruneConfirmedAsync()
    {
        IsPruneOpen = false;
        if (!PruneConsent) return;
        IsBusy = true;
        try
        {
            var removed = 0;
            foreach (var (root, folder) in Folders())
            {
                var before = (await service.ListBackupsAsync(root, folder).ConfigureAwait(true)).Count(Mine);
                if (before == 0) continue;
                var remaining = await service.PruneAsync(root, folder, 3, SaveBackupService.DefaultLimitBytes, consent: true)
                    .ConfigureAwait(true);
                removed += Math.Max(0, before - remaining.Count(Mine));
            }
            StatusMessage = removed == 0
                ? LocalizationService.Instance["SaveBackup.PruneNothing"]
                : string.Format(LocalizationService.Instance["SaveBackup.Pruned"], removed);
            PruneConsent = false;
            await LoadAsync();
        }
        catch (PhaketException ex)
        {
            StatusMessage = ex.LocalizedMessage(LocalizationService.Instance.CurrentLanguage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = LocalizationService.Instance["SaveBackup.PruneFailed"];
        }
        finally { IsBusy = false; }
    }
}

// A copy as the list shows it: friendly date, size in MB, and what it covers.
public partial class SaveBackupItem : ViewModelBase
{
    public SaveBackupItem(SaveBackupManifest manifest, string directory)
    {
        Manifest = manifest;
        Directory = directory;
    }

    public SaveBackupManifest Manifest { get; }
    public string Directory { get; }
    public DateTimeOffset CreatedAt => Manifest.CreatedAt;
    public string DateText => CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string SizeText => Manifest.TotalBytes >= 1024 * 1024
        ? $"{Manifest.TotalBytes / (1024d * 1024):0.#} MB"
        : $"{Math.Max(1, (long)Math.Round(Manifest.TotalBytes / 1024d))} KB";
    public string LocationText => string.Join(", ", Manifest.Locations.Select(location => location.Location));
    // A profile record copies nothing, and neither does an empty location; both are still
    // restorable ("remove what the recipe wrote"), so they say so instead of a rejected schema.
    public bool HasCopies => Manifest.Files.Count > 0;
    public string CopiesHint => LocalizationService.Instance[HasCopies ? "SaveBackup.FilesCopied" : "SaveBackup.ProfileOnly"]
        .Replace("{count}", Manifest.Files.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
}