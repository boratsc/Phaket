using System.Text.Json;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// settings.json is written by three independent features (theme, device nick, preferences).
// They must share one read-modify-write gate, otherwise a concurrent save drops the other
// feature's key, and none of them may replace a malformed document with a partial one.
public sealed class SettingsGateTests
{
    private static (string Root, string Path, ThemeService Theme, DeviceKeyStore Keys, SettingsViewModel Settings) Create(
        string initial)
    {
        string root = TestAccounts.TempPath();
        string path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, initial);

        return (root, path, new ThemeService(path),
            new DeviceKeyStore(new InMemorySecretStore(), path),
            new SettingsViewModel(path, new SettingsViewModel.SecretStoreSeam(new InMemorySecretStore(), root)));
    }

    [Fact]
    public async Task Concurrent_writers_keep_every_key_and_leave_valid_json()
    {
        // The stored language is written as the one already active: a test must not move the
        // shared LocalizationService singleton, other classes assert their own language.
        string language = LocalizationService.Instance.CurrentLanguage;
        var (root, path, theme, keys, settings) = Create(
            $$"""{ "language": "{{language}}", "tools": { "legendary": { "version": "0.20.0" } } }""");
        try
        {
            settings.CatalogEndpoint = "https://gate.example.test/";
            // The theme writer stays on this thread: ThemeService touches Application.Current,
            // which the headless UI thread owns. Contention is still three-way.
            var writers = new[]
            {
                Task.Run(async () =>
                {
                    for (int i = 0; i < 25; i++) await keys.SaveNickAsync("Device " + i, TestContext.Current.CancellationToken);
                }, TestContext.Current.CancellationToken),
                Task.Run(() =>
                {
                    for (int i = 0; i < 25; i++) settings.SaveCatalogEndpointCommand.Execute(null);
                }, TestContext.Current.CancellationToken)
            };
            for (int i = 0; i < 25; i++) Assert.True(theme.SetTheme(i % 2 == 0 ? "dark" : "light"));
            await Task.WhenAll(writers);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            JsonElement stored = document.RootElement;
            Assert.Equal(language, stored.GetProperty("language").GetString());
            Assert.Equal("0.20.0", stored.GetProperty("tools").GetProperty("legendary").GetProperty("version").GetString());
            Assert.Equal("https://gate.example.test/", stored.GetProperty("catalogEndpoint").GetString());
            Assert.StartsWith("Device ", stored.GetProperty("deviceNick").GetString());
            Assert.Contains(stored.GetProperty("theme").GetString(), new[] { "dark", "light" });
            // Atomic replacement leaves no half-written sibling behind.
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Malformed_settings_are_rejected_by_every_writer()
    {
        const string corrupt = "{ not json";
        var (root, path, theme, keys, settings) = Create(corrupt);
        try
        {
            Assert.False(theme.SetTheme("light"));

            settings.CatalogEndpoint = "https://gate.example.test/";
            settings.SaveCatalogEndpointCommand.Execute(null);
            Assert.Equal(LocalizationService.Instance["Settings.Catalog.SaveFailed"], settings.CatalogEndpointStatus);

            await Assert.ThrowsAsync<InvalidDataException>(() => keys.SaveNickAsync("Device", TestContext.Current.CancellationToken));

            Assert.Equal(corrupt, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
