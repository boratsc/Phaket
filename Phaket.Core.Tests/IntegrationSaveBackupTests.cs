using System.Text.Json;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-141 integration: a definition with SavePaths is copied BEFORE the first write,
// an over-limit backup never mutates the game tree, and a definition without
// SavePaths behaves exactly as before.
public sealed class IntegrationSaveBackupTests : IDisposable
{
    private const string ModPath = "BepInEx/plugins/Mod.dll";
    private readonly string root;
    private readonly string gameRoot;
    private readonly string profileRoot;
    private readonly string stagingRoot;
    private readonly string packageRoot;
    private readonly string stateRoot;

    public IntegrationSaveBackupTests()
    {
        root = Directory.CreateTempSubdirectory("phaket-int-backup-").FullName;
        gameRoot = Path.Combine(root, "game");
        profileRoot = Path.Combine(root, "profile");
        stagingRoot = Path.Combine(root, "staging");
        packageRoot = Path.Combine(root, "packages");
        stateRoot = Path.Combine(root, "state");
        foreach (var dir in new[] { Path.Combine(gameRoot, "BepInEx", "plugins"), profileRoot, stagingRoot, packageRoot, stateRoot })
            Directory.CreateDirectory(dir);
    }

    private string ModFile => Path.Combine(gameRoot, ModPath.Replace('/', Path.DirectorySeparatorChar));
    private string SaveDir => Path.Combine(profileRoot, "saveData");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Recipe Recipe(string content)
    {
        File.WriteAllText(Path.Combine(packageRoot, "mod.dll"), content);
        return new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.recipe",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["file.copy"] },
            Artifacts = [new RecipeArtifact { Id = "mod-bundle", Source = "package://mod.dll" }],
            Steps =
            [
                new RecipeStep
                {
                    Id = "copy",
                    Use = "file.copy",
                    With = new Dictionary<string, JsonElement>
                    {
                        ["from"] = JsonSerializer.SerializeToElement("package://mod-bundle"),
                        ["to"] = JsonSerializer.SerializeToElement("game://" + ModPath),
                    },
                },
            ],
        };
    }

    private GameDefinition Definition() =>
        TestFixtures.AmongUs with { SavePaths = ["profile://saveData"] };

    private PlanExecutionContext Context(GameDefinition definition) =>
        new(gameRoot, profileRoot, stagingRoot, packageRoot, definition, "test.recipe", "profile-1", stateRoot);

    private PlannerContext PlannerContext(GameDefinition definition) => new(
        "install-1", "profile-1", gameRoot, profileRoot, stagingRoot, packageRoot,
        definition, new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false,
            new Dictionary<string, string> { ["bepinex"] = "6.0.0" }, Arch: "x86"));

    private void WriteSaves(string content)
    {
        Directory.CreateDirectory(Path.Combine(SaveDir, "sub"));
        File.WriteAllText(Path.Combine(SaveDir, "Player.log"), content);
        File.WriteAllText(Path.Combine(SaveDir, "sub", "settings.cfg"), "cfg");
    }

    [Fact]
    public async Task SavePathsAreCopiedBeforeTheFirstWrite()
    {
        WriteSaves("saves-v1");
        var definition = Definition();
        var context = Context(definition);
        var plan = OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(definition));

        var begin = await new PlanLifecycle(context).BeginAsync(plan, ct: Ct);

        Assert.True(begin.Approval.Accepted);
        Assert.False(begin.Approval.SaveBackupIsProfile);
        var backup = Assert.IsType<SaveBackupRef>(begin.Backup);
        Assert.Equal(SaveBackupManifest.CurrentSchemaVersion, backup.Manifest.SchemaVersion);
        Assert.True(File.Exists(Path.Combine(backup.Directory, "manifest.json")));
        Assert.Equal(2, backup.Manifest.Files.Count);
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(backup.Directory, "files"), "Player.log", SearchOption.AllDirectories));
        Assert.Equal("saves-v1", File.ReadAllText(copy));
        // The backup happened before any game write.
        Assert.False(File.Exists(ModFile));

        // The API the UI will call: list and, if needed, restore this backup.
        var gameFolder = SaveBackupService.BackupFolderName(backup.Manifest.GameId, backup.Manifest.InstallationId);
        var listed = await new SaveBackupService().ListBackupsAsync(PlanLifecycle.SaveBackupRoot(context), gameFolder, Ct);
        Assert.Equal(backup.Manifest.Id, Assert.Single(listed).Id);

        var executor = new PlanExecutor(context);
        Assert.Equal(JournalState.Completed, executor.Execute(plan, executor.Approve(plan), Ct).State);
        Assert.True(File.Exists(ModFile));
    }

    [Fact]
    public async Task OverLimitBackupNeverMutatesTheGameTree()
    {
        WriteSaves(new string('x', 4096));
        var definition = Definition();
        var context = Context(definition);
        var plan = OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(definition));
        var journal = new PlanExecutor(context).Approve(plan);

        var begin = await new PlanLifecycle(context).BeginAsync(plan, limitBytes: 1024, ct: Ct);

        // Over the limit the copy is skipped for a profile record and the install proceeds;
        // the journal is still approved and the game tree was never touched.
        Assert.True(begin.Approval.Accepted);
        Assert.True(begin.Approval.SaveBackupIsProfile);
        Assert.Equal(SaveBackupService.ProfileSchemaVersion, begin.Backup!.Manifest.SchemaVersion);
        Assert.Equal(JournalState.Approved, PlanExecutor.LoadJournal(profileRoot)!.State);
        Assert.Empty(journal.Entries);
        Assert.False(File.Exists(ModFile));
        Assert.Equal(new string('x', 4096), File.ReadAllText(Path.Combine(SaveDir, "Player.log")));
    }

    [Fact]
    public async Task DefinitionWithoutSavePathsKeepsTheOldBehaviour()
    {
        WriteSaves("saves-v1");
        var context = Context(TestFixtures.AmongUs);
        var plan = OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(TestFixtures.AmongUs));
        var begin = await new PlanLifecycle(context).BeginAsync(plan, ct: Ct);

        Assert.True(begin.Approval.Accepted);
        Assert.Null(begin.Backup);
        Assert.False(Directory.Exists(PlanLifecycle.SaveBackupRoot(context)));

        var executor = new PlanExecutor(context);
        Assert.Equal(JournalState.Completed, executor.Execute(plan, executor.Approve(plan), Ct).State);
        Assert.True(File.Exists(ModFile));
    }

    // R3a: the backup reads where the game itself keeps saves (saveLocations), not only the
    // profile copy — otherwise a Linux player's progress is never in the copy.
    [Fact]
    public void SaveLocationsOfTheGameAreBackedUpToo()
    {
        if (OperatingSystem.IsWindows()) return; // the fixture names a home-relative Linux folder
        var real = Directory.CreateTempSubdirectory("phaket-real-saves-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(real, "Player.log"), "real-progress");
            var definition = Definition() with
            {
                SaveLocations = new SaveLocationsByOs(new Dictionary<string, IReadOnlyList<string>>
                {
                    ["linux"] = [real],
                }),
            };
            var context = Context(definition);
            var lifecycle = new Phaket.Core.Execution.SaveBackup.PlanLifecycle(context);

            var request = lifecycle.SaveBackupRequest();

            Assert.NotNull(request);
            Assert.Contains(real, request!.Locations);
            Assert.DoesNotContain(profileRoot, request.Locations);
        }
        finally { Directory.Delete(real, true); }
    }

    [Fact]
    public void ExecutorItselfCopiesSavesWhenTheHostDidNotApprove()
    {
        WriteSaves("saves-v1");
        var definition = Definition();
        var context = Context(definition);
        var plan = OperationPlanner.Plan(Recipe("content-v1"), PlannerContext(definition));
        var executor = new PlanExecutor(context);

        var journal = executor.Execute(plan, executor.Approve(plan), Ct);

        Assert.Equal(JournalState.Completed, journal.State);
        var folder = Assert.Single(Directory.GetDirectories(PlanLifecycle.SaveBackupRoot(context)));
        Assert.Contains(Directory.GetDirectories(folder), d => File.Exists(Path.Combine(d, "manifest.json")));
    }

    [Fact]
    public void SavePathOutsideTheProfileIsIgnored()
    {
        Assert.Null(PlanLifecycle.ResolveSavePath("game://saveData", profileRoot));
        Assert.Equal(Path.Combine(profileRoot, "saveData"), PlanLifecycle.ResolveSavePath("profile://saveData", profileRoot));
        Assert.Null(PlanLifecycle.ResolveSavePath("relative/without/scheme", profileRoot));
        Assert.Equal(Path.Combine(root, "absolute"), PlanLifecycle.ResolveSavePath(Path.Combine(root, "absolute"), profileRoot));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}