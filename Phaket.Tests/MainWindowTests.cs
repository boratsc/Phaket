using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
    public void Library_account_action_opens_accounts_page()
    {
        var vm = TestAccounts.ViewModel();
        vm.Library.OpenAccountsCommand.Execute(null);
        Assert.Same(vm.Accounts, vm.CurrentPage);
        Assert.Equal("account", vm.ActiveNav);
    }

    [AvaloniaFact]
    public void MainWindow_opens_and_exposes_accessible_name()
    {
        var window = new MainWindow(TestAccounts.ViewModel());
        window.Show();

        Assert.Equal("phaket", window.Title);
        Assert.Equal("phaket", new WindowAutomationPeer(window).GetName());

        window.Close();
    }

    [AvaloniaFact]
    public void Navigation_switches_to_recipe_studio_and_back()
    {
        var window = new MainWindow(TestAccounts.ViewModel());
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;

        Assert.IsType<ModpackCatalogViewModel>(vm.CurrentPage);
        Assert.Equal("catalog", vm.ActiveNav);
        vm.NavigateCommand.Execute("library");
        Assert.IsType<LibraryViewModel>(vm.CurrentPage);
        vm.Library.OpenGameCommand.Execute(vm.Library.Games.First());
        Assert.IsType<GameViewModel>(vm.CurrentPage);
        vm.Game!.BackCommand.Execute(null);
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
    public void Settings_theme_button_uses_parameterless_toggle_command()
    {
        string root = TestAccounts.TempPath();
        try
        {
            var settings = new SettingsViewModel(Path.Combine(root, "settings.json"));
            var window = new MainWindow(new MainWindowViewModel(TestAccounts.Create(), settings));
            window.Show();
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.NavigateCommand.Execute("settings");
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(control => Equals(control.Command, vm.ToggleThemeCommand));
            Assert.Equal("dark", vm.Theme.CurrentTheme);
            button.Command!.Execute(button.CommandParameter);
            Assert.Equal("light", vm.Theme.CurrentTheme);

            window.Close();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Game_picker_preserves_context_tab_when_game_changes()
    {
        var vm = TestAccounts.ViewModel();
        Assert.True(vm.Library.Games.Count >= 2);
        vm.NavigateCommand.Execute("profiles");
        var tab = vm.CurrentPage;
        var selected = vm.Library.Games.First(game => !ReferenceEquals(game, vm.SelectedGame));
        vm.SelectedGame = selected;
        Assert.Equal("profiles", vm.ActiveNav);
        Assert.IsType<ProfilesViewModel>(vm.CurrentPage);
        Assert.Equal(selected.Title, vm.Profiles!.GameTitle);
        Assert.NotSame(tab, vm.CurrentPage);
    }

    [AvaloniaFact]
    public void Selected_game_keeps_context_when_library_search_has_no_match()
    {
        var vm = TestAccounts.ViewModel();
        var selected = vm.SelectedGame;
        Assert.NotNull(selected);
        vm.Library.SearchText = "no matching game";
        Assert.Empty(vm.Library.Games);
        vm.NavigateCommand.Execute("profiles");
        Assert.IsType<ProfilesViewModel>(vm.CurrentPage);
        Assert.Equal(selected.Title, vm.Profiles!.GameTitle);
        Assert.Equal("profiles", vm.ActiveNav);
    }

    [AvaloniaFact]
    public void Game_actions_select_correct_sidebar_section()
    {
        var vm = TestAccounts.ViewModel();
        vm.Library.OpenGameCommand.Execute(vm.Library.Games.First());
        vm.OpenMods();
        Assert.Equal("mods", vm.ActiveNav);
        vm.Mods!.BackCommand.Execute(null);
        vm.OpenProfiles();
        Assert.Equal("profiles", vm.ActiveNav);
        vm.Profiles!.BackCommand.Execute(null);
        vm.OpenBackup();
        Assert.Equal("library", vm.ActiveNav);
        vm.Backup!.BackCommand.Execute(null);
        Assert.IsType<GameViewModel>(vm.CurrentPage);
    }

    [Fact]
    public void Library_search_filters_and_restores_catalog()
    {
        var vm = new LibraryViewModel(_ => { }, TestAccounts.Create(), null);
        var count = vm.Games.Count;
        vm.SearchText = "valheim";
        Assert.Single(vm.Games);
        Assert.Contains("Valheim", vm.Games[0].Title);
        vm.SearchText = "not a game";
        Assert.Empty(vm.Games);
        vm.SearchText = "";
        Assert.Equal(count, vm.Games.Count);
    }

    [AvaloniaFact]
    public void Nav_buttons_expose_automation_names()
    {
        var window = new MainWindow(TestAccounts.ViewModel());
        window.Show();
        // The user's persisted language choice must not decide this assertion.
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        loc.CurrentLanguage = "pl-PL";
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            var named = buttons.Select(b => AutomationProperties.GetName(b)).Where(n => !string.IsNullOrEmpty(n)).ToList();
            Assert.Contains("Biblioteka", named);
            Assert.Contains("Recipe Studio", named);
            Assert.Contains("Ustawienia", named);
        }
        finally
        {
            loc.CurrentLanguage = previous;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Language_switch_updates_ui_text_without_rebuild()
    {
        var window = new MainWindow(TestAccounts.ViewModel());
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var loc = LocalizationService.Instance;
            loc.CurrentLanguage = "pl-PL";
            // The sidebar entry, not the breadcrumb crumb with the same label: the breadcrumb
            // is rebuilt when the trail changes, so its containers cannot be reused here.
            var navButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => Equals(b.Command, vm.NavigateCommand) && b.CommandParameter as string == "library");
            Assert.Equal("Biblioteka", AutomationProperties.GetName(navButton));

            loc.CurrentLanguage = "en";
            Assert.Equal("Library", AutomationProperties.GetName(navButton));
            Assert.Equal("Settings", AutomationProperties.GetName(
                window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Command, vm.NavigateCommand) && b.CommandParameter as string == "settings")));
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            window.Close();
        }
    }
}

public class ThemeServiceTests
{
    [AvaloniaFact]
    public void Theme_toggle_changes_variant_and_preserves_other_settings()
    {
        string directory = TestAccounts.TempPath();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"language\":\"en\",\"catalogEndpoint\":\"https://example.test\",\"tools\":{\"depot\":{\"version\":\"1.2\"}}}");
        try
        {
            var service = new ThemeService(path);
            Assert.Equal("dark", service.CurrentTheme);
            Assert.Equal("Dark", Application.Current!.RequestedThemeVariant?.Key);
            service.Toggle();
            Assert.Equal("light", service.CurrentTheme);
            Assert.Equal("Light", Application.Current.RequestedThemeVariant?.Key);
            using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("light", settings.RootElement.GetProperty("theme").GetString());
            Assert.Equal("en", settings.RootElement.GetProperty("language").GetString());
            Assert.Equal("1.2", settings.RootElement.GetProperty("tools").GetProperty("depot").GetProperty("version").GetString());
            Assert.Equal("light", new ThemeService(path).CurrentTheme);
            Assert.Equal("Light", Application.Current.RequestedThemeVariant?.Key);
        }
        finally { Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public void Invalid_settings_json_is_not_overwritten_by_theme_toggle()
    {
        string directory = TestAccounts.TempPath();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        const string corrupt = "not json";
        File.WriteAllText(path, corrupt);
        try
        {
            var service = new ThemeService(path);
            Assert.False(service.SetTheme("light"));
            Assert.Equal(corrupt, File.ReadAllText(path));
            Assert.Equal("Light", Application.Current!.RequestedThemeVariant?.Key);
        }
        finally { Directory.Delete(directory, true); }
    }
}

public class LocalizationTests
{
    [AvaloniaFact]
    public void Satellite_resources_are_merged_into_the_shared_localization_service()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        try
        {
            Assert.Contains("Catalog.MyModpacks", loc.Keys("pl-PL"));
            Assert.Contains("Catalog.MyModpacks", loc.Keys("en"));
            loc.CurrentLanguage = "pl-PL";
            Assert.Equal("Moje pakiety", loc["Catalog.MyModpacks"]);
            loc.CurrentLanguage = "en";
            Assert.Equal("My modpacks", loc["Catalog.MyModpacks"]);
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void Navigation_labels_match_the_approved_names_in_both_languages()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        try
        {
            loc.CurrentLanguage = "pl-PL";
            Assert.Equal("Katalog pakietów", loc["Catalog.Title"]);
            Assert.Equal("Profile", loc["Nav.Profiles"]);
            Assert.Equal("Zgodność", loc["Compat.Title"]);
            Assert.Equal("Poradniki", loc["Nav.Guides"]);
            Assert.Equal("Biblioteka", loc["Nav.Library"]);
            loc.CurrentLanguage = "en";
            Assert.Equal("Modpack catalog", loc["Catalog.Title"]);
            Assert.Equal("Profiles", loc["Nav.Profiles"]);
            Assert.Equal("Compatibility", loc["Compat.Title"]);
            Assert.Equal("Guides", loc["Nav.Guides"]);
            Assert.Equal("Library", loc["Nav.Library"]);
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [Fact]
    public void PlAndEnHaveIdenticalKeySets()
    {
        var loc = LocalizationService.Instance;
        var pl = loc.Keys("pl-PL");
        var en = loc.Keys("en");
        Assert.NotEmpty(pl);
        Assert.Equal(pl.OrderBy(k => k), en.OrderBy(k => k));
    }

    [AvaloniaFact]
    public void EveryKeyResolvesInBothLanguages()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        try
        {
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
        }
        finally { loc.CurrentLanguage = previous; }
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

    [AvaloniaFact]
    public void InvalidRecipeProducesLocalizedErrors()
    {
        var loc = LocalizationService.Instance;
        var previous = loc.CurrentLanguage;
        try
        {
            loc.CurrentLanguage = "pl-PL";
            var vm = new RecipeStudioViewModel();
            vm.AddStepCommand.Execute("file.copy");
            vm.Steps[0].To = "game://../evil.dll"; // traversal
            Assert.False(vm.ValidateCore());
            Assert.Contains(vm.ValidationResults, r => r.Contains("adres") || r.Contains("URI") || r.Contains("zakres"));

            loc.CurrentLanguage = "en";
            Assert.False(vm.ValidateCore());
            Assert.Contains(vm.ValidationResults, r => r.Contains("URI") || r.Contains("path") || r.Contains("area"));
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [Fact]
    public void RichYamlRoundTripPreservesUnexposedFields()
    {
        const string yaml = """
            schemaVersion: omrl/v0alpha1
            id: community.test.rich
            version: 1.0.0
            metadata:
              title:
                pl-PL: Bogaty przepis
                en: Rich recipe
            target:
              game: among-us
              systems: [windows, linux]
              constraints:
                gameVersion: ">=2026.1"
            requires:
              capabilities: [archive.extract, file.copy]
              loaders:
                - id: bepinex
                  version: ">=6.0"
            artifacts:
              - id: mod-bundle
                source: package://mod-bundle.zip
                sha256: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
                sizeBytes: 100
            steps:
              - id: extract
                use: archive.extract
                with:
                  from: package://mod-bundle
                  to: staging://mod
                  stripComponents: 1
                when:
                  os: [linux]
              - id: copy
                use: file.copy
                with:
                  from: staging://mod/Mod.dll
                  to: game://BepInEx/plugins/Mod.dll
                  overwrite: replace
            verify:
              - use: file.verify
                with:
                  path: game://BepInEx/plugins/Mod.dll
            """;
        var vm = new RecipeStudioViewModel();
        vm.LoadYamlCore(yaml);
        var saved = vm.SaveYamlCore();
        // Fields the visual editor does not expose must survive the round-trip.
        Assert.Contains("Bogaty przepis", saved);
        Assert.Contains("gameVersion", saved);
        Assert.Contains("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", saved);
        Assert.Contains("stripComponents", saved);
        Assert.Contains("overwrite: replace", saved);
        Assert.Contains("when", saved);
        Assert.Contains("verify", saved);
        Assert.Contains("bepinex", saved);
        Assert.True(vm.RoundTripIsLossless());
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
