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
        var store = new InMemorySecretStore();
        string directory = TempPath();
        return new MainWindowViewModel(new AccountServices(store, directory),
            new SettingsViewModel(new SteamApiKeyStore(Path.Combine(directory, "steam-apikey.bin"), store), Path.Combine(directory, "settings.json")));
    }
}
