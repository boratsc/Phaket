namespace Phaket.ViewModels;

// PAK-135/136: the flow App composes. It owns the step list, so a step's own state is saved
// when Next/Finish confirms it and reset when the wizard is restarted.
public sealed class OnboardingFlow : IOnboardingFlow
{
    private readonly List<IOnboardingStep> steps;

    public OnboardingFlow(LanguageStepViewModel language, PlatformsStepViewModel platforms, BasicsStepViewModel basics) =>
        steps = [language, platforms, basics];

    public IReadOnlyList<IOnboardingStep> Steps => steps;

    // The basics page starts its read-only detection here; Next never waits for it.
    public Task ActivateAsync(IOnboardingStep step)
    {
        if (step is BasicsStepViewModel basics) basics.StartScan();
        return Task.CompletedTask;
    }

    // Only Next/Finish reach this; Skip, close and Restart leave the choice unconfirmed.
    public Task LeaveAsync(IOnboardingStep step)
    {
        if (step is LanguageStepViewModel language) language.Save();
        if (step is BasicsStepViewModel basics) basics.Save();
        return Task.CompletedTask;
    }

    public void Reset()
    {
        foreach (var step in steps)
        {
            if (step is LanguageStepViewModel language) language.Reset();
            if (step is PlatformsStepViewModel platforms) platforms.Reset();
            if (step is BasicsStepViewModel basics) basics.Reset();
        }
    }

    // The first-run marker itself is written by OnboardingService.
}
