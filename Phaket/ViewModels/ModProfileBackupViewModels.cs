using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;
using Phaket.Core.Execution;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Avalonia.Input.Platform;
using Phaket.Core.Mods;

namespace Phaket.ViewModels;

public partial class ModItemViewModel : ViewModelBase
{
    private readonly ModEntry entry;
    private readonly Action changed;

    public ModItemViewModel(ModEntry entry, Action changed)
    {
        this.entry = entry;
        this.changed = changed;
    }

    public string Name => entry.Name;
    public string Version => "v" + entry.Version;
    public bool Compatible => entry.Compatible;
    public string CompatibilityKey => entry.Compatible ? "Mods.Compatible" : "Mods.Incompatible";

    public bool IsEnabled
    {
        get => entry.Enabled;
        set
        {
            if (entry.Enabled == value) return;
            entry.Enabled = value;
            OnPropertyChanged();
            changed();
        }
    }
}

public partial class ModsViewModel : ViewModelBase, IDisposable
{
    private readonly ProfileStore store;
    private readonly Action back;
    private readonly string gameId;
    private readonly string appDataRoot;
    private readonly IReceiptTransport? receiptTransport;
    private InstallRegistry Registry => new(Path.Combine(appDataRoot, "installs.json"));

    public ModsViewModel(GameCatalogItem game, Action back)
        : this(game, back, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket")) { }

    internal ModsViewModel(GameCatalogItem game, Action back, string appDataRoot, IReceiptTransport? receiptTransport = null)
    {
        this.appDataRoot = appDataRoot;
        this.receiptTransport = receiptTransport;
        store = new ProfileStore(appDataRoot);
        this.back = back;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        Cover = game.Cover;
        Reload();
        Catalog = new ModCatalogViewModel(gameId, appDataRoot, installedChanged: ReloadInstalled, game: game);
        Catalog.Mods.CollectionChanged += (_, _) => ReloadInstalled();
        ReloadInstalled();
    }

    public ModCatalogViewModel Catalog { get; }
    // A picker switch replaces this page; the detached catalog must not keep refreshing.
    public void Dispose() => Catalog.Dispose();
    public string GameTitle { get; }
    public string GameId => gameId;
    public string Cover { get; }
    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<InstalledRecipeViewModel> InstalledRecipes { get; } = [];
    public bool InstalledEmpty => InstalledRecipes.Count == 0;
    [ObservableProperty] private ModItemViewModel? selectedMod;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string launchRequirements = "";
    public bool HasLaunchRequirements => !string.IsNullOrEmpty(LaunchRequirements);
    partial void OnLaunchRequirementsChanged(string value) => OnPropertyChanged(nameof(HasLaunchRequirements));

    [RelayCommand]
    private async Task CopyLaunchRequirementsAsync()
    {
        try
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var clipboard = desktop?.MainWindow?.Clipboard;
            if (clipboard is null || !HasLaunchRequirements) { Status = LocalizationService.Instance["Mods.CopyFailed"]; return; }
            await clipboard.SetTextAsync(LaunchRequirements);
            Status = LocalizationService.Instance["Mods.Copied"];
        }
        catch (Exception) { Status = LocalizationService.Instance["Mods.CopyFailed"]; }
    }

    private void ReloadInstalled()
    {
        InstalledRecipes.Clear();
        foreach (var entry in Registry.ForGame(gameId))
            InstalledRecipes.Add(new InstalledRecipeViewModel(entry, UninstallAsync, Launch, Catalog.LatestVersion(entry.ModFullName), UpdateAsync));
        OnPropertyChanged(nameof(InstalledEmpty));
    }

    private async Task UpdateAsync(InstalledRecipe entry, string version)
    {
        SelectedTabIndex = 1;
        await Catalog.BeginInstallAsync(entry.ModFullName, version,
            new InstallTarget(entry.GameRoot, entry.Store, entry.SandboxRoot is not null));
    }

    private async Task UninstallAsync(InstalledRecipe entry)
    {
        var loc = LocalizationService.Instance;
        try
        {
            await Task.Run(() =>
            {
                var definition = GameDefinitions.LoadAll().First(g => g.Slug == entry.GameSlug);
                var ctx = new PlanExecutionContext(entry.GameRoot, entry.ProfileRoot, entry.StagingRoot,
                    Path.Combine(appDataRoot, "packages"), definition, entry.RecipeId, "install-profile", entry.StateRoot);
                new PlanExecutor(ctx).Uninstall(entry.RecipeId);
                Registry.Remove(entry.RecipeId, entry.GameRoot);
            });
            if (entry.SandboxRoot is not null)
                try
                {
                    var root = Path.GetFullPath(entry.SandboxRoot);
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), comparison)
                        && Path.GetFileName(root).StartsWith("phaket-install-", StringComparison.Ordinal)
                        && string.Equals(Path.Combine(root, "game"), Path.GetFullPath(entry.GameRoot), comparison)
                        && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                        Directory.Delete(root, recursive: true);
                }
                catch { /* best effort */ }
            ReloadInstalled();
            Status = loc["Mods.Uninstalled"];
            if (!string.IsNullOrWhiteSpace(entry.ModFullName) && !string.IsNullOrWhiteSpace(entry.ModVersion))
            {
                try
                {
                    if (receiptTransport is not null)
                        await new ReceiptSender(receiptTransport).SendAsync(entry.GameSlug, entry.ModFullName, entry.ModVersion, entry.Store, "uninstalled");
                    else if (Uri.TryCreate(Catalog.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https")
                    {
                        using var http = new System.Net.Http.HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(5) };
                        await new ReceiptSender(new HttpReceiptTransport(http)).SendAsync(entry.GameSlug, entry.ModFullName, entry.ModVersion, entry.Store, "uninstalled");
                    }
                }
                catch (Exception) { /* best effort */ }
            }
        }
        catch (PhaketException ex) { Status = ex.LocalizedMessage(loc.CurrentLanguage); }
        catch (Exception ex) { Status = ex.Message; }
    }

    private void Launch(InstalledRecipe entry)
    {
        var loc = LocalizationService.Instance;
        LaunchRequirements = "";
        if (entry.SandboxRoot is not null) { Status = loc["Mods.SandboxLaunchHint"]; return; }
        try
        {
            // Never launch vanilla in place of a modded game without explaining launcher settings.
            LaunchRequirements = ModLaunchRequirements.Create(entry) ?? "";
            Status = loc[HasLaunchRequirements ? "Mods.LaunchInstructions" : "Mods.LaunchManual"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: mod launch requirements failed: {ex}");
            Status = loc["Mods.LaunchManual"];
        }
    }

    [ObservableProperty] private string filter = "all";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string activeProfileName = "";
    [ObservableProperty] private int selectedTabIndex;

    public string AllLabel => $"{LocalizationService.Instance["Mods.All"]} ({CountAll})";
    public bool IsAllFilter => Filter == "all";
    public bool IsActiveFilter => Filter == "active";
    public bool IsInactiveFilter => Filter == "inactive";
    private int CountAll => store.Active(gameId)?.Mods.Count ?? 0;

    private void Reload()
    {
        var previous = SelectedMod;
        ActiveProfileName = store.Active(gameId)?.Name ?? "";
        Mods.Clear();
        var profile = store.Active(gameId);
        if (profile is not null)
        {
            foreach (var mod in profile.Mods)
            {
                if (Filter == "active" && !mod.Enabled) continue;
                if (Filter == "inactive" && mod.Enabled) continue;
                if (!string.IsNullOrWhiteSpace(SearchText) &&
                    !mod.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) continue;
                Mods.Add(new ModItemViewModel(mod, () => store.Save()));
            }
        }
        SelectedMod = previous is not null && Mods.FirstOrDefault(mod => mod.Name == previous.Name) is { } match
            ? match : Mods.FirstOrDefault();
        OnPropertyChanged(nameof(AllLabel));
    }

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsActiveFilter));
        OnPropertyChanged(nameof(IsInactiveFilter));
        Reload();
    }
    partial void OnSearchTextChanged(string value) => Reload();

    [RelayCommand] private void Back() => back();
    [RelayCommand] private void SetFilter(string f) => Filter = f;

    [RelayCommand]
    private void AddMod() => SelectedTabIndex = 1;
}

public sealed class InstalledRecipeViewModel
{
    public InstalledRecipeViewModel(InstalledRecipe entry, Func<InstalledRecipe, Task> uninstall, Action<InstalledRecipe> launch, string? latestVersion = null, Func<InstalledRecipe, string, Task>? update = null)
    {
        Entry = entry;
        UninstallCommand = new AsyncRelayCommand(() => uninstall(entry));
        LaunchCommand = new RelayCommand(() => launch(entry));
        HasUpdate = !string.IsNullOrEmpty(entry.ModFullName) && !string.IsNullOrEmpty(entry.ModVersion)
            && !string.IsNullOrEmpty(latestVersion) && !string.Equals(entry.ModVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
        UpdateCommand = new AsyncRelayCommand(() => update!(entry, latestVersion!), () => HasUpdate && update is not null);
    }
    public InstalledRecipe Entry { get; }
    public string RecipeId => Entry.RecipeId;
    public string Version => Entry.RecipeVersion;
    public DateTimeOffset InstalledAt => Entry.InstalledAt.ToLocalTime();
    public bool IsSandbox => Entry.SandboxRoot is not null;
    public IAsyncRelayCommand UninstallCommand { get; }
    public IRelayCommand LaunchCommand { get; }
    public bool HasUpdate { get; }
    public IAsyncRelayCommand UpdateCommand { get; }
}

public partial class BackupViewModel : ViewModelBase
{
    private readonly ProfileStore store = new();
    private readonly Action back;
    private readonly string gameId;

    public BackupViewModel(GameCatalogItem game, Action back)
    {
        this.back = back;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        Reload();
    }

    public string GameTitle { get; }
    public string GameId => gameId;
    public ObservableCollection<BackupItem> Backups { get; } = [];
    [ObservableProperty] private string status = "";

    private void Reload()
    {
        Backups.Clear();
        foreach (var backup in store.Backups(gameId))
            Backups.Add(new BackupItem
            {
                Id = backup.Id,
                Name = backup.Name,
                Date = backup.CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm"),
                Size = $"{backup.SizeBytes / 1024.0:F0} KB",
                Kind = backup.Kind,
                Entry = backup,
            });
    }

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private void Create()
    {
        store.CreateBackup(gameId, $"{LocalizationService.Instance["Backup.Manual"]} – {DateTime.Now:HH:mm}", "manual");
        Reload();
        Status = LocalizationService.Instance["Backup.Created"];
    }

    [RelayCommand]
    private void Restore(BackupItem? item)
    {
        if (item is null) return;
        store.Restore(gameId, item.Entry);
        Status = LocalizationService.Instance["Backup.Restored"];
    }
}

public partial class BackupItem : ViewModelBase
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Date { get; set; } = "";
    public string Size { get; set; } = "";
    public string Kind { get; set; } = "";
    public BackupEntry Entry { get; set; } = new();
}
