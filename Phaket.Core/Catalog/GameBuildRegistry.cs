using System.Globalization;
using Phaket.Core.Omrl;

namespace Phaket.Core.Catalog;

// PAK-96: the cached build catalog is the only authority that turns an
// installation's Steam build id into a game version. Labels compare in the
// server's canonical form, and SusModder dbValue aliases ("2026-8-18") resolve
// to the same build as the canonical label ("2026.8.18").
public static class GameBuildRegistry
{
    public static string? VersionForBuildId(IReadOnlyList<CatalogBuild> builds, string? store, long? buildId)
    {
        if (buildId is not { } id) return null;
        var build = builds.FirstOrDefault(b => (store is null || b.Store == store) && BuildId(b) == id);
        return build is null ? null : GameVersionLabel.Normalize(build.VersionLabel) ?? build.VersionLabel;
    }

    // W1 fallback: many SusModder rows publish the Steam depot manifest but no
    // build id. The installed appmanifest lists the depot manifest actually on
    // disk, so it identifies the version when the build id cannot.
    public static string? VersionForManifests(IReadOnlyList<CatalogBuild> builds, string? store,
        IReadOnlyCollection<string> manifestIds)
    {
        if (manifestIds.Count == 0) return null;
        var build = builds.FirstOrDefault(b => (store is null || b.Store == store)
            && b.ManifestIds.Any(manifestIds.Contains));
        return build is null ? null : GameVersionLabel.Normalize(build.VersionLabel) ?? build.VersionLabel;
    }

    public static CatalogBuild? ForLabel(IReadOnlyList<CatalogBuild> builds, string? store, string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        return builds.Where(b => store is null || b.Store == store).FirstOrDefault(b => Matches(b, label));
    }

    private static bool Matches(CatalogBuild build, string label) =>
        LabelEquals(build.VersionLabel, label)
        || (build.Aliases ?? Array.Empty<string>()).Any(alias => LabelEquals(alias, label));

    private static bool LabelEquals(string stored, string wanted) =>
        (GameVersionLabel.Normalize(stored) ?? stored) == (GameVersionLabel.Normalize(wanted) ?? wanted);

    // Steam build ids are decimal; "observed-epic-..." ids are never matched.
    private static long? BuildId(CatalogBuild build) =>
        long.TryParse(build.BuildId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}