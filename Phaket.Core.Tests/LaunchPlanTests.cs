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

    [Theory]
    [InlineData("valheim", "linux", LauncherKind.Steam, "DOORSTOP_ENABLED", "1")]
    [InlineData("valheim", "windows", LauncherKind.Heroic, "DOORSTOP_TARGET_ASSEMBLY", "/profile/BepInEx/core/BepInEx.Preloader.dll")]
    [InlineData("among-us", "linux", LauncherKind.Steam, "WINEDLLOVERRIDES", "WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%")]
    [InlineData("among-us", "windows", LauncherKind.Heroic, "WINEDLLOVERRIDES", "winhttp=n,b")]
    [InlineData("valheim", "linux", LauncherKind.Heroic, "LD_PRELOAD", "libdoorstop_x64.so")]
    [InlineData("valheim", "windows", LauncherKind.Steam, "DOORSTOP_TARGET_ASSEMBLY", "/profile/BepInEx/core/BepInEx.Preloader.dll")]
    [InlineData("among-us", "linux", LauncherKind.Heroic, "WINEDLLOVERRIDES", "winhttp=n,b")]
    [InlineData("among-us", "windows", LauncherKind.Steam, "WINEDLLOVERRIDES", "WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%")]
    public void BepInExLaunchStrategiesTranslateForGameAndPlatform(string game, string os,
        LauncherKind launcher, string expectedName, string expected)
    {
        var strategy = BepInExLaunchStrategy.Create(game, os, launcher, "/profile", ProtonDbProvenance);
        Assert.All(strategy.Requirements, r => Assert.Equal(RequirementStatus.Required, r.Status));
        var translated = LaunchTranslator.Plan(strategy, launcher, Empty, null).Translations.Single();
        if (expectedName == "WINEDLLOVERRIDES")
        {
            if (launcher == LauncherKind.Steam) Assert.Equal(expected, translated.SteamLaunchOptions);
            else Assert.Equal(expected, translated.Environment[expectedName]);
        }
        else Assert.Equal(expected, translated.Environment[expectedName]);
        if (launcher != LauncherKind.Steam) Assert.Null(translated.SteamLaunchOptions);
    }

    [Fact]
    public void ValheimLinuxLaunchRequirementsUseDoorstopNativePaths()
    {
        var requirements = BepInExLaunchStrategy.Create("valheim", "linux", LauncherKind.Steam,
            "/profile", ProtonDbProvenance).Requirements;
        Assert.Contains(requirements, r => r.Name == "LD_LIBRARY_PATH" && r.Value == "/profile/doorstop_libs:$LD_LIBRARY_PATH");
        Assert.Contains(requirements, r => r.Name == "LD_PRELOAD" && r.Value == "libdoorstop_x64.so");
        Assert.Contains(requirements, r => r.Name == "DOORSTOP_TARGET_ASSEMBLY" && r.Value.EndsWith("BepInEx.Preloader.dll"));
    }

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
    public void ProtonDbShellMetacharactersAreRejectedNotExecuted()
    {
        // A malicious "hint" containing shell syntax must never become a launch
        // option; it is rejected for manual review, not translated or executed.
        var strategy = new GameLaunchStrategy("evil-hint", 1,
            new LaunchScope("among-us-steam", null, "linux", LauncherKind.Steam, RuntimeKind.Proton, null, null, null),
            [new LaunchRequirement("env", "PROTON_LOG", "1; rm -rf /", RequirementStatus.Recommended, ProtonDbProvenance)]);
        var ex = Assert.Throws<PhaketException>(() => LaunchTranslator.Plan(strategy, LauncherKind.Steam, Empty, null));
        Assert.Contains("manual review", ex.Error.Parameters["detail"]);
    }

    [Fact]
    public void QuoteAndSubstitutionInSteamValueAreRejected()
    {
        foreach (var bad in new[] { "a\"b", "$(touch x)", "a`b", "a\\b" })
        {
            var strategy = new GameLaunchStrategy("bad", 1,
                new LaunchScope("among-us-steam", null, "linux", LauncherKind.Steam, RuntimeKind.Proton, null, null, null),
                [new LaunchRequirement("env", "X", bad, RequirementStatus.Required, ProtonDbProvenance)]);
            Assert.Throws<PhaketException>(() => LaunchTranslator.Plan(strategy, LauncherKind.Steam, Empty, null));
        }
    }

    [Fact]
    public void GameArgumentsAfterPercentCommandArePreserved()
    {
        var existing = new ExistingLaunchSettings("mangohud %command% --fullscreen", new Dictionary<string, string>());
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Steam), LauncherKind.Steam, existing, null);
        var options = plan.Translations.Single().SteamLaunchOptions!;
        Assert.EndsWith("--fullscreen", options);
        Assert.Contains("mangohud", options);
        Assert.Equal(1, options.Split("%command%").Length - 1);
        // The game argument stays AFTER %command%.
        Assert.True(options.IndexOf("%command%") < options.IndexOf("--fullscreen"));
    }

    [Fact]
    public void SteamEnvConflictIsNotReEmittedIntoOptions()
    {
        var existing = new ExistingLaunchSettings(null,
            new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "winhttp=n" });
        var plan = LaunchTranslator.Plan(BepInExStrategy(LauncherKind.Steam), LauncherKind.Steam, existing, null);
        Assert.Single(plan.Conflicts);
        // User value preserved; proposed override not injected into options.
        Assert.DoesNotContain("winhttp.dll=n,b", plan.Translations.Single().SteamLaunchOptions!);
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
