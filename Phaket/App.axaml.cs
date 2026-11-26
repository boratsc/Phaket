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
            // The only place wizard steps are registered; the shell receives this shared view model.
            var languageStep = new ViewModels.LanguageStepViewModel(ViewModels.SettingsViewModel.DefaultSettingsPath);
            // PAK-135: the platforms step reuses the accounts' own services (Steam/Epic sign-in,
            // Nexus key store in the same app data directory), so the wizard and Settings always
            // see and write the same secrets.
            var platformsStep = new ViewModels.PlatformsStepViewModel(SecretStoreFactory.Create(), accounts.Steam, accounts.Epic);
            var onboardingFlow = new ViewModels.OnboardingFlow(languageStep, platformsStep);
            var onboarding = new ViewModels.OnboardingViewModel(
                ViewModels.OnboardingViewModel.CreateService(OnboardingService.DefaultSettingsPath),
                onboardingFlow);
            var onboardingWindow = new Views.OnboardingWindowManager(() => onboarding);
            settings.Onboarding = onboarding;
            onboarding.VisibilityChanged += (_, open) =>
            {
                if (open) onboardingWindow.Show();
            };
            // The shell owns the one and only profile publisher (Accounts.ProfilePublisher);
            // building another one here would leak a second HttpClient for the same identity.
            var shell = new MainWindow(new ViewModels.MainWindowViewModel(accounts, settings, signer: identity.CommunitySigner, onboarding: onboarding));
            // First start: the wizard comes first; any exit out of it (finish, skip, close)
            // writes onboardingCompleted and then opens the shell.
            if (onboarding.IsFirstRun()) onboardingWindow.Show(() => LaunchShell(shell));
            else LaunchShell(shell);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void LaunchShell(MainWindow shell)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.MainWindow = shell;
        shell.Show();
    }
}
