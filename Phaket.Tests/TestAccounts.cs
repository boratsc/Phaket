using Phaket.Core.Accounts;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

internal static class TestAccounts
{
    internal static AccountServices Create() => new(new InMemorySecretStore(), TempPath());
    internal static string TempPath() => Path.Combine(Path.GetTempPath(), "phaket-test-" + Guid.NewGuid().ToString("N"));
    internal static MainWindowViewModel ViewModel()
    {
        string directory = TempPath();
        return new MainWindowViewModel(new AccountServices(new InMemorySecretStore(), directory),
            new SettingsViewModel(Path.Combine(directory, "settings.json")));
    }
}
