using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;

namespace Phaket.ViewModels;

// Built once in App and shared by the shell and the settings page, so both see the same
// start/restart decisions. Labels read the localization keys live, so a language step
// (PAK-134) changes them without rebuilding anything.
public sealed partial class OnboardingViewModel : ViewModelBase
{
    // Nothing registered yet: one calm page saying everything is optional.
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
    }

    // PAK-134 extension point: the language step's own completion and reset hooks go here.
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
        localizationChanged = (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
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
        service.Reset();
        VisibilityChanged?.Invoke(this, true);
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
        StepCounterText = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.Instance["Onboarding.StepCounter"], StepNumber, StepCount);
        OnPropertyChanged(nameof(HasStepCounter));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        index--;
        ApplyStep();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        index++;
        ApplyStep();
    }

    // Skipping is leaving a step without acting on it: nothing blocks the flow.
    [RelayCommand]
    private void Skip()
    {
        if (CanGoNext) Next();
        else Finish();
    }

    [RelayCommand]
    private void Finish()
    {
        service.Complete();
        VisibilityChanged?.Invoke(this, false);
    }

    // Closing the window is a skip: closing the app mid-wizard must not ask again next start.
    [RelayCommand]
    private void Cancel()
    {
        service.Complete();
        VisibilityChanged?.Invoke(this, false);
    }
}