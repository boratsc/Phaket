using System.Text;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Launch;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;

namespace Phaket.Core.Tests;

// PAK-27 / §7.13.5: Linux P0 vertical acceptance on FIXTURES.
// Chain: game+installation -> build/runner -> ProtonDB hints (provenance) ->
// typed LaunchPlan -> dry-run -> acceptance -> install/verify -> rollback.
// Real-hardware end-to-end (legal Among Us install + Proton) is NOT performed
// here; this suite proves the mechanism and is explicitly fixture-level.
public class LinuxP0VerticalTests
{
    [Fact]
    public void FullLinuxPipelineOnFixtures()
    {
        var root = Directory.CreateTempSubdirectory("phaket-linux-p0-").FullName;
        try
        {
            // --- 1. Detect the local Steam installation (fixture tree) ---
            var home = Path.Combine(root, "home");
            var steamRoot = Path.Combine(home, ".local", "share", "Steam");
            var steamapps = Path.Combine(steamRoot, "steamapps");
            var gameDir = Path.Combine(steamapps, "common", "Among Us");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "Among Us.exe"), "game-binary");
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_945360.acf"), """
                "AppState"
                {
                    "appid"		"945360"
                    "name"		"Among Us"
                    "buildid"		"12345678"
                    "installdir"		"Among Us"
                }
                """);
            var definition = GameDefinitionLoader.LoadYaml(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));
            var steam = new SteamAdapter([definition]);
            var installation = steam.DetectInstallations("linux", home).Single(i => i.Manifest.AppId == "945360");
            Assert.Equal("12345678", installation.Manifest.BuildId);
            Assert.Equal(GameAvailability.Moddable, installation.Availability);

            // --- 2. ProtonDB hints with provenance and date (offline cache) ---
            using var protonDb = new ProtonDbCache(Path.Combine(root, "protondb.db"));
            using (var dump = new MemoryStream(Encoding.UTF8.GetBytes("""
                [{"app":{"steam":{"appId":"945360"},"title":"Among Us"},
                  "responses":{"variant":"official","verdict":"yes",
                    "notes":{"verdict":"BepInEx works with winhttp override"},
                    "launchOptions":"WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%"},
                  "timestamp":1730000000}]
                """)))
            {
                protonDb.Import(dump, new Dictionary<string, GameDefinition> { ["945360"] = definition },
                    "https://github.com/bdefore/protondb-data reports_nov1_2026.tar.gz", "2026-11-01");
            }
            var hint = protonDb.GetReports("945360").Single();
            Assert.Equal("protondb", hint.Source);
            Assert.Equal("2026-11-01", hint.DumpDate);
            Assert.Equal("ODbL-1.0", hint.License);
            // The hint is data: it feeds a TYPED strategy, never a shell.
            Assert.Contains("%command%", hint.Report.LaunchOptions!);

            // --- 3. Typed LaunchPlan: Steam vs Heroic, dry-run + acceptance ---
            var provenance = new LaunchProvenance("protondb", hint.SourceReference, hint.FetchedAt, "dump-2026-11", "external-hint");
            var strategy = new GameLaunchStrategy("bepinex-amongus-proton", 1,
                new LaunchScope("among-us-steam", "12345678", "linux", LauncherKind.Steam,
                    RuntimeKind.Proton, "9.0-4", "bepinex", "6.0.0"),
                [new LaunchRequirement("dll-override", "winhttp.dll", "n,b", RequirementStatus.Required, provenance)]);
            var existing = new ExistingLaunchSettings("PROTON_LOG=1 %command%", new Dictionary<string, string>());
            var steamPlan = LaunchTranslator.Plan(strategy, LauncherKind.Steam, existing,
                new RuntimeInstallation("proton", RuntimeKind.Proton, "9.0-4", "/proton"));
            Assert.True(steamPlan.RequiresAcceptance);
            var steamOptions = steamPlan.Translations.Single().SteamLaunchOptions!;
            Assert.Contains("PROTON_LOG=1", steamOptions); // user settings preserved
            Assert.Contains("WINEDLLOVERRIDES=\"winhttp.dll=n,b\"", steamOptions);
            Assert.EndsWith("%command%", steamOptions);

            var heroicPlan = LaunchTranslator.Plan(strategy, LauncherKind.Heroic, existing, null);
            Assert.Equal("winhttp=n,b", heroicPlan.Translations.Single().Environment["WINEDLLOVERRIDES"]);
            Assert.Null(heroicPlan.Translations.Single().SteamLaunchOptions);

            // Wrong Proton version: clear reason, no unverified "sure fix".
            var mismatch = Assert.Throws<PhaketException>(() => LaunchTranslator.Plan(strategy, LauncherKind.Steam, existing,
                new RuntimeInstallation("proton", RuntimeKind.Proton, "8.0-5", "/proton")));
            Assert.Contains("8.0-5", mismatch.Error.Parameters["detail"]);

            // Apply to the Phaket-managed store only after acceptance; rollback restores.
            var storePath = Path.Combine(root, "launch-settings.json");
            File.WriteAllText(storePath, """{"environment":{"PROTON_LOG":"1"},"steamLaunchOptions":"PROTON_LOG=1 %command%"}""");
            var launchStore = new LaunchConfigStore();
            Assert.Throws<PhaketException>(() => launchStore.Apply(steamPlan, LauncherKind.Steam, storePath, accepted: false));
            launchStore.Apply(steamPlan, LauncherKind.Steam, storePath, accepted: true);
            Assert.Contains("WINEDLLOVERRIDES", File.ReadAllText(storePath));
            launchStore.Rollback(storePath);
            Assert.DoesNotContain("WINEDLLOVERRIDES", File.ReadAllText(storePath));

            // --- 4. Mod install: dry-run -> approve -> execute -> verify -> uninstall ---
            var profileRoot = Path.Combine(root, "profile");
            var stagingRoot = Path.Combine(root, "staging");
            var packageRoot = Path.Combine(root, "packages");
            var stateRoot = Path.Combine(root, "state");
            Directory.CreateDirectory(profileRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(gameDir, "winhttp.dll"), "original-doorstop");
            ExecutorFixture.MakeZip(Path.Combine(packageRoot, "loader-bundle.zip"),
                ("winhttp.dll", "bepinex-doorstop"u8.ToArray()),
                ("BepInEx/core/BepInEx.Unity.IL2CPP.dll", "loader-core"u8.ToArray()));
            ExecutorFixture.MakeZip(Path.Combine(packageRoot, "mod-bundle.zip"),
                ("CustomRoles.dll", "custom-roles"u8.ToArray()));

            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1",
                Id = "community.phaket.custom-roles",
                Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
                Requires = new RecipeRequires
                {
                    Capabilities = ["loader.ensure", "archive.extract", "file.copy", "file.verify"],
                    Loaders = [new LoaderRequirement { Id = "bepinex", Version = ">=6.0" }],
                },
                Artifacts =
                [
                    new RecipeArtifact { Id = "loader-bundle", Source = "package://loader-bundle.zip", Arch = "x86" },
                    new RecipeArtifact { Id = "mod-bundle", Source = "package://mod-bundle.zip" },
                ],
                Steps =
                [
                    new RecipeStep { Id = "ensure-loader", Use = "loader.ensure",
                        With = ExecutorFixture.Params(("id", "bepinex"), ("artifact", "loader-bundle")) },
                    new RecipeStep { Id = "extract", Use = "archive.extract",
                        With = ExecutorFixture.Params(("from", "package://mod-bundle"), ("to", "staging://mod")) },
                    new RecipeStep { Id = "copy", Use = "file.copy",
                        With = ExecutorFixture.Params(("from", "staging://mod/CustomRoles.dll"), ("to", "game://BepInEx/plugins/CustomRoles.dll")) },
                ],
                Verify =
                [
                    new RecipeVerifyStep { Use = "file.verify",
                        With = ExecutorFixture.Params(("path", "game://BepInEx/plugins/CustomRoles.dll")) },
                ],
            };

            var facts = new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false, Arch: "x86");
            var plannerContext = new PlannerContext(installation.Installation.InstallationId, "profile-1",
                gameDir, profileRoot, stagingRoot, packageRoot, definition, facts);
            var plan = OperationPlanner.Plan(recipe, plannerContext);
            Assert.Equal(3, plan.Operations.Count);
            Assert.False(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "CustomRoles.dll"))); // dry-run

            var executor = new PlanExecutor(new PlanExecutionContext(gameDir, profileRoot, stagingRoot,
                packageRoot, definition, recipe.Id, "profile-1", stateRoot));
            var journal = executor.Execute(plan, executor.Approve(plan));
            Assert.Equal(JournalState.Completed, journal.State);
            Assert.True(executor.VerifyPlan(plan).Succeeded);
            Assert.Equal("custom-roles", File.ReadAllText(Path.Combine(gameDir, "BepInEx", "plugins", "CustomRoles.dll")));

            // --- 5. Rollback / uninstall restores the installation ---
            executor.Uninstall(recipe.Id);
            Assert.False(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "CustomRoles.dll")));
            Assert.Equal("original-doorstop", File.ReadAllText(Path.Combine(gameDir, "winhttp.dll")));
            Assert.Equal("game-binary", File.ReadAllText(Path.Combine(gameDir, "Among Us.exe")));

            // --- 6. Offline: cached hints remain readable with their date ---
            Assert.NotNull(protonDb.GetSummary("945360"));
            Assert.Equal("2026-11-01", protonDb.GetReports("945360")[0].DumpDate);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
