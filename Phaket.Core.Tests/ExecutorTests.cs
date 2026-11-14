using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public sealed class ExecutorFixture : IDisposable
{
    public string Root { get; }
    public string GameRoot { get; }
    public string ProfileRoot { get; }
    public string StagingRoot { get; }
    public string PackageRoot { get; }

    public ExecutorFixture()
    {
        Root = Directory.CreateTempSubdirectory("phaket-exec-").FullName;
        GameRoot = Path.Combine(Root, "game");
        ProfileRoot = Path.Combine(Root, "profile");
        StagingRoot = Path.Combine(Root, "staging");
        PackageRoot = Path.Combine(Root, "packages");
        Directory.CreateDirectory(Path.Combine(GameRoot, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "BepInEx", "config"));
        Directory.CreateDirectory(ProfileRoot);
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(PackageRoot);
        File.WriteAllText(Path.Combine(GameRoot, "winhttp.dll"), "original-loader");
        MakeZip(Path.Combine(PackageRoot, "loader-bundle.zip"),
            ("winhttp.dll", "new-loader"u8.ToArray()),
            ("BepInEx/core/BepInEx.dll", "bepinex-core"u8.ToArray()));
        MakeZip(Path.Combine(PackageRoot, "mod-bundle.zip"),
            ("CustomRoles.dll", "custom-roles-v1"u8.ToArray()));
    }

    public static void MakeZip(string path, params (string Name, byte[] Content)[] entries)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }
    }

    public PlannerContext PlannerContext(string recipeId = "test.recipe") => new(
        "install-1", "profile-1", GameRoot, ProfileRoot, StagingRoot, PackageRoot,
        TestFixtures.AmongUs,
        new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false));

    public PlanExecutionContext PlanExecutionContext(string recipeId, string? profileRoot = null) => new(
        GameRoot, profileRoot ?? ProfileRoot, StagingRoot, PackageRoot, TestFixtures.AmongUs, recipeId, "profile-1",
        Path.Combine(Root, "installation-state"));

    public Recipe FullRecipe()
    {
        var baseRecipe = TestFixtures.AmongUsRecipe();
        return baseRecipe with
        {
            Id = "test.recipe",
            Artifacts =
            [
                new RecipeArtifact { Id = "loader-bundle", Source = "package://loader-bundle.zip" },
                new RecipeArtifact { Id = "mod-bundle", Source = "package://mod-bundle.zip" },
            ],
            Steps =
            [
                new RecipeStep { Id = "ensure-loader", Use = "loader.ensure",
                    With = Params(("id", "bepinex"), ("artifact", "loader-bundle")) },
                new RecipeStep { Id = "extract-mod", Use = "archive.extract",
                    With = Params(("from", "package://mod-bundle"), ("to", "staging://custom-roles")) },
                new RecipeStep { Id = "install-plugin", Use = "file.copy",
                    With = Params(("from", "staging://custom-roles/CustomRoles.dll"), ("to", "game://BepInEx/plugins/CustomRoles.dll")) },
            ],
            Verify =
            [
                new RecipeVerifyStep { Use = "file.verify",
                    With = Params(("path", "game://BepInEx/plugins/CustomRoles.dll")) },
            ],
        };
    }

    public static Dictionary<string, JsonElement> Params(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    public Recipe CopyRecipe(string recipeId, string artifactId, string target, string content, string overwrite = "fail")
    {
        // Plain artifact file (not a zip): file.copy moves its bytes verbatim.
        File.WriteAllText(Path.Combine(PackageRoot, artifactId + ".zip"), content);
        return new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = recipeId,
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["file.copy"] },
            Artifacts = [new RecipeArtifact { Id = artifactId, Source = $"package://{artifactId}.zip" }],
            Steps =
            [
                new RecipeStep { Id = "copy", Use = "file.copy",
                    With = Params(("from", $"package://{artifactId}"), ("to", target), ("overwrite", overwrite)) },
            ],
        };
    }

    public (OperationPlan Plan, ExecutionJournal Journal) Install(Recipe recipe, string recipeId)
    {
        var plan = OperationPlanner.Plan(recipe, PlannerContext(recipeId));
        var executor = new PlanExecutor(PlanExecutionContext(recipeId));
        var journal = executor.Execute(plan, executor.Approve(plan));
        return (plan, journal);
    }

    public string[] TreeSnapshot() =>
        Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? p + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))
                : p + ":dir")
            .ToArray();

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class PlanExecutorTests
{
    [Fact]
    public void InstallVerifyUninstallRoundTrip()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var (plan, journal) = fixture.Install(recipe, "test.recipe");
        Assert.Equal(JournalState.Completed, journal.State);
        Assert.All(journal.Entries, e => Assert.True(e.Verified));

        var plugin = Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "CustomRoles.dll");
        Assert.Equal("custom-roles-v1", File.ReadAllText(plugin));
        Assert.Equal("new-loader", File.ReadAllText(Path.Combine(fixture.GameRoot, "winhttp.dll")));

        new PlanExecutor(fixture.PlanExecutionContext("test.recipe")).Uninstall("test.recipe");
        Assert.False(File.Exists(plugin));
        Assert.Equal("original-loader", File.ReadAllText(Path.Combine(fixture.GameRoot, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "core", "BepInEx.dll")));
    }

    [Fact]
    public void UninstallFirstModKeepsSecondModSharedFile()
    {
        using var fixture = new ExecutorFixture();
        const string target = "game://BepInEx/plugins/Shared.dll";
        fixture.Install(fixture.CopyRecipe("test.shared.a", "mod-a", target, "content-A"), "test.shared.a");
        fixture.Install(fixture.CopyRecipe("test.shared.b", "mod-b", target, "content-B", "replace"), "test.shared.b");
        var shared = Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Shared.dll");
        Assert.Equal("content-B", File.ReadAllText(shared));

        new PlanExecutor(fixture.PlanExecutionContext("test.shared.a")).Uninstall("test.shared.a");
        Assert.Equal("content-B", File.ReadAllText(shared)); // recomputed, not naive restore

        new PlanExecutor(fixture.PlanExecutionContext("test.shared.b")).Uninstall("test.shared.b");
        Assert.False(File.Exists(shared)); // no base existed
    }

    [Fact]
    public void CancellationInterruptsAndResumeCompletes()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var journal = executor.Approve(plan);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => executor.Execute(plan, journal, cts.Token));
        var interrupted = PlanExecutor.LoadJournal(fixture.ProfileRoot)!;
        Assert.Equal(JournalState.Interrupted, interrupted.State);

        var resumed = executor.Execute(plan, interrupted);
        Assert.Equal(JournalState.Completed, resumed.State);
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "CustomRoles.dll")));
    }

    [Fact]
    public void MidExecutionFailureCanResumeAfterFix()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var journal = executor.Approve(plan);
        var modZip = Path.Combine(fixture.PackageRoot, "mod-bundle.zip");
        var saved = File.ReadAllBytes(modZip);
        File.Delete(modZip);
        Assert.Throws<PhaketException>(() => executor.Execute(plan, journal));
        var failed = PlanExecutor.LoadJournal(fixture.ProfileRoot)!;
        Assert.Equal(JournalState.Failed, failed.State);
        Assert.True(failed.Entries[0].Verified); // loader step survived

        File.WriteAllBytes(modZip, saved);
        var resumed = executor.Execute(plan, failed);
        Assert.Equal(JournalState.Completed, resumed.State);
    }

    [Fact]
    public void NewExecutorInstanceRecoversPersistedJournal()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var first = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var journal = first.Approve(plan);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => first.Execute(plan, journal, cts.Token));

        // Simulates process restart: a fresh executor reads the durable journal.
        var second = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var recovered = PlanExecutor.LoadJournal(fixture.ProfileRoot)!;
        var completed = second.Execute(plan, recovered);
        Assert.Equal(JournalState.Completed, completed.State);
    }

    [Fact]
    public void RollbackRestoresBaseStateAndIsIdempotent()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var (plan, journal) = fixture.Install(recipe, "test.recipe");
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var rolledBack = executor.Rollback(journal);
        Assert.Equal(JournalState.RolledBack, rolledBack.State);
        Assert.Equal("original-loader", File.ReadAllText(Path.Combine(fixture.GameRoot, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "CustomRoles.dll")));
        Assert.Equal(JournalState.RolledBack, executor.Rollback(rolledBack).State);
    }

    [Fact]
    public void DivergenceFailsByDefaultAndRestorePolicyRecovers()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.CopyRecipe("test.div", "mod-div", "game://BepInEx/plugins/Div.dll", "expected");
        var (plan, journal) = fixture.Install(recipe, "test.div");
        var target = Path.Combine(fixture.GameRoot, "BepInEx", "plugins", "Div.dll");
        File.WriteAllText(target, "tampered");
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.div"));
        var ex = Assert.Throws<PhaketException>(() => executor.Rollback(journal));
        Assert.Equal(ErrorCodes.JournalDivergence, ex.Error.Code);
        executor.Rollback(journal, DivergencePolicy.Restore);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void TamperedArtifactHashBlocksExecution()
    {
        using var fixture = new ExecutorFixture();
        var recipe = fixture.FullRecipe();
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        ExecutorFixture.MakeZip(Path.Combine(fixture.PackageRoot, "mod-bundle.zip"),
            ("CustomRoles.dll", "tampered-content"u8.ToArray()));
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.recipe"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ArtifactHashMismatch, ex.Error.Code);
        Assert.Equal(JournalState.Failed, PlanExecutor.LoadJournal(fixture.ProfileRoot)!.State);
    }

    private static Recipe ExtractRecipe(string artifactId) => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.extract",
        Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
        Requires = new RecipeRequires { Capabilities = ["archive.extract"] },
        Artifacts = [new RecipeArtifact { Id = artifactId, Source = $"package://{artifactId}.zip" }],
        Steps =
        [
            new RecipeStep { Id = "extract", Use = "archive.extract",
                With = ExecutorFixture.Params(("from", $"package://{artifactId}"), ("to", "staging://out")) },
        ],
    };

    [Fact]
    public void ZipSlipIsBlocked()
    {
        using var fixture = new ExecutorFixture();
        ExecutorFixture.MakeZip(Path.Combine(fixture.PackageRoot, "bad.zip"), ("../evil.dll", "x"u8.ToArray()));
        var plan = OperationPlanner.Plan(ExtractRecipe("bad"), fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.extract"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ArchiveUnsafe, ex.Error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "evil.dll")));
    }

    [Fact]
    public void DecompressionBombIsBlocked()
    {
        using var fixture = new ExecutorFixture();
        ExecutorFixture.MakeZip(Path.Combine(fixture.PackageRoot, "bomb.zip"),
            ("zeros.bin", new byte[20_000_000]));
        var plan = OperationPlanner.Plan(ExtractRecipe("bomb"), fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.extract"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ArchiveUnsafe, ex.Error.Code);
    }

    [Fact]
    public void SymlinkArchiveEntryIsBlocked()
    {
        using var fixture = new ExecutorFixture();
        var zipPath = Path.Combine(fixture.PackageRoot, "link.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = 0xA1FF << 16; // unix symlink mode
            using var stream = entry.Open();
            stream.Write("/etc/passwd"u8);
        }
        var plan = OperationPlanner.Plan(ExtractRecipe("link"), fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.extract"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.ArchiveUnsafe, ex.Error.Code);
    }

    [Fact]
    public void FileRemoveWithoutOwnershipIsBlocked()
    {
        using var fixture = new ExecutorFixture();
        var recipe = new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.remove",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["file.remove"] },
            Steps =
            [
                new RecipeStep { Id = "remove", Use = "file.remove",
                    With = ExecutorFixture.Params(("path", "game://winhttp.dll"), ("ownership", "required")) },
            ],
        };
        var plan = OperationPlanner.Plan(recipe, fixture.PlannerContext());
        var executor = new PlanExecutor(fixture.PlanExecutionContext("test.remove"));
        var ex = Assert.Throws<PhaketException>(() => executor.Execute(plan, executor.Approve(plan)));
        Assert.Equal(ErrorCodes.OwnershipViolation, ex.Error.Code);
        Assert.True(File.Exists(Path.Combine(fixture.GameRoot, "winhttp.dll")));
    }

    [Fact]
    public void ConfigKeyUninstallRecomputesFromRemainingOwners()
    {
        using var fixture = new ExecutorFixture();
        var cfgPath = Path.Combine(fixture.GameRoot, "BepInEx", "config", "mod.cfg");
        File.WriteAllText(cfgPath, "Setting = base\n");

        Recipe ConfigRecipe(string id, string value) => new()
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = id,
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["config.set"] },
            Steps =
            [
                new RecipeStep { Id = "set", Use = "config.set",
                    With = ExecutorFixture.Params(("path", "game://BepInEx/config/mod.cfg"),
                        ("format", "keyvalue"), ("key", "Setting"), ("value", value)) },
            ],
        };

        fixture.Install(ConfigRecipe("test.cfg.a", "A"), "test.cfg.a");
        fixture.Install(ConfigRecipe("test.cfg.b", "B"), "test.cfg.b");
        Assert.Contains("Setting = B", File.ReadAllText(cfgPath));

        new PlanExecutor(fixture.PlanExecutionContext("test.cfg.a")).Uninstall("test.cfg.a");
        Assert.Contains("Setting = B", File.ReadAllText(cfgPath));

        new PlanExecutor(fixture.PlanExecutionContext("test.cfg.b")).Uninstall("test.cfg.b");
        Assert.Contains("Setting = base", File.ReadAllText(cfgPath));
    }
}
