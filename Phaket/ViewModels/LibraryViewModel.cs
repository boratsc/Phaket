using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class DetectedGameViewModel : ViewModelBase
{
    public DetectedGameViewModel(SteamInstallation installation, GameDefinition? definition)
    {
        Installation = installation;
        Definition = definition;
    }

    public SteamInstallation Installation { get; }
    public GameDefinition? Definition { get; }

    public string Title => Definition?.Title?.GetValueOrDefault(LocalizationService.Instance.CurrentLanguage)
        ?? Definition?.Title?.GetValueOrDefault("en")
        ?? Installation.Manifest.Name;
    public string Build => Installation.Manifest.BuildId;
    public string RootPath => Installation.Installation.RootPath;
    public string StateKey => Installation.Availability switch
    {
        GameAvailability.Moddable => "Library.State.Moddable",
        GameAvailability.Installed => "Library.State.Installed",
        _ => "Library.State.InLibrary",
    };

    public string LaunchInstructions =>
        $"cd \"{RootPath}\" && ./{Definition?.MainExecutable ?? Installation.Manifest.Name}";
}

public partial class LibraryViewModel : ViewModelBase
{
    private readonly IReadOnlyList<GameDefinition> definitions = GameDefinitions.LoadAll();

    public ObservableCollection<DetectedGameViewModel> Games { get; } = [];

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    private DetectedGameViewModel? selectedGame;

    [ObservableProperty]
    private string launchInstructions = "";

    [RelayCommand]
    private void Scan()
    {
        Games.Clear();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var os = OperatingSystem.IsWindows() ? "windows" : "linux";
        var adapter = new SteamAdapter(definitions);
        foreach (var installation in adapter.DetectInstallations(os, home))
        {
            var definition = definitions.FirstOrDefault(d =>
                d.StoreIds?.TryGetValue("steam", out var appId) == true && appId == installation.Manifest.AppId);
            Games.Add(new DetectedGameViewModel(installation, definition));
        }
        Status = Games.Count == 0
            ? LocalizationService.Instance["Library.NoGames"]
            : $"{Games.Count}";
    }

    [RelayCommand]
    private void AddManualFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var adapter = new SteamAdapter(definitions);
        // Manual fallback: unknown appId until the user identifies the game.
        var installation = adapter.FromManualFolder(folder, "manual");
        if (installation is not null)
            Games.Add(new DetectedGameViewModel(installation, null));
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
        if (folders.Count > 0)
            AddManualFolder(folders[0].Path.LocalPath);
    }

    [RelayCommand]
    private void PrepareLaunch(DetectedGameViewModel? game)
    {
        if (game is null) return;
        SelectedGame = game;
        // PoC: copy-instruction fallback (ADR-045); no launcher file patching,
        // no automatic execution without consent.
        LaunchInstructions = game.LaunchInstructions;
    }
}
