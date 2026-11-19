using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Models;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class GitHubReleasePickerViewModel : ViewModelBase, IDisposable
{
    private readonly GitHubReleaseService service;
    private readonly bool ownsService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object pinLock = new();
    private CancellationTokenSource? request;
    private CancellationToken requestToken;
    private GitHubRelease? selectedRelease;
    private GitHubReleaseAsset? selectedAsset;

    public ObservableCollection<GitHubRelease> Releases { get; } = [];
    public ObservableCollection<GitHubReleaseAsset> Assets { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand))] private string repositoryInput = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool showPrereleases;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand)), NotifyCanExecuteChangedFor(nameof(LoadOlderCommand))]
    private bool isBusy;
    [ObservableProperty] private bool isDemoMode;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private int page;
    [ObservableProperty] private GitHubReleasePickResult? result;
    [ObservableProperty] private bool hasNewerRelease;

    public bool HasReleases => Releases.Count > 0;
    public bool HasVisibleReleases => VisibleReleases.Count > 0;
    public bool HasHiddenPrereleases => HasReleases && !HasVisibleReleases;
    public bool HasAssets => Assets.Count > 0;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public string StatusText => string.IsNullOrEmpty(StatusMessage) ? "" : LocalizationService.Instance[StatusMessage] +
        (IsDemoMode && StatusMessage != "Sources.GitHub.DemoMode" ? " " + LocalizationService.Instance["Sources.GitHub.DemoMode"] : "");
    public bool HasPinnedSelection => PinnedSelection is not null;
    public string PinnedTag => PinnedSelection?.Tag ?? "";
    public string PinnedRepo => PinnedSelection?.Repo ?? "";
    public string PinnedStatus => HasNewerRelease
        ? LocalizationService.Instance["Sources.GitHub.PinnedNewer"].Replace("{0}", PinnedTag, StringComparison.Ordinal)
        : LocalizationService.Instance["Sources.GitHub.Pinned"].Replace("{0}", PinnedTag, StringComparison.Ordinal);
    public GitHubReleasePickResult? PinnedSelection
    {
        get => pinnedSelection;
        private set
        {
            if (SetProperty(ref pinnedSelection, value))
            {
                OnPropertyChanged(nameof(HasPinnedSelection));
                OnPropertyChanged(nameof(PinnedTag));
                OnPropertyChanged(nameof(PinnedRepo));
                OnPropertyChanged(nameof(PinnedStatus));
            }
        }
    }
    private GitHubReleasePickResult? pinnedSelection;

    public GitHubRelease? SelectedRelease
    {
        get => selectedRelease;
        set
        {
            if (!SetProperty(ref selectedRelease, value)) return;
            Assets.Clear();
            if (value is not null)
                foreach (var asset in value.Assets) Assets.Add(asset);
            OnPropertyChanged(nameof(HasAssets));
            OnPropertyChanged(nameof(NoAssetsForRelease));
            SelectedAsset = null;
            UpdateResult();
        }
    }

    public GitHubReleaseAsset? SelectedAsset
    {
        get => selectedAsset;
        set
        {
            if (!SetProperty(ref selectedAsset, value)) return;
            UpdateResult();
        }
    }

    public bool NoAssetsForRelease => SelectedRelease is { Assets.Count: 0 };
    public bool ShowReleaseList => HasReleases;

    public GitHubReleasePickerViewModel(GitHubReleaseService? service = null, GitHubReleasePickResult? pinned = null)
    {
        this.service = service ?? new GitHubReleaseService();
        ownsService = service is null;
        PinnedSelection = pinned;
        if (pinned is not null)
        {
            RepositoryInput = pinned.Repo;
            IsDemoMode = pinned.IsDemo;
            _ = CheckForNewerAsync(pinned);
        }
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusText));
    }
    partial void OnIsDemoModeChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnHasMoreChanged(bool value) => LoadOlderCommand.NotifyCanExecuteChanged();
    partial void OnShowPrereleasesChanged(bool value)
    {
        OnPropertyChanged(nameof(VisibleReleases));
        OnPropertyChanged(nameof(HasVisibleReleases));
        OnPropertyChanged(nameof(HasHiddenPrereleases));
        if (!value && SelectedRelease?.Prerelease == true) SelectedRelease = null;
    }
    partial void OnHasNewerReleaseChanged(bool value) => OnPropertyChanged(nameof(PinnedStatus));

    public IReadOnlyList<GitHubRelease> VisibleReleases => Releases.Where(r => ShowPrereleases || !r.Prerelease).ToArray();

    private bool CanSearch() => !IsBusy && !string.IsNullOrWhiteSpace(RepositoryInput);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => LoadPageAsync(1);

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadOlderAsync() => LoadPageAsync(Page + 1);

    private bool CanLoadMore() => !IsBusy && HasMore && !IsDemoMode;

    [RelayCommand]
    private void ShowDemo()
    {
        request?.Cancel();
        IsBusy = false;
        RepositoryInput = "demo/BetterNetworking";
        IsDemoMode = true;
        HasMore = false;
        Page = 0;
        Releases.Clear();
        foreach (var release in GitHubReleaseService.DemoReleases) Releases.Add(release);
        SelectedRelease = null;
        OnPropertyChanged(nameof(HasReleases));
        OnPropertyChanged(nameof(ShowReleaseList));
        StatusMessage = "Sources.GitHub.DemoMode";
        Result = null;
    }

    [RelayCommand]
    private void ClearPin()
    {
        PinnedSelection = null;
        Result = null;
        HasNewerRelease = false;
    }

    private async Task LoadPageAsync(int requestedPage)
    {
        var repo = GitHubReleaseService.ParseRepository(RepositoryInput);
        if (repo is null)
        {
            StatusMessage = "Sources.GitHub.InvalidRepository";
            return;
        }
        request?.Cancel();
        request?.Dispose();
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = requestToken = request.Token;
        IsBusy = true;
        SearchCommand.NotifyCanExecuteChanged();
        LoadOlderCommand.NotifyCanExecuteChanged();
        if (requestedPage == 1)
        {
            Releases.Clear();
            HasMore = false;
            Page = 0;
            IsDemoMode = false;
            StatusMessage = "";
            SelectedRelease = null;
            OnPropertyChanged(nameof(HasReleases));
            OnPropertyChanged(nameof(ShowReleaseList));
            Result = null;
            HasNewerRelease = false;
        }
        try
        {
            var releasePage = await service.GetReleasesAsync(repo, requestedPage, token);
            var releases = releasePage.Releases;
            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (requestedPage == 1) Releases.Clear();
                IsDemoMode = false;
                foreach (var release in releases)
                    if (!Releases.Any(r => r.Id == release.Id)) Releases.Add(release);
                Page = requestedPage;
                HasMore = releasePage.HasMore;
                OnPropertyChanged(nameof(HasReleases));
                OnPropertyChanged(nameof(ShowReleaseList));
                OnPropertyChanged(nameof(VisibleReleases));
                OnPropertyChanged(nameof(HasVisibleReleases));
                OnPropertyChanged(nameof(HasHiddenPrereleases));
                if (requestedPage == 1 && releases.Count > 0) StatusMessage = "";
                else if (requestedPage == 1) StatusMessage = "Sources.GitHub.NoReleases";
                Result = null;
                if (PinnedSelection is { } pin && string.Equals(pin.Repo, repo, StringComparison.OrdinalIgnoreCase))
                    _ = CheckForNewerAsync(pin);
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() => EnableDemoFallback("Sources.GitHub.Offline"));
        }
        catch (OperationCanceledException) { }
        catch (GitHubReleaseException ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => EnableDemoFallback(ex.LocalizationKey));
        }
        catch (HttpRequestException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => EnableDemoFallback("Sources.GitHub.Offline"));
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => EnableDemoFallback("Sources.GitHub.RequestFailed"));
        }
        finally
        {
            if (requestToken == token)
            {
                IsBusy = false;
                SearchCommand.NotifyCanExecuteChanged();
                LoadOlderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void EnableDemoFallback(string statusKey)
    {
        StatusMessage = statusKey;
        if (Releases.Count > 0 || IsDemoMode) return;
        IsDemoMode = true;
        HasMore = false;
        Releases.Clear();
        foreach (var release in GitHubReleaseService.DemoReleases) Releases.Add(release);
        OnPropertyChanged(nameof(HasReleases));
        OnPropertyChanged(nameof(ShowReleaseList));
        OnPropertyChanged(nameof(VisibleReleases));
        OnPropertyChanged(nameof(HasVisibleReleases));
        OnPropertyChanged(nameof(HasHiddenPrereleases));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    public void PinSelected()
    {
        if (Result is null) return;
        request?.Cancel();
        lock (pinLock) PinnedSelection = Result;
        HasNewerRelease = false;
        OnPropertyChanged(nameof(PinnedStatus));
        _ = CheckForNewerAsync(PinnedSelection);
    }

    private async Task CheckForNewerAsync(GitHubReleasePickResult? selection)
    {
        if (selection is null || selection.IsDemo) return;
        try
        {
            bool newer = await service.HasNewerReleaseAsync(selection.Repo, selection.Tag, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (pinLock)
                    if (PinnedSelection == selection) HasNewerRelease = newer;
            });
        }
        catch (Exception) { /* A newer-release check is informational; the existing pin remains unchanged. */ }
    }

    private void UpdateResult()
    {
        if (SelectedRelease is null || SelectedAsset is null)
        {
            Result = null;
            return;
        }
        string repo = IsDemoMode ? "demo/BetterNetworking" : GitHubReleaseService.ParseRepository(RepositoryInput) ?? "";
        Result = new GitHubReleasePickResult(repo, SelectedRelease.Tag, SelectedAsset.Id, SelectedAsset.Name,
            SelectedAsset.DownloadUrl, SelectedAsset.Size, IsDemoMode);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.CurrentLanguage))
        {
            OnPropertyChanged(nameof(VisibleReleases));
            OnPropertyChanged(nameof(PinnedStatus));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        request?.Cancel();
        request?.Dispose();
        lifetime.Dispose();
        if (ownsService) service.Dispose();
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
    }
}
