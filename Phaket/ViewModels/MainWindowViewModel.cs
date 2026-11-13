using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Phaket.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase currentPage;

    [ObservableProperty]
    private string activeNav = "library";

    public LibraryViewModel Library { get; }
    public RecipeStudioViewModel RecipeStudio { get; }
    public SettingsViewModel Settings { get; }
    public HelpViewModel Help { get; } = new();
    public GameViewModel? Game { get; private set; }
    public ModsViewModel? Mods { get; private set; }
    public ProfilesViewModel? Profiles { get; private set; }
    public BackupViewModel? Backup { get; private set; }

    private GameCatalogItem? currentGame;

    public MainWindowViewModel()
    {
        Library = new LibraryViewModel(OpenGame);
        RecipeStudio = new RecipeStudioViewModel();
        Settings = new SettingsViewModel();
        currentPage = Library;
        Library.ScanCommand.Execute(null);
    }

    private void OpenGame(GameCatalogItem game)
    {
        currentGame = game;
        Game = new GameViewModel(game, () => Navigate("library"), OpenMods, OpenProfiles, OpenBackup);
        CurrentPage = Game;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Game));
    }

    public void OpenMods()
    {
        if (currentGame is null) return;
        Mods = new ModsViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Mods;
        ActiveNav = "mods";
        OnPropertyChanged(nameof(Mods));
    }

    public void OpenProfiles()
    {
        if (currentGame is null) return;
        Profiles = new ProfilesViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Profiles;
        ActiveNav = "profiles";
        OnPropertyChanged(nameof(Profiles));
    }

    public void OpenBackup()
    {
        if (currentGame is null) return;
        Backup = new BackupViewModel(currentGame, () => OpenGame(currentGame));
        CurrentPage = Backup;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Backup));
    }

    [RelayCommand]
    private void Navigate(string target)
    {
        switch (target)
        {
            case "studio":
                CurrentPage = RecipeStudio;
                ActiveNav = "studio";
                break;
            case "settings":
                CurrentPage = Settings;
                ActiveNav = "settings";
                break;
            case "help":
                CurrentPage = Help;
                ActiveNav = "help";
                break;
            case "mods":
                currentGame ??= Library.Games.FirstOrDefault();
                if (currentGame is not null) OpenMods();
                else { CurrentPage = Library; ActiveNav = "library"; }
                break;
            case "profiles":
                currentGame ??= Library.Games.FirstOrDefault();
                if (currentGame is not null) OpenProfiles();
                else { CurrentPage = Library; ActiveNav = "library"; }
                break;
            case "game":
                if (Game is not null) CurrentPage = Game;
                ActiveNav = "library";
                break;
            default:
                CurrentPage = Library;
                ActiveNav = "library";
                break;
        }
    }
}
