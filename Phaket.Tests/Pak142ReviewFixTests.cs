using System.Text.Json;
using Avalonia.Headless.XUnit;
using Phaket.Core.Accounts;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Review fixes (group C): every plan path compares against the agreed baseline, a conflict
// decision filters the plan PER PATH instead of cutting it, the auto-pin respects the
// sandbox, the pin warning follows a scan, and renders get unique names.
public sealed class Pak142ReviewFixTests
{
    private const string ModPath = "BepInEx/plugins/Mod.dll";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Recipe ModRecipe(string content, string path) => CopyRecipe(content, path, "mod-bundle", "mod.dll");

    private static Recipe CopyRecipe(string content, string path, string artifact, string sourceFile)
    {
        return new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = "test.recipe",
            Version = "1.0.0",
            Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
            Requires = new RecipeRequires { Capabilities = ["file.copy"] },
            Artifacts = [new RecipeArtifact { Id = artifact, Source = "package://" + sourceFile }],
            Steps =
            [
                new RecipeStep
                {
                    Id = "copy",
                    Use = "file.copy",
                    With = new Dictionary<string, JsonElement>
                    {
                        ["from"] = JsonSerializer.SerializeToElement("package://" + artifact),
                        ["to"] = JsonSerializer.SerializeToElement("game://" + path),
                        ["overwrite"] = JsonSerializer.SerializeToElement("replace"),
                    },
                },
            ],
        };
    }

    // --- (1) per-path decisions: the plan keeps its writes, the executor filters them ---

    [Fact]
    public void ADecisionAppliesOnlyToItsOwnPathAndUnrelatedWritesStillRun()
    {
        var root = Directory.CreateTempSubdirectory("phaket-fixc-run-").FullName;
        try
        {
            var gameRoot = Path.Combine(root, "game");
            var packageRoot = Path.Combine(root, "packages");
            var stateRoot = Path.Combine(root, "state");
            foreach (var dir in new[] { Path.Combine(gameRoot, "BepInEx", "plugins"), packageRoot,
                         Path.Combine(root, "profile"), Path.Combine(root, "staging"), stateRoot })
                Directory.CreateDirectory(dir);
            string Game(string relative) => Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var baselinePath = InstallBaseline.DefaultPath(stateRoot);

            // Round 1: two independent files in one transaction, both agreed.
            File.WriteAllText(Path.Combine(packageRoot, "mod.dll"), "mod-v1");
            File.WriteAllText(Path.Combine(packageRoot, "other.dll"), "other-v1");
            var twoFiles = new List<RecipeStep>(ModRecipe("mod-v1", ModPath).Steps)
            {
                Capacity = 2,
            };
            twoFiles.Add(OtherStep("other.dll"));
            Run(OperationPlanner.Plan(TwoFilesRecipe(ModRecipe("mod-v1", ModPath), twoFiles), Planner(root, null)), gameRoot, root);

            // The user edits the mod file only, and the new recipe changes it too.
            var agreed = InstallBaseline.Load(baselinePath, "install-1");
            Assert.True(agreed.Files.ContainsKey("BepInEx/plugins/Other.dll"));
            File.WriteAllText(Game(ModPath), "user-edit");
            File.WriteAllText(Path.Combine(packageRoot, "mod.dll"), "mod-v2");
            File.WriteAllText(Path.Combine(packageRoot, "other.dll"), "other-v2");
            var changed = new List<RecipeStep>(ModRecipe("mod-v2", ModPath).Steps);
            changed.Add(OtherStep("other.dll"));
            var conflicted = OperationPlanner.Plan(TwoFilesRecipe(ModRecipe("mod-v2", ModPath), changed), Planner(root, agreed));
            var conflict = Assert.Single(conflicted.Conflicts);
            Assert.Equal(ModPath, conflict.Path);
            Assert.Contains(conflicted.Operations, o => o.Kind == "file.copy" && o.TargetPath == Game(ModPath));
            // The unrelated file is not part of the question and is not cut with it.
            Assert.Contains(conflicted.Operations, o => o.Kind == "file.copy" && o.TargetPath == Game("BepInEx/plugins/Other.dll"));

            // "Keep mine": the conflicted file stays as the user left it.
            var keeper = new PlanExecutor(Context(gameRoot, root));
            var kept = keeper.Execute(conflicted, keeper.Approve(conflicted), Ct,
                decisions: new Dictionary<string, ConflictDecision> { [ModPath] = ConflictDecision.KeepLocal });
            Assert.Equal(JournalState.Completed, kept.State);
            Assert.Equal("user-edit", File.ReadAllText(Game(ModPath)));
            Assert.DoesNotContain(kept.Entries, entry => entry.Intent.Kind == "file.copy" && entry.Applied
                && entry.Intent.TargetPath == Game(ModPath));

            Assert.Equal("other-v2", File.ReadAllText(Game("BepInEx/plugins/Other.dll")));

            // "Use the mod's version": the same conflicted file now takes the recipe bytes.
            File.WriteAllText(Game(ModPath), "user-edit-2");
            File.WriteAllText(Path.Combine(packageRoot, "mod.dll"), "mod-v3");
            var again = Plan(ModRecipe("mod-v3", ModPath), root, InstallBaseline.Load(baselinePath, "install-1"));
            Assert.Single(again.Conflicts);
            var taker = new PlanExecutor(Context(gameRoot, root));
            Assert.Equal(JournalState.Completed,
                taker.Execute(again, taker.Approve(again), Ct,
                    decisions: new Dictionary<string, ConflictDecision> { [ModPath] = ConflictDecision.TakeRecipe }).State);
            Assert.Equal("mod-v3", File.ReadAllText(Game(ModPath)));

            // No answer: refused before any mutation, the journal stays the approval.
            File.WriteAllText(Game(ModPath), "user-edit-3");
            File.WriteAllText(Path.Combine(packageRoot, "mod.dll"), "mod-v4");
            var third = Plan(ModRecipe("mod-v4", ModPath), root, InstallBaseline.Load(baselinePath, "install-1"));
            // A fresh journal: the previous transaction on this profile state is Completed, and
            // Approve would refuse it, which would not be the conflict gate under test.
            var refusedExecutor = new PlanExecutor(Context(gameRoot, root));
            var usedJournal = new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion, "t-refused", third.PlanId,
                JournalState.Approved, []);
            var refused = Assert.Throws<PhaketException>(() => refusedExecutor.Execute(third, usedJournal, Ct));
            Assert.Equal(ErrorCodes.FileConflictRequiresDecision, refused.Error.Code);
            Assert.Equal("user-edit-3", File.ReadAllText(Game(ModPath)));
            // The refusal wrote nothing: the journal handed in was not advanced and the file
            // on disk is the one the last real transaction left behind.
            Assert.Equal(JournalState.Approved, usedJournal.State);
            Assert.Empty(usedJournal.Entries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheOrchestratorRunsAnAnsweredPlanInsteadOfRefusingIt()
    {
        var root = Directory.CreateTempSubdirectory("phaket-fixc-orch-").FullName;
        try
        {
            var gameRoot = Path.Combine(root, "game");
            foreach (var dir in new[] { Path.Combine(gameRoot, "BepInEx", "plugins"), Path.Combine(root, "packages"),
                         Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "state") })
                Directory.CreateDirectory(dir);
            var modFile = Path.Combine(gameRoot, ModPath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(Path.Combine(root, "packages", "mod.dll"), "mod-v1");
            new InstallOrchestrator(new PlanExecutor(Context(gameRoot, root)))
                .Run(OperationPlanner.Plan(ModRecipe("mod-v1", ModPath), Planner(root)), cancellationToken: Ct);

            File.WriteAllText(modFile, "user-edit");
            File.WriteAllText(Path.Combine(root, "packages", "mod.dll"), "mod-v2");
            var plan = OperationPlanner.Plan(ModRecipe("mod-v2", ModPath), Planner(root, InstallBaseline.Load(
                InstallBaseline.DefaultPath(Path.Combine(root, "state")), "install-1")));
            Assert.Single(plan.Conflicts);

            // The lifecycle used to refuse every plan with a conflict; with the user's answer
            // it must run the same plan and record the new agreed state.
            var journal = new InstallOrchestrator(new PlanExecutor(Context(gameRoot, root)))
                .Run(plan, cancellationToken: Ct, decisions: new Dictionary<string, ConflictDecision> { [ModPath] = ConflictDecision.TakeRecipe });

            Assert.Equal(JournalState.Completed, journal.State);
            Assert.Equal("mod-v2", File.ReadAllText(modFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TheDefinitionAdapterRefusesAnUnansweredConflictBeforeWriting()
    {
        var root = Directory.CreateTempSubdirectory("phaket-fixc-adapter-").FullName;
        try
        {
            var gameRoot = Path.Combine(root, "game");
            foreach (var dir in new[] { Path.Combine(gameRoot, "BepInEx", "plugins"), Path.Combine(root, "packages"),
                         Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "state") })
                Directory.CreateDirectory(dir);
            var modFile = Path.Combine(gameRoot, ModPath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(Path.Combine(root, "packages", "mod.dll"), "mod-v1");
            new InstallOrchestrator(new PlanExecutor(Context(gameRoot, root)))
                .Run(OperationPlanner.Plan(ModRecipe("mod-v1", ModPath), Planner(root)), cancellationToken: Ct);
            File.WriteAllText(modFile, "user-edit");
            File.WriteAllText(Path.Combine(root, "packages", "mod.dll"), "mod-v2");

            var adapter = new DefinitionGameAdapter(AmongUs, "linux", "steam", gameRoot,
                Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages"),
                _ => ModRecipe("mod-v2", ModPath), Path.Combine(root, "state"));
            var plan = adapter.PlanInstall(new Profile("profile-1", "install-1", [new RecipeRef("test.recipe", "1.0.0")]),
                new InstallationFacts(new Installation("install-1", "among-us-steam", gameRoot), ["2026.3.5"],
                    OperationCatalog.Capabilities.ToArray(), "x86"));
            Assert.Single(plan.Conflicts);

            var refused = await Assert.ThrowsAsync<PhaketException>(() =>
                adapter.ApplyPlan(plan, new PlanExecutor(Context(gameRoot, root)), CancellationToken.None));
            Assert.Equal(ErrorCodes.FileConflictRequiresDecision, refused.Error.Code);
            Assert.Equal("user-edit", File.ReadAllText(modFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // --- (2) the ModInstallService planner carries the installation's baseline ---

    [Fact]
    public void ModInstallServicePlansAgainstTheBaselineOfItsStateRoot()
    {
        var stateRoot = Directory.CreateTempSubdirectory("phaket-fixc-state-").FullName;
        try
        {
            Assert.Null(ModInstallService.LoadBaseline(stateRoot));
            var agreed = InstallBaseline.Create("install", new Dictionary<string, BaselineFileState>
            {
                [ModPath] = new(new string('b', 64), DateTimeOffset.UnixEpoch, 6),
            });
            agreed.Save(InstallBaseline.DefaultPath(stateRoot));

            var loaded = ModInstallService.LoadBaseline(stateRoot);

            Assert.NotNull(loaded);
            Assert.True(InstallBaseline.SameEntries(agreed, loaded!));
            // A stale schema is ignored (the plan stays as before), it is not an empty baseline.
            File.WriteAllText(InstallBaseline.DefaultPath(stateRoot),
                File.ReadAllText(InstallBaseline.DefaultPath(stateRoot)).Replace(InstallBaseline.CurrentSchemaVersion, "install-baseline/v0"));
            Assert.Null(ModInstallService.LoadBaseline(stateRoot));
        }
        finally { Directory.Delete(stateRoot, recursive: true); }
    }

    // --- (3) the modpack auto-pin never pins a sandbox install ---

    [Fact]
    public void AutoPinSkipsSandboxInstallsAndPinsRealOnes()
    {
        var manifest = new SteamAppManifest("945360", "Among Us", "24302054", "public", "777", "Among Us", 1);
        InstallPreparation Preparation(string sandboxRoot, bool sandbox) => new("among-us", "Owner-Mod", "1.0", "",
            new InstallTarget("/games/Among Us", "steam", sandbox), [], [], [], [], sandboxRoot, "", "", "", "", Manifest: manifest);
        InstalledRecipe Entry(string sandboxRoot) => new("test.recipe", "1.0", "among-us", "steam", "/games/Among Us",
            "/profile", "/staging", "/state", sandboxRoot.Length == 0 ? null : sandboxRoot, DateTimeOffset.UtcNow);

        var sandboxed = ModpackInstallService.Pinned(Entry("/tmp/phaket-install-x"), Preparation("/tmp/phaket-install-x", true));
        Assert.False(sandboxed.IsPinned);
        // A preparation that says sandbox through its root alone is a sandbox too.
        Assert.False(ModpackInstallService.Pinned(Entry(""), Preparation("/tmp/phaket-install-y", false)).IsPinned);

        var real = ModpackInstallService.Pinned(Entry(""), Preparation("", false));
        Assert.True(real.IsPinned);
        Assert.Equal("24302054", real.PinnedBuildId);
        Assert.Equal("777", real.PinnedManifestId);
    }

    // --- (4) a scan raises the pin warning without another click ---

    // Signed in, but the library and recorded installs are empty: no network, no real Steam.
    private sealed class NoSteam : IAccountService
    {
        public string Provider => "steam";
        public AccountInfo? Current { get; } = new("steam", "1", "steam-user", null, DateTimeOffset.UtcNow);
        public event Action? AccountChanged { add { } remove { } }
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default) => Task.FromResult(Current!);
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoAccount : IAccountService
    {
        public string Provider => "epic";
        public AccountInfo? Current => null;
        public event Action? AccountChanged { add { } remove { } }
        public Task<AccountInfo> LoginAsync(CancellationToken ct = default) => Task.FromResult(new AccountInfo("epic", "1", "n", null, DateTimeOffset.UtcNow));
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void ScanShowsThePinWarningWithoutASelectedGameClick()
    {
        var home = Directory.CreateTempSubdirectory("phaket-fixc-scan-").FullName;
        // The scan also lists the signed-in Steam library; keep it offline and empty.
        var emptySteam = new NoSteam();
        try
        {
            // Build the library the way the scanner looks for it on THIS OS: a Linux layout
            // under the temp home is invisible to the Windows Program Files root.
            var steamRoot = OperatingSystem.IsWindows()
                ? Path.Combine("C:\\Program Files (x86)", "Steam")
                : Path.Combine(home, ".local", "share", "Steam");
            var steamApps = Path.Combine(steamRoot, "steamapps");
            var gameRoot = Path.Combine(steamApps, "common", "Among Us");
            Directory.CreateDirectory(gameRoot);
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_945360.acf"), """
                "AppState"
                {
                    "appid"      "945360"
                    "name"       "Among Us"
                    "buildid"    "87654321"
                    "branch"     "public"
                    "manifest"   "555"
                    "installdir" "Among Us"
                }
                """);
            var registryPath = Path.Combine(home, "installs.json");
            new InstallRegistry(registryPath).Add(new InstalledRecipe("test.recipe", "1.0.0", "among-us", "steam",
                gameRoot, gameRoot + "-profile", gameRoot + "-staging", gameRoot + "-state", null, DateTimeOffset.UnixEpoch,
                PinnedAt: DateTimeOffset.UtcNow, PinnedBuildId: "12345678", PinnedManifestId: "555"));

            var vm = new LibraryViewModel(_ => { }, new NoAccount(), null,
                _ => Task.FromResult<IReadOnlyList<OwnedEpicGame>>([]), () => [], emptySteam,
                _ => Task.FromResult<IReadOnlyList<OwnedSteamGame>>([]), () => [])
            {
                SteamHome = home,
            };
            vm.UseRegistry(registryPath);
            // Selecting the game would also refresh the pin; the scan alone has to do it.
            Assert.Null(vm.SelectedGame?.Detected);
            Assert.False(vm.HasPinUpdateWarning);

            UiTest.Run(() => vm.ScanCommand.ExecuteAsync(null));

            Assert.Equal("87654321", vm.SelectedGame?.Detected?.Manifest.BuildId);
            Assert.True(vm.HasPinUpdateWarning);
            Assert.Contains("12345678", vm.PinUpdateMessage);
            Assert.Contains("87654321", vm.PinUpdateMessage);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    // --- (6) render helper: two names never collide, and the readable part stays first ---

    [Fact]
    public void RenderNamesAreUniquePerPathAndKeepAReadablePrefix()
    {
        var first = RenderArtifacts.Path("15-account.png");
        var second = RenderArtifacts.Path("15-account.png");
        Assert.NotEqual(first, second);
        Assert.StartsWith("15-account--", Path.GetFileName(first), StringComparison.Ordinal);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
        Assert.Equal(RenderArtifacts.Directory, Path.GetDirectoryName(first));
        // A prefixed name (the per-language renders) keeps its own readable part.
        Assert.StartsWith("45-conflict-decision-en--",
            Path.GetFileName(RenderArtifacts.Path("45-conflict-decision-en.png")), StringComparison.Ordinal);
    }

    private static PlanExecutionContext Context(string gameRoot, string root) => new(
        gameRoot, Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages"),
        AmongUs, "test.recipe", "profile-1", Path.Combine(root, "state"), InstallationId: "install-1");

    private static Recipe TwoFilesRecipe(Recipe baseRecipe, List<RecipeStep> steps) => baseRecipe with
    {
        Steps = steps,
        Artifacts = [.. baseRecipe.Artifacts, new RecipeArtifact { Id = "other.dll", Source = "package://other.dll" }],
    };

    // The second, unrelated write of the same recipe: own step id, own artifact and path.
    private static RecipeStep OtherStep(string artifact) => new()
    {
        Id = "copy-other",
        Use = "file.copy",
        With = new Dictionary<string, JsonElement>
        {
            ["from"] = JsonSerializer.SerializeToElement("package://" + artifact),
            ["to"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/Other.dll"),
            ["overwrite"] = JsonSerializer.SerializeToElement("replace"),
        },
    };

    private static OperationPlan Plan(Recipe recipe, string root, InstallBaseline? baseline = null) =>
        OperationPlanner.Plan(recipe, Planner(root, baseline));

    private static ExecutionJournal Run(OperationPlan plan, string gameRoot, string root)
    {
        var executor = new PlanExecutor(Context(gameRoot, root));
        var journal = executor.Execute(plan, executor.Approve(plan), Ct);
        Assert.True(journal.State == JournalState.Completed, $"plan {plan.RecipeRef.Id} ended as {journal.State}");
        return journal;
    }

    private static readonly GameDefinition AmongUs = GameDefinitions.LoadAll().First(game => game.Slug == "among-us");

    private static PlannerContext Planner(string root, InstallBaseline? baseline = null) => new(
        "install-1", "profile-1", Path.Combine(root, "game"), Path.Combine(root, "profile"),
        Path.Combine(root, "staging"), Path.Combine(root, "packages"),
        AmongUs,
        new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false,
            new Dictionary<string, string> { ["bepinex"] = "6.0.0" }, Arch: "x86"),
        Baseline: baseline);
}
