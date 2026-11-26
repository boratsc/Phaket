using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-144: read-only duplicate report, shown in Guides. It lists every copy of a
// game found in more than one Steam library and explains which one the launcher
// most likely starts. Nothing here deletes or modifies a game folder.
public sealed partial class DuplicateDiagnosticsViewModel : ViewModelBase
{
    [ObservableProperty] private string status = "";
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<DuplicateInstallGroupItem> Groups { get; } = [];

    // Test seams: the scan and the folder opener; production never touches either on its own.
    public Func<IReadOnlyList<SteamDuplicateGroup>>? Scanner { get; set; }
    public Action<string>? Opener { get; set; }

    [RelayCommand]
    private void ScanDuplicates()
    {
        try
        {
            var groups = (Scanner ?? DefaultScan)();
            Groups.Clear();
            foreach (var group in groups) Groups.Add(new DuplicateInstallGroupItem(group));
            Status = groups.Count == 0
                ? LocalizationService.Instance["Duplicates.None"]
                : string.Format(LocalizationService.Instance["Duplicates.Found"], groups.Count);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"phaket: duplicate scan failed: {ex}");
            Status = LocalizationService.Instance["Duplicates.Error"];
        }
    }

    private static IReadOnlyList<SteamDuplicateGroup> DefaultScan() => SteamAdapter.FindDuplicateInstalls(
        OperatingSystem.IsWindows() ? "windows" : "linux",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // Shows an existing folder in the OS file manager; a missing folder is reported, never created.
    [RelayCommand]
    private void OpenInstallFolder(DuplicateInstallCopyItem? copy)
    {
        if (copy is null) return;
        if (Opener is { } opener) { opener(copy.RootPath); return; }
        try
        {
            if (!Directory.Exists(copy.RootPath)) { Status = LocalizationService.Instance["Duplicates.MissingFolder"]; return; }
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe")
                : new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(copy.RootPath);
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Status = LocalizationService.Instance["Duplicates.OpenFailed"];
        }
    }
}