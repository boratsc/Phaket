using Phaket.Core.Launch;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Recipes;

namespace Phaket.Core.Tests;

// Regressions from the 2026-11-14 crash review: one bad file on disk must never
// abort a whole scan/library load, and untrusted launch values must never become
// shell code before %command%.
public class CrashRegressionTests
{
    [Fact]
    public void RecipeLibrary_SkipsMalformedFile_KeepsTheRest()
    {
        var dir = Directory.CreateTempSubdirectory("phaket-lib-").FullName;
        try
        {
            File.Copy(Path.Combine(RecipeLibrary.Directory, "among-us-betterpolus.yaml"),
                Path.Combine(dir, "good.yaml"));
            File.WriteAllText(Path.Combine(dir, "broken.yaml"), "steps: [unclosed");
            var recipes = RecipeLibrary.LoadAll(dir);
            Assert.Single(recipes);
            Assert.Equal("community.thunderstore.better-polus", recipes[0].Id);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string SteamHome(out string steamapps)
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-").FullName;
        steamapps = Path.Combine(home, ".local", "share", "Steam", "steamapps");
        Directory.CreateDirectory(steamapps);
        return home;
    }

    private const string ValidAcf = """
        "AppState"
        {
        	"appid"		"945360"
        	"name"		"Among Us"
        	"buildid"		"12345678"
        	"installdir"		"Among Us"
        }
        """;

    [Fact]
    public void DetectInstallations_SkipsCorruptAcf_StillFindsValidOnes()
    {
        var home = SteamHome(out var steamapps);
        try
        {
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), ValidAcf);
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_999.acf"), "\"AppState\"\n{\n\"appid\"");
            var detected = new SteamAdapter([]).DetectInstallations("linux", home);
            Assert.Single(detected);
            Assert.Equal("945360", detected[0].Manifest.AppId);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void DetectInstallations_CorruptLibraryFolders_KeepsRootLibrary()
    {
        var home = SteamHome(out var steamapps);
        try
        {
            File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), "\"libraryfolders\" { broken");
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), ValidAcf);
            var detected = new SteamAdapter([]).DetectInstallations("linux", home);
            Assert.Single(detected);
            Assert.Equal("945360", detected[0].Manifest.AppId);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private static GameLaunchStrategy StrategyWithArg(string value) => new(
        "test-strategy", 1,
        new LaunchScope("among-us-steam", null, "linux", LauncherKind.Steam, RuntimeKind.Proton, null, null, null),
        [new LaunchRequirement("game-arg", "arg", value, RequirementStatus.Required,
            new LaunchProvenance("test", "fixture", DateTimeOffset.UtcNow, "1", "verified"))]);

    [Fact]
    public void SteamGameArg_WithWhitespace_IsRejectedNotExecuted()
    {
        // "touch /tmp/pwned #" before %command% would be a shell command, not an argument.
        var ex = Assert.Throws<PhaketException>(() => LaunchTranslator.Plan(
            StrategyWithArg("touch /tmp/pwned #"), LauncherKind.Steam,
            new ExistingLaunchSettings(null, new Dictionary<string, string>()), null));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
    }

    [Fact]
    public void SteamGameArg_LandsAfterCommand_NeverBefore()
    {
        var plan = LaunchTranslator.Plan(
            StrategyWithArg("-modded"), LauncherKind.Steam,
            new ExistingLaunchSettings("FOO=\"bar\" %command% -existing", new Dictionary<string, string>()), null);
        Assert.Equal("FOO=\"bar\" %command% -existing -modded", plan.Translations[0].SteamLaunchOptions);
    }
}
