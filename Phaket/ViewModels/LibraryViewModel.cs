using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class LibraryViewModel : ViewModelBase
{
    private readonly IReadOnlyList<GameDefinition> definitions = GameDefinitions.LoadAll();
    private readonly Action<GameCatalogItem> openGame;
    private readonly List<GameCatalogItem> catalog = [];

    public LibraryViewModel(Action<GameCatalogItem> openGame)
    {
        this.openGame = openGame;
        BuildCatalog();
    }

    public ObservableCollection<GameCatalogItem> Games { get; } = [];

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string status = "";

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
    private void Scan()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var os = OperatingSystem.IsWindows() ? "windows" : "linux";
        var adapter = new SteamAdapter(definitions);
        var detected = adapter.DetectInstallations(os, home);
        foreach (var game in catalog)
        {
            game.Detected = detected.FirstOrDefault(d =>
                d.Manifest.AppId == game.SteamAppId);
        }
        Status = "";
    }

    [RelayCommand]
    private void OpenGame(GameCatalogItem? game)
    {
        if (game is not null) openGame(game);
    }

    [RelayCommand]
    private void Play(GameCatalogItem? game)
    {
        if (game is null) return;
        if (game.IsInstalled)
            new LaunchService().LaunchGame(game.SteamAppId);
        else
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
        }
    }
}
