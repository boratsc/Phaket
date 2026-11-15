using System.Security.Cryptography;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// The first REAL recipe (BetterPolus via Thunderstore, BepInEx 6 IL2CPP pack).
// Proven against the actual package structure observed 2026-11-13:
//   loader zip  -> single wrapper folder BepInExPack_AmongUs/ (strip 1)
//   mod zip     -> plugins/BetterPolus.dll + manifest.json/icon/README
// The repo cannot ship the 31 MB loader, so execution tests rebuild fixtures
// with the SAME layout; the shipped YAML itself is validated with real digests.
public class FirstRecipeTests
{
    private static GameDefinition ShippedDefinition() => GameDefinitionLoader.LoadYaml(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));

    private static Recipe FirstRecipe() =>
        RecipeParser.ParseYaml(File.ReadAllText(Path.Combine(Phaket.Core.Recipes.RecipeLibrary.Directory, "among-us-betterpolus.yaml")));

    [Fact]
    public void ShippedRecipeParsesAndValidatesAgainstShippedDefinition()
    {
        var recipe = FirstRecipe();
        var definition = ShippedDefinition();
        var element = System.Text.Json.JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement;
        Assert.Empty(RecipeSchema.Validate(element));
        Assert.Empty(RecipeValidator.Validate(recipe, definition));
        // Real download URLs pass the source allowlist; digests are declared.
        Assert.All(recipe.Artifacts, a =>
        {
            Assert.True(SourcePolicy.IsAllowedHttpsSource(a.Source), a.Source);
            Assert.NotNull(a.Sha256);
        });
        Assert.True(recipe.Steps[0].With.ContainsKey("stripComponents"));
    }

    [Fact]
    public void PlanWithoutLocalPackagesIsHonestAboutDownload()
    {
        var root = Directory.CreateTempSubdirectory("phaket-first-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            foreach (var d in new[] { game, Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages") })
                Directory.CreateDirectory(d);
            var facts = new PlanFacts("linux", "steam", "2023.7.12", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86");
            var plan = OperationPlanner.Plan(FirstRecipe(), new PlannerContext(
                "i", "p", game, Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages"),
                ShippedDefinition(), facts));
            Assert.True(plan.RequiresDownload);
            Assert.Equal(3, plan.Operations.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FullPipelineWithRealPackageStructure_InstallVerifyUninstall()
    {
        var definition = ShippedDefinition();
        var root = Directory.CreateTempSubdirectory("phaket-first-e2e-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            var profile = Path.Combine(root, "profile");
            var staging = Path.Combine(root, "staging");
            var packages = Path.Combine(root, "packages");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(game);
            foreach (var d in new[] { profile, staging, packages, state }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(game, "Among Us.exe"), "game");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "windows-system-proxy");

            // Same layout as the real downloads (wrapper folder / plugins subfolder).
            ExecutorFixture.MakeZip(Path.Combine(packages, "loader-pack.zip"),
                ("BepInExPack_AmongUs/winhttp.dll", "doorstop-proxy"u8.ToArray()),
                ("BepInExPack_AmongUs/doorstop_config.ini", "target_assembly = BepInEx\\core\\BepInEx.Unity.IL2CPP.dll"u8.ToArray()),
                ("BepInExPack_AmongUs/.doorstop_version", "4.3.0"u8.ToArray()),
                ("BepInExPack_AmongUs/changelog.txt", "6.0.0-be.700"u8.ToArray()),
                ("BepInExPack_AmongUs/dotnet/coreclr.dll", "coreclr"u8.ToArray()),
                ("BepInExPack_AmongUs/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "il2cpp-core"u8.ToArray()),
                ("BepInExPack_AmongUs/BepInEx/config/BepInEx.cfg", "[Chainloader]"u8.ToArray()));
            ExecutorFixture.MakeZip(Path.Combine(packages, "better-polus.zip"),
                ("manifest.json", "{}"u8.ToArray()),
                ("icon.png", "icon"u8.ToArray()),
                ("README.md", "readme"u8.ToArray()),
                ("plugins/BetterPolus.dll", "better-polus-plugin"u8.ToArray()));

            // Local packages: same steps, package:// sources, no remote digests.
            var recipe = FirstRecipe() with
            {
                Artifacts =
                [
                    new RecipeArtifact { Id = "loader-pack", Source = "package://loader-pack.zip", Arch = "x86" },
                    new RecipeArtifact { Id = "better-polus", Source = "package://better-polus.zip" },
                ],
                Verify = [new RecipeVerifyStep { Use = "file.verify",
                    With = ExecutorFixture.Params(("path", "game://BepInEx/plugins/BetterPolus.dll")) }],
            };

            var facts = new PlanFacts("linux", "steam", "2023.7.12", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86");
            var plan = OperationPlanner.Plan(recipe, new PlannerContext(
                "i", "p", game, profile, staging, packages, definition, facts));
            Assert.False(plan.RequiresDownload);
            Assert.Contains(plan.Warnings, w => w.StartsWith("executable-library:", StringComparison.Ordinal));

            var executor = new PlanExecutor(new PlanExecutionContext(
                game, profile, staging, packages, definition, recipe.Id, "p", state));
            var journal = new InstallOrchestrator(executor).Run(plan);
            Assert.Equal(JournalState.Completed, journal.State);
            Assert.True(executor.VerifyPlan(plan).Succeeded);

            // Loader content sits at the game ROOT — no wrapper folder survives.
            Assert.False(Directory.Exists(Path.Combine(game, "BepInExPack_AmongUs")));
            Assert.Equal("doorstop-proxy", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(game, "doorstop_config.ini")));
            Assert.True(File.Exists(Path.Combine(game, "dotnet", "coreclr.dll")));
            Assert.True(File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll")));
            Assert.Equal("better-polus-plugin", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "BetterPolus.dll")));
            // Untouched game files stay untouched.
            Assert.Equal("game", File.ReadAllText(Path.Combine(game, "Among Us.exe")));

            executor.Uninstall(recipe.Id);
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "plugins", "BetterPolus.dll")));
            Assert.Equal("windows-system-proxy", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LinuxLaunchOptionsComeFromDefinitionAsData()
    {
        var definition = ShippedDefinition();
        // The user-critical fact: on Linux/Proton the winhttp proxy DLL needs an
        // override or BepInEx never loads; on Windows nothing extra is required.
        Assert.Equal("WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%",
            GameLaunchOptions.SteamLaunchOptions(definition, "linux"));
        Assert.Null(GameLaunchOptions.SteamLaunchOptions(definition, "windows"));
        Assert.Equal("WINEDLLOVERRIDES=\"winhttp.dll=n,b\" PROTON_NO_ESYNC=\"1\" %command%",
            GameLaunchOptions.SteamLaunchOptions(definition, "linux", includeOptional: true));

        var requirement = definition.LaunchRequirementsFor("linux").Single(r => r.Status == "required");
        Assert.False(string.IsNullOrEmpty(requirement.Source));   // provenance is mandatory data
        Assert.False(string.IsNullOrEmpty(requirement.ObservedAt));
        Assert.Empty(definition.LaunchRequirementsFor("windows").Where(r => r.Status == "required"));
    }

    [Fact]
    public void ShippedDefinitionCoversRealLoaderPackTopLevelEntries()
    {
        // Top-level entries of the REAL BepInExPack_AmongUs-6.0.700.zip.
        var definition = ShippedDefinition();
        foreach (var entry in new[]
        {
            ".doorstop_version", "BepInEx/config/BepInEx.cfg", "changelog.txt",
            "doorstop_config.ini", "dotnet/coreclr.dll", "winhttp.dll",
        })
            Assert.True(definition.AllowsWrite(entry), entry);
        // Anything else in the game folder stays read-only for recipes.
        Assert.False(definition.AllowsWrite("Among Us_Data/globalgamemanagers"));
        Assert.False(definition.AllowsWrite("UnityPlayer.dll"));
    }

    [Fact]
    public void LoaderEnsureRejectsUnsafeStripComponents()
    {
        var recipe = FirstRecipe() with
        {
            Steps = [FirstRecipe().Steps[0] with { With = ExecutorFixture.Params(
                ("id", "bepinex"), ("artifact", "loader-pack"), ("stripComponents", "9")) }],
        };
        var errors = RecipeValidator.Validate(recipe, ShippedDefinition());
        Assert.Contains(errors, e => e.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void FailedLoaderInstallRollsBackEveryCreatedFileAndDirectory()
    {
        // Regression: a real wrapper-folder pack installed WITHOUT stripComponents
        // fails on writableAreas; rollback must leave the game tree byte-identical
        // (files AND directories) — previously wrapper dirs survived.
        var definition = ShippedDefinition();
        var root = Directory.CreateTempSubdirectory("phaket-first-rb-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            var profile = Path.Combine(root, "profile");
            var staging = Path.Combine(root, "staging");
            var packages = Path.Combine(root, "packages");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(game);
            foreach (var d in new[] { profile, staging, packages, state }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(game, "Among Us.exe"), "game");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "windows-system-proxy");
            ExecutorFixture.MakeZip(Path.Combine(packages, "loader-pack.zip"),
                ("BepInExPack_AmongUs/winhttp.dll", "doorstop-proxy"u8.ToArray()),
                ("BepInExPack_AmongUs/changelog.txt", "notes"u8.ToArray()),
                ("BepInExPack_AmongUs/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "il2cpp-core"u8.ToArray()),
                ("BepInExPack_AmongUs/dotnet/coreclr.dll", "coreclr"u8.ToArray()));

            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1", Id = "test.wrapper", Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
                Requires = new RecipeRequires { Capabilities = ["loader.ensure"] },
                Artifacts = [new RecipeArtifact { Id = "loader-pack", Source = "package://loader-pack.zip", Arch = "x86" }],
                Steps = [new RecipeStep { Id = "install", Use = "loader.ensure",
                    With = ExecutorFixture.Params(("id", "bepinex"), ("artifact", "loader-pack")) }],
            };

            string[] Snapshot() => Directory.GetFileSystemEntries(game, "*", SearchOption.AllDirectories)
                .Select(p => p[(game.Length + 1)..].Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var before = Snapshot();

            var facts = new PlanFacts("linux", "steam", "2023.7.12", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.700" }, Arch: "x86");
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, definition, facts));
            var executor = new PlanExecutor(new PlanExecutionContext(game, profile, staging, packages, definition, recipe.Id, "p", state));
            var ex = Assert.Throws<PhaketException>(() => new InstallOrchestrator(executor).Run(plan));
            Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
            // The wrapper-folder prefix itself is outside writableAreas: this is
            // exactly why the real recipe must declare stripComponents: 1.
            Assert.StartsWith("BepInExPack_AmongUs/", ex.Error.Parameters["path"]);

            var journal = PlanExecutor.LoadJournal(profile)!;
            Assert.Equal(JournalState.Failed, journal.State);
            executor.Rollback(journal);
            Assert.Equal(before, Snapshot());
            Assert.Equal("windows-system-proxy", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
