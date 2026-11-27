using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Launch;

// PAK-146 (model B): the saves plan of ONE direct launch of a game. A managed installation
// (gameRoot = the modded copy Phaket installed) keeps its saves beside itself when the
// definition allows it; a vanilla one passes no gameRoot and keeps the user's own profile.
public static class SavesLaunchPreparation
{
    // redirect/seed run inside the managed folder; gameRoot null = vanilla launch.
    public static PackageSavesIsolation.Result Prepare(string os, GameDefinition definition, string? gameRoot, string profileRoot) =>
        PackageSavesIsolation.Prepare(new PackageSavesIsolation.Request(os, definition.SavesIsolationFor(os),
            gameRoot, definition.SavePaths, profileRoot));

    // The same hash InstallViewModel uses for one installation (profile/state keys).
    public static string Hash(string gameRoot) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot))));
}