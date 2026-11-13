using Phaket.Core.Launch;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-17: one semantic requirement -> correct per-launcher syntax, preservation
// of user settings, collisions, rollback, acceptance, no auto-execution.
public class LaunchPlanTests
{
    private static readonly LaunchProvenance ProtonDbProvenance = new(
        "protondb", "https://www.protondb.com/app/945360", DateTimeOffset.UtcNow, "dump-2026-11", "external-hint");

    private static GameLaunchStrategy BepInExStrategy(LauncherKind launcher) => new(
        "bepinex-steam-proton", 1,
        new LaunchScope("among-us-steam", "12345678", "linux", launcher,
            RuntimeKind.Proton, "9.0-4", "bepinex", "6.0.0"),
        [new LaunchRequirement("dll-override", "winhttp.dll", "n,b", RequirementStatus.Required, ProtonDbProvenance)]);

    private static readonly ExistingLaunchSettings Empty = new(null, new Dictionary<string, string>());

    [Fact]
    public void SteamTranslationUsesPercentCommand()
    {
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Steam), LauncherKind.Steam, Empty, null);
        var translation = plan.Translations.Single();
        Assert.Equal("WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%", translation.SteamLaunchOptions);
        Assert.True(plan.RequiresAcceptance);
    }

    [Fact]
    public void HeroicTranslationHasNoPercentCommand()
    {
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Heroic), LauncherKind.Heroic, Empty, null);
        var translation = plan.Translations.Single();
        Assert.Null(translation.SteamLaunchOptions);
        Assert.Equal("winhttp=n,b", translation.Environment["WINEDLLOVERRIDES"]);
        Assert.DoesNotContain("%command%", string.Join(" ", translation.Environment.Values));
        Assert.DoesNotContain("%command%", string.Join(" ", translation.Arguments));
    }

    [Fact]
    public void LegendaryTranslationHasNoPercentCommand()
    {
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Legendary), LauncherKind.Legendary, Empty, null);
        var translation = plan.Translations.Single();
        Assert.Null(translation.SteamLaunchOptions);
        Assert.Equal("winhttp=n,b", translation.Environment["WINEDLLOVERRIDES"]);
        Assert.Contains("no %command%", translation.CopyInstruction);
    }

    [Fact]
    public void ExistingSteamOptionsArePreserved()
    {
        var existing = new ExistingLaunchSettings("PROTON_LOG=1 %command%", new Dictionary<string, string>());
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Steam), LauncherKind.Steam, existing, null);
        var options = plan.Translations.Single().SteamLaunchOptions!;
        Assert.Contains("PROTON_LOG=1", options);
        Assert.Contains("WINEDLLOVERRIDES=\"winhttp.dll=n,b\"", options);
        Assert.Equal(1, options.Split("%command%").Length - 1); // exactly one %command%
        Assert.EndsWith("%command%", options);
    }

    [Fact]
    public void CollidingOverrideIsReportedAndUserValuePreserved()
    {
        var existing = new ExistingLaunchSettings(null,
            new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "winhttp=n" });
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Heroic), LauncherKind.Heroic, existing, null);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("WINEDLLOVERRIDES", conflict.Name);
        Assert.Equal("winhttp=n", conflict.ExistingValue);
        Assert.Equal("winhttp=n", plan.Translations.Single().Environment["WINEDLLOVERRIDES"]);
    }

    [Fact]
    public void RuntimeVersionMismatchFailsWithClearReason()
    {
        var runtime = new RuntimeInstallation("proton-9", RuntimeKind.Proton, "8.0-5", "/proton");
        var ex = Assert.Throws<PhaketException>(() =>
            LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Steam), LauncherKind.Steam, Empty, runtime));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
        Assert.Contains("8.0-5", ex.Error.Parameters["detail"]);
        Assert.Contains("9.0-4", ex.Error.Parameters["detail"]);
    }

    [Fact]
    public void ApplyRequiresAcceptanceAndRollbackRestoresPreviousState()
    {
        var root = Directory.CreateTempSubdirectory("phaket-launch-").FullName;
        try
        {
            var storePath = Path.Combine(root, "settings.json");
            var store = new LaunchConfigStore();
            var existing = new ExistingLaunchSettings("PROTON_LOG=1 %command%",
                new Dictionary<string, string> { ["PROTON_LOG"] = "1" });
            // Seed the store with the user's previous state.
            File.WriteAllText(storePath, """{"environment":{"PROTON_LOG":"1"},"steamLaunchOptions":"PROTON_LOG=1 %command%"}""");
            var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Steam), LauncherKind.Steam, existing, null);

            Assert.Throws<PhaketException>(() => store.Apply(plan, LauncherKind.Steam, storePath, accepted: false));
            var applied = store.Apply(plan, LauncherKind.Steam, storePath, accepted: true);
            Assert.Equal("1", applied.PreviousEnvironment["PROTON_LOG"]);
            Assert.Contains("WINEDLLOVERRIDES", File.ReadAllText(storePath));

            store.Rollback(storePath);
            var restored = File.ReadAllText(storePath);
            Assert.DoesNotContain("WINEDLLOVERRIDES", restored);
            Assert.Contains("PROTON_LOG", restored);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProtonDbCommandStringsAreDataNeverExecuted()
    {
        // A malicious "hint" containing shell syntax must stay inert data.
        var strategy = new GameLaunchStrategy("evil-hint", 1,
            new LaunchScope("among-us-steam", null, "linux", LauncherKind.Steam, RuntimeKind.Proton, null, null, null),
            [new LaunchRequirement("env", "PROTON_LOG", "1; rm -rf /", RequirementStatus.Recommended, ProtonDbProvenance)]);
        var plan = LaunchTranslator.Plan(strategy, LauncherKind.Steam, Empty, null);
        var translation = plan.Translations.Single();
        // The value is carried verbatim as data in the environment map...
        Assert.Equal("1; rm -rf /", translation.Environment["PROTON_LOG"]);
        // ...and the translator exposes no execution path: only strings out.
        Assert.Contains("PROTON_LOG=\"1; rm -rf /\"", translation.SteamLaunchOptions);
        Assert.Equal("protondb", strategy.Requirements[0].Provenance.Source);
    }

    [Fact]
    public void ControlCharactersInRequirementsAreRejected()
    {
        var strategy = new GameLaunchStrategy("bad", 1,
            new LaunchScope("among-us-steam", null, "linux", LauncherKind.Steam, RuntimeKind.Proton, null, null, null),
            [new LaunchRequirement("env", "X", "a\nb", RequirementStatus.Required, ProtonDbProvenance)]);
        Assert.Throws<PhaketException>(() => LaunchTranslator.Plan(strategy, LauncherKind.Steam, Empty, null));
    }

    [Fact]
    public void UnsupportedRequirementsAreSkipped()
    {
        var strategy = new GameLaunchStrategy("partial", 1,
            new LaunchScope("among-us-steam", null, "linux", LauncherKind.Heroic, RuntimeKind.Proton, null, null, null),
            [
                new LaunchRequirement("dll-override", "winhttp.dll", "n,b", RequirementStatus.Required, ProtonDbProvenance),
                new LaunchRequirement("env", "EXPERIMENTAL", "1", RequirementStatus.Unsupported, ProtonDbProvenance),
            ]);
        var plan = LaunchTranslator.Plan(strategy, LauncherKind.Heroic, Empty, null);
        Assert.DoesNotContain("EXPERIMENTAL", plan.Translations.Single().Environment.Keys);
    }
}
