using Avalonia.Controls;
using Phaket.ViewModels;

namespace Phaket.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // Release tags are fetched only when the version combo is opened, so the settings
    // page stays usable when GitHub is unreachable.
    private static void OnToolDropDownOpened(object? sender, EventArgs e)
    {
        if (((ComboBox)sender!).DataContext is ToolSettingsSection tool) tool.OnDropDownOpened();
    }
}
