using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Phaket.Views;

public partial class EmptyStateView : UserControl
{
    public EmptyStateView() => AvaloniaXamlLoader.Load(this);
}
