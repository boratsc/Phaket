namespace Phaket.ViewModels;

// PAK-134: the flow App composes. It owns the step list, so a step's own state is saved
// when Next/Finish confirms it and reset when the wizard is restarted; parts 135/136 append
// their step here without touching the shell.
public sealed class LanguageStepActions : IOnboardingFlow
{
    private readonly List<IOnboardingStep> steps;

    public LanguageStepActions(LanguageStepViewModel language) => steps = [language];

    public IReadOnlyList<IOnboardingStep> Steps => steps;

    public Task ActivateAsync(IOnboardingStep step) => Task.CompletedTask;

    // Only Next/Finish reach this; Skip, close and Restart leave the choice unconfirmed.
    public Task LeaveAsync(IOnboardingStep step)
    {
        if (step is LanguageStepViewModel language) language.Save();
        return Task.CompletedTask;
    }

    public void Reset()
    {
        foreach (var step in steps)
            if (step is LanguageStepViewModel language) language.Reset();
    }

    // The first-run marker itself is written by OnboardingService.
}
