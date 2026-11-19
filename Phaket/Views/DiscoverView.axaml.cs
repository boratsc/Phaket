using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Phaket.ViewModels;

namespace Phaket.Views;

public partial class DiscoverView : UserControl
{
    public DiscoverView() => AvaloniaXamlLoader.Load(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is DiscoverViewModel vm)
        {
            vm.SearchText = "";
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
