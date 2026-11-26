using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

// Built once in App and shared by the shell and the settings page, so both see the same
// start/restart decisions. Labels read the localization keys live, so a language step
// (PAK-134) changes them without rebuilding anything. Next and Finish confirm the current
// step (the flow saves it in LeaveAsync); Skip and closing do not.
public sealed partial class OnboardingViewModel : ViewModelBase
{
    // Empty-registry fixture: one calm page saying everything is optional (tests keep it).
    public sealed class PlaceholderStep : ObservableObject, IOnboardingStep
    {
        public string StepId => "placeholder";
        public string Title => LocalizationService.Instance["Onboarding.Placeholder.Title"];
        public string Description => LocalizationService.Instance["Onboarding.Placeholder.Description"];
    }

    public sealed class Registry : IOnboardingFlow
    {
        private readonly List<IOnboardingStep> steps;

        public Registry(IEnumerable<IOnboardingStep>? steps = null) => this.steps = steps?.ToList() ?? [];

        public IReadOnlyList<IOnboardingStep> Steps => steps;

        public Task ActivateAsync(IOnboardingStep step) => Task.CompletedTask;
        public Task LeaveAsync(IOnboardingStep step) => Task.CompletedTask;
        public void Reset() { }
    }

    // PAK-134: the registry is built in App and shared with the shell, so a step that saves
    // its own state is confirmed and reset by the wizard itself, not by the first-run marker.
    public static OnboardingService CreateService(string settingsPath) => new(settingsPath);

    private readonly OnboardingService service;
    private readonly IOnboardingFlow flow;
    private readonly System.ComponentModel.PropertyChangedEventHandler localizationChanged;
    private int index;

    public OnboardingViewModel() : this(CreateService(OnboardingService.DefaultSettingsPath)) { }

    public OnboardingViewModel(OnboardingService service, IOnboardingFlow? flow = null)
    {
        this.service = service;
        this.flow = flow ?? new Registry();
        localizationChanged = (_, e) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            // The counter is a format string too; re-read it in the same turn, before the
            // asynchronous Item[] notification reaches any bindings.
            if (e.PropertyName == "Item[]") StepCounterText = Counter();
        };
        LocalizationService.Instance.PropertyChanged += localizationChanged;
        ApplyStep();
    }

    public IOnboardingFlow Flow => flow;

    [ObservableProperty] private IOnboardingStep? currentStep;
    [ObservableProperty] private int stepNumber = 1;
    [ObservableProperty] private int stepCount = 1;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoNext;
    [ObservableProperty] private string stepCounterText = "";

    public bool HasStepCounter => StepCount > 1;
    public string Title => LocalizationService.Instance["Onboarding.Title"];
    public string Subtitle => LocalizationService.Instance["Onboarding.Subtitle"];

    // confirm: Next/Finish, which keeps the current step's choice; skipping and closing do not.
    private void LeaveFlow(bool confirm)
    {
        if (confirm && flow.Steps.Count > 0) _ = flow.LeaveAsync(CurrentStep!);
        // Re-arming every step's preset, so a restarting wizard shows the new saved state.
        if (confirm) flow.Reset();
        service.Complete();
        VisibilityChanged?.Invoke(this, false);
    }

    // Raised by the settings entry and when the flow ends; the window layer subscribes once.
    public event EventHandler<bool>? VisibilityChanged;

    public bool IsFirstRun() => !service.IsCompleted();

    public void TryOpen()
    {
        if (IsFirstRun()) VisibilityChanged?.Invoke(this, true);
    }

    // Settings entry: forgetting the marker is what makes the next start show the wizard.
    [RelayCommand]
    private void Restart()
    {
        if (flow.Steps.Count > 0) index = 0;
        flow.Reset();
        service.Reset();
        ApplyStep();
        VisibilityChanged?.Invoke(this, true);
    }

    // Every step shows on arrival; a step that loads state (basics: detection) does it here.
    private void Activate()
    {
        ApplyStep();
        if (CurrentStep is not null) _ = flow.ActivateAsync(CurrentStep);
    }

    private void ApplyStep()
    {
        var steps = flow.Steps;
        // An empty registry still presents one placeholder page, so Finish always works.
        StepCount = Math.Max(1, steps.Count);
        StepNumber = steps.Count == 0 ? 1 : index + 1;
        CurrentStep = steps.Count == 0 ? new PlaceholderStep() : steps[index];
        CanGoBack = index > 0;
        CanGoNext = steps.Count > 0 && index < steps.Count - 1;
        StepCounterText = Counter();
        OnPropertyChanged(nameof(HasStepCounter));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    private string Counter() => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        LocalizationService.Instance["Onboarding.StepCounter"], StepNumber, StepCount);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        index--;
        Activate();
    }

    // Leaving a step through Next keeps its choice (the flow saves it in LeaveAsync).
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        _ = flow.LeaveAsync(CurrentStep!);
        index++;
        Activate();
    }

    // Skipping is leaving a step without acting on it: nothing blocks the flow, it lands on
    // the last page, and no step's choice is saved (Finish there would count as confirming).
    [RelayCommand]
    private void Skip()
    {
        if (flow.Steps.Count > 0 && index < flow.Steps.Count - 1)
        {
            index = flow.Steps.Count - 1;
            Activate();
            return;
        }
        LeaveFlow(false);
    }

    [RelayCommand]
    private void Finish() => LeaveFlow(true);

    // Closing the window is a skip: closing the app mid-wizard must not ask again next start.
    [RelayCommand]
    private void Cancel() => LeaveFlow(false);
}