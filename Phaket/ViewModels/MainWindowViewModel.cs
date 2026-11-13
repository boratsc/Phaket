using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase currentPage;

    public LibraryViewModel Library { get; }
    public RecipeStudioViewModel RecipeStudio { get; }
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel()
    {
        Library = new LibraryViewModel();
        RecipeStudio = new RecipeStudioViewModel();
        Settings = new SettingsViewModel();
        currentPage = Library;
    }

    [RelayCommand]
    private void Navigate(string target)
    {
        CurrentPage = target switch
        {
            "studio" => RecipeStudio,
            "settings" => Settings,
            _ => Library,
        };
    }
}
