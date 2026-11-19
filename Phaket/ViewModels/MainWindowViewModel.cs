using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private ViewModelBase currentPage;
    [ObservableProperty] private string activeNav = "catalog";
    [ObservableProperty] private GameCatalogItem? selectedGame;
    [ObservableProperty] private bool isNarrowSidebar;
    [ObservableProperty] private bool isIconSidebar;
    [ObservableProperty] private bool isTopbarPanelOpen;
    [ObservableProperty] private string topbarPanelTitle = "";
    [ObservableProperty] private string topbarPanelMessage = "";

    public LibraryViewModel Library { get; }
    public RecipeStudioViewModel RecipeStudio { get; }
    public SettingsViewModel Settings { get; }
    public HelpViewModel Help { get; } = new();
    public AccountsViewModel Accounts { get; }
    public ThemeService Theme { get; }
    public GameViewModel? Game { get; private set; }
    public ModsViewModel? Mods { get; private set; }
    public ProfilesViewModel? Profiles { get; private set; }
    public BackupViewModel? Backup { get; private set; }
    public CompatibilityViewModel? Compatibility { get; private set; }
    public InstallViewModel? Install { get; private set; }
    public BuildsViewModel? Builds { get; private set; }
    public ShareViewModel? Share { get; private set; }
    public GraphViewModel? Graph { get; private set; }
    public ReportViewModel? Report { get; private set; }
    public ModpackCatalogViewModel? ModpackCatalog { get; private set; }
    public DiscoverViewModel Discover { get; }
    public CommunitiesViewModel Communities { get; }
    public DownloadsViewModel Downloads { get; }

    private readonly string appDataRoot;
    private readonly IModpackApiClient modpackApi;
    private readonly ICommunitiesApiClient communitiesApi;
    private readonly PropertyChangedEventHandler localizationChanged;
    private GameCatalogItem? currentGame;
    private string currentContextTab = "catalog";
    private ViewModelBase previousPage;
    private bool initializing = true;

    public MainWindowViewModel() : this(new AccountServices(), new SettingsViewModel()) { }

    internal MainWindowViewModel(AccountServices accounts, SettingsViewModel settings, string? appDataRoot = null,
        IModpackApiClient? modpackApi = null, ICommunitiesApiClient? communitiesApi = null,
        ISignedRequestSigner? signer = null)
    {
        this.appDataRoot = appDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");
        var endpoint = CatalogSettings.Load(CatalogSettings.SettingsFile(this.appDataRoot));
        this.modpackApi = modpackApi ?? new ModpackApiClient(endpoint, this.appDataRoot);
        this.communitiesApi = communitiesApi ?? new CommunitiesApiClient(endpoint, signer);
        Accounts = new AccountsViewModel(accounts);
        Library = new LibraryViewModel(OpenGame, accounts, () => Navigate("account"));
        RecipeStudio = new RecipeStudioViewModel();
        Settings = settings;
        Theme = new ThemeService(settings.SettingsFile);
        Library.ScanCommand.Execute(null);
        currentGame = Library.Games.FirstOrDefault();
        selectedGame = currentGame;
        // One catalog view model for the life of the window: it owns the install queue, so the
        // tab, the downloads page and the update badge must all look at the same instance.
        currentPage = EnsureModpackCatalog();
        Discover = new DiscoverViewModel(currentGame, this.modpackApi, this.communitiesApi);
        Communities = new CommunitiesViewModel(currentGame, this.appDataRoot, this.communitiesApi, this.modpackApi);
        Downloads = new DownloadsViewModel(this.appDataRoot, currentGame, ModpackCatalog!);
        previousPage = currentPage;
        LocalizationService.Instance.PropertyChanged += localizationChanged = (_, _) =>
        {
            OnPropertyChanged(nameof(GameName));
            OnPropertyChanged(nameof(BreadcrumbLibrary));
            OnPropertyChanged(nameof(BreadcrumbView));
            OnPropertyChanged(nameof(PlatformName));
        };
        initializing = false;
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= localizationChanged;
        ModpackCatalog?.Dispose();
    }

    public string GameName => currentGame?.Title ?? LocalizationService.Instance["Shell.NoGame"];
    public string BreadcrumbLibrary => LocalizationService.Instance["Nav.Library"];
    public string BreadcrumbView => LocalizationService.Instance[ActiveNav switch
    {
        "catalog" => "Catalog.Title",
        "profiles" => "Nav.Profiles",
        "compatibility" => "Compat.Title",
        "help" => "Nav.Guides",
        "library" => "Nav.Library",
        "discover" => "Discover.Title",
        "communities" => "Communities.Title",
        "downloads" => "Downloads.Title",
        "studio" => "Nav.RecipeStudio",
        "settings" => "Nav.Settings",
        "account" => "Account.Nav",
        _ => "Nav.Library"
    }];
    public bool IsGameContext => ActiveNav is "catalog" or "profiles" or "compatibility" or "help";
    public string PlatformName => OperatingSystem.IsWindows()
        ? LocalizationService.Instance["Shell.Platform.Windows"]
        : LocalizationService.Instance["Shell.Platform.Linux"];
    public bool HasGame => currentGame is not null;

    partial void OnActiveNavChanged(string value)
    {
        OnPropertyChanged(nameof(BreadcrumbView));
        OnPropertyChanged(nameof(IsGameContext));
    }

    partial void OnIsNarrowSidebarChanged(bool value)
    {
        Communities.IsCompactLayout = value;
        Discover.IsCompactLayout = value;
        Downloads.IsCompactLayout = value;
    }

    partial void OnSelectedGameChanged(GameCatalogItem? value)
    {
        currentGame = value;
        OnPropertyChanged(nameof(GameName));
        OnPropertyChanged(nameof(HasGame));
        Discover.SetGame(value);
        Communities.SetGame(value);
        Downloads.SetGame(value);
        if (ModpackCatalog is not null) ModpackCatalog.Game = value;
        if (!initializing && IsGameContext) ShowContextTab(currentContextTab);
    }

    private void OpenGame(GameCatalogItem game)
    {
        currentGame = game;
        SelectedGame = game;
        Game = new GameViewModel(game, () => Navigate("library"), OpenMods, OpenProfiles, OpenBackup, OpenCompatibility, OpenInstall, OpenBuilds, OpenShare, OpenGraph, OpenReport);
        CurrentPage = Game;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Game));
    }

    private ModpackCatalogViewModel EnsureModpackCatalog()
    {
        if (ModpackCatalog is not null) return ModpackCatalog;
        ModpackCatalog = new ModpackCatalogViewModel(currentGame, appDataRoot, modpackApi, openProfiles: OpenProfiles);
        OnPropertyChanged(nameof(ModpackCatalog));
        return ModpackCatalog;
    }

    // Leaving the communities page must stop the chat: its polling loop belongs to a view model
    // owned by another workstream, so the shell only calls the public command it exposes.
    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        if (ReferenceEquals(previousPage, Communities) && !ReferenceEquals(value, Communities))
            Communities.CloseChatCommand.Execute(null);
        previousPage = value;
    }

    private void ShowContextTab(string tab)
    {
        currentContextTab = tab;
        switch (tab)
        {
            case "profiles" when currentGame is not null:
                Profiles = new ProfilesViewModel(currentGame, () => OpenGame(currentGame), Accounts.ProfilePublisher);
                CurrentPage = Profiles;
                ActiveNav = "profiles";
                OnPropertyChanged(nameof(Profiles));
                break;
            case "compatibility" when currentGame is not null:
                Compatibility = new CompatibilityViewModel(currentGame, () => OpenGame(currentGame));
                CurrentPage = Compatibility;
                ActiveNav = "compatibility";
                OnPropertyChanged(nameof(Compatibility));
                break;
            case "help":
                CurrentPage = Help;
                ActiveNav = "help";
                break;
            default:
                var catalog = EnsureModpackCatalog();
                // The view model survives the tab switch, so its local list is re-read here
                // instead of by building a second catalog instance with a fresh install queue.
                catalog.ReloadLocal();
                CurrentPage = catalog;
                ActiveNav = "catalog";
                break;
        }
        OnPropertyChanged(nameof(IsGameContext));
        OnPropertyChanged(nameof(BreadcrumbView));
    }

    public void OpenMods()
    {
        if (currentGame is null) return;
        Mods = new ModsViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Mods;
        ActiveNav = "mods";
        OnPropertyChanged(nameof(Mods));
    }

    public void OpenProfiles() => ShowContextTab("profiles");

    public void OpenBackup()
    {
        if (currentGame is null) return;
        Backup = new BackupViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Backup;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Backup));
    }

    public void OpenCompatibility() => ShowContextTab("compatibility");

    public void OpenInstall()
    {
        if (currentGame is null) return;
        Install = new InstallViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Install;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Install));
    }

    public void OpenBuilds()
    {
        if (currentGame is null) return;
        Builds = new BuildsViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Builds;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Builds));
    }

    public void OpenShare()
    {
        if (currentGame is null) return;
        Share = new ShareViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Share;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Share));
    }

    public void OpenGraph()
    {
        if (currentGame is null) return;
        Graph = new GraphViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Graph;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Graph));
    }

    public void OpenReport()
    {
        if (currentGame is null) return;
        Report = new ReportViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Report;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Report));
    }

    [RelayCommand]
    private void Navigate(string target)
    {
        switch (target)
        {
            case "catalog": ShowContextTab("catalog"); break;
            case "profiles": ShowContextTab("profiles"); break;
            case "compatibility": ShowContextTab("compatibility"); break;
            case "help": ShowContextTab("help"); break;
            case "discover": CurrentPage = Discover; ActiveNav = "discover"; Discover.RefreshCommand.Execute(null); break;
            case "communities": CurrentPage = Communities; ActiveNav = "communities"; Communities.RefreshCommand.Execute(null); break;
            case "downloads": CurrentPage = Downloads; ActiveNav = "downloads"; break;
            case "studio": CurrentPage = RecipeStudio; ActiveNav = "studio"; break;
            case "settings": CurrentPage = Settings; ActiveNav = "settings"; break;
            case "account": CurrentPage = Accounts; ActiveNav = "account"; break;
            case "mods":
                currentGame ??= Library.Games.FirstOrDefault();
                if (currentGame is not null) OpenMods();
                else { CurrentPage = Library; ActiveNav = "library"; }
                break;
            case "game": ShowContextTab("catalog"); break;
            case "library": CurrentPage = Library; ActiveNav = "library"; break;
            default: CurrentPage = Library; ActiveNav = "library"; break;
        }
        OnPropertyChanged(nameof(IsGameContext));
        OnPropertyChanged(nameof(BreadcrumbView));
    }

    [RelayCommand]
    private void NavigateBreadcrumb(string? target)
    {
        if (target == "library") Navigate("library");
        else if (target == "catalog") Navigate("catalog");
        else if (target is not null) Navigate(target);
    }

    [RelayCommand]
    private void ToggleTheme() => Theme.Toggle();

    [RelayCommand]
    private void OpenTopbarPanel(string target)
    {
        if (target == "chat")
        {
            IsTopbarPanelOpen = false;
            Navigate("communities");
            Communities.OpenChatCommand.Execute(null);
            return;
        }

        TopbarPanelTitle = LocalizationService.Instance["Shell.NotificationsTitle"];
        var updates = GetAvailableModpackUpdates();
        TopbarPanelMessage = updates.Count > 0
            ? string.Format(LocalizationService.Instance["Downloads.NotificationUpdates"], string.Join(Environment.NewLine, updates))
            : LocalizationService.Instance[ModpackCatalog?.IsOffline == true
                ? "Downloads.NotificationOffline" : "Downloads.NotificationEmpty"];
        IsTopbarPanelOpen = true;
    }

    private IReadOnlyList<string> GetAvailableModpackUpdates()
    {
        if (ModpackCatalog is null) return [];
        var publicPacks = ModpackCatalog.PublicPacks.Where(pack => !string.IsNullOrWhiteSpace(pack.Slug))
            .ToDictionary(pack => pack.Slug!, StringComparer.OrdinalIgnoreCase);
        return ModpackCatalog.LocalPacks
            .Select(pack =>
            {
                var slug = pack.Slug ?? pack.Id;
                var installed = pack.PublicVersion ?? (int.TryParse(pack.Version, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var version) ? version : 0);
                return publicPacks.TryGetValue(slug, out var remote) && remote.PublicVersion is { } latest && latest > installed
                    ? string.Format(LocalizationService.Instance["Downloads.UpdateItem"], pack.Name, remote.Version)
                    : null;
            })
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
    }

    [RelayCommand]
    private void CloseTopbarPanel() => IsTopbarPanelOpen = false;
}
