using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Catalog;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Recipes;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-65 (review 2026-11-15): the install flow must carry the arch/buildId facts, refuse loudly
// when a recipe needs a fact it does not have, and never register a "successful" empty install.
public sealed class Pak65InstallFactsTests
{
    private static readonly string Artifact = "/tmp/opencode/phaket-research/artifacts/tou-mira-1.7.9-beta1-x64.zip";

    // The UI shows the localized message, never the raw code.
    private static string Plaintext(string code, string? fact = null) => new PhaketException(code,
        fact is null ? [] : [("fact", fact)]).LocalizedMessage(LocalizationService.Instance.CurrentLanguage);

    private sealed class FixtureTransport(Dictionary<string, byte[]> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(files[url], writable: false));
    }

    private static void WritePe(string path, bool x64)
    {
        var bytes = new byte[512];
        void U16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o), v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(o), v);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, x64 ? (ushort)0x8664 : (ushort)0x14c);
        U16(0x94, x64 ? (ushort)240 : (ushort)224);
        U16(0x98, x64 ? (ushort)0x20b : (ushort)0x10b);
        U32(0x98 + (x64 ? 108 : 92), 16);
        File.WriteAllBytes(path, bytes);
    }

    private static Recipe MiraX64() => RecipeParser.ParseYaml(
        File.ReadAllText(Path.Combine(RecipeLibrary.Directory, "among-us-tou-mira-1.7.9-beta1-x64.yaml")));

    private static GameCatalogItem AmongUs(GameDefinition definition) => new(definition, "", "");

    [AvaloniaFact]
    public async Task SandboxWithoutBuildFactRefusesAndRegistersNothing()
    {
        if (!File.Exists(Artifact)) Assert.Skip("Real x64 artifact is not available locally.");
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak65-nofacts-").FullName;
        try
        {
            var transport = new FixtureTransport(new Dictionary<string, byte[]> { [MiraX64().Artifacts[0].Source] = File.ReadAllBytes(Artifact) });
            var game = AmongUs(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"));
            // Sandbox: the architecture has a default from the definition, the Steam build id does not exist.
            var install = new InstallViewModel(game, () => { }, appRoot, transport);
            install.SelectedRecipe = new RecipeOption(MiraX64(), "Town of Us Mira");
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(install.IsDone);
            Assert.Equal(Plaintext(ErrorCodes.InstallFactsMissing, "buildId"), install.StatusMessage);
            Assert.Empty(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaTheory]
    [InlineData(true, "25134893")]   // satisfies ">=25134893": the recipe plans
    [InlineData(false, "24302054")]  // older build: conditions do not match, still no install
    public async Task DetectedInstallCarriesArchAndBuildIdFacts(bool compatible, string buildId)
    {
        if (!File.Exists(Artifact)) Assert.Skip("Real x64 artifact is not available locally.");
        var appRoot = Directory.CreateTempSubdirectory("phaket-pak65-facts-").FullName;
        var gameRoot = Path.Combine(appRoot, "fixture-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            WritePe(Path.Combine(gameRoot, "Among Us.exe"), x64: true);
            var transport = new FixtureTransport(new Dictionary<string, byte[]> { [MiraX64().Artifacts[0].Source] = File.ReadAllBytes(Artifact) });
            var game = AmongUs(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"));
            game.Detected = new SteamInstallation(new Installation("fixture", "steam", gameRoot),
                new SteamAppManifest(game.SteamAppId, "Among Us", buildId, "public", "1", "fixture", 0),
                new SteamLibrary(appRoot, false), GameAvailability.Moddable);
            var install = new InstallViewModel(game, () => { }, appRoot, transport) { UseDetectedInstall = true };
            install.SelectedRecipe = new RecipeOption(MiraX64(), "Town of Us Mira");
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var registry = new InstallRegistry(Path.Combine(appRoot, "installs.json")).All();
            if (compatible)
            {
                Assert.True(install.IsDone, install.StatusMessage);
                Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "TownOfUsMira.dll")));
                Assert.Single(registry);
            }
            else
            {
                // Facts are known but do not match: readable refusal, no phantom installation.
                Assert.False(install.IsDone);
                Assert.Equal(Plaintext(ErrorCodes.PlanEmpty), install.StatusMessage);
                Assert.Empty(registry);
                Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "TownOfUsMira.dll")));
            }
        }
        finally { Directory.Delete(appRoot, true); }
    }

    [AvaloniaFact]
    public async Task EmptyPlanIsNotOfferedForAcceptanceOrExecution()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pak65-catalog-").FullName;
        try
        {
            var vm = new ModCatalogViewModel("among-us", root, autoSync: false);
            vm.PrepareOverride = (_, _, _, _, _) => Task.FromResult(new InstallPreparation("among-us", "Owner-Mod", "2.0",
                "http://127.0.0.1:1", new("", "steam", true),
                [new OperationPlan(OperationPlan.CurrentSchemaVersion, new RecipeRef("mod", "2"), "i", "p", [], [], [], false, [])],
                [], [], [new InstallPreview("Owner-Mod", "2.0", [], 0)], "/tmp/sandbox", "", "", "", ""));
            await vm.BeginInstallAsync("Owner-Mod", "2.0", new("", "steam", true));
            Assert.False(vm.HasPreview);
            Assert.False(vm.PlanAccepted);
            Assert.False(vm.ExecuteInstallCommand.CanExecute(null));
            Assert.Equal(Plaintext(ErrorCodes.PlanEmpty), vm.InstallStatus);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(null, "x64")]    // no explicit choice: the definition's current retail architecture
    [InlineData("x86", "x86")]   // the user's choice wins
    public async Task SandboxArchSelectorFeedsThePlanFacts(string? chosen, string expected)
    {
        var root = Directory.CreateTempSubdirectory("phaket-pak65-arch-").FullName;
        try
        {
            var game = AmongUs(GameDefinitions.LoadAll().First(g => g.Slug == "among-us"));
            var vm = new ModCatalogViewModel("among-us", root, autoSync: false, game: game);
            InstallTarget? seen = null;
            vm.PrepareOverride = (_, _, _, target, _) =>
            {
                seen = target;
                return Task.FromResult(new InstallPreparation("among-us", "Owner-Mod", "2.0", "http://127.0.0.1:1",
                    target, [], [], [], [], "", "", "", "", ""));
            };
            Assert.Equal(game.Definition.DefaultArch, vm.SelectedArch);
            if (chosen is not null) vm.SelectedArch = chosen;
            await vm.BeginInstallAsync("Owner-Mod", "2.0", new("", "steam", true));
            Assert.Equal(expected, seen!.Arch);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task MissingArchFactIsReportedInsteadOfGenericFailure()
    {
        var root = Directory.CreateTempSubdirectory("phaket-pak65-facts-vm-").FullName;
        try
        {
            var vm = new ModCatalogViewModel("among-us", root, autoSync: false);
            vm.PrepareOverride = (_, _, _, _, _) => throw new PhaketException(ErrorCodes.InstallFactsMissing, ("fact", "arch"));
            await vm.BeginInstallAsync("Owner-Mod", "2.0", new("", "steam", true));
            Assert.Equal(Plaintext(ErrorCodes.InstallFactsMissing, "arch"), vm.InstallStatus);
            Assert.False(vm.HasPreview);
        }
        finally { Directory.Delete(root, true); }
    }
}
