using Phaket.Core.Execution;

namespace Phaket.Core.Platforms;

// PAK-144: what happened to a pinned installation since it was pinned.
// Ok = same build; VersionChanged = the store updated the game under the mods;
// Missing = the folder or its appmanifest is gone (uninstalled/moved).
public abstract record PinStatus
{
    public sealed record Ok : PinStatus;
    public sealed record VersionChanged(string? From, string? To) : PinStatus;
    public sealed record Missing : PinStatus;

    public bool NeedsAttention => this is not Ok;
}

public static class VersionPin
{
    // The build identity of an installed copy. A build id is the primary evidence;
    // the appmanifest manifest id covers builds whose appmanifest carries no buildid.
    public static (string? BuildId, string? ManifestId) Identity(SteamAppManifest manifest) =>
        (manifest.BuildId.Length > 0 ? manifest.BuildId : null,
         manifest.ManifestId.Length > 0 ? manifest.ManifestId : manifest.DepotManifests?.Values.FirstOrDefault());

    // Compares the build recorded at pin time with what Steam reports now. A pin
    // recorded without any identity (manual folder, Linux install) cannot report
    // a change; it only reports a missing installation.
    public static PinStatus Check(InstalledRecipe entry, SteamAppManifest? current)
    {
        if (current is null) return new PinStatus.Missing();
        var (build, manifest) = Identity(current);
        // Nothing was recorded at pin time: the pin cannot claim a change.
        if (string.IsNullOrEmpty(entry.PinnedBuildId) && string.IsNullOrEmpty(entry.PinnedManifestId))
            return new PinStatus.Ok();
        bool? same;
        if (Compare(entry.PinnedBuildId, build) is { } byBuild) same = byBuild;
        // The store may fill buildid after an update on a copy pinned without one:
        // a different build is a change, never a match.
        else if (!string.IsNullOrEmpty(entry.PinnedBuildId) && !string.IsNullOrEmpty(build)) same = false;
        else same = Compare(entry.PinnedManifestId, manifest);
        return same == true ? new PinStatus.Ok() : new PinStatus.VersionChanged(Label(entry.PinnedBuildId, entry.PinnedManifestId), Label(build, manifest));
    }

    // null when either side is unknown (nothing can be said), otherwise exact match.
    private static bool? Compare(string? pinned, string? now) =>
        string.IsNullOrEmpty(pinned) || string.IsNullOrEmpty(now) ? null : pinned == now;

    private static string? Label(string? build, string? manifest) => build ?? manifest;
}