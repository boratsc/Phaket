using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Phaket.Services;

namespace Phaket.ViewModels;

public abstract class EmptyStateViewModel : ViewModelBase
{
    protected EmptyStateViewModel(string titleKey, string subtitleKey, string messageKey, string emptyTitleKey = "Shell.ComingSoon")
    {
        TitleKey = titleKey;
        SubtitleKey = subtitleKey;
        MessageKey = messageKey;
        EmptyTitleKey = emptyTitleKey;
        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(HasAction));
        };
    }

    protected string TitleKey { get; set; }
    protected string SubtitleKey { get; }
    protected string MessageKey { get; }
    protected string EmptyTitleKey { get; }
    public virtual string Title => LocalizationService.Instance[TitleKey];
    public virtual string Subtitle => LocalizationService.Instance[SubtitleKey];
    public string EmptyTitle => LocalizationService.Instance[EmptyTitleKey];
    public string EmptyMessage => LocalizationService.Instance[MessageKey];
    public virtual MaterialIconKind Icon => MaterialIconKind.PackageVariant;
    public virtual string? ActionLabel => null;
    public virtual ICommand? ActionCommand => null;
    public bool HasAction => ActionLabel is not null;
}

// One page for the four game-context tabs when no game is selected: none of them can render
// without a game, so they share this prompt instead of showing a broken or blank screen.
// The shell keeps a single instance and only swaps the heading of the tab that was clicked.
public sealed class SelectGameViewModel : EmptyStateViewModel
{
    private readonly RelayCommand openLibrary;

    // The default heading is the catalog tab, the one the shell opens first; ShowTab swaps it
    // when the user clicks another context tab without a game.
    public SelectGameViewModel(Action openLibrary) : base("Catalog.Title", "", "Shell.SelectGameMessage", "Shell.SelectGame")
    {
        ArgumentNullException.ThrowIfNull(openLibrary);
        this.openLibrary = new RelayCommand(openLibrary);
    }

    public override MaterialIconKind Icon => MaterialIconKind.GamepadVariant;
    public override string Subtitle => "";
    public override string ActionLabel => LocalizationService.Instance["Shell.OpenLibrary"];
    public override ICommand ActionCommand => openLibrary;

    public void ShowTab(string titleKey)
    {
        if (TitleKey == titleKey) return;
        TitleKey = titleKey;
        OnPropertyChanged(nameof(Title));
    }
}
