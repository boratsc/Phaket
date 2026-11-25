using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Nexus;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed record NexusBrowserDownload(NxmLink Link, NexusModMetadata? Mod, NexusDownloadResult Result);

public sealed class NexusFileItem(NexusFileInfo file)
{
    public NexusFileInfo File { get; } = file;
    public string Name => File.Name;
    public string Meta => string.Join(" · ", new[] { File.Version, File.Category, SizeText }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public string SizeText => File.SizeKb is not { } kb || kb <= 0
        ? ""
        : kb >= 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{kb / 1024d:0.#} MB")
            : string.Create(CultureInfo.CurrentCulture, $"{kb} kB");
}

// Reads a mod page from REST v1 (metadata + file list) and downloads one file from a
// pasted nxm:// link. The free path needs the link's short-lived key/expires pair;
// the premium-only endpoint is tried only when validate.json said is_premium.
public partial class NexusBrowserViewModel : ViewModelBase
{
    private readonly INexusService service;
    private readonly NexusSource? gameSource;
    private string? loadedDomain;
    private int loadedModId;

    public NexusBrowserViewModel(INexusService? service = null, NexusSource? gameSource = null)
    {
        this.service = service ?? new NexusService();
        this.gameSource = gameSource;
    }

    public event Action<NexusBrowserDownload>? DownloadCompleted;

    [ObservableProperty] private string reference = "";
    [ObservableProperty] private string nxmLinkText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusKey = "";
    [ObservableProperty] private string warningKey = "";
    [ObservableProperty] private NexusModMetadata? mod;
    [ObservableProperty] private string downloadedPath = "";
    [ObservableProperty] private string downloadedName = "";

    public ObservableCollection<NexusFileItem> Files { get; } = [];

    public bool HasStatus => StatusKey.Length > 0;
    public bool HasWarning => WarningKey.Length > 0;
    public bool HasMod => Mod is not null;
    public bool HasFiles => Files.Count > 0;
    public bool HasDownloaded => DownloadedPath.Length > 0;

    partial void OnStatusKeyChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnWarningKeyChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnModChanged(NexusModMetadata? value) => OnPropertyChanged(nameof(HasMod));
    partial void OnDownloadedPathChanged(string value) => OnPropertyChanged(nameof(HasDownloaded));

    [RelayCommand]
    private async Task LoadModAsync()
    {
        if (IsBusy) return;
        StatusKey = "";
        WarningKey = "";
        if (!TryResolveReference(out string domain, out int modId, out string errorKey))
        {
            StatusKey = errorKey;
            return;
        }

        IsBusy = true;
        try
        {
            NexusModMetadata metadata = await service.GetModAsync(domain, modId).ConfigureAwait(true);
            IReadOnlyList<NexusFileInfo> files = await service.GetFilesAsync(domain, modId).ConfigureAwait(true);
            Mod = metadata;
            loadedDomain = domain;
            loadedModId = modId;
            Files.Clear();
            foreach (var file in files) Files.Add(new NexusFileItem(file));
            OnPropertyChanged(nameof(HasFiles));
            StatusKey = "Nexus.Status.Loaded";
            NoteLimits();
        }
        catch (NexusApiException ex)
        {
            StatusKey = KeyFor(ex.Code);
            ShowStaleFallback(domain, modId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            StatusKey = "Nexus.Error.Unavailable";
            ShowStaleFallback(domain, modId);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy) return;
        StatusKey = "";
        WarningKey = "";
        DownloadedPath = "";
        DownloadedName = "";
        if (!NxmLink.TryParse(NxmLinkText, out NxmLink? link))
        {
            StatusKey = "Nexus.Error.InvalidNxm";
            return;
        }
        if (gameSource is not null && !string.Equals(link!.GameDomain, gameSource.Domain, StringComparison.OrdinalIgnoreCase))
        {
            StatusKey = "Nexus.Error.GameMismatch";
            return;
        }

        IsBusy = true;
        try
        {
            NexusDownloadResult result = await service.DownloadAsync(link!).ConfigureAwait(true);
            DownloadedPath = result.Path;
            DownloadedName = result.FileName;
            StatusKey = "Nexus.Status.Downloaded";
            if (Mod is null || loadedModId != link!.ModId || loadedDomain != link.GameDomain)
                await LoadMetadataQuietlyAsync(link!).ConfigureAwait(true);
            DownloadCompleted?.Invoke(new(link!, Mod, result));
            NoteLimits();
        }
        catch (NexusApiException ex) { StatusKey = KeyFor(ex.Code); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            StatusKey = "Nexus.Error.DownloadFailed";
        }
        finally { IsBusy = false; }
    }

    private async Task LoadMetadataQuietlyAsync(NxmLink link)
    {
        try
        {
            Mod = await service.GetModAsync(link.GameDomain, link.ModId).ConfigureAwait(true);
            IReadOnlyList<NexusFileInfo> files = await service.GetFilesAsync(link.GameDomain, link.ModId).ConfigureAwait(true);
            loadedDomain = link.GameDomain;
            loadedModId = link.ModId;
            Files.Clear();
            foreach (var file in files) Files.Add(new NexusFileItem(file));
            OnPropertyChanged(nameof(HasFiles));
        }
        catch (NexusApiException) { /* the download itself succeeded; the form falls back to the file name */ }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
    }

    // After a quota error, keep showing what was read earlier with an explicit warning.
    private void ShowStaleFallback(string domain, int modId)
    {
        bool stale = false;
        if (Mod is null && service.CachedMod(domain, modId) is { } cached)
        {
            Mod = cached;
            loadedDomain = domain;
            loadedModId = modId;
            stale = true;
        }
        if (Files.Count == 0 && service.CachedFiles(domain, modId) is { } files)
        {
            foreach (var file in files) Files.Add(new NexusFileItem(file));
            OnPropertyChanged(nameof(HasFiles));
            stale = true;
        }
        if (stale) WarningKey = "Nexus.Warning.StaleCache";
    }

    private void NoteLimits()
    {
        if (service.LastReadFromStaleCache) WarningKey = "Nexus.Warning.StaleCache";
        else if (service.LastLimits is { HourlyRemaining: <= 0 } or { DailyRemaining: <= 0 })
            WarningKey = "Nexus.Warning.LimitReached";
    }

    private bool TryResolveReference(out string domain, out int modId, out string errorKey)
    {
        domain = "";
        modId = 0;
        errorKey = "";
        if (!NexusModReference.TryParse(Reference, out NexusModReference? reference))
        {
            errorKey = "Nexus.Error.InvalidReference";
            return false;
        }
        if (reference!.Domain is not null && gameSource is not null &&
            !string.Equals(reference.Domain, gameSource.Domain, StringComparison.OrdinalIgnoreCase))
        {
            errorKey = "Nexus.Error.GameMismatch";
            return false;
        }
        // A /games/{id}/mods/... address names the game by id; a foreign id must not be read
        // as the selected game's domain.
        if (reference.GameId is { } gameId && gameSource is not null && gameId != gameSource.GameId)
        {
            errorKey = "Nexus.Error.GameMismatch";
            return false;
        }
        if ((reference.Domain ?? gameSource?.Domain) is not { Length: > 0 } resolved)
        {
            errorKey = "Nexus.Error.DomainRequired";
            return false;
        }
        domain = resolved;
        modId = reference.ModId;
        return true;
    }

    private static string KeyFor(string code) => NexusMessages.KeyFor(code);
}