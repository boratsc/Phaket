using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

// PAK-133: first-run wizard skeleton. The marker lives in settings.json, every step is
// skippable and the settings entry restarts the flow; parts 134-136 only append steps.
public class OnboardingTests : IDisposable
{
    private readonly string directory = TestAccounts.TempPath();
    private readonly string settings;
    private readonly List<Window> windows = [];

    public OnboardingTests()
    {
        Directory.CreateDirectory(directory);
        settings = Path.Combine(directory, "settings.json");
    }

    public void Dispose()
    {
        foreach (var window in windows) window.Close();
        Directory.Delete(directory, true);
    }

    private bool Marker()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        return doc.RootElement.TryGetProperty("onboardingCompleted", out var value) && value.GetBoolean();
    }

    [AvaloniaFact]
    public void Fresh_profile_opens_the_wizard_outside_the_shell()
    {
        var vm = new OnboardingViewModel(new OnboardingService(settings));
        Assert.False(File.Exists(settings));
        Assert.True(vm.IsFirstRun());

        bool? opened = null;
        vm.VisibilityChanged += (_, open) => opened = open;
        vm.TryOpen();
        Assert.True(opened);

        // Empty registry: one placeholder page, nothing to go forward to, Finish is the action.
        Assert.Equal(1, vm.StepCount);
        Assert.IsType<OnboardingViewModel.PlaceholderStep>(vm.CurrentStep);
        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.HasStepCounter);
        // Opening the wizard alone writes nothing; only leaving it does.
        Assert.False(File.Exists(settings));
    }

    [AvaloniaFact]
    public void Finishing_writes_the_marker_and_the_next_start_skips_the_wizard()
    {
        var first = new OnboardingViewModel(new OnboardingService(settings));
        bool? opened = null;
        first.VisibilityChanged += (_, open) => opened = open;
        first.FinishCommand.Execute(null);

        Assert.True(Marker());
        Assert.False(opened);
        Assert.False(new OnboardingViewModel(new OnboardingService(settings)).IsFirstRun());

        // Keep every other settings node when the marker is written.
        var foreign = new OnboardingViewModel(new OnboardingService(settings));
        File.WriteAllText(settings, """{"language":"en","toolPins":{"legendary":"0.20.0"}}""");
        Assert.True(foreign.IsFirstRun());
        foreign.FinishCommand.Execute(null);
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.Equal("en", doc.RootElement.GetProperty("language").GetString());
        Assert.True(doc.RootElement.GetProperty("onboardingCompleted").GetBoolean());
    }

    [AvaloniaFact]
    public void Skipping_every_step_finishes_and_closing_also_counts_as_a_skip()
    {
        var vm = new OnboardingViewModel(new OnboardingService(settings));
        vm.SkipCommand.Execute(null);
        Assert.True(Marker());
        Assert.False(new OnboardingViewModel(new OnboardingService(settings)).IsFirstRun());

        File.Delete(settings);
        var closed = new OnboardingViewModel(new OnboardingService(settings));
        closed.CancelCommand.Execute(null);
        Assert.True(Marker());
    }

    [AvaloniaFact]
    public void Settings_entry_resets_the_marker_and_opens_the_wizard()
    {
        var service = new OnboardingService(settings);
        var vm = new OnboardingViewModel(service);
        vm.FinishCommand.Execute(null);
        Assert.False(vm.IsFirstRun());

        var settingsVm = new SettingsViewModel(settings) { Onboarding = vm };
        bool? opened = null;
        vm.VisibilityChanged += (_, open) => opened = open;
        settingsVm.RunOnboardingCommand.Execute(null);

        Assert.True(opened);
        Assert.False(Marker());
        Assert.True(vm.IsFirstRun());
        Assert.True(File.Exists(settings));

        // Back to the shell from the restarted run, then the marker is complete again.
        vm.FinishCommand.Execute(null);
        Assert.True(Marker());
    }

    [AvaloniaFact]
    public void Registered_steps_get_back_next_skip_and_finish_on_the_last_step()
    {
        var flow = new OnboardingViewModel.Registry([new Step("one"), new Step("two")]);
        var vm = new OnboardingViewModel(new OnboardingService(settings), flow);

        Assert.True(vm.HasStepCounter);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoNext);
        Assert.Same(flow.Steps[0], vm.CurrentStep);
        Assert.Equal(LocalizationService.Instance["Onboarding.StepCounter"]
            .Replace("{0}", "1").Replace("{1}", "2"), vm.StepCounterText);

        vm.NextCommand.Execute(null);
        Assert.Same(flow.Steps[1], vm.CurrentStep);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoNext);

        vm.BackCommand.Execute(null);
        Assert.Same(flow.Steps[0], vm.CurrentStep);

        vm.SkipCommand.Execute(null);
        Assert.Same(flow.Steps[1], vm.CurrentStep);
        Assert.False(File.Exists(settings));

        vm.SkipCommand.Execute(null);
        Assert.True(Marker());
    }

    [AvaloniaFact]
    public void Completed_step_hook_keeps_the_wizard_on_the_next_start()
    {
        // How part 134 hooks a step that saves its own state into the first-run decision.
        var chosen = false;
        var service = new OnboardingService(settings, isCompleteHooks: [() => chosen], resetHooks: [() => chosen = false]);
        new OnboardingViewModel(service).FinishCommand.Execute(null);
        Assert.True(new OnboardingViewModel(service).IsFirstRun());

        chosen = true;
        Assert.False(new OnboardingViewModel(service).IsFirstRun());

        new OnboardingViewModel(service).RestartCommand.Execute(null);
        Assert.False(chosen);
    }

    [AvaloniaFact]
    public void Wizard_windows_render_their_controls_and_the_settings_entry()
    {
        var loc = LocalizationService.Instance;
        string previous = loc.CurrentLanguage;
        try
        {
            loc.CurrentLanguage = "pl-PL";
            var vm = new OnboardingViewModel(new OnboardingService(settings));
            var window = new OnboardingWindow(vm);
            windows.Add(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(loc["Onboarding.Title"], window.Title);
            var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
            // Compiled bindings render the localized text via ContentPresenter, so the button
            // commands are what identifies each action.
            Assert.Contains(buttons, button => Equals(button.Command, vm.SkipCommand) && button.IsEffectivelyVisible);
            Assert.Contains(buttons, button => Equals(button.Command, vm.FinishCommand) && button.IsEffectivelyVisible);
            Assert.Contains(buttons, button => Equals(button.Command, vm.BackCommand) && !button.IsEffectivelyVisible);
            Assert.DoesNotContain(buttons, button => Equals(button.Command, vm.NextCommand) && button.IsEffectivelyVisible);
            Assert.Contains(loc["Onboarding.Skip"], window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));

            // A registered step gets Next + Skip in the same window.
            var stepping = new OnboardingViewModel(new OnboardingService(settings),
                new OnboardingViewModel.Registry([new Step("one"), new Step("two")]));
            var stepWindow = new OnboardingWindow(stepping);
            windows.Add(stepWindow);
            stepWindow.Show();
            Dispatcher.UIThread.RunJobs();
            var stepButtons = stepWindow.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Contains(stepButtons, button => Equals(button.Command, stepping.NextCommand) && button.IsEffectivelyVisible);
            Assert.Contains(stepButtons, button => Equals(button.Command, stepping.BackCommand) && !button.IsEffectivelyVisible);
            stepping.NextCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(stepWindow.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Command, stepping.BackCommand) && button.IsEffectivelyVisible);
            Assert.Contains(stepWindow.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == stepping.StepCounterText && text.IsEffectivelyVisible);

            // The real wiring: the settings page carries the shell's wizard view model and the
            // entry command is the button the user clicks.
            var shellVm = TestAccounts.ViewModel();
            shellVm.Settings.Onboarding = vm;
            shellVm.NavigateCommand.Execute("settings");
            var shell = new MainWindow(shellVm);
            windows.Add(shell);
            shell.Show();
            Dispatcher.UIThread.RunJobs();

            var entry = shell.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Command, shellVm.Settings.RunOnboardingCommand));
            Assert.True(entry.IsEffectivelyVisible);
            Assert.Equal(loc["Onboarding.Restart.Action"], entry.Content);
            // The standalone wizard above was never left, so this starts as a first run.
            Assert.True(vm.IsFirstRun());
            vm.CancelCommand.Execute(null);
            Assert.False(vm.IsFirstRun());

            // Settings entry: forget the marker and open the wizard again on demand.
            bool reopened = false;
            vm.VisibilityChanged += (_, open) => reopened |= open;
            shellVm.Settings.RunOnboardingCommand.Execute(null);
            Assert.True(reopened);
            Assert.True(vm.IsFirstRun());
        }
        finally { loc.CurrentLanguage = previous; }
    }

    // Visual check of the skeleton in both languages, saved next to the other shell renders.
    [AvaloniaFact]
    public void Render_wizard_state()
    {
        var loc = LocalizationService.Instance;
        string previous = loc.CurrentLanguage;
        try
        {
            foreach (var language in new[] { "pl-PL", "en" })
            {
                loc.CurrentLanguage = language;
                var vm = new OnboardingViewModel(new OnboardingService(settings),
                    new OnboardingViewModel.Registry([new Step("one"), new Step("two")]));
                var window = new OnboardingWindow(vm) { Width = 760, Height = 540 };
                windows.Add(window);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Directory.CreateDirectory("/tmp/opencode/phaket-render");
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine("/tmp/opencode/phaket-render", $"60-onboarding-{language}-steps.png"), PngBitmapEncoderOptions.Default);

                // Empty registry = the page a skeleton-only build shows.
                var skeleton = new OnboardingViewModel(new OnboardingService(settings));
                window.DataContext = skeleton;
                Dispatcher.UIThread.RunJobs();
                using var empty = window.CaptureRenderedFrame();
                empty?.Save(Path.Combine("/tmp/opencode/phaket-render", $"61-onboarding-{language}-empty.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { loc.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void Window_manager_opens_once_and_runs_the_follow_up_after_close()
    {
        var service = new OnboardingService(settings);
        int created = 0;
        bool followUp = false;
        var manager = new OnboardingWindowManager(() =>
        {
            created++;
            return new OnboardingViewModel(service);
        });

        manager.Show(() => followUp = true);
        Assert.True(manager.IsOpen);
        manager.Show();
        Assert.Equal(1, created);
        Assert.False(followUp);

        // The shell going away takes the wizard with it and releases the follow-up.
        manager.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(followUp);
        Assert.False(manager.IsOpen);
    }

    [AvaloniaFact]
    public void System_window_close_skips_onboarding_and_opens_the_shell_once()
    {
        var service = new OnboardingService(settings);
        var flow = new TrackingFlow();
        var vm = new OnboardingViewModel(service, flow);
        int shellOpens = 0;
        var manager = new OnboardingWindowManager(() => vm);
        try
        {
            manager.Show(() =>
            {
                Assert.True(Marker());
                shellOpens++;
            });
            var window = manager.CurrentWindow!;

            // The title-bar X raises Closed without any wizard button or command.
            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(Marker());
            Assert.False(vm.IsFirstRun());
            Assert.Equal(1, shellOpens);
            Assert.Equal(0, flow.LeaveCalls);
            Assert.Equal(0, flow.ResetCalls);
            Assert.False(manager.IsOpen);

            window.Close();
            manager.Close();
            Assert.Equal(1, shellOpens);
        }
        finally { manager.Close(); }
    }

    private sealed class TrackingFlow : IOnboardingFlow
    {
        public int LeaveCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public IReadOnlyList<IOnboardingStep> Steps { get; } = [new Step("one")];
        public Task ActivateAsync(IOnboardingStep step) => Task.CompletedTask;
        public Task LeaveAsync(IOnboardingStep step)
        {
            LeaveCalls++;
            return Task.CompletedTask;
        }
        public void Reset() => ResetCalls++;
    }

    private sealed class Step(string id) : IOnboardingStep
    {
        public string StepId => id;
        public string Title => id;
        public string Description => id;
    }
}