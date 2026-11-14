using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Execution;
using Phaket.Core.Domain;
using Phaket.Core.Platforms;
using Phaket.Core.Omrl;
using Phaket.Core.Recipes;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

public class ModLifecycleTests
{
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallDownloadLaunchAndUninstall(bool useSandbox)
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
                game.Detected = new SteamInstallation(new Installation("fixture", "steam", gameRoot),
                    new SteamAppManifest("1", "fixture", "1", "", "", "fixture", 0),
                    new SteamLibrary(appRoot, false), GameAvailability.Installed);
            }
            var install = new InstallViewModel(game, () => { }, appRoot, transport) { UseDetectedInstall = !useSandbox };
            install.SelectedRecipe = new RecipeOption(recipe, recipe.Id);
            await install.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(install.IsDone, install.StatusMessage);
            var registry = new InstallRegistry(Path.Combine(appRoot, "installs.json"));
            var entry = Assert.Single(registry.ForGame("among-us"));
            sandbox = entry.SandboxRoot;
            Assert.Equal(useSandbox, sandbox is not null);
            Assert.Equal("plugin", File.ReadAllText(Path.Combine(entry.GameRoot, "BepInEx/plugins/BetterPolus.dll")));
            Assert.Equal("proxy", File.ReadAllText(Path.Combine(entry.GameRoot, "winhttp.dll")));
            install.BackCommand.Execute(null);
            if (useSandbox) Assert.True(Directory.Exists(sandbox));
            var mods = new ModsViewModel(game, () => { }, appRoot);
            var item = Assert.Single(mods.InstalledRecipes);
            // Visual evidence: the installed-sets section with a real entry.
            var window = new Avalonia.Controls.Window { Width = 1280, Height = 800, DataContext = mods, Content = new Phaket.Views.ModsView() };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using (var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window))
                frame.Save($"/tmp/opencode/phaket-render/05-mods-installed-{(useSandbox ? "sandbox" : "detected")}.png");
            window.Close();
            if (useSandbox)
            {
                item.LaunchCommand.Execute(null);
                Assert.Equal(LocalizationService.Instance["Mods.SandboxLaunchHint"], mods.Status);
            }
            await item.UninstallCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(registry.All());
            Assert.Empty(mods.InstalledRecipes);
            if (useSandbox) Assert.False(Directory.Exists(sandbox));
            else
            {
                Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx/plugins/BetterPolus.dll")));
                Assert.Equal("original", File.ReadAllText(Path.Combine(gameRoot, "winhttp.dll")));
            }
        }
        finally
        {
            if (sandbox is not null && Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.Delete(appRoot, true);
        }
    }
}
