using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Controls.Platform;

namespace Phaket;

public partial class MainWindow : Window
{
    private readonly Views.OnboardingWindowManager onboardingWindow;

    public MainWindow() : this(new ViewModels.MainWindowViewModel()) { }

    internal MainWindow(ViewModels.MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        onboardingWindow = new Views.OnboardingWindowManager(() => viewModel.Onboarding);
        viewModel.Onboarding.VisibilityChanged += OnOnboardingVisibilityChanged;
        Closed += (_, _) =>
        {
            viewModel.Onboarding.VisibilityChanged -= OnOnboardingVisibilityChanged;
            onboardingWindow.Close();
            (DataContext as IDisposable)?.Dispose();
        };
        SizeChanged += (_, args) =>
        {
            double width = args.NewSize.Width;
            viewModel.IsNarrowSidebar = width < 980;
            viewModel.IsIconSidebar = width < 740;
            if (this.FindControl<Grid>("ShellRoot") is { } shell && shell.ColumnDefinitions.Count > 0)
            {
                shell.Classes.Set("sidebarNarrow", width < 980);
                shell.Classes.Set("sidebarIcon", width < 740);
                shell.ColumnDefinitions[0].Width = new GridLength(width < 740 ? 58 : width < 980 ? 192 : 231);
            }
            if (this.FindControl<Grid>("TopbarGrid") is { } topbar && topbar.ColumnDefinitions.Count > 0)
                topbar.ColumnDefinitions[0].Width = new GridLength(width < 740 ? 58 : width < 980 ? 192 : 231);
            if (this.FindControl<ComboBox>("GamePicker") is { } picker)
                picker.Width = width < 740 ? 44 : double.NaN;
        };
        if (OperatingSystem.IsWindows())
        {
            WindowDecorations = WindowDecorations.None;
            if (this.FindControl<Control>("WindowControls") is { } controls) controls.IsVisible = true;
        }
        viewModel.IsNarrowSidebar = Width < 980;
        viewModel.IsIconSidebar = Width < 740;
        if (this.FindControl<Grid>("ShellRoot") is { } shell)
        {
            shell.Classes.Set("sidebarNarrow", viewModel.IsNarrowSidebar);
            shell.Classes.Set("sidebarIcon", viewModel.IsIconSidebar);
            shell.ColumnDefinitions[0].Width = new GridLength(Width < 740 ? 58 : Width < 980 ? 192 : 231);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Settings asks for the wizard again (PAK-133); the shell stays behind it.
    private void OnOnboardingVisibilityChanged(object? sender, bool open)
    {
        if (open) onboardingWindow.Show();
    }

    private void OnDragRegionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsWindows() && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnToggleMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
