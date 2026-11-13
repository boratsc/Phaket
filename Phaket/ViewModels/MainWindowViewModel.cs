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
    public GameViewModel? Game { get; private set; }

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
        Game = new GameViewModel(game, () => Navigate("library"));
        CurrentPage = Game;
        ActiveNav = "library";
        OnPropertyChanged(nameof(Game));
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
