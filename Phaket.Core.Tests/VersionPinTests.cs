using Phaket.Core.Execution;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-144: version pin on a modded installation and the read-only duplicate report.
// Fixture Steam trees only; no real library is ever touched.
public class VersionPinTests
{
    private static string Manifest(string appId, string buildId, string manifestId = "987654321", long lastUpdated = 0) => $$"""
        "AppState"
        {
            "appid"		"{{appId}}"
            "name"		"Game {{appId}}"
            "buildid"		"{{buildId}}"
            "branch"		"public"
            "manifest"		"{{manifestId}}"
            "installdir"		"Game {{appId}}"
            "SizeOnDisk"		"1000"
            "LastUpdated"		"{{lastUpdated}}"
        }
        """;

    private static InstalledRecipe Entry(string root, string build, string manifest) =>
        new("test.recipe", "1.0.0", "among-us", "steam", Path.Combine(root, "game"), Path.Combine(root, "profile"),
            Path.Combine(root, "staging"), Path.Combine(root, "state"), null, DateTimeOffset.UnixEpoch,
            PinnedAt: DateTimeOffset.UnixEpoch, PinnedBuildId: build, PinnedManifestId: manifest);

    [Fact]
    public void PinAndUnpinRoundTripThroughTheRegistryFile()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pin-").FullName;
        try
        {
            var registry = new InstallRegistry(Path.Combine(root, "installs.json"));
            var entry = Entry(root, "", "") with { PinnedAt = null };
            registry.Add(entry);
            Assert.False(registry.ForGame("among-us").Single().IsPinned);

            registry.Pin(entry, "12345678", "555");
            var pinned = Assert.Single(new InstallRegistry(Path.Combine(root, "installs.json")).All());
            Assert.True(pinned.IsPinned);
            Assert.Equal("12345678", pinned.PinnedBuildId);
            Assert.Equal("555", pinned.PinnedManifestId);
            // The rest of the record survives the pin untouched.
            Assert.Equal(entry.GameRoot, pinned.GameRoot);
            Assert.Equal(entry.InstalledAt, pinned.InstalledAt);

            registry.Unpin(entry.RecipeId, entry.GameRoot);
            var unpinned = Assert.Single(registry.All());
            Assert.False(unpinned.IsPinned);
            Assert.Null(unpinned.PinnedBuildId);
            Assert.Null(unpinned.PinnedManifestId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string BuildHome(string home, string buildId, string manifestId = "987654321",
        string? extraLibraryManifest = null, long lastUpdated = 0)
    {
        var steamRoot = Path.Combine(home, ".local", "share", "Steam");
        var steamapps = Path.Combine(steamRoot, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Game 945360"));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), Manifest("945360", buildId, manifestId, lastUpdated));
        var extra = Path.Combine(home, "games-library");
        Directory.CreateDirectory(Path.Combine(extra, "steamapps", "common", "Game 945360"));
        if (extraLibraryManifest is not null)
            File.WriteAllText(Path.Combine(extra, "steamapps", "appmanifest_945360.acf"), extraLibraryManifest);
        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path"	"{{steamRoot.Replace("\\", "\\\\")}}" }
                "1" { "path"	"{{extra.Replace("\\", "\\\\")}}" }
            }
            """);
        return steamRoot;
    }

    [Fact]
    public void PinStatusIsOkWhenTheBuildIsUnchanged()
    {
        var home = Directory.CreateTempSubdirectory("phaket-pin-").FullName;
        try
        {
            BuildHome(home, "12345678");
            var current = SteamAdapter.FindDuplicateInstalls("linux", home) is { Count: > 0 } dupes
                ? dupes[0].Copies[0].Manifest : SteamAdapter.DetectLibraries("linux", home).Single().Manifest;
            Assert.IsType<PinStatus.Ok>(VersionPin.Check(Entry(home, "12345678", "987654321"), current));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void PinStatusReportsAStoreUpdate()
    {
        var home = Directory.CreateTempSubdirectory("phaket-pin-").FullName;
        try
        {
            BuildHome(home, "87654321");
            var current = SteamAdapter.DetectLibraries("linux", home).Single().Manifest;
            var status = Assert.IsType<PinStatus.VersionChanged>(VersionPin.Check(Entry(home, "12345678", "987654321"), current));
            Assert.Equal("12345678", status.From);
            Assert.Equal("87654321", status.To);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void PinStatusFallsBackToTheManifestIdAndReportsMissingInstalls()
    {
        var home = Directory.CreateTempSubdirectory("phaket-pin-").FullName;
        try
        {
            BuildHome(home, "", manifestId: "222");
            var current = SteamAdapter.DetectLibraries("linux", home).Single().Manifest;
            // No build id on either side: the appmanifest manifest id is the evidence.
            Assert.Equal("222", VersionPin.Identity(current).ManifestId);
            Assert.IsType<PinStatus.Ok>(VersionPin.Check(Entry(home, "", "222"), current));
            Assert.IsType<PinStatus.VersionChanged>(VersionPin.Check(Entry(home, "", "111"), current));
            // A pin without any recorded identity cannot claim a change.
            Assert.IsType<PinStatus.Ok>(VersionPin.Check(Entry(home, "", ""), current));
            var missing = Assert.IsType<PinStatus.Missing>(VersionPin.Check(Entry(home, "12345678", ""), null));
            Assert.True(missing.NeedsAttention);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void DuplicatesReportListsEveryCopyAndTheOneSteamUpdatedLast()
    {
        var home = Directory.CreateTempSubdirectory("phaket-dup-").FullName;
        try
        {
            var steamRoot = BuildHome(home, "1", lastUpdated: 1_700_000_000,
                extraLibraryManifest: Manifest("945360", "1", lastUpdated: 1_800_000_000));
            var group = Assert.Single(SteamAdapter.FindDuplicateInstalls("linux", home));
            Assert.Equal("945360", group.AppId);
            Assert.Equal(2, group.Copies.Count);
            Assert.Equal(Path.Combine(home, "games-library", "steamapps", "common", "Game 945360").TrimEnd('/'),
                group.Live.Installation.RootPath.TrimEnd('/'));
            Assert.Contains(group.Copies, c => c.Library.Path.TrimEnd('/') == steamRoot.TrimEnd('/'));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void FlatpakLibrariesAreScannedToo()
    {
        var home = Directory.CreateTempSubdirectory("phaket-dup-flatpak-").FullName;
        try
        {
            BuildHome(home, "1", lastUpdated: 10);
            var flatpakRoot = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            var steamapps = Path.Combine(flatpakRoot, "steamapps");
            Directory.CreateDirectory(Path.Combine(steamapps, "common", "Game 945360"));
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), Manifest("945360", "1", lastUpdated: 20));
            var group = Assert.Single(SteamAdapter.FindDuplicateInstalls("linux", home));
            Assert.Equal(2, group.Copies.Count);
            Assert.Contains(group.Copies, c => c.Library.IsFlatpak);
            Assert.True(group.Live.Library.IsFlatpak);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ASingleInstallationIsNotADuplicate()
    {
        var home = Directory.CreateTempSubdirectory("phaket-dup-none-").FullName;
        try
        {
            BuildHome(home, "1");
            Assert.Empty(SteamAdapter.FindDuplicateInstalls("linux", home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void DuplicateScanNeverWritesToTheFixture()
    {
        var home = Directory.CreateTempSubdirectory("phaket-dup-ro-").FullName;
        try
        {
            BuildHome(home, "1", lastUpdated: 5, extraLibraryManifest: Manifest("945360", "1", lastUpdated: 6));
            string[] Snapshot() => Directory.GetFileSystemEntries(home, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .SelectMany(p => Directory.Exists(p) ? [p] : new[] { p, File.GetLastWriteTimeUtc(p).Ticks.ToString() })
                .ToArray();
            var before = Snapshot();
            var groups = SteamAdapter.FindDuplicateInstalls("linux", home);
            Assert.Single(groups);
            Assert.Equal(before, Snapshot());
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}