using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Omrl;
using Phaket.Core.Tools;
using Phaket.Services;

namespace Phaket.ViewModels;

// One tool of the "Tools" settings section: the version pin stored in settings.json
// (latest = no pin), the release list pulled from GitHub when the combo is opened, and a
// forced update check through ToolProvider. Network failures end as localized TOOL_*
// messages from Core's error resources, never as a raw exception.
public partial class ToolSettingsSection : ObservableObject
{
    private readonly SettingsViewModel settings;
    private readonly ToolProvider provider;
    private readonly string toolsRoot;
    private IReadOnlyList<string> tags = [];
    private string? pin;

    public string Tool { get; }
    public string TitleKey => "Settings.Tools." + (Tool == "legendary" ? "Legendary" : "Depot");
    public string Title => Loc(TitleKey);

    // The combo shows display strings; the localized "latest" entry stands for a null pin.
    public ObservableCollection<string> Versions { get; } = [];

    [ObservableProperty] private string? selectedVersion;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand)),
     NotifyCanExecuteChangedFor(nameof(ReloadReleasesCommand))] private bool busy;
    [ObservableProperty] private string installedVersion = "";
    [ObservableProperty] private string releasesStatus = "";
    [ObservableProperty] private string updateStatus = "";
    [ObservableProperty] private bool releasesFailed;

    public bool HasReleasesStatus => !string.IsNullOrEmpty(ReleasesStatus);
    partial void OnReleasesStatusChanged(string value) => OnPropertyChanged(nameof(HasReleasesStatus));

    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);
    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(HasUpdateStatus));

    public string InstalledText => InstalledVersion.Length > 0
        ? Loc("Settings.Tools.Installed") + " " + InstalledVersion
        : Loc("Settings.Tools.NotInstalled");
    partial void OnInstalledVersionChanged(string value) => OnPropertyChanged(nameof(InstalledText));

    public string LatestLabel => Loc("Settings.Tools.Latest");

    internal ToolSettingsSection(SettingsViewModel settings, ToolProvider provider, string toolsRoot, string tool)
    {
        this.settings = settings;
        this.provider = provider;
        this.toolsRoot = toolsRoot;
        Tool = tool;
        pin = ToolSettings.GetPinnedVersion(tool, settings.SettingsFile);
        settings.NoteToolPin(Tool, pin);
        InstalledVersion = ScanInstalled();
        Rebuild();
        // The pinned "latest" entry is a translated string, so it is rebuilt when the language changes.
        // shortcut: lives as long as the settings page (one instance per app run); unsubscribe if the
        // page is ever recreated at runtime.
        LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.CurrentLanguage)) Rebuild();
        };
    }

    // Rebuild and the constructor assign the selection themselves; only a user choice writes.
    private bool applying;

    partial void OnSelectedVersionChanged(string? value)
    {
        if (applying) return;
        // The combo resets its selection when the item list is rebuilt: that is not a user choice.
        if (value is null) { Rebuild(); return; }
        pin = value == LatestLabel ? null : value;
        UpdateStatus = settings.SetToolPin(Tool, pin) ? "" : Loc("Settings.Tools.SaveFailed");
        RefreshInstalled();
    }

    // Lazy load: opening the combo (or the keyboard equivalent) is the request for the list,
    // and it is also when an install done elsewhere becomes visible. GitHub answers only once
    // per run (or again after a failure), because unauthenticated calls are rate limited.
    internal void OnDropDownOpened()
    {
        RefreshInstalled();
        if (tags.Count == 0 || ReleasesFailed) _ = ReloadReleasesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private Task ReloadReleasesAsync() => LoadReleasesAsync();

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private async Task CheckUpdatesAsync()
    {
        if (Busy) return;
        Busy = true;
        string before = InstalledVersion;
        try
        {
            await provider.EnsureAsync(Tool, pin, forceRefresh: true);
            UpdateStatus = Loc(ScanInstalled() is { Length: > 0 } now && now != before
                ? "Settings.Tools.Updated" : "Settings.Tools.UpToDate");
        }
        catch (Exception ex)
        {
            UpdateStatus = Describe(ex);
        }
        finally { Busy = false; RefreshInstalled(); }
    }

    private bool CanQuery => !Busy;

    private async Task LoadReleasesAsync()
    {
        if (Busy) return;
        Busy = true;
        ReleasesFailed = false;
        ReleasesStatus = "";
        try
        {
            tags = await provider.ListReleasesAsync(Tool);
            await Dispatcher.UIThread.InvokeAsync(() => { ReleasesFailed = false; ReleasesStatus = ""; Rebuild(); });
        }
        catch (Exception ex)
        {
            // Offline is the normal state here: report it and keep whatever is already listed.
            await Dispatcher.UIThread.InvokeAsync(() => { ReleasesFailed = true; ReleasesStatus = Describe(ex); });
        }
        finally { Busy = false; }
    }

    // Rebuild preserves the current selection; an unknown pin stays visible so the user can clear it.
    private void Rebuild()
    {
        string? previous = pin;
        applying = true;
        try
        {
            Versions.Clear();
            Versions.Add(LatestLabel);
            foreach (string tag in tags) Versions.Add(tag);
            if (previous is not null && !Versions.Contains(previous)) Versions.Add(previous);
            OnPropertyChanged(nameof(LatestLabel));
            OnPropertyChanged(nameof(Title));
            SelectedVersion = previous ?? LatestLabel;
        }
        finally { applying = false; }
    }

    private void RefreshInstalled()
    {
        string found = ScanInstalled();
        if (found == InstalledVersion) return;
        InstalledVersion = found;
        Rebuild();
    }

    // Installed versions are the folders under tools/<tool>/; ".stage-*" and ".updatecheck" are internal.
    private string ScanInstalled()
    {
        try
        {
            string root = Path.Combine(toolsRoot, Tool);
            if (!Directory.Exists(root)) return "";
            string binary = Tool == "legendary" ? "" : OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader";
            string? newest = Directory.GetDirectories(root).Select(Path.GetFileName)
                .Where(v => v is not null && !v!.StartsWith('.') &&
                            (binary.Length == 0 || File.Exists(Path.Combine(root, v!, binary))))
                .OrderByDescending(VersionOf)
                .FirstOrDefault();
            return newest ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // Tags read "0.21.1" or "DepotDownloader_3.4.0": compare the numeric tail, because
    // sorting the strings puts "0.9.0" above "0.21.1".
    private static Version VersionOf(string? tag)
    {
        if (tag is null) return new Version(0, 0);
        int cut = tag.LastIndexOfAny(['_', 'v', 'V']) + 1;
        return Version.TryParse(tag[cut..], out var version) ? version : new Version(0, 0);
    }

    // TOOL_* codes resolve through Core's error resources in the user's language.
    private static string Describe(Exception ex) => ex switch
    {
        PhaketException pe => ErrorMessages.Format(pe.Error.Code, LocalizationService.Instance.CurrentLanguage, pe.Error.Parameters),
        HttpRequestException or TaskCanceledException or IOException => ErrorMessages.Format("TOOL_OFFLINE", LocalizationService.Instance.CurrentLanguage),
        _ => ErrorMessages.Format("TOOL_DOWNLOAD_FAILED", LocalizationService.Instance.CurrentLanguage)
    };

    private static string Loc(string key) => LocalizationService.Instance[key];
}
