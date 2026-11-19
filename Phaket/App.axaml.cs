using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Phaket.Core.Accounts;
using Phaket.Services;

namespace Phaket;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var keyStore = new DeviceKeyStore(SecretStoreFactory.Create(), CatalogSettings.DefaultSettingsPath);
            var identity = new DeviceIdentityServices(keyStore, new DeviceKeySigner(keyStore));
            DeviceIdentityServices.SetCurrent(identity);
            var accounts = new AccountServices();
            var settings = new ViewModels.SettingsViewModel();
            // The shell owns the one and only profile publisher (Accounts.ProfilePublisher);
            // building another one here would leak a second HttpClient for the same identity.
            desktop.MainWindow = new MainWindow(new ViewModels.MainWindowViewModel(accounts, settings, signer: identity.CommunitySigner));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
