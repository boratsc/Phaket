using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Accounts;
using Phaket.Core.Nexus;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// PAK-135: the platforms step lists Steam, Epic Games and Nexus with a live status, signs in
// through the same services Settings uses, and skipping writes nothing. Fakes only.
public sealed class OnboardingPlatformsTests : IDisposable
{
    private readonly string directory = TestAccounts.TempPath();
    private readonly string settings;
    private readonly List<Window> windows = [];
    private readonly string previousLanguage = LocalizationService.Instance.CurrentLanguage;
    // Shared by every wizard built here, so a later restart reads the same "signed in" state.
    private readonly InMemorySecretStore secrets = new();

    public OnboardingPlatformsTests()
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

    private (OnboardingViewModel Wizard, LanguageStepViewModel Language, PlatformsStepViewModel Platforms,
        TestAccounts.FakeAccount Steam, TestAccounts.FakeAccount Epic, NexusApiKeyStore NexusKeys) Wizard()
    {
        var language = new LanguageStepViewModel(settings);
        var steam = new TestAccounts.FakeAccount();
        var epic = new TestAccounts.FakeAccount("epic");
        var nexusKeys = new NexusApiKeyStore(secrets);
        var platforms = new PlatformsStepViewModel(steam, epic, nexusKeys);
        // PAK-136 appended the basics step: an empty fake scan keeps these platform tests offline.
        var basics = new BasicsStepViewModel(settings, new UpdateService(settings), _ => Task.FromResult<IReadOnlyList<DetectedGame>>([]));
        var wizard = new OnboardingViewModel(new OnboardingService(settings), new OnboardingFlow(language, platforms, basics));
        return (wizard, language, platforms, steam, epic, nexusKeys);
    }

    [AvaloniaFact]
    public void The_step_lists_three_platforms_with_status_and_no_saved_state()
    {
        var (wizard, _, platforms, _, _, _) = Wizard();
        Assert.Equal(["language", "platforms", "basics"], wizard.Flow.Steps.Select(step => step.StepId));
        Assert.Equal(3, wizard.StepCount);
        Assert.Equal("Język", wizard.CurrentStep!.Title);

        wizard.NextCommand.Execute(null);
        Assert.Same(platforms, wizard.CurrentStep);
        Assert.Equal("Platformy", platforms.Title);
        Assert.Equal(2, wizard.StepNumber);
        Assert.True(wizard.CanGoBack);
        Assert.True(wizard.CanGoNext);
        Assert.Equal(["Steam", "Epic Games", "Nexus Mods"], platforms.Platforms.Select(platform => platform.DisplayName));
        Assert.All(platforms.Platforms, platform => Assert.False(platform.IsSignedIn));
        Assert.All(platforms.Platforms, platform => Assert.Null(platform.Status));
        Assert.True(platforms.Platforms[2].HasKey);
        Assert.False(platforms.Platforms[0].HasKey);
    }

    [AvaloniaFact]
    public async Task Signing_in_runs_the_existing_flow_and_the_status_goes_live()
    {
        var (_, _, platforms, steam, epic, nexusKeys) = Wizard();
        var steamRow = platforms.Platforms[0];
        var epicRow = platforms.Platforms[1];
        var nexusRow = platforms.Platforms[2];

        await steamRow.SignInCommand.ExecuteAsync(null);
        await epicRow.SignInCommand.ExecuteAsync(null);
        await nexusRow.PasteKeyCommand.ExecuteAsync(null); // empty key is refused
        Assert.Equal(1, steam.LoginCalls);
        Assert.Equal(1, epic.LoginCalls);
        Assert.True(steamRow.IsSignedIn);
        Assert.Contains("steam-user", steamRow.Status);
        Assert.Contains("epic-user", epicRow.Status);
        Assert.False(nexusRow.IsSignedIn);
        Assert.Equal(LocalizationService.Instance["Onboarding.Platforms.Error"], nexusRow.Status);

        nexusRow.KeyInput = "synthetic-nexus-key";
        await nexusRow.PasteKeyCommand.ExecuteAsync(null);
        Assert.True(nexusRow.IsSignedIn);
        Assert.False(nexusRow.CanSignIn);
        Assert.Equal("", nexusRow.KeyInput);
        Assert.Equal("synthetic-nexus-key", await nexusKeys.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(LocalizationService.Instance["Onboarding.Platforms.SignedIn"], nexusRow.Status);

        // Status read back from the store, not from the wizard's memory.
        Assert.True(new PlatformSignInViewModel("nexus", "Nexus Mods", nexusKeys).IsSignedIn);
    }

    [AvaloniaFact]
    public void Later_skips_to_the_last_page_and_writes_nothing_but_the_marker()
    {
        var (wizard, _, _, steam, _, _) = Wizard();
        wizard.NextCommand.Execute(null);
        wizard.SkipCommand.Execute(null);
        // PAK-136: Skip from Platforms now lands on Basics instead of ending the flow.
        Assert.Equal("basics", wizard.CurrentStep!.StepId);
        wizard.FinishCommand.Execute(null);
        Assert.Equal(0, steam.LoginCalls);
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.True(doc.RootElement.GetProperty("onboardingCompleted").GetBoolean());
    }

    [AvaloniaFact]
    public async Task Closing_the_wizard_signs_nobody_in()
    {
        var (wizard, _, _, steam, epic, _) = Wizard();
        wizard.NextCommand.Execute(null);
        wizard.CancelCommand.Execute(null);
        Assert.Equal(0, steam.LoginCalls);
        Assert.Equal(0, epic.LoginCalls);
        Assert.Null(await secrets.GetAsync(NexusApiKeyStore.SecretKey, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public void Back_next_and_finish_walk_both_steps()
    {
        var (wizard, language, platforms, _, _, _) = Wizard();
        wizard.NextCommand.Execute(null);
        Assert.Same(platforms, wizard.CurrentStep);
        wizard.BackCommand.Execute(null);
        Assert.Same(language, wizard.CurrentStep);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        Assert.False(wizard.IsFirstRun());
        Assert.Equal("pl-PL", LanguageStepViewModel.Saved(settings));
        Assert.Equal(3, wizard.StepCount);
    }

    [AvaloniaFact]
    public async Task Restart_from_settings_rearms_both_steps()
    {
        var (wizard, language, platforms, _, _, nexusKeys) = Wizard();
        // A click also previews the UI language; set it back, so the preset assertion is real.
        language.Options.Single(option => option.Code == "en").IsSelected = true;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        wizard.NextCommand.Execute(null);
        await nexusKeys.SaveAsync("synthetic-nexus-key", TestContext.Current.CancellationToken);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        // A restart re-reads the stores and puts the wizard back on the first step.
        wizard.RestartCommand.Execute(null);
        Assert.Equal("en", LanguageStepViewModel.Saved(settings));
        Assert.True(wizard.IsFirstRun());
        Assert.Same(language, wizard.CurrentStep);
        Assert.Equal(1, wizard.StepNumber);
        // English was saved by the finish, so the preset is English; Nexus is still signed in.
        Assert.Equal("en", language.SelectedLanguage);
        Assert.True(platforms.Platforms[2].IsSignedIn);
    }

    [AvaloniaFact]
    public async Task Render_platforms_step_in_both_languages_with_one_signed_in()
    {
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        var platforms = default(PlatformsStepViewModel);
        foreach (var language in new[] { "pl-PL", "en" })
        {
            LocalizationService.Instance.CurrentLanguage = language;
            var (wizard, step, current, _, _, _) = Wizard();
            step.Options.Single(option => option.Code == language).IsSelected = true;
            // One platform already signed in on the second run, through the row's own command.
            if (language == "en") await current.Platforms[0].SignInCommand.ExecuteAsync(null);
            // Only this step's own row may follow a sign-in while an older wizard is still alive.
            Assert.Equal(language == "en", current.Platforms[0].IsSignedIn);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(language == "en", current.Platforms[0].IsSignedIn);
            Assert.False(current.Platforms[1].IsSignedIn);
            // Note: these platform rows are the wizard's; the basics scan is a fake with no games.
            Assert.False(current.Platforms[2].IsSignedIn);
            wizard.NextCommand.Execute(null);
            Assert.Same(current, wizard.CurrentStep);
            platforms = current;

            var window = new OnboardingWindow(wizard);
            windows.Add(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine("/tmp/opencode/phaket-render", $"71-onboarding-platforms-{language}.png"), PngBitmapEncoderOptions.Default);

            // The rows exist in the visual tree, not just in the view model.
            var loginButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => AutomationProperties.GetAutomationId(button)?.StartsWith("onboarding.platform.", StringComparison.Ordinal) == true).ToArray();
            Assert.Equal(3, loginButtons.Length);
            if (language == "en")
            {
                Assert.Equal("Signed in as steam-user", current.Platforms[0].Status);
                Assert.Equal("Sign in", current.Platforms[1].LoginText);
            }
        }
        Assert.NotNull(platforms);
    }
}