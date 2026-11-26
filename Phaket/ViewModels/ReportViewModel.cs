using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed record ReportResultOption(CompatibilityResult Value, string LabelKey);

public sealed record ReportItem(CompatibilityReportRecord Record)
{
    public string ModId => Record.ModId;
    public string ModVersion => Record.ModVersion;
    public string ResultKey => $"Report.{Record.Result}";
    public string TrustKey => Record.Trust is ReporterTrust.Verified or ReporterTrust.TrustedTester or ReporterTrust.Author
        ? "Report.Trusted" : "Report.Untrusted";
    public string Date => Record.ObservedAt.ToLocalTime().ToString("g");
    public string ResultColor => Record.Result switch
    {
        CompatibilityResult.Working => "#70D6A1",
        CompatibilityResult.Partial => "#F2B84B",
        CompatibilityResult.Broken => "#F08080",
        _ => "#C4C8D0"
    };
}

public partial class ReportViewModel : ViewModelBase
{
    private readonly Action back;
    private readonly string gameId;
    private readonly IReadOnlyList<ModEntry> profileMods;
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "reports.db");
    public string GameTitle { get; }
    public string GameId => gameId;
    public IReadOnlyList<string> Mods { get; }
    public IReadOnlyList<ReportResultOption> Results { get; } =
    [
        new(CompatibilityResult.Working, "Report.Working"),
        new(CompatibilityResult.Partial, "Report.Partial"),
        new(CompatibilityResult.Broken, "Report.Broken")
    ];
    public ObservableCollection<ReportItem> Reports { get; } = [];
    public bool IsEmpty => Reports.Count == 0;
    [ObservableProperty] private string? selectedMod;
    [ObservableProperty] private string modVersion = "";
    [ObservableProperty] private string gameVersion = "";
    [ObservableProperty] private ReportResultOption? selectedResult;
    [ObservableProperty] private string comment = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private int trustedCount;
    [ObservableProperty] private int untrustedCount;
    [ObservableProperty] private int positiveCount;

    public ReportViewModel(GameCatalogItem game, Action back)
    {
        this.back = back;
        gameId = game.Definition.Slug;
        GameTitle = game.Title;
        GameVersion = game.Detected?.Manifest.BuildId ?? "";
        profileMods = new ProfileStore().Active(gameId)?.Mods ?? [];
        Mods = profileMods.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
        try { Refresh(); }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Studio.ErrorTitle"]; }
    }

    partial void OnSelectedModChanged(string? value) =>
        ModVersion = profileMods.FirstOrDefault(m => m.Name == value)?.Version ?? "";

    private void Refresh()
    {
        using var store = new CompatibilityReportStore(StorePath);
        var reports = store.GetReports(gameId);
        var (trusted, untrusted, positive) = store.Summarize(gameId);
        Reports.Clear();
        foreach (var report in reports) Reports.Add(new ReportItem(report));
        TrustedCount = trusted;
        UntrustedCount = untrusted;
        PositiveCount = positive;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand] private void Back() => back();

    [RelayCommand]
    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(SelectedMod) || !Mods.Contains(SelectedMod) ||
            SelectedResult is null || !Results.Contains(SelectedResult))
        {
            StatusMessage = LocalizationService.Instance["Report.Required"];
            return;
        }
        try
        {
            using (var store = new CompatibilityReportStore(StorePath))
                store.Add(new CompatibilityReportRecord(Guid.NewGuid().ToString(), gameId, GameVersion ?? "",
                    OperatingSystem.IsWindows() ? "windows" : "linux", "Proton", SelectedMod,
                    ModVersion ?? "", SelectedResult.Value, ReporterTrust.Untrusted, DateTimeOffset.UtcNow, null, Comment));
            Refresh();
            StatusMessage = LocalizationService.Instance["Report.Submitted"];
        }
        catch (Exception) { StatusMessage = LocalizationService.Instance["Studio.ErrorTitle"]; }
    }
}
