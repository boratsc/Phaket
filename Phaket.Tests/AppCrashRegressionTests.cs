using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Regressions from the 2026-11-14 crash review: corrupt user data must survive,
// Steam config writes must be recoverable, and YAML scalars must not crash Studio.
public class AppCrashRegressionTests
{
    [Fact]
    public void ProfileStore_CorruptFile_IsNeverOverwrittenWithSamples()
    {
        var root = Directory.CreateTempSubdirectory("phaket-profiles-").FullName;
        try
        {
            var path = Path.Combine(root, "profiles.json");
            File.WriteAllText(path, "{ this is not valid json");
            var store = new ProfileStore(root);
            Assert.Equal("{ this is not valid json", File.ReadAllText(path));
            Assert.Empty(store.For("among-us").Profiles);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProfileStore_FreshRoot_StillSeedsSamples()
    {
        var root = Directory.CreateTempSubdirectory("phaket-profiles-").FullName;
        try
        {
            var store = new ProfileStore(root);
            Assert.NotEmpty(store.For("among-us").Profiles);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string SteamConfigFixture(out string configPath)
    {
        var root = Directory.CreateTempSubdirectory("phaket-launch-").FullName;
        var configDir = Path.Combine(root, "userdata", "123", "config");
        Directory.CreateDirectory(configDir);
        configPath = Path.Combine(configDir, "localconfig.vdf");
        File.WriteAllText(configPath, "\"UserLocalConfigStore\"\n{\n}\n");
        return root;
    }

    [Fact]
    public void ApplyPlan_TwoAppliesInSameSecond_KeepDistinctBackups_AndRollbackRestores()
    {
        var root = SteamConfigFixture(out var configPath);
        try
        {
            var original = File.ReadAllText(configPath);
            var service = new SteamLaunchOptionsService(root);
            var plan = service.BuildPlan("945360", "WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%");
            Assert.NotNull(plan);
            var first = service.ApplyPlan(plan!);
            var second = service.ApplyPlan(plan! with { Proposed = "OTHER" });
            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            // The live config is always parseable VDF (atomic replace).
            Assert.NotNull(VdfParser.Parse(File.ReadAllText(configPath)).Child("UserLocalConfigStore"));
            service.Rollback(first);
            Assert.Equal(original, File.ReadAllText(configPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StudioLoad_NonStringYamlScalars_DoNotThrow()
    {
        // Unquoted YAML scalars parse as numbers/bools; GetString() used to throw.
        const string yaml = """
            schemaVersion: omrl/v0alpha1
            id: test.scalars
            version: 1.0.0
            target:
              game: among-us
              systems:
                - windows
                - linux
            steps:
              - id: copy
                use: file.copy
                with:
                  from: 123
                  to: true
            """;
        var vm = new RecipeStudioViewModel();
        vm.LoadYamlCore(yaml);
        Assert.Equal("123", vm.Steps[0].From);
        Assert.Equal("true", vm.Steps[0].To);
    }
}
