using System.Collections.ObjectModel;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed class GuideStepViewModel(int number, string text)
{
    public int Number { get; } = number;
    public string Text { get; } = text;
}

public sealed class GuideViewModel(string id, string title, string summary, IEnumerable<string> steps)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Summary { get; } = summary;
    public IReadOnlyList<GuideStepViewModel> Steps { get; } = steps.Select((text, index) => new GuideStepViewModel(index + 1, text)).ToArray();
}

public sealed class HelpViewModel : ViewModelBase
{
    private readonly LocalizationService loc = LocalizationService.Instance;
    private GameCatalogItem? game;
    private GuideViewModel? selectedGuide;
    private string searchText = "";
    private readonly List<GuideViewModel> allGuides = [];

    public HelpViewModel()
    {
        loc.PropertyChanged += (_, _) => RefreshGuides();
        RefreshGuides();
    }
    public HelpViewModel(GameCatalogItem? game) : this() => Game = game;

    public ObservableCollection<GuideViewModel> Guides { get; } = [];
    public IReadOnlyList<GuideViewModel> AllGuides => allGuides;
    public GameCatalogItem? Game
    {
        get => game;
        set
        {
            if (ReferenceEquals(game, value)) return;
            game = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameName));
            OnPropertyChanged(nameof(HasGame));
            RefreshGuides();
        }
    }

    public string GameName => Game?.Title ?? "";
    public string EmptyTitle => loc["Guides.EmptyTitle"];
    public string EmptyMessage => loc["Guides.EmptyMessage"];
    public string EmptyList => loc["Guides.EmptyList"];
    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value)) ApplyFilter();
        }
    }
    public bool HasGuides => Guides.Count > 0;
    public bool HasGame => Game is not null;
    public bool HasSelectedGuide => SelectedGuide is not null;

    public GuideViewModel? SelectedGuide
    {
        get => selectedGuide;
        set
        {
            if (!SetProperty(ref selectedGuide, value)) return;
            OnPropertyChanged(nameof(HasSelectedGuide));
        }
    }

    private void RefreshGuides()
    {
        var previousId = SelectedGuide?.Id;
        allGuides.Clear();
        allGuides.AddRange(new[] { "Guides.General.ProfilePackage", "Guides.General.Compatibility",
            "Guides.Valheim.FirstSetup", "Guides.AmongUs.TownOfUs", "Guides.AmongUs.CleanGame" }.Select(CreateGuide));
        ApplyFilter(previousId);
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EmptyList));
    }

    private void ApplyFilter(string? preferredId = null)
    {
        var previousId = preferredId ?? SelectedGuide?.Id;
        var guides = Game is null ? [] : allGuides.Where(guide =>
            guide.Id.StartsWith("Guides.General.", StringComparison.Ordinal)
            || Game.Definition.Slug == "valheim" && guide.Id.StartsWith("Guides.Valheim.", StringComparison.Ordinal)
            || Game.Definition.Slug == "among-us" && guide.Id.StartsWith("Guides.AmongUs.", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(SearchText))
            guides = guides.Where(guide => (guide.Title + " " + guide.Summary).Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));

        var items = guides.ToArray();
        Guides.Clear();
        foreach (var guide in items) Guides.Add(guide);
        SelectedGuide = Guides.FirstOrDefault(guide => guide.Id == previousId) ?? Guides.FirstOrDefault();
        OnPropertyChanged(nameof(HasGuides));
    }

    private GuideViewModel CreateGuide(string key)
    {
        var steps = new List<string>();
        for (var count = 1; count <= 10; count++)
        {
            var step = loc[$"{key}.Step{count}"];
            if (step == $"{key}.Step{count}") break;
            steps.Add(step);
        }
        return new GuideViewModel(key, loc[$"{key}.Title"], loc[$"{key}.Summary"], steps);
    }
}
