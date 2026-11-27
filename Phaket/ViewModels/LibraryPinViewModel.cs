using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Execution;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-144: the version pin of a modded installation, surfaced in the library.
public partial class LibraryViewModel
{
    private InstallRegistry registry = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "installs.json"));

    // Test seams: the installs.json under test, so the user's real registry is never touched;
    // where the duplicate scan looks, and which folder is opened for the user.
    internal void UseRegistry(string path) => registry = new InstallRegistry(path);
    internal Func<IReadOnlyList<SteamDuplicateGroup>>? DuplicateScanner { get; set; }
    internal Action<string>? FolderOpener { get; set; }
    // Where the scan looks; a test points it at its own Steam fixture instead of the real home.
    internal string? SteamHome { get; set; }
    // Same seam one level deeper: the Steam roots the scan reads. On Windows the fixture must
    // live in temp, never under the real Program Files, so a test sets this instead.
    internal static Func<string, string, IReadOnlyList<string>?>? SteamRootsOverride
    {
        get => SteamAdapter.RootsOverrideForTests;
        set => SteamAdapter.RootsOverrideForTests = value;
    }

    [ObservableProperty] private bool hasPinUpdateWarning;
    [ObservableProperty] private string pinUpdateMessage = "";

    // Re-reads every pinned recipe and shows one friendly banner when a store
    // updated a game under its mods. Runs after each scan, never in the background.
    private void RefreshPinStatus()
    {
        var current = SelectedGame;
        InstalledRecipe? affected = null;
        PinStatus status = new PinStatus.Ok();
        if (current?.Detected is { } detected)
        {
            foreach (var entry in registry.All().Where(e => e.IsPinned))
                if (SteamAdapter.SameInstallation(detected, entry.GameRoot) &&
                    VersionPin.Check(entry, detected.Manifest) is var check && check.NeedsAttention)
                {
                    affected = entry;
                    status = check;
                    break;
                }
        }
        HasPinUpdateWarning = affected is not null;
        var loc = LocalizationService.Instance;
        PinUpdateMessage = status switch
        {
            PinStatus.VersionChanged changed => string.Format(loc["Pin.UpdateChanged"], changed.From ?? "?", changed.To ?? "?"),
            PinStatus.Missing => loc["Pin.Missing"],
            _ => ""
        };
    }

    [RelayCommand]
    private void DismissPinWarning() => HasPinUpdateWarning = false;

    [RelayCommand]
    private void TogglePin(GameCatalogItem? game)
    {
        if (game is null) return;
        var installation = game.Detected;
        var entries = installation is null ? []
            : registry.All().Where(e => SteamAdapter.SameInstallation(installation, e.GameRoot)).ToArray();
        if (entries.Length == 0) return;
        var (build, manifest) = installation is null ? (null, null) : VersionPin.Identity(installation.Manifest);
        foreach (var entry in entries)
        {
            if (entry.IsPinned) registry.Unpin(entry.RecipeId, entry.GameRoot);
            else registry.Pin(entry, build, manifest);
        }
        var pinned = !entries[0].IsPinned;
        game.IsVersionPinned = pinned;
        Status = LocalizationService.Instance[pinned ? "Pin.On" : "Pin.Off"];
        RefreshPinStatus();
    }

    // Read-only diagnostic as a library command: the same Steam app in more than one library.
    [RelayCommand]
    private void ScanDuplicates()
    {
        try
        {
            var groups = (DuplicateScanner ?? DefaultDuplicateScan)();
            DuplicateGroups.Clear();
            foreach (var group in groups) DuplicateGroups.Add(new DuplicateInstallGroupItem(group));
            DuplicateStatus = groups.Count == 0
                ? LocalizationService.Instance["Duplicates.None"]
                : string.Format(LocalizationService.Instance["Duplicates.Found"], groups.Count);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: duplicate scan failed: {ex}");
            DuplicateStatus = LocalizationService.Instance["Duplicates.Error"];
        }
    }

    private static IReadOnlyList<SteamDuplicateGroup> DefaultDuplicateScan() => SteamAdapter.FindDuplicateInstalls(
        OperatingSystem.IsWindows() ? "windows" : "linux",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public ObservableCollection<DuplicateInstallGroupItem> DuplicateGroups { get; } = [];
    [ObservableProperty] private string duplicateStatus = "";
    public bool HasDuplicateStatus => !string.IsNullOrEmpty(DuplicateStatus);
    partial void OnDuplicateStatusChanged(string value) => OnPropertyChanged(nameof(HasDuplicateStatus));

    // Shows an existing folder in the OS file manager; a missing folder is reported, never created.
    [RelayCommand]
    private void OpenInstallFolder(DuplicateInstallCopyItem? copy)
    {
        if (copy is null) return;
        if (FolderOpener is { } opener) { opener(copy.RootPath); return; }
        try
        {
            if (!Directory.Exists(copy.RootPath)) { DuplicateStatus = LocalizationService.Instance["Duplicates.MissingFolder"]; return; }
            var start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe")
                : new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(copy.RootPath);
            using var process = System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            DuplicateStatus = LocalizationService.Instance["Duplicates.OpenFailed"];
        }
    }
}
