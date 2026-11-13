using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class CompatibilityViewModel : ViewModelBase
{
    private readonly Action back;
    public GameCatalogItem Game { get; }
    public CompatibilityReport Report { get; }
    public string Title => Game.Title;
    public string Cover => Game.Cover;
    public string RuntimeTier => Report.Runtime?.Tier ?? "";
    public string RuntimeConfidence => Report.Runtime?.Confidence ?? "";
    public bool HasRuntime => Report.Runtime is not null;
    public IReadOnlyList<ModCompatibility> Mods => Report.Mods;
    public int ModCount => Mods.Count;
    public bool AllModsCompatible => Report.AllModsCompatible;
    public string ProtonVersion => "—";
    public string GameSource => "Steam";
    public bool IsCompat => SelectedTab == "compat";
    public bool IsGeneral => SelectedTab == "general";
    public bool IsOther => !IsCompat && !IsGeneral;
    [ObservableProperty] private string selectedTab = "compat";

    public CompatibilityViewModel(GameCatalogItem game, Action back)
    {
        Game = game;
        this.back = back;
        var mods = new ProfileStore().Active(game.Definition.Slug)?.Mods.Where(m => m.Enabled).ToArray() ?? [];
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "protondb.db");
        CompatibilityReport report;
        if (File.Exists(path))
        {
            try
            {
                using var cache = new ProtonDbCache(path);
                report = CompatibilityAnalyzer.Analyze(game.Definition.Slug, cache, mods.Select(m => (m.Name, m.Version, m.Enabled)));
            }
            catch
            {
                // A corrupt/inaccessible cache must never block the view.
                report = CompatibilityAnalyzer.Analyze(game.Definition.Slug, null, mods.Select(m => (m.Name, m.Version, m.Enabled)));
            }
        }
        else report = CompatibilityAnalyzer.Analyze(game.Definition.Slug, null, mods.Select(m => (m.Name, m.Version, m.Enabled)));
        // The analyzer currently assumes compatibility; use the profile's declared values instead.
        var declared = mods.Select(m => new ModCompatibility(m.Name, m.Version, m.Compatible, "")).ToArray();
        Report = report with { Mods = declared, AllModsCompatible = declared.All(m => m.Compatible) };
    }

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompat));
        OnPropertyChanged(nameof(IsGeneral));
        OnPropertyChanged(nameof(IsOther));
    }
    [RelayCommand] private void Back() => back();
    [RelayCommand] private void Tab(string tab) => SelectedTab = tab;
}
