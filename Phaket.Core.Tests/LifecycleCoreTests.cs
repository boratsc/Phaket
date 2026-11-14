using System.Security.Cryptography;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;

namespace Phaket.Core.Tests;

// Mod lifecycle core: artifact download (trust boundary), install registry,
// version-fact requirements and the orchestrator's installation lock.
public class LifecycleCoreTests
{
    private sealed class FakeTransport(byte[] bytes) : IPackageTransport
    {
        public int Calls;
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Recipe RemoteRecipe(byte[] bytes, long? size = null, string? sha = null,
        string source = "https://thunderstore.io/package/download/Owner/Mod/1.0.0/") => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.remote",
        Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["linux"] },
        Artifacts = [new RecipeArtifact { Id = "mod-pack", Source = source, Sha256 = sha ?? Hash(bytes), SizeBytes = size ?? bytes.LongLength }],
        Steps = [new RecipeStep { Id = "extract", Use = "archive.extract",
            With = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["from"] = System.Text.Json.JsonSerializer.SerializeToElement("package://mod-pack"),
                ["to"] = System.Text.Json.JsonSerializer.SerializeToElement("staging://mod"),
            } }],
    };

    [Fact]
    public async Task Downloader_FetchesVerifiesAndCaches()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dl-").FullName;
        try
        {
            var bytes = "fake zip content"u8.ToArray();
            var transport = new FakeTransport(bytes);
            var downloaded = await PackageDownloader.EnsureArtifactsAsync(RemoteRecipe(bytes), root, transport);
            Assert.Single(downloaded);
            Assert.Equal(Path.Combine(root, "mod-pack.zip"), downloaded[0]);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded[0]));
            // Second run: cached and hash-valid, no transport call.
            await PackageDownloader.EnsureArtifactsAsync(RemoteRecipe(bytes), root, transport);
            Assert.Equal(1, transport.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloader_HashMismatch_LeavesNoFile()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dl-").FullName;
        try
        {
            var transport = new FakeTransport("tampered"u8.ToArray());
            var ex = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(RemoteRecipe("original"u8.ToArray()), root, transport));
            Assert.Equal(ErrorCodes.ArtifactHashMismatch, ex.Error.Code);
            Assert.False(File.Exists(Path.Combine(root, "mod-pack.zip")));
            Assert.False(File.Exists(Path.Combine(root, "mod-pack.zip.phaket-tmp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloader_RejectsNonAllowlistedSource()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dl-").FullName;
        try
        {
            var bytes = "x"u8.ToArray();
            var recipe = RemoteRecipe(bytes, source: "https://evil.example/mod.zip");
            var ex = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(recipe, root, new FakeTransport(bytes)));
            Assert.Equal(ErrorCodes.ArtifactDownloadUnavailable, ex.Error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloader_RefusesRemoteArtifactWithoutHash()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dl-").FullName;
        try
        {
            var bytes = "x"u8.ToArray();
            var recipe = RemoteRecipe(bytes);
            recipe = recipe with { Artifacts = [recipe.Artifacts[0] with { Sha256 = null }] };
            var ex = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(recipe, root, new FakeTransport(bytes)));
            Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloader_EnforcesDeclaredSize()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dl-").FullName;
        try
        {
            var bytes = new byte[100];
            var recipe = RemoteRecipe(bytes, size: 10);
            var ex = await Assert.ThrowsAsync<PhaketException>(() =>
                PackageDownloader.EnsureArtifactsAsync(recipe, root, new FakeTransport(bytes)));
            Assert.Equal(ErrorCodes.ArtifactDownloadUnavailable, ex.Error.Code);
            Assert.False(File.Exists(Path.Combine(root, "mod-pack.zip")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Downloader_SkipsLocalPackageArtifacts()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dl-").FullName;
        try
        {
            var recipe = RemoteRecipe([]);
            recipe = recipe with { Artifacts = [recipe.Artifacts[0] with { Source = "package://mod-pack.zip", Sha256 = null }] };
            var transport = new FakeTransport([]);
            var downloaded = await PackageDownloader.EnsureArtifactsAsync(recipe, root, transport);
            Assert.Empty(downloaded);
            Assert.Equal(0, transport.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Registry_RoundTripsAndSurvivesCorruption()
    {
        var root = Directory.CreateTempSubdirectory("phaket-reg-").FullName;
        try
        {
            var registry = new InstallRegistry(Path.Combine(root, "installs.json"));
            var entry = new InstalledRecipe("test.remote", "1.0.0", "among-us", "steam",
                Path.Combine(root, "game"), Path.Combine(root, "profile"), Path.Combine(root, "staging"),
                Path.Combine(root, "state"), root, DateTimeOffset.UtcNow);
            registry.Add(entry);
            Assert.Single(registry.ForGame("among-us"));
            Assert.Empty(registry.ForGame("valheim"));
            // Re-adding the same recipe+root replaces, never duplicates.
            registry.Add(entry with { RecipeVersion = "1.0.1" });
            Assert.Single(registry.All());
            Assert.Equal("1.0.1", registry.All()[0].RecipeVersion);
            registry.Remove("test.remote", entry.GameRoot);
            Assert.Empty(registry.All());

            File.WriteAllText(Path.Combine(root, "installs.json"), "{ corrupt");
            Assert.Empty(registry.All());
            Assert.Equal("{ corrupt", File.ReadAllText(Path.Combine(root, "installs.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RequiresGameVersion_DistinguishesModOnlyFromProvisionRecipes()
    {
        var shipped = RecipeLibrary.LoadAll();
        Assert.False(OperationPlanner.RequiresGameVersion(shipped.First(r => r.Id == "community.thunderstore.better-polus")));
        Assert.True(OperationPlanner.RequiresGameVersion(shipped.First(r => r.Id == "community.github.town-of-us-mira")));
    }

    [Fact]
    public void Uninstall_RemovesCreatedDirectories_TreeIdentical()
    {
        var definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));
        var root = Directory.CreateTempSubdirectory("phaket-uninstall-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(game, "Among Us.exe"), "game");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "original-proxy");
            var profile = Path.Combine(root, "profile");
            var staging = Path.Combine(root, "staging");
            var packages = Path.Combine(root, "packages");
            var state = Path.Combine(root, "state");
            foreach (var d in new[] { profile, staging, packages, state }) Directory.CreateDirectory(d);
            ExecutorFixture.MakeZip(Path.Combine(packages, "loader-pack.zip"),
                ("BepInExPack_AmongUs/winhttp.dll", "proxy"u8.ToArray()),
                ("BepInExPack_AmongUs/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "core"u8.ToArray()),
                ("BepInExPack_AmongUs/dotnet/coreclr.dll", "runtime"u8.ToArray()));
            ExecutorFixture.MakeZip(Path.Combine(packages, "better-polus.zip"),
                ("plugins/BetterPolus.dll", "plugin"u8.ToArray()));

            string[] Snapshot() => Directory.GetFileSystemEntries(game, "*", SearchOption.AllDirectories)
                .Select(p => p[(game.Length + 1)..].Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var before = Snapshot();

            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1", Id = "test.uninstall-dirs", Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["linux"] },
                Requires = new RecipeRequires { Capabilities = ["loader.ensure", "archive.extract", "file.copy"] },
                Artifacts =
                [
                    new RecipeArtifact { Id = "loader-pack", Source = "package://loader-pack.zip" },
                    new RecipeArtifact { Id = "better-polus", Source = "package://better-polus.zip" },
                ],
                Steps =
                [
                    new RecipeStep { Id = "loader", Use = "loader.ensure", With = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["id"] = System.Text.Json.JsonSerializer.SerializeToElement("bepinex"),
                        ["artifact"] = System.Text.Json.JsonSerializer.SerializeToElement("loader-pack"),
                        ["version"] = System.Text.Json.JsonSerializer.SerializeToElement("6.0.700"),
                        ["stripComponents"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                    } },
                    new RecipeStep { Id = "extract", Use = "archive.extract", With = ExecutorFixture.Params(
                        ("from", "package://better-polus"), ("to", "staging://better-polus")) },
                    new RecipeStep { Id = "copy", Use = "file.copy", With = ExecutorFixture.Params(
                        ("from", "staging://better-polus/plugins/BetterPolus.dll"), ("to", "game://BepInEx/plugins/BetterPolus.dll")) },
                ],
            };
            var facts = new PlanFacts("linux", "steam", "2026.1.1", OperationCatalog.Capabilities, _ => false,
                new Dictionary<string, string> { ["bepinex"] = "6.0.700" });
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game, profile, staging, packages, definition, facts));
            var executor = new PlanExecutor(new PlanExecutionContext(game, profile, staging, packages, definition, recipe.Id, "p", state));
            var journal = new InstallOrchestrator(executor).Run(plan);
            Assert.Equal(JournalState.Completed, journal.State);
            Assert.True(Directory.Exists(Path.Combine(game, "dotnet")));

            executor.Uninstall(recipe.Id);
            Assert.Equal(before, Snapshot());
            Assert.Equal("original-proxy", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Provision_UsesCatalogManifest_WhenStepDoesNotPinOne()
    {
        var definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));
        var root = Directory.CreateTempSubdirectory("phaket-manifest-").FullName;
        try
        {
            Recipe ProvisionRecipe(string? stepManifest) => new()
            {
                SchemaVersion = "omrl/v0alpha1", Id = "test.provision", Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["linux"] },
                Requires = new RecipeRequires { Capabilities = ["game.provision"] },
                Steps = [new RecipeStep { Id = "provision", Use = "game.provision",
                    With = stepManifest is null
                        ? ExecutorFixture.Params(("store", "steam"), ("version", "2026.8.18"))
                        : ExecutorFixture.Params(("store", "steam"), ("version", "2026.8.18"), ("manifestId", stepManifest)) }],
            };
            var facts = new PlanFacts("linux", "steam", "2026.8.18", OperationCatalog.Capabilities, _ => false,
                null, "1397756378225229500");
            PlannerContext Ctx() => new("i", "p", Path.Combine(root, "game"), Path.Combine(root, "profile"),
                Path.Combine(root, "staging"), Path.Combine(root, "packages"), definition, facts);

            // No step pin: the selected catalog build's manifest flows into the plan.
            var plan = OperationPlanner.Plan(ProvisionRecipe(null), Ctx());
            Assert.Equal("1397756378225229500", plan.Operations[0].Parameters!["manifestId"]);

            // An explicit step pin always wins over the catalog fact.
            var pinned = OperationPlanner.Plan(ProvisionRecipe("999"), Ctx());
            Assert.Equal("999", pinned.Operations[0].Parameters!["manifestId"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Orchestrator_RespectsInstallationLock()
    {
        var root = Directory.CreateTempSubdirectory("phaket-lock-").FullName;
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            var definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml")));
            var recipe = new Recipe
            {
                SchemaVersion = "omrl/v0alpha1", Id = "test.lock", Version = "1.0.0",
                Target = new RecipeTarget { Game = "among-us", Systems = ["linux"] },
                Requires = new RecipeRequires { Capabilities = ["directory.ensure"] },
                Steps = [new RecipeStep { Id = "dir", Use = "directory.ensure",
                    With = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["path"] = System.Text.Json.JsonSerializer.SerializeToElement("game://BepInEx/plugins"),
                    } }],
            };
            var facts = new PlanFacts("linux", "steam", "2026.1.1", OperationCatalog.Capabilities, _ => false);
            var plan = OperationPlanner.Plan(recipe, new PlannerContext("i", "p", game,
                Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages"), definition, facts));
            var executor = new PlanExecutor(new PlanExecutionContext(game,
                Path.Combine(root, "profile"), Path.Combine(root, "staging"), Path.Combine(root, "packages"),
                definition, recipe.Id, "p", Path.Combine(root, "state")));
            using var held = executor.LockInstallation();
            var ex = Assert.Throws<PhaketException>(() => new InstallOrchestrator(executor).Run(plan));
            Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
