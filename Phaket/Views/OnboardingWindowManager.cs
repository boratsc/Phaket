using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phaket.Views;

// Shows the first-run wizard (PAK-133) in its own window before the shell and again when
// the settings page asks for a restart. Window, not overlay: the wizard is the whole process
// start, so the shell's pages (catalog, library scan) never run behind it.
public sealed class OnboardingWindowManager
{
    private readonly Func<ViewModels.OnboardingViewModel> createViewModel;
    private Action? onClosed;
    private Window? window;

    public OnboardingWindowManager(Func<ViewModels.OnboardingViewModel> createViewModel) =>
        this.createViewModel = createViewModel;

    public bool IsOpen => window is not null;

    // Never leave a wizard window behind the shell that owns it.
    public void Close() => window?.Close();

    // then: what to show once the wizard is gone (the shell, on a first start).
    public void Show(Action? then = null)
    {
        if (window is not null) return;
        var viewModel = createViewModel();
        window = new OnboardingWindow(viewModel);
        window.Closed += (_, _) =>
        {
            window = null;
            var next = onClosed;
            onClosed = null;
            next?.Invoke();
        };
        onClosed = then;
        window.Show();
    }
}