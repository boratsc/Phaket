using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.ViewModels;

namespace Phaket.Views;

// The "which version do you keep?" dialog (PAK-142). Focus moves to the first choice when it
// opens, and Escape cancels the install, like every modal.
public partial class ConflictDecisionDialog : UserControl
{
    private ConflictDecisionViewModel? viewModel;

    public ConflictDecisionDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (viewModel is not null) viewModel.PropertyChanged -= OnViewModelChanged;
        viewModel = DataContext as ConflictDecisionViewModel;
        if (viewModel is not null) viewModel.PropertyChanged += OnViewModelChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusChoices();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || viewModel?.IsOpen != true) return;
        e.Handled = true;
        viewModel.Dismiss();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConflictDecisionViewModel.IsOpen)) FocusChoices();
    }

    private void FocusChoices() => Dispatcher.UIThread.Post(() =>
    {
        if (viewModel?.IsOpen != true) return;
        this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyVisible)?.Focus();
    });
}