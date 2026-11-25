using Phaket.Core.Execution;
using Phaket.Services;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public class ModLaunchRequirementsTests
{
    private static InstalledRecipe Entry(string game = "valheim", string store = "steam", string root = "/games/valheim") =>
        new("test", "1.0.0", game, store, root, "/profile", "/stage", "/state", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void SteamTranslationIncludesCommandAndRejectsExpansionInPath()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = ModLaunchRequirements.Create(Entry());
        Assert.Contains("%command%", result);
        Assert.Contains("DOORSTOP_TARGET_ASSEMBLY", result);
        Assert.Null(ModLaunchRequirements.Create(Entry(root: "/games/$HOME")));
    }

    [Fact]
    public void HeroicTranslationDoesNotIncludeSteamCommand()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = ModLaunchRequirements.Create(Entry(store: "epic"));
        Assert.Contains("DOORSTOP_TARGET_ASSEMBLY", result);
        Assert.DoesNotContain("%command%", result);
    }
}

public class ModLaunchViewModelTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void LaunchShowsPlanWithoutStartingSteam()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("phaket-launch-vm-").FullName;
        try
        {
            var entry = new InstalledRecipe("test", "1.0.0", "valheim", "steam", "/games/valheim", "/profile", "/stage", "/state", null, DateTimeOffset.UnixEpoch);
            new InstallRegistry(Path.Combine(root, "installs.json")).Add(entry);
            var definition = GameDefinitions.LoadAll().First(g => g.Slug == "valheim");
            var vm = new Phaket.ViewModels.ModsViewModel(new Phaket.ViewModels.GameCatalogItem(definition, "", ""), () => { }, root);
            Assert.Single(vm.InstalledRecipes).LaunchCommand.Execute(null);
            Assert.Contains("%command%", vm.LaunchRequirements);
            Assert.True(vm.HasLaunchRequirements);
            var window = new Avalonia.Controls.Window { Width = 1280, Height = 800, DataContext = vm, Content = new Phaket.Views.ModsView() };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            Assert.NotNull(frame);
            frame.Save("/tmp/opencode/phaket-render/ui2-launch-requirements.png", PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }
}
