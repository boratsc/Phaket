using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Models;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

public sealed class NexusManualTests
{
    [Theory]
    [InlineData("https://www.nexusmods.com/amongus/mods/123", true)]
    [InlineData("https://nexusmods.com/game/mods/1?tab=files", true)]
    [InlineData("https://www.nexusmods.com/games/1/mods/123", true)]
    [InlineData("http://www.nexusmods.com/game/mods/123", false)]
    [InlineData("https://nexusmods.com.evil.test/game/mods/123", false)]
    [InlineData("https://www.nexusmods.com/game/mods/not-an-id", false)]
    [InlineData("https://www.nexusmods.com/game/mods/0", false)]
    [InlineData("https://www.nexusmods.com/game/mods/1/", true)]
    public void Nexus_mod_url_is_validated(string url, bool expected) =>
        Assert.Equal(expected, NexusManualViewModel.IsValidNexusModUrl(url));

    [Fact]
    public void Archive_requires_existing_supported_file_extension()
    {
        string root = Path.Combine(Path.GetTempPath(), "phaket-nexus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "mod.zip");
            string exe = Path.Combine(root, "mod.exe");
            File.WriteAllText(zip, "not extracted");
            File.WriteAllText(exe, "not archive");

            Assert.True(NexusManualViewModel.IsValidArchive(zip));
            Assert.False(NexusManualViewModel.IsValidArchive(zip + ".missing"));
            Assert.False(NexusManualViewModel.IsValidArchive(exe));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Pin_returns_validated_manual_source_and_archive_path()
    {
        string root = Path.Combine(Path.GetTempPath(), "phaket-nexus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "mod.7z");
            File.WriteAllText(archive, "archive bytes");
            var vm = new NexusManualViewModel(() => Task.FromResult<string?>(archive))
            {
                Url = "https://www.nexusmods.com/game/mods/123?tab=files",
                Name = "Example mod",
                PinnedVersion = "1.2.3"
            };

            await vm.ChooseArchiveCommand.ExecuteAsync(null);
            vm.PinSelectionCommand.Execute(null);

            Assert.Equal(new NexusManualPickResult(vm.Url, "Example mod", "1.2.3", Path.GetFullPath(archive)), vm.Result);
            Assert.Equal("", vm.ValidationMessage);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Nexus_manual_form_renders_headless_and_exposes_labels()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        var vm = new NexusManualViewModel();
        var window = new Window { Width = 920, Height = 720, Content = new NexusManualView { DataContext = vm } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var inputs = window.GetVisualDescendants().OfType<TextBox>().ToArray();
            Assert.Equal(4, inputs.Length);
            Assert.All(inputs, input => Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(input))));
            using var frame = window.CaptureRenderedFrame();
            Assert.True(frame!.PixelSize.Width > 0);
        }
        finally
        {
            window.Close();
            LocalizationService.Instance.CurrentLanguage = language;
        }
    }
}
