using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System.ComponentModel;
using Phaket.ViewModels;

namespace Phaket.Views;

public partial class HelpView : UserControl
{
    private MainWindowViewModel? shell;

    public HelpView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        shell = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
        if (shell is not null) shell.PropertyChanged += OnShellPropertyChanged;
        SyncGame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (shell is not null) shell.PropertyChanged -= OnShellPropertyChanged;
        shell = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame)) SyncGame();
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => SyncGame();

    private void SyncGame()
    {
        if (shell is not null && DataContext is HelpViewModel viewModel) viewModel.Game = shell.SelectedGame;
    }
}
