using Avalonia.Controls;

namespace Phaket;

public partial class MainWindow : Window
{
    public MainWindow() : this(new ViewModels.MainWindowViewModel()) { }

    internal MainWindow(ViewModels.MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
