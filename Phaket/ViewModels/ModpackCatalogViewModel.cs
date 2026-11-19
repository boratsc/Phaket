using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Threading;
using Phaket.Core.Omrl;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Models;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class ModpackCatalogViewModel : ViewModelBase, IDisposable
{
    private readonly string appDataRoot;
    private readonly ModpackStore store;
    private readonly IModpackApiClient api;
    private readonly IModpackInstallService installer;
    private readonly IModpackPublisher? publisher;
    private readonly Action openProfilesAction;
    private readonly PropertyChangedEventHandler localizationChanged;
    private CancellationTokenSource? searchCancellation;
    private int selectionGeneration;
    private ModpackInstallPlan? pendingPlan;
    private ModpackDefinition? lastPublishedPack;
    private bool hasLoadedPublic;
    private GameCatalogItem? game;
    private bool loadingMore;

    public ModpackCatalogViewModel(GameCatalogItem? game, Action? openProfiles = null)
        : this(game, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket"),
            openProfiles, true) { }

    internal string AppDataRoot => appDataRoot;

    private ModpackCatalogViewModel(GameCatalogItem? game, string appDataRoot, Action? openProfiles, bool autoLoad)
        : this(game, appDataRoot, new ModpackApiClient(CatalogSettings.Load(CatalogSettings.SettingsFile(appDataRoot)), appDataRoot),
            new ModpackInstallService(appDataRoot), openProfiles, autoLoad) { }

    internal ModpackCatalogViewModel(GameCatalogItem? game, string appDataRoot,
        IModpackApiClient? api = null, IModpackInstallService? installer = null,
        Action? openProfiles = null, bool autoLoad = true, IModpackPublisher? publisher = null)
    {
        this.appDataRoot = appDataRoot;
        this.game = game;
        this.openProfilesAction = openProfiles ?? (() => { });
        store = new ModpackStore(appDataRoot);
        this.api = api ?? new ModpackApiClient(CatalogSettings.Load(CatalogSettings.SettingsFile(appDataRoot)), appDataRoot);
        this.installer = installer ?? new ModpackInstallService(appDataRoot);
        this.publisher = publisher ?? ModpackPublishGateway.For(this.api);
        // The shell keeps one catalog view model per window, so the shared localization service
        // must not hold on to it after the window is closed: Dispose removes this handler.
        LocalizationService.Instance.PropertyChanged += localizationChanged = (_, _) =>
        {
            OnPropertyChanged(nameof(GameName));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(MyPackCount));
            OnPropertyChanged(nameof(InstallActionLabel));
            OnPropertyChanged(nameof(ReadyTitle));
            OnPropertyChanged(nameof(ReadyDescription));
            OnPropertyChanged(nameof(ProgressDescription));
            OnPropertyChanged(nameof(PlanSteps));
            OnPropertyChanged(nameof(PublishLabel));
            RefreshInstallWarnings();
        };
        ReloadLocal();
        if (autoLoad) _ = RefreshPublicAsync();
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= localizationChanged;
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = null;
    }

    public ObservableCollection<ModpackDefinition> LocalPacks { get; } = [];
    public ObservableCollection<ModpackDefinition> PublicPacks { get; } = [];
    public ObservableCollection<ModpackDefinition> VisiblePacks { get; } = [];

    [ObservableProperty] private bool showPublic;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isOffline;
    public bool IsRefreshing => IsLoading && ShowPublic;
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(IsLocalEmpty));
        OnPropertyChanged(nameof(IsPublicEmpty));
    }
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string? nextCursor;
    [ObservableProperty] private ModpackDefinition? selectedPack;
    [ObservableProperty] private string? statusKey;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusKey);
    public string ReadyTitle => LocalizationService.Instance[SelectedPack is not null && HasInstalledProfile(SelectedPack)
        ? "Catalog.ReadyInstalledTitle" : "Catalog.ReadyTitle"];
    public string ReadyDescription => LocalizationService.Instance[SelectedPack is not null && HasInstalledProfile(SelectedPack)
        ? "Catalog.ReadyInstalledDescription" : "Catalog.ReadyDescription"];
    [ObservableProperty] private bool isPlanOpen;
    [ObservableProperty] private bool isPlanPreparing;
    [ObservableProperty] private bool isProgressOpen;
    [ObservableProperty] private bool isFailureOpen;
    [ObservableProperty] private bool isInstallRunning;
    [ObservableProperty] private double installProgress;
    // F3: a public package carries no path from this device, so the archive of a Nexus item is picked here first.
    [ObservableProperty] private bool isNexusPickerOpen;
    [ObservableProperty] private NexusManualViewModel? nexusPicker;
    private ModpackMod? pendingNexusItem;
    partial void OnInstallProgressChanged(double value) => OnPropertyChanged(nameof(ProgressDescription));
    [ObservableProperty] private string? installErrorKey;
    [ObservableProperty] private ModPackViewModel? editor;
    public ObservableCollection<string> InstallPlanWarnings { get; } = [];

    public GameCatalogItem? Game
    {
        get => game;
        set
        {
            if (ReferenceEquals(game, value)) return;
            game = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameName));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(HasGame));
            hasLoadedPublic = false;
            PublicPacks.Clear();
            IsOffline = false;
            // The same view model is reused for every game, so nothing from the previous one
            // may stay selected: publication and installation always follow the current game.
            SelectedPack = null;
            StatusKey = null;
            ReloadLocal();
            _ = RefreshPublicAsync();
        }
    }

    public string GameName => Game?.Title ?? LocalizationService.Instance["Shell.NoGame"];
    public string Subtitle => string.Format(LocalizationService.Instance["Catalog.Subtitle"], GameName);
    public string SelectedMeta => SelectedPack is null ? "" : string.Format(LocalizationService.Instance["Catalog.AuthorVersionFormat"], SelectedPack.Author, SelectedPack.Version);
    public string ContentsLabel => SelectedPack is null ? "" : string.Format(LocalizationService.Instance["Catalog.Contents"], SelectedPack.Items.Count);
    public bool HasGame => Game is not null;
    public bool ShowCatalog => Editor is null;
    public bool HasEditor => Editor is not null;
    public bool IsLocalTab => !ShowPublic;
    public bool IsPublicTab => ShowPublic;
    public int MyPackCount => LocalPacks.Count(p => !IsCleanGamePack(p));
    public bool HasSelectedPack => SelectedPack is not null;
    public bool HasNoSelection => SelectedPack is null;
    public bool CanShowEditor => !ShowPublic && SelectedPack is not null;
    public bool IsLocalEmpty => !ShowPublic && VisiblePacks.Count == 0 && !IsLoading;
    public bool IsPublicEmpty => ShowPublic && VisiblePacks.Count == 0 && !IsLoading;
    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText);
    public bool CanLoadMore => ShowPublic && HasMore && !IsLoading;
    public bool IsProfileReady => SelectedPack is not null && HasInstalledProfile(SelectedPack);
    public bool CanInstall => SelectedPack is not null && !IsProfileReady;
    public bool CanPlay => IsProfileReady && Game?.SelectedInstallation?.Store == "steam";
    public bool ShowPlayUnavailable => IsProfileReady && !CanPlay;
    public bool CanInstallButton => CanInstall;
    public string InstallActionLabel => LocalizationService.Instance[IsProfileReady ? "Catalog.Play" : "Catalog.Install"];
    // Publication is offered for the user's own packs only, and never twice at the same time.
    public bool HasPublishAction => publisher is not null && !ShowPublic && SelectedPack is not null;
    public bool CanPublish => HasPublishAction && !IsPublishing;
    public string PublishLabel => LocalizationService.Instance[IsPublishing ? "Catalog.Publishing" : "Catalog.Publish"];
    [ObservableProperty] private bool isPublishing;
    [ObservableProperty] private string? publishMessage;
    public bool HasPublishMessage => !string.IsNullOrWhiteSpace(PublishMessage);
    partial void OnPublishMessageChanged(string? value) => OnPropertyChanged(nameof(HasPublishMessage));
    partial void OnIsPublishingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(PublishLabel));
    }
    public string ProgressDescription => LocalizationService.Instance[InstallProgress < 55 ? "Catalog.InstallDownloading"
        : InstallProgress < 100 ? "Catalog.InstallVerifying" : "Catalog.InstallComplete"];

    public IReadOnlyList<string> PlanSteps => pendingPlan?.Steps.Select(step =>
        step.StartsWith("Catalog.", StringComparison.Ordinal) ? LocalizationService.Instance[step] : step).ToArray() ??
    [
        LocalizationService.Instance["Catalog.PlanCheck"],
        LocalizationService.Instance["Catalog.PlanDownload"],
        LocalizationService.Instance["Catalog.PlanRestore"]
    ];

    private void RefreshInstallWarnings()
    {
        InstallPlanWarnings.Clear();
        if (pendingPlan is null) return;
        foreach (var code in pendingPlan.Warnings)
            InstallPlanWarnings.Add(code == "GITHUB_HASH_UNVERIFIED"
                ? LocalizationService.Instance[code]
                : LocalizationService.Instance["ModInstall.WarningOther"] + " " + code);
    }

    partial void OnStatusKeyChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnShowPublicChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(IsLocalTab));
        OnPropertyChanged(nameof(IsPublicTab));
        OnPropertyChanged(nameof(CanShowEditor));
        OnPropertyChanged(nameof(HasPublishAction));
        OnPropertyChanged(nameof(CanPublish));
        SelectedPack = null;
        StatusKey = null;
        PublishMessage = null;
        Filter();
        if (value && !hasLoadedPublic) _ = RefreshPublicAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        Filter();
        if (ShowPublic) QueuePublicSearch();
    }

    partial void OnSelectedPackChanged(ModpackDefinition? value)
    {
        if (!ReferenceEquals(value, lastPublishedPack)) PublishMessage = null;
        OnPropertyChanged(nameof(ReadyTitle));
        OnPropertyChanged(nameof(ReadyDescription));
        OnPropertyChanged(nameof(SelectedMeta));
        OnPropertyChanged(nameof(ContentsLabel));
        OnPropertyChanged(nameof(HasSelectedPack));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(CanShowEditor));
        OnPropertyChanged(nameof(IsProfileReady));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(ShowPlayUnavailable));
        OnPropertyChanged(nameof(CanInstallButton));
        OnPropertyChanged(nameof(InstallActionLabel));
        OnPropertyChanged(nameof(HasPublishAction));
        OnPropertyChanged(nameof(CanPublish));
        var generation = ++selectionGeneration;
        if (value is not null && ShowPublic && value.Slug is { } slug) _ = LoadDetailsAsync(slug, generation);
    }

    internal void ReloadLocal()
    {
        LocalPacks.Clear();
        if (Game is not null)
            foreach (var pack in store.ForGame(Game.Definition.Slug).Where(p => !IsCleanGamePack(p))) LocalPacks.Add(pack);
        OnPropertyChanged(nameof(MyPackCount));
        Filter();
    }

    private void Filter()
    {
        var source = ShowPublic ? PublicPacks : LocalPacks;
        var query = SearchText.Trim();
        VisiblePacks.Clear();
        foreach (var pack in source.Where(p => query.Length == 0 || Matches(p, query))) VisiblePacks.Add(pack);
        IsEmpty = VisiblePacks.Count == 0 && !IsLoading;
        OnPropertyChanged(nameof(IsLocalEmpty));
        OnPropertyChanged(nameof(IsPublicEmpty));
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private static bool Matches(ModpackDefinition pack, string query) =>
        pack.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || pack.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
        || pack.Items.Any(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static bool IsCleanGamePack(ModpackDefinition pack) =>
        string.Equals(pack.Name.Trim(), "Czysta gra", StringComparison.OrdinalIgnoreCase)
        || string.Equals(pack.Name.Trim(), "Clean game", StringComparison.OrdinalIgnoreCase);

    private bool HasInstalledProfile(ModpackDefinition pack)
    {
        if (Game is null || !string.Equals(pack.GameId, Game.Definition.Slug, StringComparison.OrdinalIgnoreCase)) return false;
        var enabledItems = pack.Items.Where(item => item.Enabled).ToArray();
        return enabledItems.Length > 0 && new ProfileStore(appDataRoot).For(pack.GameId).Profiles.Any(profile =>
            string.Equals(profile.Name, pack.Name, StringComparison.OrdinalIgnoreCase)
            && enabledItems.All(item => profile.Mods.Any(mod => string.Equals(mod.Name, item.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mod.Version, item.Version, StringComparison.OrdinalIgnoreCase))));
    }

    private GameProfile? FindInstalledProfile(ModpackDefinition pack)
    {
        if (Game is null || !string.Equals(pack.GameId, Game.Definition.Slug, StringComparison.OrdinalIgnoreCase)) return null;
        var enabledItems = pack.Items.Where(item => item.Enabled).ToArray();
        return enabledItems.Length == 0 ? null : new ProfileStore(appDataRoot).For(pack.GameId).Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, pack.Name, StringComparison.OrdinalIgnoreCase)
            && enabledItems.All(item => profile.Mods.Any(mod => string.Equals(mod.Name, item.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mod.Version, item.Version, StringComparison.OrdinalIgnoreCase))));
    }

    private void QueuePublicSearch()
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
            await Task.Delay(250, cancellationToken);
            await RefreshPublicAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task RefreshPublicAsync(CancellationToken cancellationToken = default)
    {
        if (Game is null) return;
        IsLoading = true;
        StatusKey = null;
        var gameId = Game.Definition.Slug;
        var query = SearchText;
        try
        {
            var result = await api.SearchAsync(gameId, query, cancellationToken: cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Game?.Definition.Slug != gameId || SearchText != query) return;
                hasLoadedPublic = true;
                PublicPacks.Clear();
                foreach (var pack in result.Packages) PublicPacks.Add(pack);
                NextCursor = result.NextCursor;
                HasMore = result.NextCursor is not null;
                IsOffline = result.IsOffline;
                StatusKey = result.IsOffline ? "Catalog.Offline" : null;
                if (SelectedPack is not null && !PublicPacks.Any(p => p.Slug == SelectedPack.Slug)) SelectedPack = null;
                Filter();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException or IOException or UriFormatException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Game?.Definition.Slug == gameId && SearchText == query)
                {
                    IsOffline = true;
                    StatusKey = "Catalog.Offline";
                    Filter();
                }
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                Filter();
            });
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (Game is null || !HasMore || loadingMore) return;
        loadingMore = true;
        try
        {
            var result = await api.SearchAsync(Game.Definition.Slug, SearchText, NextCursor);
            foreach (var pack in result.Packages) PublicPacks.Add(pack);
            NextCursor = result.NextCursor;
            HasMore = result.NextCursor is not null;
            IsOffline = result.IsOffline;
            Filter();
        }
        finally { loadingMore = false; }
    }

    private async Task LoadDetailsAsync(string slug, int generation)
    {
        try
        {
            var detail = await api.GetAsync(slug);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != selectionGeneration || detail is null) return;
                var current = PublicPacks.FirstOrDefault(p => p.Slug == slug);
                if (current is null) return;
                current.Items = detail.Items;
                current.Description = detail.Description;
                SelectedPack = current;
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException or IOException or JsonException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (generation == selectionGeneration) StatusKey = "Catalog.Offline"; });
        }
    }

    [RelayCommand]
    private void SelectPack(ModpackDefinition? pack)
    {
        SelectedPack = pack;
        StatusKey = null;
    }

    [RelayCommand] private void OpenProfiles() => openProfilesAction();
    [RelayCommand] private void ShowLocal() => ShowPublic = false;
    [RelayCommand] private void ShowPublicPacks() => ShowPublic = true;

    [RelayCommand]
    private void OpenEditor()
    {
        if (SelectedPack is null || ShowPublic) return;
        OpenEditor(SelectedPack, isNew: false);
    }

    [RelayCommand]
    private void CreatePack()
    {
        if (Game is null || ShowPublic) return;
        OpenEditor(new ModpackDefinition
        {
            Name = LocalizationService.Instance["Catalog.NewPack"],
            Author = LocalizationService.Instance["Catalog.LocalAuthor"],
            Version = "1.0.0",
            GameId = Game.Definition.Slug,
        }, isNew: true);
    }

    private void OpenEditor(ModpackDefinition pack, bool isNew)
    {
        Editor = new ModPackViewModel(pack, store, () =>
        {
            Editor = null;
            OnPropertyChanged(nameof(HasEditor));
            OnPropertyChanged(nameof(ShowCatalog));
            ReloadLocal();
        }, AppDataRoot, isNew);
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(HasEditor));
        OnPropertyChanged(nameof(ShowCatalog));
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        var pack = SelectedPack;
        if (publisher is null || pack is null || ShowPublic || IsPublishing || IsInstallRunning || IsPlanPreparing) return;
        if (pack.Items.Count == 0)
        {
            PublishMessage = LocalizationService.Instance["Catalog.PublishInvalid"];
            return;
        }

        IsPublishing = true;
        PublishMessage = null;
        string slug = ModpackPublishGateway.SlugOf(pack);
        bool createNew = pack.PublicVersion is null;
        try
        {
            var result = await publisher.PublishAsync(pack, slug, createNew);
            pack.Slug = result.Slug;
            pack.PublicVersion = result.Version;
            lastPublishedPack = pack;
            try { store.Save(pack); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { System.Diagnostics.Trace.TraceWarning("The published modpack slug could not be saved: {0}", ex); }
            PublishMessage = string.Format(LocalizationService.Instance["Catalog.PublishSuccess"], result.Slug, result.Version);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Modpack publication failed: {0}", ex);
            PublishMessage = LocalizationService.Instance[ex is ArgumentException or InvalidOperationException
                ? "Catalog.PublishInvalid" : "Catalog.PublishFailed"];
        }
        finally { IsPublishing = false; }
    }

    [RelayCommand]
    private async Task StartInstallAsync()
    {
        if (SelectedPack is null || Game is null) return;
        // One installation at a time: the plan and the file writes belong to this view model and
        // navigating back to the tab must not start a second, parallel run of the same pack.
        if (IsInstallRunning || IsPlanPreparing) return;
        if (HasInstalledProfile(SelectedPack))
        {
            if (CanPlay && Game.SteamAppId.Length > 0 && FindInstalledProfile(SelectedPack) is { } profile)
            {
                var profiles = new ProfileStore(appDataRoot);
                var previous = profiles.For(SelectedPack.GameId).ActiveProfileId;
                try
                {
                    profiles.SetActive(SelectedPack.GameId, profile.Id);
                    if (new LaunchService().LaunchGame(Game.SteamAppId)) StatusKey = "Catalog.LaunchStarted";
                    else
                    {
                        if (previous is not null) profiles.SetActive(SelectedPack.GameId, previous);
                        StatusKey = "Catalog.PlayUnavailable";
                    }
                }
                catch (Exception) { StatusKey = "Catalog.PlayUnavailable"; }
            }
            else StatusKey = "Catalog.PlayUnavailable";
            return;
        }
        // ModpackInstallService refuses to plan a public package whose Nexus item has no local archive,
        // so the user is asked for that file before any plan or write happens.
        if (SelectedPack is { IsPublic: true } && FirstNexusItemWithoutArchive(SelectedPack) is { } missing)
        {
            OpenNexusPicker(missing);
            return;
        }
        IsPlanPreparing = true;
        InstallErrorKey = null;
        InstallPlanWarnings.Clear();
        try
        {
            pendingPlan = await installer.PrepareAsync(Game, SelectedPack);
            RefreshInstallWarnings();
            OnPropertyChanged(nameof(PlanSteps));
            if (!pendingPlan.CanInstall)
            {
                InstallErrorKey = pendingPlan.BlockReason ?? "Catalog.InstallPlanFailed";
                IsFailureOpen = true;
                return;
            }
            IsPlanOpen = true;
        }
        catch (PhaketException ex)
        {
            InstallErrorKey = MapInstallError(ex.Error.Code);
            IsFailureOpen = true;
        }
        catch (Exception)
        {
            InstallErrorKey = "Catalog.InstallPlanFailed";
            IsFailureOpen = true;
        }
        finally { IsPlanPreparing = false; }
    }

    private static ModpackMod? FirstNexusItemWithoutArchive(ModpackDefinition pack) =>
        pack.Items.FirstOrDefault(item => item.Enabled && item.Source == "nexus" &&
            !NexusManualViewModel.IsValidArchive(item.ManualPath));

    private void OpenNexusPicker(ModpackMod item)
    {
        pendingNexusItem = item;
        InstallErrorKey = null;
        IsFailureOpen = false;
        var picker = new NexusManualViewModel
        {
            Url = item.Link ?? "",
            Name = item.Name,
            PinnedVersion = item.Version,
        };
        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NexusManualViewModel.Result) && picker.Result is { } picked) ResolveNexusArchive(picked);
        };
        NexusPicker = picker;
        IsNexusPickerOpen = true;
    }

    private void ResolveNexusArchive(NexusManualPickResult picked)
    {
        if (!IsNexusPickerOpen || pendingNexusItem is null) return;
        // Only the path the user just chose is stored; the package definition keeps nothing of it.
        pendingNexusItem.ManualPath = picked.LocalArchivePath;
        CloseNexusPicker();
        _ = StartInstallAsync();
    }

    private void CloseNexusPicker()
    {
        IsNexusPickerOpen = false;
        NexusPicker = null;
        pendingNexusItem = null;
    }

    [RelayCommand]
    private void CancelNexusPicker()
    {
        CloseNexusPicker();
        // A cancelled pick leaves the pack untouched: no plan, no profile, and the reason on screen.
        InstallErrorKey = "Catalog.NexusArchiveSkipped";
        IsFailureOpen = true;
    }

    [RelayCommand]
    private void CancelPlan()
    {
        IsPlanOpen = false;
        pendingPlan = null;
        InstallPlanWarnings.Clear();
    }

    [RelayCommand]
    private async Task ConfirmInstallAsync()
    {
        if (pendingPlan is null || SelectedPack is null || Game is null || !pendingPlan.CanInstall) return;
        if (IsInstallRunning) return;
        IsPlanOpen = false;
        IsProgressOpen = true;
        IsInstallRunning = true;
        InstallProgress = 0;
        InstallErrorKey = null;
        try
        {
            var progress = new Progress<double>(value => InstallProgress = Math.Clamp(value * 100, 0, 100));
            var result = await installer.InstallAsync(Game, SelectedPack, pendingPlan, progress);
            if (!result.Success)
            {
                InstallErrorKey = result.Error is { } error ? MapInstallError(error) : "Catalog.InstallFailed";
                IsProgressOpen = false;
                IsFailureOpen = true;
                return;
            }
            IsProgressOpen = false;
            try
            {
                store.Save(new ModpackDefinition
                {
                    Id = SelectedPack.Slug ?? Guid.NewGuid().ToString("N"),
                    Name = SelectedPack.Name, Author = SelectedPack.Author, Version = SelectedPack.Version,
                    GameId = SelectedPack.GameId, Description = SelectedPack.Description,
                    Items = SelectedPack.Items.Select(item => new ModpackMod
                    {
                        Name = item.Name, Version = item.Version, Source = item.Source, Required = item.Required, Enabled = item.Enabled,
                        Dependencies = [.. item.Dependencies], ModId = item.ModId, Repository = item.Repository,
                        Tag = item.Tag, AssetId = item.AssetId, Sha256 = item.Sha256, Link = item.Link, ManualPath = item.ManualPath
                    }).ToList()
                });
                StatusKey = "Catalog.InstallSuccess";
            }
            catch (Exception) { StatusKey = "Catalog.InstallSuccessNotSaved"; }
            ReloadLocal();
            OnPropertyChanged(nameof(IsProfileReady));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(ShowPlayUnavailable));
            OnPropertyChanged(nameof(InstallActionLabel));
            pendingPlan = null;
        }
        catch (OperationCanceledException)
        {
            InstallErrorKey = "Catalog.InstallFailed";
            IsProgressOpen = false;
            IsFailureOpen = true;
        }
        catch (Exception)
        {
            InstallErrorKey = "Catalog.InstallFailed";
            IsProgressOpen = false;
            IsFailureOpen = true;
        }
        finally { IsInstallRunning = false; }
    }

    private static string MapInstallError(string error) => error switch
    {
        ErrorCodes.ArchiveFormatUnsupported => "Catalog.InstallArchiveUnsupported",
        ErrorCodes.ArtifactHashMismatch => "Catalog.InstallHashMismatch",
        ErrorCodes.ArtifactSizeMismatch => "Catalog.InstallSizeMismatch",
        ErrorCodes.ModSourceHostNotAllowed => "Catalog.InstallHostNotAllowed",
        ErrorCodes.ArtifactDownloadUnavailable => "Catalog.InstallPinnedAssetUnavailable",
        _ => error.StartsWith("Catalog.", StringComparison.Ordinal) ? error : "Catalog.InstallFailed"
    };

    [RelayCommand]
    private async Task RetryInstallAsync()
    {
        IsFailureOpen = false;
        if (pendingPlan is not null) await ConfirmInstallAsync();
        else await StartInstallAsync();
    }

    [RelayCommand]
    private void CloseFailure() { IsFailureOpen = false; pendingPlan = null; }
}
