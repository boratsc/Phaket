using Phaket.Core.Execution;
using Phaket.Core.Launch;

namespace Phaket.Services;

internal static class ModLaunchRequirements
{
    internal static string? Create(InstalledRecipe entry)
    {
        // A literal dollar in the installation path would be expanded by Steam's shell.
        if (!OperatingSystem.IsLinux() || entry.GameSlug is not ("valheim" or "among-us") || entry.GameRoot.Contains('$') || entry.Store is not ("steam" or "epic"))
            return null;
        var launcher = entry.Store == "steam" ? LauncherKind.Steam : LauncherKind.Heroic;
        var strategy = BepInExLaunchStrategy.Create(entry.GameSlug, "linux", launcher, entry.GameRoot,
            new LaunchProvenance("phaket", "bepinex", DateTimeOffset.UtcNow, "1", "built-in"));
        var plan = LaunchTranslator.Plan(strategy, launcher, new ExistingLaunchSettings(null, new Dictionary<string, string>()), null);
        var translation = plan.Translations.Single();
        return launcher == LauncherKind.Steam ? translation.SteamLaunchOptions
            : string.Join(Environment.NewLine, translation.Environment.Select(p => $"{p.Key}={p.Value}"));
    }
}
