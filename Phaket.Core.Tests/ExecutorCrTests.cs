using System.IO.Compression;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// Regression tests for the executor code review: crash durability, shared
// installations, overwrite policy, archive edge cases, divergence handling.
public class ExecutorCrTests
{
    [Fact]
    public void CrashAfterFileWrite_BaseIsDurable_ResumeAndUninstallRestore()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.CopyRecipe("test.crash", "mod-crash", "game://BepInEx/plugins/Crash.dll", "mod-content");
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var crashing = new PlanExecutor(fixture.PlanExecutionContext("test.crash"),
            phase => { if (phase == "after-file-write") throw new IOException("simulated process kill"); });
        var journal = crashing.Approve(plan);
        Assert.Throws<IOException>(() => crashing.Execute(plan, journal));
        // The file was mutated, but the base copy is already durable.
        Assert.Equal("mod-content", File.ReadAllText(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Crash.dll")));

        var recovered = new PlanExecutor(fixture.PlanExecutionContext("test.crash"));
        var completed = recovered.Execute(plan, PlanExecutor.LoadJournal(fixture.ProfileRoot)!);
        Assert.Equal(JournalState.Completed, completed.State);
        recovered.Uninstall("test.crash");
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Crash.dll")));
    }

    [Fact]
    public void TwoProfilesSharingOneInstallation_UninstallKeepsOther()
    {
        using var fixture = new ExecutorFixture();
        var profileB = Path.Combine(fixture.Root, "profile-b");
        Directory.CreateDirectory(profileB);
        const string target = "game://BepInEx/plugins/Shared.dll";
        fixture.Install(fixture.CopyRecipe("test.pa", "mod-pa", target, "A"), "test.pa");
        var recipeB = fixture.CopyRecipe("test.pb", "mod-pb", target, "B", "replace");
        var planB = OperationPlanner.Plan(recipeB, fixture.PlannerContext());
        var executorB = new PlanExecutor(fixture.PlanExecutionContext("test.pb", profileB));
        executorB.Execute(planB, executorB.Approve(planB));

        new PlanExecutor(fixture.PlanExecutionContext("test.pa")).Uninstall("test.pa");
        Assert.Equal("B", File.ReadAllText(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Shared.dll")));
    }

    [Fact]
    public void OverwritePolicyFailBlocksExistingTarget()
    {
        using var fixture = new ExecutorFixture();
        var target = Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Existing.dll");
        File.WriteAllText(target, "user-file");
        var recipe = fixture.CopyRecipe("test.ow", "mod-ow", "game://BepInEx/plugins/Existing.dll", "mod");
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.ow"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
        Assert.Equal("user-file", File.ReadAllText(target));
    }

    [Fact]
    public void OverwriteReplaceIsHonored()
    {
        using var fixture = new ExecutorFixture();
        var target = Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Existing.dll");
        File.WriteAllText(target, "user-file");
        var recipe = fixture.CopyRecipe("test.ow2", "mod-ow2", "game://BepInEx/plugins/Existing.dll", "mod");
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with
            {
                With = new Dictionary<string, JsonElement>
                {
                    ["from"] = JsonSerializer.SerializeToElement("package://mod-ow2"),
                    ["to"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/Existing.dll"),
                    ["overwrite"] = JsonSerializer.SerializeToElement("replace"),
                }
            }]
        };
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.ow2"));
        executor.Execute(plan, executor.Approve(plan));
        Assert.Equal("mod", File.ReadAllText(target));
        executor.Uninstall("test.ow2");
        Assert.Equal("user-file", File.ReadAllText(target));
    }

    [Fact]
    public void ZipDirectoryEntriesAreAccepted()
    {
        using var fixture = new ExecutorFixture();
        var zipPath = Path.Combine(fixture.PackageRoot, "dirs.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("BepInEx/");
            zip.CreateEntry("BepInEx/plugins/");
            var entry = zip.CreateEntry("BepInEx/plugins/mod.dll");
            using var stream = entry.Open();
            stream.Write("mod"u8);
        }
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.dirs",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["archive.extract"] },
            Artifacts = [new RecipeArtifact { Id = "dirs", Source = "package://dirs.zip" }],
            Steps = [new RecipeStep { Id = "extract", Use = "archive.extract",
                With = ExecutorFixture.Params(("from", "package://dirs"), ("to", "staging://out")) }],
        };
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.dirs"));
        var journal = executor.Execute(plan, executor.Approve(plan));
        Assert.Equal(JournalState.Completed, journal.State);
        Assert.True(File.Exists(Path.Combine(fixture.StagingRoot, "out", "BepInEx", "plugins", "mod.dll")));
    }

    [Fact]
    public void ConfigKeyAbsentInitially_UninstallRemovesKey()
    {
        using var fixture = new ExecutorFixture();
        var cfgPath = Path.Combine(fixture.GameRoot, "BepInEx", "config", "mod.cfg");
        File.WriteAllText(cfgPath, "Other = keep\n");
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.cfgnew",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["config.set"] },
            Steps = [new RecipeStep { Id = "set", Use = "config.set",
                With = ExecutorFixture.Params(("path", "game://BepInEx/config/mod.cfg"),
                    ("format", "keyvalue"), ("key", "NewKey"), ("value", "v")) }],
        };
        fixture.Install(recipe, "test.cfgnew");
        Assert.Contains("NewKey = v", File.ReadAllText(cfgPath));
        new PlanExecutor(fixture.PlanExecutionContext("test.cfgnew")).Uninstall("test.cfgnew");
        var content = File.ReadAllText(cfgPath);
        Assert.DoesNotContain("NewKey", content);
        Assert.Contains("Other = keep", content);
    }

    [Fact]
    public void LoaderArchiveHashMismatchIsBlocked()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        ExecutorFixture.MakeZip(Path.Combine(fixture.PackageRoot, "loader-bundle.zip"),
            ("winhttp.dll", "tampered"u8.ToArray()),
            ("BepInEx/core/BepInEx.IL2CPP.dll", "x"u8.ToArray()));
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ArtifactHashMismatch, ex.Error.Code);
        Assert.Equal("original-loader", File.ReadAllText(Path.Combine(fixture.GameRoot, "winhttp.dll")));
    }

    [Fact]
    public void PreExistingEmptyDirectorySurvivesRollback()
    {
        using var fixture = new ExecutorFixture();
        var existing = Path.Combine(fixture.GameRoot, "BepInEx", "plugins"); // created by fixture, empty
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.dir",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["directory.ensure"] },
            Steps = [new RecipeStep { Id = "ensure", Use = "directory.ensure",
                With = ExecutorFixture.Params(("path", "game://BepInEx/plugins")) }],
        };
        var (plan, journal) = fixture.Install(recipe, "test.dir");
        new PlanExecutor(fixture.PlanExecutionContext("test.dir")).Rollback(journal);
        Assert.True(Directory.Exists(existing));
    }

    [Fact]
    public void UnsafeArchiveIsRejectedBeforeAnyWrite()
    {
        using var fixture = new ExecutorFixture();
        var zipPath = Path.Combine(fixture.PackageRoot, "partial.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var first = zip.CreateEntry("first.dll");
            using (var s = first.Open()) s.Write("first"u8);
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = 0xA1FF << 16;
            using (var s = link.Open()) s.Write("/etc/passwd"u8);
        }
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.partial",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["archive.extract"] },
            Artifacts = [new RecipeArtifact { Id = "partial", Source = "package://partial.zip" }],
            Steps = [new RecipeStep { Id = "extract", Use = "archive.extract",
                With = ExecutorFixture.Params(("from", "package://partial"), ("to", "staging://out")) }],
        };
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.partial"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ArchiveUnsafe, ex.Error.Code);
        // Two-pass validation: nothing was written, not even the first entry.
        Assert.False(File.Exists(Path.Combine(fixture.StagingRoot, "out", "first.dll")));
    }

    [Fact]
    public void ExistingSymlinkInTargetCannotRedirectArchiveWrites()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Symlink creation requires Linux in this suite.");
        using var fixture = new ExecutorFixture();
        var outside = Directory.CreateTempSubdirectory("phaket-outside-").FullName;
        try
        {
            Directory.Delete(Path.Combine(fixture.GameRoot, "BepInEx", "plugins"));
            Directory.CreateSymbolicLink(Path.Combine(fixture.GameRoot, "BepInEx", "plugins"), outside);
            ExecutorFixture.MakeZip(Path.Combine(fixture.PackageRoot, "redirect.zip"),
                ("plugins/evil.dll", "evil"u8.ToArray()));
            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1",
                Id = "test.redirect",
                Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
                Requires = new RecipeRequires { Capabilities = ["archive.extract"] },
                Artifacts = [new RecipeArtifact { Id = "redirect", Source = "package://redirect.zip" }],
                Steps = [new RecipeStep { Id = "extract", Use = "archive.extract",
                    With = ExecutorFixture.Params(("from", "package://redirect"), ("to", "game://BepInEx")) }],
            };
            var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
            var executor = new PlanExecutor(fixture.PlanExecutionContext("test.redirect"));
            var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
            Assert.Equal(ErrorCodes.ArchiveUnsafe, ex.Error.Code);
            Assert.False(File.Exists(Path.Combine(outside, "evil.dll")));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void UninstallDetectsUserEditsAndRequiresPolicy()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.CopyRecipe("test.edit", "mod-edit", "game://BepInEx/plugins/Edit.dll", "mod");
        fixture.Install(recipe, "test.edit");
        var target = Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Edit.dll");
        File.WriteAllText(target, "user-edit");
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.edit"));
        var ex = Assert.Throws<PhaketException>(() => executor.Uninstall("test.edit"));
        Assert.Equal(ErrorCodes.JournalDivergence, ex.Error.Code);
        Assert.Equal("user-edit", File.ReadAllText(target));
        executor.Uninstall("test.edit", DivergencePolicy.Restore);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void ProfileActivateWritesStateFileAndRollbackRestores()
    {
        using var fixture = new ExecutorFixture();
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.activate",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["profile.activate"] },
            Steps = [new RecipeStep { Id = "activate", Use = "profile.activate",
                With = ExecutorFixture.Params(("profile", "profile-1")) }],
        };
        var (plan, journal) = fixture.Install(recipe, "test.activate");
        var stateFile = Path.Combine(fixture.ProfileRoot, "active-profile.json");
        Assert.True(File.Exists(stateFile));
        Assert.Contains("profile-1", File.ReadAllText(stateFile));
        new PlanExecutor(fixture.PlanExecutionContext("test.activate")).Rollback(journal);
        Assert.False(File.Exists(stateFile));
    }
}
