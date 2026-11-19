using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class DiscoverViewModel : ViewModelBase
{
    private readonly IModpackApiClient packagesApi;
    private readonly ICommunitiesApiClient communitiesApi;
    private CancellationTokenSource? searchCancellation;

    public DiscoverViewModel(GameCatalogItem? game, IModpackApiClient packagesApi, ICommunitiesApiClient communitiesApi)
    {
        Game = game;
        this.packagesApi = packagesApi;
        this.communitiesApi = communitiesApi;
        IsDemoMode = communitiesApi.IsDemoMode;
        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(OfflineNotice));
            OnPropertyChanged(nameof(DemoNotice));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(GameName));
            foreach (var community in Communities) community.RefreshLocalized();
            SelectedCommunity?.RefreshLocalized();
        };
    }

    public GameCatalogItem? Game { get; private set; }
    public ObservableCollection<ModpackDefinition> Packages { get; } = [];
    public ObservableCollection<CommunityItemViewModel> Communities { get; } = [];

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool showCommunities;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private bool isDemoMode;
    [ObservableProperty] private bool isCompactLayout;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private ModpackDefinition? selectedPackage;
    [ObservableProperty] private CommunityItemViewModel? selectedCommunity;

    public string Title => LocalizationService.Instance["Discover.Title"];
    public string Subtitle => LocalizationService.Instance["Discover.Subtitle"];
    public string GameName => Game?.Title ?? LocalizationService.Instance["Shell.NoGame"];
    public string OfflineNotice => IsOffline ? LocalizationService.Instance["Discover.Offline"] : "";
    public string DemoNotice => IsDemoMode && ShowCommunities ? LocalizationService.Instance["Communities.DemoMode"] : "";
    public bool IsPackageTab => !ShowCommunities;
    public bool IsCommunityTab => ShowCommunities;
    public bool HasPackages => Packages.Count > 0;
    public bool HasCommunities => Communities.Count > 0;
    public bool HasNoPackages => !HasPackages;
    public bool HasNoCommunities => !HasCommunities;
    public bool HasSelectedPackage => SelectedPackage is not null;
    public bool HasSelectedCommunity => SelectedCommunity is not null;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsNotLoading => !IsLoading;
    public string EmptyTitle => LocalizationService.Instance[SearchText.Length == 0 ? "Discover.EmptyTitle" : "Discover.NoResultsTitle"];
    public string EmptyMessage => LocalizationService.Instance[SearchText.Length == 0 ? "Discover.EmptyMessage" : "Discover.NoResultsMessage"];

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        QueueSearch();
    }
    partial void OnShowCommunitiesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPackageTab));
        OnPropertyChanged(nameof(IsCommunityTab));
        OnPropertyChanged(nameof(DemoNotice));
        _ = RefreshAsync();
    }
    partial void OnIsOfflineChanged(bool value) => OnPropertyChanged(nameof(OfflineNotice));
    partial void OnIsDemoModeChanged(bool value) => OnPropertyChanged(nameof(DemoNotice));
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(IsNotLoading));
    }
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnSelectedPackageChanged(ModpackDefinition? value) => OnPropertyChanged(nameof(HasSelectedPackage));
    partial void OnSelectedCommunityChanged(CommunityItemViewModel? value) => OnPropertyChanged(nameof(HasSelectedCommunity));

    public void SetGame(GameCatalogItem? game)
    {
        if (ReferenceEquals(Game, game)) return;
        Game = game;
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(GameName));
        if (!ShowCommunities) _ = RefreshAsync();
    }

    [RelayCommand]
    private void ShowPackagesTab() => ShowCommunities = false;

    [RelayCommand]
    private void ShowCommunitiesTab() => ShowCommunities = true;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        IsOffline = false;
        StatusMessage = "";
        try
        {
            if (ShowCommunities)
            {
                var page = await communitiesApi.ListAsync(SearchText, cancellationToken: cancellationToken);
                Communities.Clear();
                foreach (var item in page.Communities.Where(item => item.AccessMode != CommunityAccessModes.InviteCode || item.IsMember))
                    Communities.Add(new CommunityItemViewModel(item));
                IsOffline = page.IsOffline;
                SelectedCommunity = Communities.FirstOrDefault();
                SelectedPackage = null;
            }
            else if (Game is null)
            {
                Packages.Clear();
                SelectedPackage = null;
                StatusMessage = LocalizationService.Instance["Discover.SelectGame"];
            }
            else
            {
                var page = await packagesApi.SearchAsync(Game.Definition.Slug, SearchText, cancellationToken: cancellationToken);
                Packages.Clear();
                foreach (var item in page.Packages) Packages.Add(item);
                IsOffline = page.IsOffline;
                SelectedPackage = Packages.FirstOrDefault();
                SelectedCommunity = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            IsOffline = true;
            StatusMessage = LocalizationService.Instance["Discover.Offline"];
            if (ShowCommunities) { Communities.Clear(); SelectedCommunity = null; }
            else { Packages.Clear(); SelectedPackage = null; }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(HasCommunities));
            OnPropertyChanged(nameof(HasNoPackages));
            OnPropertyChanged(nameof(HasNoCommunities));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    private void QueueSearch()
    {
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = new CancellationTokenSource();
        _ = SearchAfterTypingAsync(searchCancellation.Token);
    }

    private async Task SearchAfterTypingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }
}
