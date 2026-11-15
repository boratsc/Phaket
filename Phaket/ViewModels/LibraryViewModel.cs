using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Avalonia.Threading;
using Phaket.Core.Accounts;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Phaket.ViewModels;

public partial class LibraryViewModel : ViewModelBase
{
    private readonly IReadOnlyList<GameDefinition> definitions = GameDefinitions.LoadAll();
    private readonly Action<GameCatalogItem> openGame;
    private readonly List<GameCatalogItem> catalog = [];
    private readonly IAccountService epic;
    private IAccountService? steam;
    private Func<AccountInfo, Task<IReadOnlyList<OwnedSteamGame>>>? steamOwnedGames;
    private Func<IReadOnlyList<DepotRecordedInstallation>>? steamRecords;
    private int steamRefreshVersion;
    private readonly Func<AccountInfo, Task<IReadOnlyList<OwnedEpicGame>>> ownedGames;
    private readonly Func<IReadOnlyList<LegendaryRecordedInstallation>> recordedInstallations;
    private readonly Dictionary<GameCatalogItem, OwnedEpicGame> epicGames = [];
    private readonly Dictionary<GameCatalogItem, OwnedSteamGame> steamGames = [];
    private LegendaryInstallAdapter? installAdapter;
    // Steam install credentials come from the platform secret store (PAK-60); null when
    // the view model is built without AccountServices (test seams), which keeps the old manual flow.
    private SteamCredentialStore? steamCredentials;
    private static readonly HttpClient epicHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private int refreshVersion;
    private readonly Action? openAccounts;

    public LibraryViewModel(Action<GameCatalogItem> openGame) : this(openGame, new AccountServices(), null) { }

    public LibraryViewModel(Action<GameCatalogItem> openGame, AccountServices accounts, Action? openAccounts)
        : this(openGame, accounts.Epic, openAccounts,
            account => new EpicLibraryService(async () => (await LegendaryTokenProvider.From(accounts.Epic)(CancellationToken.None)).AccessToken,
                epicHttp, account.UserId, GameDefinitions.LoadAll()).GetOwnedGamesAsync(),
            () => new LegendaryInstallAdapter(LegendaryTokenProvider.From(accounts.Epic)).GetRecordedInstallations())
    {
        installAdapter = new LegendaryInstallAdapter(LegendaryTokenProvider.From(accounts.Epic));
        steamCredentials = accounts.SteamCredentials;
        ConfigureSteam(accounts.Steam,
            account => new SteamLibraryService(accounts.GetSteamApiKeyAsync, account.UserId, epicHttp, definitions).GetOwnedGamesAsync(),
            () => new DepotDownloaderInstallAdapter().GetRecordedInstallations());
    }

    public LibraryViewModel(Action<GameCatalogItem> openGame, IAccountService epic, Action? openAccounts,
        Func<AccountInfo, Task<IReadOnlyList<OwnedEpicGame>>> ownedGames,
        Func<IReadOnlyList<LegendaryRecordedInstallation>> recordedInstallations, IAccountService steam,
        Func<AccountInfo, Task<IReadOnlyList<OwnedSteamGame>>> steamOwnedGames,
        Func<IReadOnlyList<DepotRecordedInstallation>> steamRecords)
        : this(openGame, epic, openAccounts, ownedGames, recordedInstallations) => ConfigureSteam(steam, steamOwnedGames, steamRecords);

    private void ConfigureSteam(IAccountService account, Func<AccountInfo, Task<IReadOnlyList<OwnedSteamGame>>> fetch,
        Func<IReadOnlyList<DepotRecordedInstallation>> records)
    {
        steam = account;
        steamOwnedGames = fetch;
        steamRecords = records;
        steam.AccountChanged += () => Dispatcher.UIThread.Post(async () => await RefreshSteamAsync());
    }

    public LibraryViewModel(Action<GameCatalogItem> openGame, IAccountService epic, Action? openAccounts,
        Func<AccountInfo, Task<IReadOnlyList<OwnedEpicGame>>> ownedGames,
        Func<IReadOnlyList<LegendaryRecordedInstallation>> recordedInstallations)
    {
        this.openGame = openGame;
        this.epic = epic;
        this.ownedGames = ownedGames;
        this.recordedInstallations = recordedInstallations;
        this.openAccounts = openAccounts;
        epic.AccountChanged += () => Dispatcher.UIThread.Post(async () => { OnPropertyChanged(nameof(EpicSignedIn)); await RefreshEpicAsync(); });
        BuildCatalog();
    }

    public bool EpicSignedIn => epic.Current is not null;
    public bool CanOpenAccounts => openAccounts is not null;

    [RelayCommand(CanExecute = nameof(CanOpenAccounts))]
    private void OpenAccounts() => openAccounts?.Invoke();

    public ObservableCollection<GameCatalogItem> Games { get; } = [];

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool epicError;
    [ObservableProperty] private string steamStatus = "";

    // The library shows the games Phaket can mod, with cover art. Detection
    // fills in real local status; Steam runtimes/tools are never listed.
    private void BuildCatalog()
    {
        Games.Clear();
        foreach (var definition in definitions)
        {
            var cover = $"avares://Phaket/Assets/games/{definition.Slug}.png";
            var heroPath = $"avares://Phaket/Assets/games/{definition.Slug}-hero.png";
            string hero;
            try { hero = Avalonia.Platform.AssetLoader.Exists(new Uri(heroPath)) ? heroPath : cover; }
            catch { hero = cover; } // platform may be unavailable in plain unit tests
            var game = new GameCatalogItem(definition, cover, hero);
            catalog.Add(game);
            Games.Add(game);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        Games.Clear();
        foreach (var game in catalog.Where(game => game.Title.Contains(value ?? "", StringComparison.CurrentCultureIgnoreCase)))
            Games.Add(game);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var os = OperatingSystem.IsWindows() ? "windows" : "linux";
            var adapter = new SteamAdapter(definitions);
            var detected = adapter.DetectInstallations(os, home);
            foreach (var game in catalog)
            {
                game.Installations.Clear();
                foreach (var installation in detected.Where(d => d.Manifest.AppId == game.SteamAppId))
                    game.Installations.Add(installation);
                game.Detected = game.Installations.FirstOrDefault();
            }
            await RefreshEpicAsync();
            await RefreshSteamAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: library scan failed: {ex}");
            Status = LocalizationService.Instance["Library.ScanError"];
        }
    }

    private async Task RefreshEpicAsync()
    {
        int version = ++refreshVersion;
        var account = epic.Current;
        if (account is null)
        {
            ApplyEpic([], []);
            EpicError = false;
            Status = LocalizationService.Instance["Library.EpicSignInHint"];
            return;
        }
        try
        {
            var owned = await ownedGames(account);
            var records = recordedInstallations();
            if (version != refreshVersion || epic.Current != account) return;
            ApplyEpic(owned, records);
            EpicError = false;
            Status = "";
        }
        catch (Exception ex)
        {
            if (version != refreshVersion || epic.Current != account) return;
            ApplyEpic([], []);
            EpicError = true;
            Status = LocalizationService.Instance[ex is PhaketException pe && pe.Error.Code == "EPIC_TOKEN_INVALID"
                ? "Library.EpicTokenInvalid" : "Library.EpicUnavailable"];
        }
    }

    private void ApplyEpic(IReadOnlyList<OwnedEpicGame> owned, IReadOnlyList<LegendaryRecordedInstallation> records)
    {
        epicGames.Clear();
        catalog.RemoveAll(g => g.Definition.Slug.StartsWith("epic-owned-", StringComparison.Ordinal));
        foreach (var item in catalog) { item.EpicOwned = false; item.EpicInstallPath = null; }
        foreach (var game in owned)
        {
            var item = catalog.FirstOrDefault(g => g.Definition.StoreIds?.TryGetValue("epic", out var id) == true &&
                (string.Equals(id, game.AppName, StringComparison.OrdinalIgnoreCase) || string.Equals(id, game.CatalogItemId, StringComparison.OrdinalIgnoreCase) || string.Equals(id, game.Namespace, StringComparison.OrdinalIgnoreCase)));
            // Epic's library can return the catalog ID without the launcher app ID.
            if (item is null && game.CatalogItemId.Equals("729a86a5146640a2ace9e8c595414c56", StringComparison.OrdinalIgnoreCase))
                item = catalog.FirstOrDefault(g => g.Definition.Slug == "among-us");
            if (item is null)
            {
                var key = game.CatalogItemId.Length > 0 ? game.CatalogItemId : game.AppName;
                var slug = "epic-owned-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                item = catalog.FirstOrDefault(g => g.Definition.Slug == slug);
                if (item is null)
                {
                    var definition = new GameDefinition(slug, [], Title: new Dictionary<string, string> { ["en"] = game.Title, ["pl-PL"] = game.Title });
                    item = new GameCatalogItem(definition, "", "");
                    catalog.Add(item);
                }
            }
            item.EpicOwned = true;
            epicGames[item] = game;
            var record = records.FirstOrDefault(r => !string.IsNullOrEmpty(game.AppName) && string.Equals(r.AppName, game.AppName, StringComparison.OrdinalIgnoreCase) && IsValidRecord(r));
            if (record is not null) item.EpicInstallPath = record.TargetPath;
        }
        OnSearchTextChanged(SearchText);
    }

    private async Task RefreshSteamAsync()
    {
        int version = ++steamRefreshVersion;
        var account = steam?.Current;
        if (account is null || steamOwnedGames is null)
        {
            ApplySteam([]);
            SteamStatus = "";
            return;
        }
        try
        {
            var owned = await steamOwnedGames(account);
            if (version != steamRefreshVersion || steam?.Current != account) return;
            ApplySteam(owned);
            SteamStatus = "";
        }
        catch (Exception ex)
        {
            if (version != steamRefreshVersion || steam?.Current != account) return;
            ApplySteam([]);
            var key = (ex as PhaketException)?.Error.Code switch
            {
                "STEAM_API_KEY_MISSING" => "Library.SteamKeyMissing",
                "STEAM_API_KEY_INVALID" => "Library.SteamKeyInvalid",
                "STEAM_LIBRARY_PRIVATE" => "Library.SteamPrivate",
                _ => "Library.SteamUnavailable"
            };
            SteamStatus = LocalizationService.Instance[key];
        }
    }

    private void ApplySteam(IReadOnlyList<OwnedSteamGame> owned)
    {
        steamGames.Clear();
        catalog.RemoveAll(g => g.Definition.Slug.StartsWith("steam-owned-", StringComparison.Ordinal));
        foreach (var item in catalog) item.SteamOwned = false;
        var records = steamRecords?.Invoke() ?? [];
        var adapter = new SteamAdapter(definitions);
        foreach (var record in records)
        {
            var item = catalog.FirstOrDefault(g => g.IsSupported && g.SteamAppId == record.AppId);
            if (item is not null && IsValidSteamRecord(record, item.Definition) && adapter.FromManualFolder(record.TargetPath, record.AppId) is { } installation &&
                !item.Installations.Any(i => i.Installation.RootPath == record.TargetPath))
            {
                item.Installations.Add(installation);
                item.Detected ??= installation;
            }
        }
        foreach (var game in owned)
        {
            var item = catalog.FirstOrDefault(g => g.SteamAppId == game.AppId && g.IsSupported);
            if (item is null)
            {
                var slug = "steam-owned-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(game.AppId)));
                var definition = new GameDefinition(slug, [], Title: new Dictionary<string, string> { ["en"] = game.Name, ["pl-PL"] = game.Name });
                item = new GameCatalogItem(definition, "", "");
                catalog.Add(item);
            }
            item.SteamOwned = true;
            steamGames[item] = game;
        }
        OnSearchTextChanged(SearchText);
    }

    private static bool IsValidSteamRecord(DepotRecordedInstallation record, GameDefinition definition)
    {
        try
        {
            if (Path.IsPathRooted(record.Executable) || string.IsNullOrWhiteSpace(record.Executable) || record.Executable != definition.MainExecutable) return false;
            var root = Path.GetFullPath(record.TargetPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var executable = Path.GetFullPath(Path.Combine(root, record.Executable));
            if (!executable.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(executable)) return false;
            var current = Path.GetPathRoot(executable)!;
            foreach (var part in executable[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static bool IsValidRecord(LegendaryRecordedInstallation record)
    {
        try
        {
            if (Path.IsPathRooted(record.Executable)) return false;
            var root = Path.GetFullPath(record.TargetPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var executable = Path.GetFullPath(Path.Combine(root, record.Executable));
            return executable.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(executable);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    [RelayCommand]
    private void OpenGame(GameCatalogItem? game)
    {
        if (game?.CanOpenGame == true) openGame(game);
    }

    [RelayCommand]
    private void Play(GameCatalogItem? game)
    {
        if (game is null) return;
        if (game.SelectedInstallation?.Store == "steam")
            new LaunchService().LaunchGame(game.SteamAppId);
        else if (game.CanOpenGame)
            openGame(game);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow : null;
        if (top?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Library.AddFolder"],
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var folder = folders[0].Path.LocalPath;
        // Match the chosen folder to a supported game by executable name.
        var match = catalog.FirstOrDefault(g =>
            File.Exists(Path.Combine(folder, g.Definition.MainExecutable ?? "")));
        if (match is not null)
        {
            var adapter = new SteamAdapter(definitions);
            match.Detected = adapter.FromManualFolder(folder, match.SteamAppId);
            if (match.Detected is { } installation && !match.Installations.Any(i => i.Installation.RootPath == folder))
                match.Installations.Add(installation);
        }
    }
}
