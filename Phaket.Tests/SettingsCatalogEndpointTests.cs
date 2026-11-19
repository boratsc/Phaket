using Avalonia.Headless.XUnit;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// The Steam Web API key is gone (the library is read from local Steam files);
// settings.json now also carries the catalog endpoint, merged with foreign nodes.
public class SettingsCatalogEndpointTests
{
    private static (SettingsViewModel Vm, string Settings, string Directory) Create(string? initial = null)
    {
        string dir = System.IO.Directory.CreateTempSubdirectory("phaket-settings-").FullName;
        string path = Path.Combine(dir, "settings.json");
        if (initial is not null) File.WriteAllText(path, initial);
        return (new SettingsViewModel(path), path, dir);
    }

    [AvaloniaFact]
    public async Task Saves_and_reloads_the_endpoint_and_keeps_other_settings()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        var (vm, path, dir) = Create("""{ "language": "en", "tools": { "legendary": { "version": "0.20.0" } } }""");
        try
        {
            Assert.Equal(CatalogSettings.DefaultEndpoint, vm.CatalogEndpoint);
            vm.CatalogEndpoint = "https://catalog.example.test/";
            vm.SaveCatalogEndpointCommand.Execute(null);
            Assert.Equal(LocalizationService.Instance["Settings.Catalog.Saved"], vm.CatalogEndpointStatus);
            string json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("catalog.example.test", json);
            // Merge, not replace: the tool pins written by Core/Tools survive.
            Assert.Contains("legendary", json);
            Assert.Equal("https://catalog.example.test/", new SettingsViewModel(path).CatalogEndpoint);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            System.IO.Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void Invalid_endpoint_is_reported_and_not_written()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        var (vm, path, dir) = Create();
        try
        {
            vm.CatalogEndpoint = "catalog.example.test";
            vm.SaveCatalogEndpointCommand.Execute(null);
            Assert.Equal(LocalizationService.Instance["Settings.Catalog.Invalid"], vm.CatalogEndpointStatus);
            Assert.False(File.Exists(path));

            // A language switch must not save the rejected address either.
            vm.SelectedLanguage = vm.Languages.First(x => x != vm.SelectedLanguage);
            Assert.DoesNotContain("catalog.example.test", File.ReadAllText(path));
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            System.IO.Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void Corrupt_settings_fall_back_to_the_local_default()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        var (vm, _, dir) = Create("{ not json");
        try { Assert.Equal(CatalogSettings.DefaultEndpoint, vm.CatalogEndpoint); }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            System.IO.Directory.Delete(dir, true);
        }
    }
}
