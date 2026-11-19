using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-66 (review of the facts wave): every fact a recipe conditions on has to come from the SAME
// build. A detected installation never borrows identity from a catalog build it cannot be matched
// to, and a sandbox takes its architecture from the build the user actually selected.
public sealed class Pak66InstallFactsTests
{
    private static string Plaintext(string code, string? fact = null) => new PhaketException(code,
        fact is null ? [] : [("fact", fact)]).LocalizedMessage(LocalizationService.Instance.CurrentLanguage);

    private sealed class FixtureTransport(Dictionary<string, byte[]> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(files[url], writable: false));
    }

    // One file, one step: the plan either matches the facts or it does not.
    private const string ModUrl = "https://thunderstore.io/package/download/Owner/Mod/1.0.0/";

    // A real (empty) zip is what a downloaded package has to look like.
    private static readonly byte[] SmallPackage = CreateZip();

    private static byte[] CreateZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, true))
            zip.CreateEntry("plugins/Mod.dll");
        return buffer.ToArray();
    }

    // Minimal PE header with the machine type the architecture probe reads.
    private static void WritePe(string path, bool x64)
    {
        var bytes = new byte[512];
        void U16(int o, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o), v);
        void U32(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o), v);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c);
        U16(0x94, x64 ? (ushort)240 : (ushort)224);
        U16(0x98, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(0x98 + (x64 ? 108 : 92), 16);
        File.WriteAllBytes(path, bytes);
    }

    private static Recipe ArchRecipe(string arch) =>
        Gated(new RecipeCondition { Arch = arch }, "arch-" + arch);

    // Gates on the Steam build id only: it needs a build fact from the same installation.
    private static Recipe BuildRecipe() =>
        Gated(new RecipeCondition { BuildId = ">=25134893" }, "build");

    private static Recipe Gated(RecipeCondition when, string id) => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.pak66." + id,
        Version = "1.0.0",
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
        When = when,
        Requires = new RecipeRequires { Capabilities = ["file.copy"] },
        Artifacts = [new RecipeArtifact
        {
            Id = "bundle", Source = ModUrl, Format = "zip",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(SmallPackage)),
            SizeBytes = SmallPackage.LongLength,
        }],
        Steps = [new RecipeStep { Id = "copy", Use = "file.copy", With = new Dictionary<string, JsonElement>
        {
            ["from"] = JsonSerializer.SerializeToElement("package://bundle"),
            ["to"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/Mod.dll"),
        } }],
    };

    private static GameDefinition AmongUsDefinition() =>
        GameDefinitions.LoadAll().First(g => g.Slug == "among-us");

    private static GameCatalogItem Detected(string root, string? buildId)
    {
        var game = new GameCatalogItem(AmongUsDefinition(), "", "");
        game.Detected = new SteamInstallation(new Installation("fixture", "steam", root),
            new SteamAppManifest("945360", "Among Us", buildId ?? "", "public", "1", "fixture", 0),
            new SteamLibrary(root, false), GameAvailability.Moddable);
        return game;
    }

    // Seeds the catalog cache the install stepper reads, exactly as a sync would.
    private static void SeedCatalog(string appRoot, params (string Version, string BuildId, string? Arch)[] builds)
    {
        using var store = new CatalogStore(Path.Combine(appRoot, "catalog.db"));
        var items = builds.Select((b, i) => JsonSerializer.SerializeToElement(new
        {
            id = $"build-{i}", gameId = "among-us", store = "steam", versionLabel = b.Version,
            buildId = b.BuildId, depotIds = new[] { "945361" }, manifestIds = new[] { $"m{i}" },
            branch = "public", availabilityStatus = "known", source = "poc", sourceReference = "ref",
            observedAt = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("O"), verifiedAt = (DateTimeOffset?)null,
            confidence = 0.9, provenance = "poc-manual-import", releasedAt = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("O"),
            arch = b.Arch,
        })).ToArray();
        store.ReplaceAll(JsonSerializer.SerializeToElement(items), 1, "\"pak66\"");
    }

    private static InstallViewModel SandboxInstall(string appRoot, GameCatalogItem game)
    {
        var install = new InstallViewModel(game, () => { }, appRoot,
            new FixtureTransport(new Dictionary<string, byte[]> { [ModUrl] = SmallPackage }));
        return install;
    }

    [AvaloniaFact]
    public async Task DetectedInstallWithoutBuildIdRefusesASelectedCatalogBuild()
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-mixed-").FullName;
        var gameRoot = Path.Combine(appRoot, "fixture-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            WritePe(Path.Combine(gameRoot, "Among Us.exe"), x64: true);
            // The installation cannot say which build it is (manual folder), the catalog offers one.
            var game = Detected(gameRoot, buildId: null);
            SeedCatalog(appRoot, ("2026.9.29", "25134893", "x64"));
            var install = SandboxInstall(appRoot, game);
            install.UseDetectedInstall = true;
            install.SelectedRecipe = new RecipeOption(ArchRecipe("x86"), "x86 build");
            install.SelectedVersion = install.Versions.Last(); // the user picked the catalog build
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(install.IsDone);
            Assert.Equal(Plaintext(ErrorCodes.InstallFactsMissing, "buildId"), install.StatusMessage);
            // Nothing was planned, copied or remembered.
            Assert.Empty(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
            Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Mod.dll")));
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaFact]
    public async Task DetectedInstallWhoseBuildIdDiffersFromTheSelectedBuildIsRefused()
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-other-").FullName;
        var gameRoot = Path.Combine(appRoot, "fixture-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            WritePe(Path.Combine(gameRoot, "Among Us.exe"), x64: true);
            var game = Detected(gameRoot, "24302054");
            SeedCatalog(appRoot, ("2026.9.29", "25134893", "x64"));
            var install = SandboxInstall(appRoot, game);
            install.UseDetectedInstall = true;
            install.SelectedRecipe = new RecipeOption(ArchRecipe("x86"), "x86 build");
            install.SelectedVersion = install.Versions.Last();
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(install.IsDone);
            Assert.Equal(Plaintext(ErrorCodes.InstallFactsMissing, "buildId"), install.StatusMessage);
            Assert.Empty(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaFact]
    public async Task DetectedInstallMatchedToItsOwnBuildPlansFromThoseFacts()
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-same-").FullName;
        var gameRoot = Path.Combine(appRoot, "fixture-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            WritePe(Path.Combine(gameRoot, "Among Us.exe"), x64: true);
            var game = Detected(gameRoot, "25134893");
            SeedCatalog(appRoot, ("2026.9.29", "25134893", "x64"));
            var install = SandboxInstall(appRoot, game);
            install.UseDetectedInstall = true;
            install.SelectedRecipe = new RecipeOption(ArchRecipe("x64"), "x64 build");
            install.SelectedVersion = install.Versions.Last();
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(install.IsDone, install.StatusMessage);
            Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Mod.dll")));
            Assert.Single(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
        }
        finally { Directory.Delete(appRoot, true); }
    }

    // PAK-66 (b): with "latest" the architecture fact is the architecture of the build "latest"
    // resolved to — never the definition's current retail default.
    [AvaloniaFact]
    public async Task SandboxLatestUsesTheResolvedBuildArchitecture()
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-latest-").FullName;
        try
        {
            var definition = AmongUsDefinition();
            Assert.NotEqual("x86", definition.DefaultArch); // only meaningful as a contrast with x86
            // The only build the catalog knows is an older x86 release, so "latest" is x86.
            SeedCatalog(appRoot, ("2025.1.1", "20000000", "x86"));
            var install = SandboxInstall(appRoot, new GameCatalogItem(definition, "", ""));
            install.SelectedRecipe = new RecipeOption(ArchRecipe("x86"), "x86 build");
            Assert.Equal("latest", install.SelectedVersion!.Value);
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(install.IsDone, install.StatusMessage);
            var entry = Assert.Single(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
            Assert.True(File.Exists(Path.Combine(entry.GameRoot, "BepInEx", "plugins", "Mod.dll")));
        }
        finally { Directory.Delete(appRoot, true); }
    }

    // A chosen build wins over both the retail default and the newest release.
    [AvaloniaFact]
    public async Task SandboxChosenBuildArchitectureWinsOverTheNewestRelease()
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-chosen-").FullName;
        try
        {
            // The newest release is x64; the user selects the older x86 one.
            SeedCatalog(appRoot, ("2026.9.29", "25134893", "x64"), ("2025.1.1", "20000000", "x86"));
            var install = SandboxInstall(appRoot, new GameCatalogItem(AmongUsDefinition(), "", ""));
            install.SelectedRecipe = new RecipeOption(ArchRecipe("x86"), "x86 build");
            install.SelectedVersion = install.Versions.Single(v => v.Value == "2025.1.1");
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(install.IsDone, install.StatusMessage);
            var entry = Assert.Single(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
            Assert.True(File.Exists(Path.Combine(entry.GameRoot, "BepInEx", "plugins", "Mod.dll")));
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaFact]
    public async Task SandboxWithoutAnyBuildFactStillFallsBackToTheDefinitionArch()
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-default-").FullName;
        try
        {
            // No catalog cache at all: the retail default is the only architecture there is, and
            // the sandbox must still plan rather than refuse.
            var install = SandboxInstall(appRoot, new GameCatalogItem(AmongUsDefinition(), "", ""));
            install.SelectedRecipe = new RecipeOption(ArchRecipe(AmongUsDefinition().DefaultArch!), "default arch build");
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(install.IsDone, install.StatusMessage);
            Assert.Single(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaFact]
    public async Task DetectedInstallLatestResolvesNothingFromTheCatalog()
    {
        // Same installation and catalog as the refusal above, but the default "latest": nothing
        // may be lent either, so a recipe gating on the build id is refused, never matched to a
        // catalog build the installation cannot be identified with.
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-latest-detected-").FullName;
        var gameRoot = Path.Combine(appRoot, "fixture-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            WritePe(Path.Combine(gameRoot, "Among Us.exe"), x64: true);
            var game = Detected(gameRoot, buildId: null);
            SeedCatalog(appRoot, ("2026.9.29", "25134893", "x64"));
            var install = SandboxInstall(appRoot, game);
            install.UseDetectedInstall = true;
            install.SelectedRecipe = new RecipeOption(BuildRecipe(), "build-gated mod");
            Assert.Equal("latest", install.SelectedVersion!.Value);
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(install.IsDone);
            Assert.Equal(Plaintext(ErrorCodes.InstallFactsMissing, "buildId"), install.StatusMessage);
            Assert.Empty(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
            Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Mod.dll")));
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaFact]
    public async Task DetectedInstallWithUnreadableExecutableNeverBorrowsCatalogArch()
    {
        // The installation cannot state its architecture; the matched catalog build says x64. The
        // architecture fact comes from the installation only, so the x64 gate is refused outright.
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak66-detected-arch-").FullName;
        var gameRoot = Path.Combine(appRoot, "fixture-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(gameRoot, "Among Us.exe"), "not a PE file at all");
            var game = Detected(gameRoot, "25134893");
            SeedCatalog(appRoot, ("2026.9.29", "25134893", "x64"));
            var install = SandboxInstall(appRoot, game);
            install.UseDetectedInstall = true;
            install.SelectedRecipe = new RecipeOption(ArchRecipe("x64"), "x64 build");
            install.SelectedVersion = install.Versions.Last();
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(install.IsDone);
            Assert.Equal(Plaintext(ErrorCodes.InstallFactsMissing, "arch"), install.StatusMessage);
            Assert.Empty(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
            Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Mod.dll")));
        }
        finally { Directory.Delete(appRoot, true); }
    }
}
