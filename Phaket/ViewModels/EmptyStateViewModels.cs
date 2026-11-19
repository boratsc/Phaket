using Phaket.Services;

namespace Phaket.ViewModels;

public abstract class EmptyStateViewModel : ViewModelBase
{
    protected EmptyStateViewModel(string titleKey, string subtitleKey, string messageKey)
    {
        TitleKey = titleKey;
        SubtitleKey = subtitleKey;
        MessageKey = messageKey;
        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(EmptyMessage));
        };
    }

    protected string TitleKey { get; }
    protected string SubtitleKey { get; }
    protected string MessageKey { get; }
    public string Title => LocalizationService.Instance[TitleKey];
    public virtual string Subtitle => LocalizationService.Instance[SubtitleKey];
    public string EmptyTitle => LocalizationService.Instance["Shell.ComingSoon"];
    public string EmptyMessage => LocalizationService.Instance[MessageKey];
}

