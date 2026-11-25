using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

public class SteamLibraryServiceTests
{
    [Fact]
    public async Task IncludesDepotDownloaderRegistryInstallation()
    {
        var home = Directory.CreateTempSubdirectory("phaket-steam-library-").FullName;
        var dataRoot = Path.Combine(home, "data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            File.WriteAllText(Path.Combine(dataRoot, "steam-installs.json"), "[{\"AppId\":\"12345\",\"TargetPath\":\"/games/Test Game\",\"Executable\":\"game.exe\",\"InstalledAt\":\"2024-01-01T00:00:00Z\"}]");
            var definitions = new[] { new GameDefinition("test-game", [], new Dictionary<string, string> { ["steam"] = "12345" }) };
            var service = new SteamLibraryService("76561198000000000", definitions, dataRoot: dataRoot, userProfile: home);
            var game = Assert.Single(await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken));
            Assert.Equal("12345", game.AppId);
            Assert.Equal("test-game", game.Definition!.Slug);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public async Task MissingApiKeyDoesNotBlockOfflineLibrary()
    {
        // The fixture is a native Linux Steam layout; Windows detection reads Program Files.
        if (!OperatingSystem.IsLinux()) return;
        var home = Directory.CreateTempSubdirectory("phaket-steam-library-").FullName;
        try
        {
            var steamapps = Path.Combine(home, ".local", "share", "Steam", "steamapps");
            Directory.CreateDirectory(Path.Combine(steamapps, "common", "Among Us"));
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), "\"AppState\" { \"appid\" \"945360\" \"name\" \"Among Us\" \"buildid\" \"123\" \"installdir\" \"Among Us\" }");
            var definitions = new[] { new GameDefinition("among-us", [], new Dictionary<string, string> { ["steam"] = "945360" }) };
            var service = new SteamLibraryService("76561198000000000", definitions, dataRoot: Path.Combine(home, "data"), userProfile: home);

            var game = Assert.Single(await service.GetOwnedGamesAsync(TestContext.Current.CancellationToken));
            Assert.Equal("945360", game.AppId);
            Assert.Equal("among-us", game.Definition!.Slug);
            Assert.Equal("local", game.Source);
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
