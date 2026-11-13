using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Globalization;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class CompatibilityViewModel : ViewModelBase
{
    private readonly Action back;
    public GameCatalogItem Game { get; }
    public CompatibilityReport Report { get; private set; }
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
    [ObservableProperty] private string statusMessage = "";

    public CompatibilityViewModel(GameCatalogItem game, Action back)
    {
        Game = game;
        this.back = back;
        Report = BuildReport();
    }

    private static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "protondb.db");

    private CompatibilityReport BuildReport()
    {
        var game = Game;
        var mods = new ProfileStore().Active(game.Definition.Slug)?.Mods.Where(m => m.Enabled).ToArray() ?? [];
        var path = CachePath;
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
        return report with { Mods = declared, AllModsCompatible = declared.All(m => m.Compatible) };
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            var top = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (top?.StorageProvider is not { } storage)
                throw new InvalidOperationException();
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["Compat.Import"],
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON / TAR.GZ") { Patterns = ["*.json", "*.tar.gz"] }],
            });
            if (files.Count == 0) return;
            var file = files[0];
            if (file.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = LocalizationService.Instance["Compat.Unpack"];
                return;
            }
            await using var stream = await file.OpenReadAsync();
            var source = file.Path.IsFile ? file.Path.LocalPath : file.Path.ToString();
            var count = await Task.Run(() =>
            {
                var definitions = GameDefinitions.LoadAll()
                    .Where(d => d.StoreIds?.ContainsKey("steam") == true)
                    .ToDictionary(d => d.StoreIds!["steam"], StringComparer.Ordinal);
                using var cache = new ProtonDbCache(CachePath);
                return cache.Import(stream, definitions, source, DateTime.UtcNow.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            });
            Report = BuildReport();
            OnPropertyChanged(nameof(Report));
            OnPropertyChanged(nameof(RuntimeTier));
            OnPropertyChanged(nameof(RuntimeConfidence));
            OnPropertyChanged(nameof(HasRuntime));
            OnPropertyChanged(nameof(Mods));
            OnPropertyChanged(nameof(ModCount));
            OnPropertyChanged(nameof(AllModsCompatible));
            StatusMessage = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Compat.Imported"], count);
        }
        catch (Exception)
        {
            StatusMessage = LocalizationService.Instance["Compat.ImportError"];
        }
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
