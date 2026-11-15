using Avalonia.Headless.XUnit;
using Phaket.Core.Accounts;
using Phaket.ViewModels;
using Phaket.Core.Tests;

namespace Phaket.Tests;

public class SettingsSteamKeyTests
{
    [AvaloniaFact] public async Task SavesAndReloadsEncryptedKeyOutsideSettingsJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phaket-test-" + Guid.NewGuid());
        var language = Phaket.Services.LocalizationService.Instance.CurrentLanguage;
        Directory.CreateDirectory(dir);
        try
        {
            string keyPath = Path.Combine(dir, "steam-apikey.bin"), settings = Path.Combine(dir, "settings.json");
            var memory = new InMemorySecretStore();
            var vm = new SettingsViewModel(new SteamApiKeyStore(keyPath, memory), settings) { SteamApiKey = "super-secret-key" };
            await vm.SaveSteamApiKeyCommand.ExecuteAsync(null);
            Assert.False(File.Exists(keyPath));
            vm.SelectedLanguage = vm.Languages.First(x => x != vm.SelectedLanguage);
            Assert.DoesNotContain("super-secret-key", await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken));
            Assert.Equal("super-secret-key", new SettingsViewModel(new SteamApiKeyStore(keyPath, memory), settings).SteamApiKey);
        }
        finally
        {
            // Switching the UI language is global state: leaving it on "en" makes later
            // Polish assertions in other classes flaky on reruns.
            Phaket.Services.LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(dir, true);
        }
    }
}
