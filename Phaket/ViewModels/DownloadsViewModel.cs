using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed class DownloadItemViewModel : ViewModelBase
{
    public DownloadItemViewModel(string name, string detail, string statusKey, double progress = 0,
        bool showProgress = false, bool isError = false)
    {
        Name = name;
        Detail = detail;
        StatusKey = statusKey;
        Progress = Math.Clamp(progress, 0, 100);
        ShowProgress = showProgress;
        IsError = isError;
    }

    public string Name { get; }
    public string Detail { get; }
    public string StatusKey { get; }
    public double Progress { get; }
    public bool ShowProgress { get; }
    public bool IsError { get; }
    public string StatusText => LocalizationService.Instance[StatusKey];
}

public partial class DownloadsViewModel : ViewModelBase
{
    private readonly ModpackCatalogViewModel catalog;
    private readonly ModpackStore store;
    private GameCatalogItem? game;

    public DownloadsViewModel(string appDataRoot, GameCatalogItem? game, ModpackCatalogViewModel catalog)
    {
        this.game = game;
        this.catalog = catalog;
        store = new ModpackStore(appDataRoot);
        catalog.PropertyChanged += OnCatalogChanged;
        catalog.LocalPacks.CollectionChanged += OnLocalPacksChanged;
        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
            Refresh();
        };
        Refresh();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
    [ObservableProperty] private bool isCompactLayout;

    public string Title => LocalizationService.Instance["Downloads.Title"];
    public string Subtitle => LocalizationService.Instance["Downloads.Subtitle"];
    public string EmptyTitle => LocalizationService.Instance["Downloads.EmptyTitle"];
    public string EmptyMessage => LocalizationService.Instance["Downloads.EmptyMessage"];
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !HasItems;

    public void SetGame(GameCatalogItem? value)
    {
        if (ReferenceEquals(game, value)) return;
        game = value;
        Refresh();
    }

    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModpackCatalogViewModel.IsInstallRunning) or nameof(ModpackCatalogViewModel.IsPlanPreparing)
            or nameof(ModpackCatalogViewModel.IsProgressOpen) or nameof(ModpackCatalogViewModel.IsFailureOpen)
            or nameof(ModpackCatalogViewModel.InstallProgress) or nameof(ModpackCatalogViewModel.InstallErrorKey)
            or nameof(ModpackCatalogViewModel.SelectedPack) or nameof(ModpackCatalogViewModel.Game))
            Refresh();
    }

    private void OnLocalPacksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Items.Clear();
        var active = catalog.IsInstallRunning || catalog.IsPlanPreparing || catalog.IsProgressOpen;
        if (active && catalog.SelectedPack is { } activePack)
        {
            Items.Add(new DownloadItemViewModel(activePack.Name, catalog.GameName,
                catalog.IsInstallRunning ? "Downloads.Installing" : "Downloads.Preparing",
                catalog.InstallProgress, showProgress: true));
        }
        else if (catalog.IsFailureOpen && catalog.SelectedPack is { } failedPack)
        {
            Items.Add(new DownloadItemViewModel(failedPack.Name, catalog.InstallErrorKey is { } key
                    ? LocalizationService.Instance[key] : LocalizationService.Instance["Downloads.InstallFailed"],
                "Downloads.InstallFailed", isError: true));
        }

        if (game is not null)
            foreach (var localPack in store.ForGame(game.Definition.Slug).OrderBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase))
                Items.Add(new DownloadItemViewModel(localPack.Name,
                    string.Format(LocalizationService.Instance["Downloads.ModpackDetail"], localPack.Version, game.Title),
                    "Downloads.Ready"));

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
