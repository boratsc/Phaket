using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phaket.ViewModels;

namespace Phaket.Views;

// Focus follows the prompt: a dialog that opens without keyboard focus strands screen
// reader and keyboard users behind the dimmed window. Escape cancels, like every modal.
public partial class SteamPromptDialog : UserControl
{
    private SteamPromptViewModel? prompt;

    public SteamPromptDialog()
    {
        InitializeComponent();
        // Tunnel so Escape cancels even when a focused TextBox would swallow it.
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (prompt is not null) prompt.PropertyChanged -= OnPromptChanged;
        prompt = DataContext as SteamPromptViewModel;
        if (prompt is not null) prompt.PropertyChanged += OnPromptChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusPrompt();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || prompt?.HasDialog != true) return;
        e.Handled = true;
        prompt.CancelDialogCommand.Execute(null);
    }

    private void OnPromptChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SteamPromptViewModel.CredentialOpen) or nameof(SteamPromptViewModel.TwoFactorOpen)
            or nameof(SteamPromptViewModel.MobileConfirmationOpen))) return;
        FocusPrompt();
    }

    private void FocusPrompt() => Dispatcher.UIThread.Post(() =>
    {
        if (prompt?.CredentialOpen == true) PasswordBox.Focus();
        else if (prompt?.TwoFactorOpen == true) TwoFactorBox.Focus();
        else if (prompt?.MobileConfirmationOpen == true) MobileCancelButton.Focus();
    });
}