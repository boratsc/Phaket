using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-14: Steam detection on fixture trees (no real installations, no credentials).
public class SteamAdapterTests
{
    private static readonly GameDefinition AmongUs = new("among-us", ["BepInEx/**"],
        new Dictionary<string, string> { ["steam"] = "945360" });

    private static string BuildFixtureSteam(string home, bool flatpak = false)
    {
        var steamRoot = flatpak
            ? Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
            : Path.Combine(home, ".local", "share", "Steam");
        var steamapps = Path.Combine(steamRoot, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Among Us"));
        File.WriteAllText(Path.Combine(steamapps, "common", "Among Us", "Among Us.exe"), "game");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), """
            "AppState"
            {
                "appid"		"945360"
                "name"		"Among Us"
                "buildid"		"12345678"
                "branch"		"public"
                "manifest"		"987654321"
                "installdir"		"Among Us"
                "SizeOnDisk"		"1000000"
            }
            """);
        // Extra library on another "disk".
        var extra = Path.Combine(home, "extra-library");
        Directory.CreateDirectory(Path.Combine(extra, "steamapps", "common", "Valheim"));
        File.WriteAllText(Path.Combine(extra, "steamapps", "appmanifest_892970.acf"), """
            "AppState"
            {
                "appid"		"892970"
                "name"		"Valheim"
                "buildid"		"22222222"
                "installdir"		"Valheim"
            }
            """);
        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"{{steamRoot.Replace("\\", "\\\\")}}"
                }
                "1"
                {
                    "path"		"{{extra.Replace("\\", "\\\\")}}"
                }
            }
            """);
        return steamRoot;
    }

    [Fact]
    public void DetectsNativeInstallWithAppIdBuildAndPaths()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            BuildFixtureSteam(home);
            var adapter = new SteamAdapter([AmongUs]);
            var installs = adapter.DetectInstallations("linux", home);
            var amongUs = installs.Single(i => i.Manifest.AppId == "945360");
            Assert.Equal("12345678", amongUs.Manifest.BuildId);
            Assert.Equal("among-us-steam", amongUs.Installation.EditionId);
            Assert.True(Directory.Exists(amongUs.Installation.RootPath));
            Assert.Equal(GameAvailability.Moddable, amongUs.Availability);
            Assert.Equal(["12345678"], adapter.ProbeVersion(amongUs));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ExtraLibrariesAreDetected()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            BuildFixtureSteam(home);
            var adapter = new SteamAdapter([AmongUs]);
            var installs = adapter.DetectInstallations("linux", home);
            Assert.Contains(installs, i => i.Manifest.AppId == "892970");
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void FlatpakSteamRootIsDetected()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            BuildFixtureSteam(home, flatpak: true);
            var adapter = new SteamAdapter([AmongUs]);
            var installs = adapter.DetectInstallations("linux", home);
            var amongUs = installs.Single(i => i.Manifest.AppId == "945360");
            Assert.True(amongUs.Library.IsFlatpak);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ManualFolderFallbackWorks()
    {
        var folder = Directory.CreateTempSubdirectory("phaket-manual-").FullName;
        try
        {
            var adapter = new SteamAdapter([AmongUs]);
            var install = adapter.FromManualFolder(folder, "945360");
            Assert.NotNull(install);
            Assert.Equal("among-us-steam", install!.Installation.EditionId);
            Assert.Equal(GameAvailability.Moddable, install.Availability);
            Assert.Empty(adapter.ProbeVersion(install)); // unknown version, never guessed
            Assert.Null(adapter.FromManualFolder(Path.Combine(folder, "missing"), "945360"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void DetectionDoesNotMutateFixtures()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            BuildFixtureSteam(home);
            var before = Directory.GetFiles(home, "*", SearchOption.AllDirectories)
                .OrderBy(p => p).Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks).ToArray();
            var adapter = new SteamAdapter([AmongUs]);
            adapter.DetectInstallations("linux", home);
            var after = Directory.GetFiles(home, "*", SearchOption.AllDirectories)
                .OrderBy(p => p).Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks).ToArray();
            Assert.Equal(before, after);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void InstallationIdIsStableAcrossAdapterInstances()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            BuildFixtureSteam(home);
            var id1 = new SteamAdapter([AmongUs]).DetectInstallations("linux", home)
                .Single(i => i.Manifest.AppId == "945360").Installation.InstallationId;
            var id2 = new SteamAdapter([AmongUs]).DetectInstallations("linux", home)
                .Single(i => i.Manifest.AppId == "945360").Installation.InstallationId;
            Assert.Equal(id1, id2);
            Assert.StartsWith("steam-945360-", id1);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void VdfParserHandlesCommentsEscapesAndNesting()
    {
        var node = VdfParser.Parse("""
            // comment
            "root"
            {
                "key"		"va\"lue"
                "child"
                {
                    "n"		"1"
                }
            }
            """);
        var root = node.Child("root")!;
        Assert.Equal("va\"lue", root.Get("key"));
        Assert.Equal("1", root.Child("child")!.Get("n"));
    }

    [Fact]
    public void UnknownGameIsInstalledButNotModdable()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        try
        {
            BuildFixtureSteam(home);
            var adapter = new SteamAdapter([]); // no definitions
            var installs = adapter.DetectInstallations("linux", home);
            Assert.All(installs, i => Assert.Equal(GameAvailability.Installed, i.Availability));
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
