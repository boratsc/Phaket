using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phaket.Views;

// Built once in App: the window only renders the shared view model.
public partial class OnboardingWindow : Window
{
    private bool cancelHandled;

    public OnboardingWindow()
    {
        InitializeComponent();
        Closed += (_, _) => Cancel();
    }

    public OnboardingWindow(ViewModels.OnboardingViewModel viewModel) : this() => DataContext = viewModel;

    // Both the window chrome and an explicit close action are a skip: the app must not ask
    // again on the next start. The flag makes a close following Cancel idempotent.
    private void Cancel()
    {
        if (cancelHandled) return;
        cancelHandled = true;
        ((ViewModels.OnboardingViewModel)DataContext!).CancelCommand.Execute(null);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Cancel();
        Close();
    }
}