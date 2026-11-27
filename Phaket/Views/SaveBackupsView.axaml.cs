using Avalonia.Controls;
using Phaket.ViewModels;

namespace Phaket.Views;

public partial class SaveBackupsView : UserControl
{
    public SaveBackupsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is not SaveBackupsViewModel viewModel) return;
            // The list is read when the page loads; the refresh button is the user's command.
            if (viewModel.LoadCommand.CanExecute(null)) _ = LoadAsync(viewModel);
        };
    }

    // Errors are the view model's own status line; this only stops the startup task from
    // escaping unobserved when the page is left mid-load.
    private static async Task LoadAsync(SaveBackupsViewModel viewModel)
    {
        try { await viewModel.LoadCommand.ExecuteAsync(null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("phaket: save backup list failed: {0}", ex.Message);
        }
    }
}