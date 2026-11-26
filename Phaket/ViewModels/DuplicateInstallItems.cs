using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

// One app installed more than once; Live marks the copy Steam updated last.
public sealed class DuplicateInstallGroupItem
{
    public DuplicateInstallGroupItem(SteamDuplicateGroup group)
    {
        Title = group.Name.Length > 0 ? group.Name : group.AppId;
        AppId = group.AppId;
        Copies = group.Copies.Select(c => new DuplicateInstallCopyItem(
            c.Installation.RootPath, c.Library.IsFlatpak, c.Manifest.LastUpdated == group.Live.Manifest.LastUpdated)).ToArray();
    }

    public string Title { get; }
    public string AppId { get; }
    public IReadOnlyList<DuplicateInstallCopyItem> Copies { get; }
    public string Hint => LocalizationService.Instance["Duplicates.Explanation"];
}

public sealed record DuplicateInstallCopyItem(string RootPath, bool IsFlatpak, bool IsMostRecent)
{
    public string Label => IsFlatpak
        ? string.Format(LocalizationService.Instance["Duplicates.Flatpak"], RootPath)
        : RootPath;
    public string RecentHint => IsMostRecent ? LocalizationService.Instance["Duplicates.MostRecent"] : "";
}