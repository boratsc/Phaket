using Avalonia.Controls;

namespace Phaket.ViewModels;

// A step is a view model; its view arrives through the wizard's DataTemplates, so a step
// never needs code-behind and the VM stays testable without a window.
public interface IOnboardingStep
{
    string StepId { get; }
    string Title { get; }
    string Description { get; }
}

// Step registry contract: the wizard shell is built once and later parts (language,
// platforms, basics) append steps here without touching it.
public interface IOnboardingFlow
{
    IReadOnlyList<IOnboardingStep> Steps { get; }

    // Called when a step becomes current (load its state) and when the user leaves it.
    Task ActivateAsync(IOnboardingStep step);
    Task LeaveAsync(IOnboardingStep step);

    // Called by the wizard on restart, so every step re-arms its own state in one place.
    void Reset();
}
