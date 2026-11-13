using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

// A supported game shown as a library card / game hub. Cover art always present;
// status reflects real local detection. Technical facts live behind "Szczegóły".
public partial class GameCatalogItem : ViewModelBase
{
    public GameCatalogItem(GameDefinition definition, string cover, string hero)
    {
        Definition = definition;
        Cover = cover;
        Hero = hero;
    }

    public GameDefinition Definition { get; }
    public string Cover { get; }
    public string Hero { get; }
    public string Title => Definition.Title?.GetValueOrDefault(LocalizationService.Instance.CurrentLanguage)
        ?? Definition.Title?.GetValueOrDefault("en") ?? Definition.Slug;
    public string SteamAppId => Definition.StoreIds?.GetValueOrDefault("steam") ?? "";

    [ObservableProperty] private SteamInstallation? detected;
    [ObservableProperty] private string profileName = "Profil domyślny";
    [ObservableProperty] private int modCount;

    public bool IsInstalled => Detected is not null && Directory.Exists(Detected.Installation.RootPath);
    public string StatusKey => IsInstalled ? "Library.State.Moddable" : "Library.State.NotDetected";
    public string Build => Detected?.Manifest.BuildId ?? "—";
    public string RootPath => Detected?.Installation.RootPath ?? "—";

    partial void OnDetectedChanged(SteamInstallation? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusKey));
        OnPropertyChanged(nameof(Build));
        OnPropertyChanged(nameof(RootPath));
    }
}

public partial class GameViewModel : ViewModelBase
{
    private readonly LaunchService launch = new();
    private readonly Action back;

    public GameViewModel(GameCatalogItem game, Action back)
    {
        Game = game;
        this.back = back;
    }

    public GameCatalogItem Game { get; }
    public string Hero => Game.Hero;
    public string Title => Game.Title;

    [ObservableProperty] private bool showDetails;
    [ObservableProperty] private string statusMessage = "";

    public string StatusKey => Game.IsInstalled ? "Game.Ready" : "Game.NotDetected";
    public string StatusHint => Game.IsInstalled ? "Game.ReadyHint" : "Game.NotDetectedHint";

    [RelayCommand]
    private void Back() => back();

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [RelayCommand]
    private void Play()
    {
        var loc = LocalizationService.Instance;
        if (!Game.IsInstalled)
        {
            StatusMessage = loc["Game.NotDetectedHint"];
            return;
        }
        // One click: launch through Steam. Mod launch options (if any) are applied
        // separately with consent — never a manual copy box.
        var ok = launch.LaunchGame(Game.SteamAppId);
        StatusMessage = ok ? loc["Game.Launched"] : loc["Game.LaunchFailed"];
    }

    [RelayCommand]
    private void ApplyModLaunchOptions()
    {
        var loc = LocalizationService.Instance;
        var root = SteamLaunchOptionsService.FindSteamRoot();
        if (root is null || !Game.IsInstalled)
        {
            StatusMessage = loc["Game.NoSteam"];
            return;
        }
        var service = new SteamLaunchOptionsService(root);
        var backups = service.Apply(Game.SteamAppId, "WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%");
        StatusMessage = backups.Count > 0 ? loc["Game.OptionsApplied"] : loc["Game.NoSteam"];
    }
}
