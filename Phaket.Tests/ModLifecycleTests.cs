using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Execution;
using Phaket.Core.Mods;
using Phaket.Core.Domain;
using Phaket.Core.Platforms;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

public class ModLifecycleTests
{
    private sealed class ReceiptCollector : IReceiptTransport
    {
        public List<ModReceipt> Items { get; } = [];
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken) { Items.Add(receipt); return Task.CompletedTask; }
    }

    private sealed class FixtureTransport(Dictionary<string, byte[]> files) : IPackageTransport
    {
        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(files[url], writable: false));
    }

    private static byte[] Zip(params (string Path, string Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, contents) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(contents);
            }
        return stream.ToArray();
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task InstallDownloadLaunchAndUninstall(bool useSandbox, bool epic)
    {
        var appRoot = Directory.CreateTempSubdirectory("phaket-lifecycle-").FullName;
        string? sandbox = null;
        try
        {
            var loader = Zip(("BepInExPack_AmongUs/winhttp.dll", "proxy"),
                ("BepInExPack_AmongUs/doorstop_config.ini", "config"),
                ("BepInExPack_AmongUs/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "core"),
                ("BepInExPack_AmongUs/dotnet/coreclr.dll", "runtime"));
            var mod = Zip(("plugins/BetterPolus.dll", "plugin"));
            var shipped = RecipeParser.ParseYaml(File.ReadAllText(Path.Combine(RecipeLibrary.Directory, "among-us-betterpolus.yaml")));
            var bytes = new Dictionary<string, byte[]> { ["loader-pack"] = loader, ["better-polus"] = mod };
            var recipe = shipped with
            {
                Target = epic ? shipped.Target with { Editions = [new EditionTarget { Store = "epic" }] } : shipped.Target,
                Artifacts = shipped.Artifacts.Select(a => a with
                {
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes[a.Id])),
                    SizeBytes = bytes[a.Id].Length,
                }).ToArray(),
                Verify = [new RecipeVerifyStep { Use = "file.verify", With = new Dictionary<string, JsonElement>
                { ["path"] = JsonSerializer.SerializeToElement("game://BepInEx/plugins/BetterPolus.dll") } }],
            };
            var transport = new FixtureTransport(recipe.Artifacts.ToDictionary(a => a.Source, a => bytes[a.Id]));
            var definition = GameDefinitions.LoadAll().First(g => g.Slug == "among-us");
            var game = new GameCatalogItem(definition, "", "");
            var gameRoot = Path.Combine(appRoot, "fixture-game");
            if (!useSandbox)
            {
                Directory.CreateDirectory(gameRoot);
                File.WriteAllText(Path.Combine(gameRoot, "winhttp.dll"), "original");
                if (epic) game.EpicInstallPath = gameRoot;
                else game.Detected = new SteamInstallation(new Installation("fixture", "steam", gameRoot),
                    new SteamAppManifest("1", "fixture", "1", "", "", "fixture", 0),
                    new SteamLibrary(appRoot, false), GameAvailability.Installed);
            }
            var install = new InstallViewModel(game, () => { }, appRoot, transport) { UseDetectedInstall = !useSandbox };
            install.SelectedRecipe = new RecipeOption(recipe, recipe.Id);
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            // This legacy UI path supplies no architecture fact, including for its sandbox.
            // The marked x86 loader must be refused rather than guessing.
            Assert.False(install.IsDone);
            Assert.Contains("LOADER_ARCH_UNAVAILABLE", install.StatusMessage);
            Assert.Empty(new InstallRegistry(Path.Combine(appRoot, "installs.json")).All());
        }
        finally
        {
            if (sandbox is not null && Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.Delete(appRoot, true);
        }
    }
}
