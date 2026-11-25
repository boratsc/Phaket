using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Phaket.Core.Nexus;
using Phaket.Core.Omrl;
using Phaket.Models;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class NexusManualViewModel : ViewModelBase
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
    private readonly Func<Task<string?>>? archivePicker;

    [ObservableProperty] private string url = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string pinnedVersion = "";
    [ObservableProperty] private string localArchivePath = "";
    [ObservableProperty] private string validationMessage = "";
    [ObservableProperty] private NexusManualPickResult? result;

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);
    public bool HasResult => Result is not null;

    public NexusManualViewModel(Func<Task<string?>>? archivePicker = null, NexusSource? nexusSource = null,
        INexusService? nexusService = null)
    {
        this.archivePicker = archivePicker;
        Browser = new NexusBrowserViewModel(nexusService, nexusSource);
        Browser.DownloadCompleted += OnNexusDownloaded;
    }

    // Browse/download from Nexus API v1; a finished download fills the manual form below.
    public NexusBrowserViewModel Browser { get; }

    private void OnNexusDownloaded(NexusBrowserDownload download)
    {
        Url = $"https://www.nexusmods.com/{download.Link.GameDomain}/mods/{download.Link.ModId}";
        Name = download.Mod?.Name is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(download.Result.FileName);
        PinnedVersion = download.Mod?.Version ?? "";
        LocalArchivePath = download.Result.Path;
        ValidationMessage = "";
    }

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));
    partial void OnResultChanged(NexusManualPickResult? value) => OnPropertyChanged(nameof(HasResult));
    partial void OnUrlChanged(string value) => Result = null;
    partial void OnNameChanged(string value) => Result = null;
    partial void OnPinnedVersionChanged(string value) => Result = null;
    partial void OnLocalArchivePathChanged(string value) => Result = null;

    public static bool IsValidNexusModUrl(string? value) =>
        value is not null && value.Contains("://", StringComparison.Ordinal) && NexusModReference.TryParse(value, out _);

    public static bool IsValidArchive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    [RelayCommand]
    private async Task ChooseArchiveAsync()
    {
        string? path;
        if (archivePicker is not null) path = await archivePicker();
        else
        {
            var app = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storage = app?.MainWindow?.StorageProvider;
            if (storage is null) return;
            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["Sources.Nexus.ChooseArchive"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizationService.Instance["Sources.Nexus.Archives"]) { Patterns = ["*.zip", "*.7z", "*.rar"] }
                ]
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }

        if (string.IsNullOrWhiteSpace(path)) return;
        try { LocalArchivePath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LocalArchivePath = "";
            ValidationMessage = "Sources.Nexus.InvalidArchive";
            return;
        }
        ValidationMessage = IsValidArchive(LocalArchivePath) ? "" : "Sources.Nexus.InvalidArchive";
    }

    [RelayCommand]
    private void PinSelection()
    {
        if (!IsValidNexusModUrl(Url))
        {
            ValidationMessage = "Sources.Nexus.InvalidUrl";
            return;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "Sources.Nexus.InvalidName";
            return;
        }
        if (string.IsNullOrWhiteSpace(PinnedVersion))
        {
            ValidationMessage = "Sources.Nexus.InvalidVersion";
            return;
        }
        if (!IsValidArchive(LocalArchivePath))
        {
            ValidationMessage = "Sources.Nexus.InvalidArchive";
            return;
        }

        ValidationMessage = "";
        Result = new NexusManualPickResult(new Uri(Url.Trim()).AbsoluteUri, Name.Trim(), PinnedVersion.Trim(), Path.GetFullPath(LocalArchivePath));
    }
}
