using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.Core.Nexus;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// PAK-136: the basics step writes the update channel through UpdateService (the same
// settings.json key Settings writes) only when the user changed it, lists what a read-only
// detection found, and never blocks navigation while scanning. Fakes only.
public sealed class OnboardingBasicsTests : IDisposable
{
    private readonly string directory = TestAccounts.TempPath();
    private readonly string settings;
    private readonly List<Window> windows = [];
    private readonly string previousLanguage = LocalizationService.Instance.CurrentLanguage;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DetectedGame>>> games = _ => Task.FromResult<IReadOnlyList<DetectedGame>>(
        [new DetectedGame("Stardew Valley", "Steam"), new DetectedGame("Among Us", "Epic Games")]);

    public OnboardingBasicsTests()
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

    private (OnboardingViewModel Wizard, BasicsStepViewModel Basics) Wizard(
        Func<CancellationToken, Task<IReadOnlyList<DetectedGame>>>? scan = null)
    {
        var language = new LanguageStepViewModel(settings);
        var platforms = new PlatformsStepViewModel(new TestAccounts.FakeAccount(), new TestAccounts.FakeAccount(),
            new NexusApiKeyStore(new InMemorySecretStore()));
        var basics = new BasicsStepViewModel(settings, new UpdateService(settings), scan ?? games);
        var wizard = new OnboardingViewModel(new OnboardingService(settings), new OnboardingFlow(language, platforms, basics));
        return (wizard, basics);
    }

    private static void ToBasics(OnboardingViewModel wizard)
    {
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
    }

    private string? SavedChannel()
    {
        if (!File.Exists(settings)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        return doc.RootElement.TryGetProperty("updateChannel", out var value) ? value.GetString() : null;
    }

    [AvaloniaFact]
    public async Task Basics_is_the_third_and_last_step_with_a_detected_game_list()
    {
        var (wizard, basics) = Wizard();
        Assert.Equal(["language", "platforms", "basics"], wizard.Flow.Steps.Select(step => step.StepId));
        ToBasics(wizard);

        Assert.Same(basics, wizard.CurrentStep);
        Assert.Equal("Podstawy", basics.Title);
        Assert.Equal(3, wizard.StepNumber);
        Assert.False(wizard.CanGoNext);
        // The scan runs from the flow's activation, without awaiting it here.
        await WaitForScanAsync(basics);
        Assert.False(basics.IsScanning);
        Assert.True(basics.HasGames);
        Assert.Null(basics.ScanStatus);
        Assert.Equal(["Stardew Valley", "Among Us"], basics.Games.Select(game => game.Title));
        Assert.Equal(["Steam", "Epic Games"], basics.Games.Select(game => game.Platform));
    }

    [AvaloniaFact]
    public async Task An_empty_scan_shows_a_friendly_state()
    {
        var (wizard, basics) = Wizard(_ => Task.FromResult<IReadOnlyList<DetectedGame>>([]));
        ToBasics(wizard);
        await WaitForScanAsync(basics);
        Assert.False(basics.HasGames);
        Assert.Equal(LocalizationService.Instance["Onboarding.Basics.Games.Empty"], basics.ScanStatus);
    }

    [AvaloniaFact]
    public async Task A_failed_scan_never_blocks_the_wizard()
    {
        var (wizard, basics) = Wizard(_ => Task.FromException<IReadOnlyList<DetectedGame>>(new IOException("gone")));
        ToBasics(wizard);
        wizard.FinishCommand.Execute(null);
        Assert.False(wizard.IsFirstRun());
        await WaitForScanAsync(basics);
        Assert.False(basics.IsScanning);
        Assert.Equal(LocalizationService.Instance["Onboarding.Basics.Games.Error"], basics.ScanStatus);
    }

    [AvaloniaFact]
    public void Scanning_does_not_block_next_or_finish()
    {
        // A scan that never finishes: the page stays "detecting" and navigation still works.
        var gate = new TaskCompletionSource<IReadOnlyList<DetectedGame>>();
        var (wizard, basics) = Wizard(_ => gate.Task);
        ToBasics(wizard);
        Assert.True(basics.IsScanning);
        Assert.Equal(LocalizationService.Instance["Onboarding.Basics.Games.Scanning"], basics.ScanStatus);
        wizard.BackCommand.Execute(null);
        Assert.True(wizard.CanGoNext);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);
        Assert.False(wizard.IsFirstRun());
        Assert.True(basics.IsScanning);
        gate.SetResult([]);
    }

    [AvaloniaFact]
    public async Task A_changed_channel_is_saved_through_update_service()
    {
        var (wizard, basics) = Wizard();
        ToBasics(wizard);
        await WaitForScanAsync(basics);
        basics.SelectChannelCommand.Execute("beta");
        wizard.FinishCommand.Execute(null);

        Assert.Equal("beta", SavedChannel());
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.True(doc.RootElement.GetProperty("onboardingCompleted").GetBoolean());
        // Settings reads the same key through the same service.
        Assert.Equal(UpdateChannel.Beta, UpdateService.LoadChannel(settings));
    }

    [AvaloniaFact]
    public async Task An_unchanged_channel_writes_nothing()
    {
        var (wizard, basics) = Wizard();
        ToBasics(wizard);
        await WaitForScanAsync(basics);
        // Only a changed channel is written: changing and changing back leaves the file alone.
        basics.SelectChannelCommand.Execute("beta");
        basics.SelectChannelCommand.Execute("stable");
        wizard.FinishCommand.Execute(null);
        Assert.Null(SavedChannel());

        // Without a click at all, Next/Finish leave the file's channel untouched.
        using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
            Assert.False(doc.RootElement.TryGetProperty("updateChannel", out _));
        var (second, secondBasics) = Wizard();
        ToBasics(second);
        await WaitForScanAsync(secondBasics);
        second.FinishCommand.Execute(null);
        Assert.Null(SavedChannel());
    }

    [AvaloniaFact]
    public async Task Skip_never_saves_the_channel()
    {
        var (wizard, basics) = Wizard();
        ToBasics(wizard);
        await WaitForScanAsync(basics);
        basics.SelectChannelCommand.Execute("beta");
        wizard.SkipCommand.Execute(null);
        Assert.Null(SavedChannel());
        Assert.True(new OnboardingService(settings).IsCompleted());
    }

    [AvaloniaFact]
    public async Task Closing_the_wizard_never_saves_the_channel()
    {
        var (wizard, basics) = Wizard();
        ToBasics(wizard);
        await WaitForScanAsync(basics);
        basics.SelectChannelCommand.Execute("beta");
        wizard.CancelCommand.Execute(null);
        Assert.Null(SavedChannel());
    }

    [AvaloniaFact]
    public async Task Restart_rearms_all_three_steps()
    {
        var (wizard, basics) = Wizard();
        ToBasics(wizard);
        await WaitForScanAsync(basics);
        basics.SelectChannelCommand.Execute("beta");
        wizard.FinishCommand.Execute(null);
        Assert.Equal("beta", SavedChannel());

        wizard.RestartCommand.Execute(null);
        Assert.True(wizard.IsFirstRun());
        Assert.Equal("language", wizard.CurrentStep!.StepId);
        Assert.Equal(1, wizard.StepNumber);
        Assert.Equal(3, wizard.StepCount);
        // The saved channel is the preset again, and no stale list survives the restart.
        Assert.True(basics.IsBetaChannel);
        Assert.False(basics.HasGames);
        ToBasics(wizard);
        wizard.FinishCommand.Execute(null);
        Assert.Equal("beta", SavedChannel());
    }

    [AvaloniaFact]
    public async Task Render_basics_step_in_both_languages_with_and_without_games()
    {
        Directory.CreateDirectory("/tmp/opencode/phaket-render");
        foreach (var (language, scan) in new (string, Func<CancellationToken, Task<IReadOnlyList<DetectedGame>>>)[]
                 { ("pl-PL", games), ("pl-PL", _ => Task.FromResult<IReadOnlyList<DetectedGame>>([])), ("en", games) })
        {
            LocalizationService.Instance.CurrentLanguage = language;
            var (wizard, basics) = Wizard(scan);
            ToBasics(wizard);
            await WaitForScanAsync(basics);
            bool withGames = scan != null && basics.HasGames;

            var window = new OnboardingWindow(wizard);
            windows.Add(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            string name = $"72-onboarding-basics-{language}-{(withGames ? "games" : "empty")}.png";
            frame?.Save(Path.Combine("/tmp/opencode/phaket-render", name), PngBitmapEncoderOptions.Default);

            // The radios and the rows live in the visual tree, not only in the view model.
            var channels = window.GetVisualDescendants().OfType<RadioButton>()
                .Where(button => AutomationProperties.GetAutomationId(button)?.StartsWith("onboarding.basics.", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(2, channels.Length);
            Assert.True(File.Exists(Path.Combine("/tmp/opencode/phaket-render", name)));
        }
    }

    private static async Task WaitForScanAsync(BasicsStepViewModel basics)
    {
        for (int i = 0; i < 200 && basics.IsScanning; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}