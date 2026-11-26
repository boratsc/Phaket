using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// PAK-134: the language step is the first registered step. Locale preselects it, a click
// previews the language across the wizard, and only Next/Finish write it to settings.json.
public sealed class OnboardingLanguageTests : IDisposable
{
    private readonly string directory = TestAccounts.TempPath();
    private readonly string settings;
    private readonly List<Window> windows = [];
    private readonly string previousLanguage = LocalizationService.Instance.CurrentLanguage;

    public OnboardingLanguageTests()
    {
        Directory.CreateDirectory(directory);
        settings = Path.Combine(directory, "settings.json");
    }

    public void Dispose()
    {
        LocalizationService.Instance.CurrentLanguage = previousLanguage;
        foreach (var window in windows) window.Close();
        Directory.Delete(directory, true);
    }

    private (OnboardingViewModel Wizard, LanguageStepViewModel Step) Wizard()
    {
        var step = new LanguageStepViewModel(settings);
        // Part 135 made the language step share the flow with the platforms step.
        var nexusKeys = new Phaket.Core.Nexus.NexusApiKeyStore(new Phaket.Core.Tests.InMemorySecretStore());
        var platforms = new PlatformsStepViewModel(
            new TestAccounts.FakeAccount(), new TestAccounts.FakeAccount(), nexusKeys);
        // PAK-136 appended the basics step; an empty fake scan keeps this test offline.
        var basics = new BasicsStepViewModel(settings, new Phaket.Services.UpdateService(settings),
            _ => Task.FromResult<IReadOnlyList<DetectedGame>>([]));
        var wizard = new OnboardingViewModel(new OnboardingService(settings), new OnboardingFlow(step, platforms, basics));
        return (wizard, step);
    }

    private string? SavedLanguage()
    {
        if (!File.Exists(settings)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        return doc.RootElement.TryGetProperty("language", out var value) ? value.GetString() : null;
    }

    [Fact]
    public void Locale_preselects_the_step_and_an_unsupported_locale_falls_back_to_polish()
    {
        Assert.Equal("en", LanguageStepViewModel.Detect(CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("pl-PL", LanguageStepViewModel.Detect(CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Null(LanguageStepViewModel.Detect(CultureInfo.GetCultureInfo("de-DE")));

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            // A fresh profile: the OS locale alone decides, and it previews nothing by itself.
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            var preselected = new LanguageStepViewModel(Path.Combine(directory, "fresh.json"));
            Assert.Equal("en", preselected.SelectedLanguage);
            Assert.True(preselected.Options.Single(option => option.Code == "en").IsSelected);
            Assert.Equal("pl-PL", LocalizationService.Instance.CurrentLanguage);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("pl-PL", new LanguageStepViewModel(Path.Combine(directory, "fresh2.json")).SelectedLanguage);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaFact]
    public void Choosing_a_language_previews_the_wizard_labels_live_and_finish_saves_it()
    {
        var (wizard, step) = Wizard();
        Assert.Same(step, wizard.CurrentStep);
        // Parts 135/136 appended platforms and basics, so the counter is live now.
        Assert.True(wizard.HasStepCounter);
        Assert.Equal("Krok 1 z 3", wizard.StepCounterText);
        Assert.Equal("Język", step.Title);

        var titleChanges = new List<string?>();
        wizard.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(wizard.Title)) titleChanges.Add(wizard.Title); };

        // The preset itself is not a preview: the UI language is still the started one.
        Assert.Equal("pl-PL", step.SelectedLanguage);
        Assert.Equal("pl-PL", LocalizationService.Instance.CurrentLanguage);

        step.Options.Single(option => option.Code == "en").IsSelected = true;
        Assert.Equal("en", LocalizationService.Instance.CurrentLanguage);
        // The list names every locale in its own language, never in the current UI language.
        Assert.DoesNotContain(step.Options, option => option.DisplayName == option.Code);
        Assert.Equal("Language", step.Title);
        Assert.Equal("Initial setup", wizard.Title);
        Assert.Contains(titleChanges, title => title == "Initial setup");
        Assert.True(step.Options.Single(option => option.Code == "en").IsSelected);
        Assert.False(step.Options.Single(option => option.Code == "pl-PL").IsSelected);
        // Preview only: nothing written before Finish.
        Assert.Null(SavedLanguage());

        // Next on the last step is disabled; Finish is the leave that confirms the choice.
        wizard.FinishCommand.Execute(null);
        using (var saved = JsonDocument.Parse(File.ReadAllText(settings)))
            Assert.Equal("en", saved.RootElement.GetProperty("language").GetString());
        Assert.Equal(step, wizard.CurrentStep);

        // A later start starts in that language (the settings page reads the same node).
        var settingsVm = new SettingsViewModel(settings);
        Assert.Equal("en", settingsVm.SelectedLanguage);
        Assert.Equal("en", new LanguageStepViewModel(settings).SelectedLanguage);
    }

    [AvaloniaFact]
    public void Clicking_a_radio_option_picks_the_language_and_skip_keeps_the_saved_one()
    {
        var (wizard, step) = Wizard();
        var window = new OnboardingWindow(wizard);
        windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The window title follows the wizard's own title binding on the next notification.
        string? windowTitle = null;
        wizard.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(wizard.Title)) windowTitle = window.Title; };

        var radios = window.GetVisualDescendants().OfType<RadioButton>().ToArray();
        Assert.Equal(2, radios.Length);
        var english = radios.Single(radio => Equals(radio.DataContext, step.Options.Single(option => option.Code == "en")));
        english.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("en", step.SelectedLanguage);
        Assert.Equal("Initial setup", windowTitle);

        // Skip on the first page jumps to the last one and saves nothing there either.
        wizard.SkipCommand.Execute(null);
        Assert.IsType<BasicsStepViewModel>(wizard.CurrentStep);
        wizard.SkipCommand.Execute(null);
        Assert.Null(LanguageStepViewModel.Saved(settings));
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.True(doc.RootElement.GetProperty("onboardingCompleted").GetBoolean());
        Assert.DoesNotContain("language", doc.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [AvaloniaFact]
    public void Finishing_on_the_language_step_saves_it_and_a_restart_starts_from_the_saved_language()
    {
        var (wizard, step) = Wizard();
        // Like a click on the English option, through the option's own selection handler.
        step.Options.Single(option => option.Code == "en").IsSelected = true;
        wizard.FinishCommand.Execute(null);
        using (var saved = JsonDocument.Parse(File.ReadAllText(settings)))
            Assert.Equal("en", saved.RootElement.GetProperty("language").GetString());
        Assert.Equal(step, wizard.CurrentStep);
        // A fresh step starts from the saved English: it is the radio the list shows checked.
        Assert.Equal("en", new LanguageStepViewModel(settings).Options.Single(option => option.IsSelected).Code);

        // Restart: the saved language is the preset again, not whatever was clicked last.
        wizard.RestartCommand.Execute(null);
        Assert.True(wizard.IsFirstRun());
        Assert.Equal("en", LanguageStepViewModel.Saved(settings));
        Assert.Equal("en", new LanguageStepViewModel(settings).Options.Single(option => option.IsSelected).Code);
    }

    // Both languages of the real step, rendered next to the other shell captures.
    [AvaloniaFact]
    public void Render_language_step_in_both_languages()
    {
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        foreach (var language in new[] { "pl-PL", "en" })
        {
            LocalizationService.Instance.CurrentLanguage = language;
            var (wizard, step) = Wizard();
            // The language also comes from the step's own option (UI locale is not in play here).
            step.Options.Single(option => option.Code == language).IsSelected = true;
            var window = new OnboardingWindow(wizard);
            windows.Add(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine("/tmp/opencode/phaket-render", $"70-onboarding-language-{language}.png"), PngBitmapEncoderOptions.Default);
        }
    }
}