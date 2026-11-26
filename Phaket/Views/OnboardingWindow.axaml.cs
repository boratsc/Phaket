using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phaket.Views;

// Built once in App: the window only renders the shared view model.
public partial class OnboardingWindow : Window
{
    public OnboardingWindow() => InitializeComponent();

    public OnboardingWindow(ViewModels.OnboardingViewModel viewModel) : this() => DataContext = viewModel;

    // Closing the window is a skip: the app must not ask again on the next start.
    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        ((ViewModels.OnboardingViewModel)DataContext!).CancelCommand.Execute(null);
}