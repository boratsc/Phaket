using Avalonia.Headless.XUnit;
using Phaket.Core.Nexus;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// The "no credential yet" state of the Nexus settings must be readable in both languages and
// must not touch the network: the real NexusService refuses locally with NEXUS_API_KEY_MISSING.
public sealed class E2ENexusSettingsTests
{
    [AvaloniaFact]
    public async Task Checking_without_a_saved_key_reports_the_missing_key_in_pl_and_en()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        var root = Directory.CreateTempSubdirectory("phaket-e2e-nexus-settings-").FullName;
        try
        {
            var secrets = new Phaket.Core.Tests.InMemorySecretStore();
            var nexus = new NexusService(new NexusApiKeyStore(() => secrets), appDataRoot: root);
            var settings = new SettingsViewModel(Path.Combine(root, "settings.json"),
                new SettingsViewModel.SecretStoreSeam(secrets, root), nexusService: nexus);
            settings.SelectSectionCommand.Execute("nexus");
            Assert.True(settings.IsNexusSection);

            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            await settings.CheckNexusKeyCommand.ExecuteAsync(null);
            var pl = settings.NexusStatusText;
            Assert.Equal(LocalizationService.Instance["Nexus.Error.KeyMissing"], pl);
            Assert.Contains("klucz", pl, StringComparison.OrdinalIgnoreCase);

            LocalizationService.Instance.CurrentLanguage = "en";
            await settings.CheckNexusKeyCommand.ExecuteAsync(null);
            var en = settings.NexusStatusText;
            Assert.Equal(LocalizationService.Instance["Nexus.Error.KeyMissing"], en);
            Assert.Contains("API key", en, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(pl, en);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }
}