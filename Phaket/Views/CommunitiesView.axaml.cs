using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Phaket.ViewModels;

namespace Phaket.Views;

public partial class CommunitiesView : UserControl
{
    public CommunitiesView() => AvaloniaXamlLoader.Load(this);

    private void OnCommunitySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CommunitiesViewModel vm && vm.IsCompactLayout && vm.SelectedCommunity is not null)
            vm.SelectCommunityDetailsCommand.Execute(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not CommunitiesViewModel vm) return;
        if (vm.IsChatOpen) vm.CloseChatCommand.Execute(null);
        else if (vm.IsCreateDialogOpen || vm.IsJoinDialogOpen || vm.IsJoinCodeDialogOpen)
            vm.CloseCommunityDialogCommand.Execute(null);
        e.Handled = true;
    }
}
