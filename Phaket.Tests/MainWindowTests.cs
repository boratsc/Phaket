using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phaket;
using Phaket.Services;
using Phaket.ViewModels;

[assembly: AvaloniaTestApplication(typeof(Phaket.Tests.TestAppBuilder))]

namespace Phaket.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_opens_and_exposes_accessible_name()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("phaket", window.Title);
        Assert.Equal("phaket", new WindowAutomationPeer(window).GetName());

        window.Close();
    }

    [AvaloniaFact]
    public void Navigation_switches_to_recipe_studio_and_back()
    {
        var window = new MainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;

        Assert.IsType<LibraryViewModel>(vm.CurrentPage);
        vm.NavigateCommand.Execute("studio");
        Assert.IsType<RecipeStudioViewModel>(vm.CurrentPage);
        vm.NavigateCommand.Execute("settings");
        Assert.IsType<SettingsViewModel>(vm.CurrentPage);
        vm.NavigateCommand.Execute("library");
        Assert.IsType<LibraryViewModel>(vm.CurrentPage);

        window.Close();
    }

    [AvaloniaFact]
    public void Nav_buttons_expose_automation_names()
    {
        var window = new MainWindow();
        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        var named = buttons.Select(b => AutomationProperties.GetName(b)).Where(n => !string.IsNullOrEmpty(n)).ToList();
        Assert.Contains("Gry", named);
        Assert.Contains("Recipe Studio", named);
        Assert.Contains("Ustawienia", named);

        window.Close();
    }

    [AvaloniaFact]
    public void Language_switch_updates_ui_text_without_rebuild()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var loc = LocalizationService.Instance;
            loc.CurrentLanguage = "pl-PL";
            var navButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => AutomationProperties.GetName(b) == "Gry");
            Assert.Equal("Gry", navButton.Content);

            loc.CurrentLanguage = "en";
            Assert.Equal("Games", navButton.Content);
            Assert.Equal("Settings", AutomationProperties.GetName(
                window.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Content == "Settings")));
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            window.Close();
        }
    }
}

public class LocalizationTests
{
    [Fact]
    public void PlAndEnHaveIdenticalKeySets()
    {
        var loc = LocalizationService.Instance;
        var pl = loc.Keys("pl-PL");
        var en = loc.Keys("en");
        Assert.NotEmpty(pl);
        Assert.Equal(pl.OrderBy(k => k), en.OrderBy(k => k));
    }

    [Fact]
    public void EveryKeyResolvesInBothLanguages()
    {
        var loc = LocalizationService.Instance;
        foreach (var key in loc.Keys("pl-PL"))
        {
            loc.CurrentLanguage = "pl-PL";
            var pl = loc[key];
            loc.CurrentLanguage = "en";
            var en = loc[key];
            Assert.NotEqual(key, pl);
            Assert.NotEqual(key, en);
            Assert.False(string.IsNullOrWhiteSpace(pl));
            Assert.False(string.IsNullOrWhiteSpace(en));
        }
        loc.CurrentLanguage = "pl-PL";
    }

    [Fact]
    public void MissingKeyFallsBackDeterministically()
    {
        var loc = LocalizationService.Instance;
        Assert.Equal("No.Such.Key", loc["No.Such.Key"]);
    }
}

public class RecipeStudioFlowTests
{
    [Fact]
    public void FullPocFlow_AddSteps_SaveYaml_Reload_Validate_Plan()
    {
        var vm = new RecipeStudioViewModel();
        Assert.NotNull(vm.SelectedGame);

        // PoC flow: archive.extract + file.copy (PAK-20).
        vm.AddStepCommand.Execute("archive.extract");
        vm.AddStepCommand.Execute("file.copy");
        Assert.Equal(2, vm.Steps.Count);
        Assert.NotEmpty(vm.Artifacts); // package artifact auto-declared

        // Save to YAML and reload without data loss.
        var yaml = vm.SaveYamlCore();
        Assert.Contains("archive.extract", yaml);
        Assert.Contains("file.copy", yaml);
        var reloaded = new RecipeStudioViewModel();
        reloaded.LoadYamlCore(yaml);
        Assert.Equal(vm.Steps.Count, reloaded.Steps.Count);
        Assert.Equal(vm.RecipeId, reloaded.RecipeId);
        Assert.True(reloaded.RoundTripIsLossless());

        // Validation passes for the reference flow.
        Assert.True(reloaded.ValidateCore());

        // Dry-run plan preview on a fixture sandbox.
        Assert.True(reloaded.PlanCore());
        Assert.Contains(reloaded.PlanLines, l => l.Contains("archive.extract"));
        Assert.Contains(reloaded.PlanLines, l => l.Contains("file.copy"));
        Assert.Contains(reloaded.PlanLines, l => l.Contains("BepInEx"));
    }

    [Fact]
    public void InvalidRecipeProducesLocalizedErrors()
    {
        var loc = LocalizationService.Instance;
        loc.CurrentLanguage = "pl-PL";
        var vm = new RecipeStudioViewModel();
        vm.AddStepCommand.Execute("file.copy");
        vm.Steps[0].To = "game://../evil.dll"; // traversal
        Assert.False(vm.ValidateCore());
        Assert.Contains(vm.ValidationResults, r => r.Contains("adres") || r.Contains("URI") || r.Contains("zakres"));

        loc.CurrentLanguage = "en";
        Assert.False(vm.ValidateCore());
        Assert.Contains(vm.ValidationResults, r => r.Contains("URI") || r.Contains("path") || r.Contains("area"));
        loc.CurrentLanguage = "pl-PL";
    }

    [Fact]
    public void YamlEditedExternallyLoadsIntoVisualModel()
    {
        const string yaml = """
            schemaVersion: omrl/v0alpha1
            id: community.test.external
            version: 2.0.0
            target:
              game: valheim
              systems: [windows, linux]
            requires:
              capabilities: [file.copy]
            artifacts:
              - id: mod-bundle
                source: package://mod-bundle.zip
            steps:
              - id: copy
                use: file.copy
                with:
                  from: package://mod-bundle
                  to: game://BepInEx/plugins/Mod.dll
            """;
        var vm = new RecipeStudioViewModel();
        vm.LoadYamlCore(yaml);
        Assert.Equal("community.test.external", vm.RecipeId);
        Assert.Equal("2.0.0", vm.RecipeVersion);
        Assert.Equal("valheim", vm.SelectedGame!.Slug);
        Assert.Single(vm.Steps);
        Assert.Equal("game://BepInEx/plugins/Mod.dll", vm.Steps[0].To);
        Assert.True(vm.ValidateCore());
    }
}
