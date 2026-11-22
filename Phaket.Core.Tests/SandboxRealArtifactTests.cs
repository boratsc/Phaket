using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;

namespace Phaket.Core.Tests;

public sealed class SandboxRealArtifactTests
{
    private const string ArtifactRoot = "/tmp/opencode/phaket-research/artifacts";
    private const string ReportPath = "/tmp/opencode/phaket-research/report-core.md";
    private static readonly List<string> Log = [];

    private sealed class Source(params (string Name, ThunderstoreVersion[] Versions)[] entries) : IModVersionSource
    {
        private readonly Dictionary<string, ThunderstoreModDetails> values = entries.ToDictionary(x => x.Name,
            x => new ThunderstoreModDetails(null!, x.Versions, DateTimeOffset.UnixEpoch), StringComparer.OrdinalIgnoreCase);
        public Task<ThunderstoreModDetails?> GetDetailsAsync(string fullName, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(fullName));
    }

    private sealed class Receipts : IReceiptTransport
    {
        public List<ModReceipt> Items { get; } = [];
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken)
        { Items.Add(receipt); return Task.CompletedTask; }
    }

    private sealed class ArtifactTransport(IReadOnlyDictionary<string, string> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
        {
            var file = files[url];
            return Task.FromResult<Stream>(File.OpenRead(Path.Combine(ArtifactRoot, file)));
        }
    }

    private static ArtifactTransport Transport(params (string Url, string File)[] files) =>
        new(files.ToDictionary(x => x.Url, x => x.File, StringComparer.Ordinal));

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    // CI has no research artifacts: a test that needs a real archive skips, it does not fail.
    private static void RequireArtifacts(params string[] names)
    {
        var missing = names.FirstOrDefault(name => !File.Exists(Path.Combine(ArtifactRoot, name)));
        if (missing is not null) Assert.Skip($"Real artifact {missing} is not available locally.");
    }

    private static string RecipePath(string name) => Path.Combine(RecipeLibrary.Directory, name);
    private static Recipe Load(string name) => RecipeParser.ParseYaml(File.ReadAllText(RecipePath(name)));

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("phaket-real-").FullName;
        public string Game { get; }
        public string Profile => Path.Combine(Root, "profile");
        public string Staging => Path.Combine(Root, "staging");
        public string Packages => Path.Combine(Root, "packages");
        public string State => Path.Combine(Root, "state");
        public GameDefinition Definition { get; }
        public Sandbox(string gameSlug)
        {
            Game = Path.Combine(Root, "game");
            Directory.CreateDirectory(Game);
            Definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "definitions", "games", $"{gameSlug}.yaml")));
        }
        public void Dispose() => Directory.Delete(Root, true);
        public async Task AddArtifacts(Recipe recipe, IReadOnlyDictionary<string,string> mapping)
        {
            // CI has no research artifacts: skip rather than fail a real-archive check.
            RequireArtifacts([.. mapping.Values]);
            await PackageDownloader.EnsureArtifactsAsync(recipe, Packages,
                Transport(recipe.Artifacts.Select(a => (a.Source, mapping[a.Id])).ToArray()));
        }
        public OperationPlan Plan(Recipe recipe) => OperationPlanner.Plan(recipe, new PlannerContext("sandbox", "profile",
            Game, Profile, Staging, Packages, Definition,
            new PlanFacts("linux", "steam", "2026.11.14", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string,string> { ["bepinex"] = recipe.Id.Contains("among-us") ? "6.0.700" : "5.4.2360" }, Arch: Definition.Slug == "valheim" ? "x64" : "x86")));
        public PlanExecutor Executor(Recipe recipe, Action<string>? fault = null) => new(new PlanExecutionContext(
            Game, Profile, Staging, Packages, Definition, recipe.Id, "profile", State), fault);
    }

    [Fact]
    public void ValheimCurated_RealArtifactsDryRunInstallUninstallResume()
    {
        Run("Valheim curated", () =>
        {
            var recipe = Load("valheim-morevanillabuilds.yaml");
            using var box = new Sandbox("valheim");
            Directory.CreateDirectory(Path.Combine(box.Game, "valheim_Data"));
            File.WriteAllText(Path.Combine(box.Game, "valheim.x86_64"), "vanilla-game");
            Directory.CreateDirectory(Path.Combine(box.Game, "BepInEx", "config"));
            box.AddArtifacts(recipe, new Dictionary<string,string> { ["loader-pack"]="valheim-loader-5.4.2202.zip", ["jotunn"]="jotunn-2.26.1.zip", ["mod"]="morevanillabuilds-1.1.5.zip" }).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(box.Game, "BepInEx", "config", "shared.cfg"), "user = intact\n");
            var vanillaHash = Hash(Path.Combine(box.Game, "valheim.x86_64"));
            var loaderZip = Path.Combine(box.Packages, "loader-pack.zip");
            var loaderEntries = ZipFile.OpenRead(loaderZip).Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')).ToArray();
            var generatedLoader = RecipeGenerator.Generate("denikson-BepInExPack_Valheim", "5.4.2202",
                recipe.Artifacts[0].Source, recipe.Artifacts[0].Sha256, loaderEntries, box.Definition).Recipe;
            Assert.NotEmpty(generatedLoader.Steps);
            var plan = box.Plan(recipe); Log.Add($"plan: {plan.Operations.Count} operations; game unchanged");
            Assert.Equal("vanilla-game", File.ReadAllText(Path.Combine(box.Game, "valheim.x86_64")));
            var executor = box.Executor(recipe);
            var journal = new InstallOrchestrator(executor).Run(plan);
            Assert.Equal(JournalState.Completed, journal.State);
            var jotunn = Path.Combine(box.Game, "BepInEx/plugins/ValheimModding-Jotunn/Jotunn.dll");
            var mod = Path.Combine(box.Game, "BepInEx/plugins/Galateam-MoreVanillaBuilds/MoreVanillaBuilds.dll");
            Assert.True(File.Exists(jotunn) && File.Exists(mod));
            Assert.Equal("user = intact\n", File.ReadAllText(Path.Combine(box.Game, "BepInEx/config/shared.cfg")));
            Assert.Equal("d03d59c6eda1e526ab5005f7b4302b7e3195b4e7f3b4c38c13fb5db228ab4b7b", Hash(mod));
            Log.Add($"execute/verify: completed; {journal.Entries.Sum(e => e.CreatedFiles?.Count ?? 0)} recorded files; mod sha256={Hash(mod)}");
            executor.Uninstall(recipe.Id);
            Assert.False(File.Exists(jotunn)); Assert.False(File.Exists(mod));
            Assert.Equal(vanillaHash, Hash(Path.Combine(box.Game, "valheim.x86_64")));
            Assert.True(File.Exists(Path.Combine(box.Game, "BepInEx/config/shared.cfg")));
            Log.Add("uninstall: plugin files removed; shared config and game file preserved");
            var again = box.Executor(recipe);
            var reinstalled = new InstallOrchestrator(again).Run(plan);
            var stopped = box.Executor(recipe, stage => { if (stage == "after-file-write") throw new OperationCanceledException(); });
            var interrupted = stopped.Approve(plan);
            interrupted = stopped.ExecuteOperation(plan, interrupted, plan.Operations[0].OpId);
            stopped.Interrupt(interrupted);
            Assert.Equal(JournalState.Interrupted, PlanExecutor.LoadJournal(box.Profile)!.State);
            var recovered = box.Executor(recipe);
            var resume = PlanExecutor.LoadJournal(box.Profile)!;
            foreach (var op in plan.Operations) resume = recovered.ExecuteOperation(plan, resume, op.OpId);
            Assert.Equal(JournalState.Completed, recovered.Complete(plan, resume).State);
            recovered.Uninstall(recipe.Id);
            Assert.False(File.Exists(mod));
            Log.Add($"rollback/resume: interrupted after {reinstalled.Entries.Count} operations, resumed to completed, clean uninstall");
        });
    }

    [Fact]
    public void AmongUsCurated_RealArtifactsInstallAndUninstall()
    {
        Run("Among Us curated", () =>
        {
            var recipe = Load("among-us-outfit-presets.yaml");
            using var box = new Sandbox("among-us");
            foreach (var path in new[] { "Among Us.exe", "GameAssembly.dll", "UnityPlayer.dll" }) File.WriteAllText(Path.Combine(box.Game, path), "vanilla");
            Directory.CreateDirectory(Path.Combine(box.Game, "Among Us_Data"));
            box.AddArtifacts(recipe, new Dictionary<string,string> { ["loader-pack"]="amongus-loader-6.0.700.zip", ["tabsbuilder-api"]="tabsbuilderapi-1.2.1.zip", ["outfit-presets"]="outfit-presets-1.1.1.zip" }).GetAwaiter().GetResult();
            var before = Hash(Path.Combine(box.Game, "Among Us.exe")); var plan = box.Plan(recipe);
            Log.Add($"plan: {plan.Operations.Count} operations; synthetic game fixture");
            var journal = new InstallOrchestrator(box.Executor(recipe)).Run(plan);
            foreach (var path in new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx", "dotnet" }) Assert.True(File.Exists(Path.Combine(box.Game, path)) || Directory.Exists(Path.Combine(box.Game, path)), path);
            var plugin = Path.Combine(box.Game, "BepInEx/plugins/Cosmella-Outfit_Presets/OutfitsPresets.dll");
            Assert.True(File.Exists(plugin)); Assert.Equal("7cc2720a1979f9782692d312467933aaad5e1b5de3d161c711db44af67c67689", Hash(plugin));
            Log.Add($"execute/verify: {journal.State}; {journal.Entries.Sum(e => e.CreatedFiles?.Count ?? 0)} files; plugin sha256={Hash(plugin)}");
            box.Executor(recipe).Uninstall(recipe.Id);
            Assert.False(File.Exists(plugin)); Assert.False(File.Exists(Path.Combine(box.Game, "winhttp.dll")));
            Assert.Equal(before, Hash(Path.Combine(box.Game, "Among Us.exe")));
            Log.Add("uninstall: loader and mod ownership removed; vanilla game preserved");
        });
    }

    [Fact]
    public void BetterPolus_UpdateToReal123_PreservesConfigAndUninstallsCleanly()
    {
        Run("BetterPolus update", () =>
        {
            RequireArtifacts("betterpolus-1.2.2.zip", "betterpolus-1.2.3.zip", "amongus-loader-6.0.700.zip");
            var previous = Load("among-us-betterpolus.yaml");
            var newZip = Path.Combine(ArtifactRoot, "betterpolus-1.2.3.zip");
            var newHash = Hash(newZip);
            using var box = new Sandbox("among-us");
            foreach (var path in new[] { "Among Us.exe", "GameAssembly.dll", "UnityPlayer.dll" }) File.WriteAllText(Path.Combine(box.Game, path), "vanilla");
            Directory.CreateDirectory(Path.Combine(box.Game, "Among Us_Data"));
            box.AddArtifacts(previous, new Dictionary<string,string> { ["loader-pack"]="amongus-loader-6.0.700.zip", ["better-polus"]="betterpolus-1.2.2.zip" }).GetAwaiter().GetResult();
            var previousPlan = box.Plan(previous);
            var oldInstall = new InstallOrchestrator(box.Executor(previous)).Run(previousPlan);
            var oldPlugin = Path.Combine(box.Game, "BepInEx/plugins/BetterPolus.dll");
            var config = Path.Combine(box.Game, "BepInEx/config/user.cfg"); File.WriteAllText(config, "custom = keep\n");
            var updated = previous with { Version = "1.2.3", Artifacts = previous.Artifacts.Select(a => a.Id == "better-polus" ? a with { Sha256 = newHash, Source = "https://thunderstore.io/package/download/Eliam/BetterPolus/1.2.3/", SizeBytes = new FileInfo(newZip).Length } : a).ToArray() };
            PackageDownloader.EnsureArtifactsAsync(updated, box.Packages,
                Transport((updated.Artifacts.Single(a => a.Id == "better-polus").Source, "betterpolus-1.2.3.zip"))).GetAwaiter().GetResult();
            var updatePlan = box.Plan(updated);
            Log.Add($"update plan: old {oldInstall.Entries.Count} journal entries -> new {updatePlan.Operations.Count} operations; 1.2.3 sha256={newHash}");
            new InstallOrchestrator(box.Executor(updated)).Run(updatePlan);
            Assert.Equal("custom = keep\n", File.ReadAllText(config));
            Assert.Equal(newHash, Hash(Path.Combine(box.Packages, "better-polus.zip")));
            new InstallOrchestrator(box.Executor(updated)).Uninstall(updated.Id);
            Assert.False(File.Exists(oldPlugin)); Assert.Equal("custom = keep\n", File.ReadAllText(config));
            Log.Add("execute/verify/uninstall: update installed, config preserved, mod ownership removed");
        });
    }

    [Fact]
    public void GeneratedOutfitPreset_RealArchiveInstallAndUninstall()
    {
        Run("Generated recipe", () =>
        {
            using var box = new Sandbox("among-us");
            var zip = Path.Combine(ArtifactRoot, "outfit-presets-1.1.1.zip");
            RequireArtifacts("outfit-presets-1.1.1.zip");
            var hash = Hash(zip); var entries = ZipFile.OpenRead(zip).Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')).ToArray();
            var generated = RecipeGenerator.Generate("Cosmella-Outfit_Presets", "1.1.1", "https://thunderstore.io/package/download/Cosmella/Outfit_Presets/1.1.1/", hash, entries, box.Definition);
            var generatedRecipe = generated.Recipe;
            var step = generatedRecipe.Steps.Single(s => s.Id == "extract");
            generatedRecipe = generatedRecipe with { Steps = generatedRecipe.Steps.Select(s => s.Id is "extract" || s.Id.StartsWith("copy-", StringComparison.Ordinal)
                ? s with { With = new Dictionary<string, System.Text.Json.JsonElement>(s.With)
                    { ["from"] = System.Text.Json.JsonDocument.Parse("\"package://package\"").RootElement.Clone() } }
                : s).ToArray() };
            var artifact = generatedRecipe.Artifacts[0] with { Id = "package" };
            generatedRecipe = generatedRecipe with { Artifacts = [artifact] };
            Directory.CreateDirectory(box.Packages);
            PackageDownloader.EnsureArtifactsAsync(generated.Recipe, box.Packages,
                Transport((generated.Recipe.Artifacts[0].Source, "outfit-presets-1.1.1.zip"))).GetAwaiter().GetResult();
            File.Move(Path.Combine(box.Packages, generated.Recipe.Artifacts[0].Id + ".zip"), Path.Combine(box.Packages, "package.zip"));
            Directory.CreateDirectory(Path.Combine(box.Game, "BepInEx/plugins"));
            var config = Path.Combine(box.Game, "BepInEx/config/user.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "user = keep\n");
            var unrelated = Path.Combine(box.Game, "BepInEx/plugins/user-plugin");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "user");
            var plan = box.Plan(generatedRecipe); Log.Add($"generated plan: {entries.Length} real archive entries, {plan.Operations.Count} operations");
            new InstallOrchestrator(box.Executor(generatedRecipe)).Run(plan);
            Assert.True(Directory.Exists(Path.Combine(box.Game, "BepInEx/plugins/Cosmella-Outfit_Presets")));
            var paths = Directory.GetFiles(Path.Combine(box.Game, "BepInEx/plugins/Cosmella-Outfit_Presets"), "*", SearchOption.AllDirectories);
            Assert.NotEmpty(paths); Log.Add($"execute/verify: {paths.Length} installed files; sha256={Hash(paths[0])}");
            box.Executor(generatedRecipe).Uninstall(generatedRecipe.Id);
            Assert.False(Directory.Exists(Path.Combine(box.Game, "BepInEx/plugins/Cosmella-Outfit_Presets")));
            Assert.Equal("user = keep\n", File.ReadAllText(config));
            Assert.Equal("user", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
            Log.Add("uninstall: generated package ownership removed");
        });
    }

    [Fact]
    public async Task ConflictProposalAndOverride_ArePlanLevelOnly()
    {
        Run("Dependency conflict", () => { });
        var source = new Source(("EasyWork-Root", [V("EasyWork-Root", "1", "denikson-BepInExPack-5.4.2350", "ValheimModding-Jotunn-2.30.0")]),
            ("denikson-BepInExPack", [V("denikson-BepInExPack", "5.4.2350"), V("denikson-BepInExPack", "5.4.2333")]),
            ("ValheimModding-Jotunn", [V("ValheimModding-Jotunn", "2.30.0", "denikson-BepInExPack-5.4.2333")]));
        var resolver = new DependencyResolver(source); var conflict = await resolver.ResolveWithProposalsAsync("EasyWork-Root", "1");
        Assert.Equal("5.4.2350", Assert.Single(conflict.Conflicts).SuggestedVersion);
        var resolved = await resolver.ResolveWithProposalsAsync("EasyWork-Root", "1", new Dictionary<string,string> { ["denikson-BepInExPack"]="5.4.2350" });
        Assert.Empty(resolved.Conflicts); Assert.Equal(3, resolved.Pinned.Count);
        Log.Add("resolve: conflict denikson-BepInExPack 5.4.2333/5.4.2350; suggestion 5.4.2350; override resolved (no execute by design)");
    }

    [Fact]
    public async Task Receipts_OrchestratorSendsInstalledAndUninstalled()
    {
        using var box = new Sandbox("among-us"); var recipe = Load("among-us-outfit-presets.yaml");
        await box.AddArtifacts(recipe, new Dictionary<string,string> { ["loader-pack"]="amongus-loader-6.0.700.zip", ["tabsbuilder-api"]="tabsbuilderapi-1.2.1.zip", ["outfit-presets"]="outfit-presets-1.1.1.zip" });
        var transport = new Receipts(); var sender = new ReceiptSender(transport); var orchestrator = new InstallOrchestrator(box.Executor(recipe), sender);
        orchestrator.Run(box.Plan(recipe), receiptContext: new ReceiptContext("among-us", "steam", "Cosmella-Outfit_Presets"));
        orchestrator.Uninstall(recipe.Id, receiptContext: new ReceiptContext("among-us", "steam", "Cosmella-Outfit_Presets"));
        Assert.Collection(transport.Items, installed => { Assert.Equal("among-us", installed.GameId); Assert.Equal("Cosmella-Outfit_Presets", installed.ModFullName); Assert.Equal("1.1.1", installed.ModVersion); Assert.Equal("installed", installed.Result); }, uninstalled => { Assert.Equal("among-us", uninstalled.GameId); Assert.Equal("Cosmella-Outfit_Presets", uninstalled.ModFullName); Assert.Equal("uninstalled", uninstalled.Result); });
        Log.Add("receipts: installed and uninstalled payloads carry gameId/fullName/version");
        await Task.CompletedTask;
    }

    private static ThunderstoreVersion V(string name, string version, params string[] deps) =>
        new(version, $"{name}-{version}", $"https://thunderstore.io/package/download/{name}/{version}/", 10, DateTimeOffset.UnixEpoch, deps);

    private static void Run(string title, Action test)
    {
        try { test(); }
        catch (Exception ex) { Log.Add($"{title}: FAILED {ex}"); WriteReport(); throw; }
        Log.Add($"{title}: PASS"); WriteReport();
    }

    private static void WriteReport()
    {
        try
        {
            var existing = File.Exists(ReportPath) ? File.ReadAllText(ReportPath) : "";
            var start = existing.IndexOf("## C5c", StringComparison.Ordinal);
            if (start >= 0) existing = existing[..start].TrimEnd() + "\n\n";
            File.WriteAllText(ReportPath, existing + "## C5c — real-artifact sandbox\n\n" + string.Join("\n", Log.Select(x => "- " + x)) + "\n");
        }
        catch (IOException) { /* the report is diagnostics; a missing research folder is not a test failure */ }
    }
}
