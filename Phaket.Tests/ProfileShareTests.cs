using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using System.Text.Json;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

public sealed class ProfileShareTests
{
    [Theory]
    [InlineData("phaket://profile/clean-game/1", true)]
    [InlineData("phaket://profile/valheim-qol/42", true)]
    [InlineData("phaket://profile/Valheim/1", false)]
    [InlineData("phaket://profile/a/0", false)]
    [InlineData("https://example.test/profile/a/1", false)]
    [InlineData("phaket://profile/a/1?redirect=x", false)]
    public void Phaket_profile_links_are_strictly_validated(string value, bool valid)
    {
        Assert.Equal(valid, ProfileLink.TryParse(value, out var parsed));
        if (valid) Assert.Equal(value, parsed!.ToUri());
    }

    [AvaloniaFact]
    public void Invalid_and_offline_imports_show_errors_without_saving()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        try
        {
            var game = new GameCatalogItem(new GameDefinition("fixture-game", []), "", "");
            using var client = new HttpClient(new HttpStatusHandler(HttpStatusCode.ServiceUnavailable));
            var vm = new ProfilesViewModel(game, () => { }, root, new HttpProfileSnapshotClientSeam(client));
            vm.ImportProfileCommand.Execute(null);
            vm.ImportLink = "not a phaket link";
            vm.PreviewImportCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(LocalizationService.Instance["Profiles.ImportInvalid"], vm.StatusMessage);
            Assert.False(vm.HasImportPreview);

            vm.ImportLink = "phaket://profile/shared-pack/3";
            vm.PreviewImportCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(LocalizationService.Instance["Profiles.ImportOffline"], vm.StatusMessage);
            Assert.False(vm.HasImportPreview);
            Assert.DoesNotContain(new ProfileStore(root).For("fixture-game").Profiles, p => p.Name.Contains("shared", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Offline_share_builds_link_and_copy_command_reports_clipboard_result()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        try
        {
            var store = new ProfileStore(root);
            var profile = store.AddProfile("fixture-game", "Shared pack", [new ModEntry { Name = "Shared mod", Version = "1" }]);
            var game = new GameCatalogItem(new GameDefinition("fixture-game", []), "", "");
            var vm = new ProfilesViewModel(game, () => { }, root);
            vm.SelectCommand.Execute(vm.Profiles.Single(item => item.Id == profile.Id));
            vm.ShareProfileCommand.Execute(null);
            Assert.Equal("phaket://profile/shared-pack/1", vm.ShareLink);
            Assert.Contains(LocalizationService.Instance["Profiles.ShareOffline"], vm.ShareStatus);
            Assert.True(vm.IsToastVisible);
            await vm.CopyShareLinkCommand.ExecuteAsync(null);
            Assert.Equal(LocalizationService.Instance["Profiles.LinkCopyFailed"], vm.ShareStatus);
            Assert.True(vm.IsToastVisible);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = language;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Import_preview_validates_server_payload_and_does_not_save_until_confirmed()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        var oldLanguage = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        try
        {
            using var client = new HttpClient(new SnapshotHandler());
            var snapshot = new HttpProfileSnapshotClientSeam(client);
            var game = new GameCatalogItem(new GameDefinition("fixture-game", []), "", "");
            var vm = new ProfilesViewModel(game, () => { }, root, snapshot);
            vm.ImportProfileCommand.Execute(null);
            vm.ImportLink = "phaket://profile/shared-pack/3";
            vm.PreviewImportCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.True(vm.HasImportPreview);
            Assert.Single(vm.Diff);
            Assert.DoesNotContain(new ProfileStore(root).For("fixture-game").Profiles, p => p.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase));
            vm.ConfirmImportCommand.Execute(null);
            Assert.Contains(new ProfileStore(root).For("fixture-game").Profiles, p => p.Name == "Shared pack (import)");
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = oldLanguage;
            Directory.Delete(root, true);
        }
    }

    private sealed class HttpStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class HttpProfileSnapshotClientSeam(HttpClient client) : IProfileSnapshotClient
    {
        public async Task<JsonDocument> FetchAsync(ProfileLink link, CancellationToken cancellationToken = default)
        {
            using var response = await client.GetAsync($"http://fixture/packages/{link.Slug}/versions/{link.Version}", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
    }

    private sealed class SnapshotHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"gameId":"fixture-game","name":"Shared pack","snapshot":{"schemaVersion":1,"items":[{"modName":"Shared mod","modVersion":"1.2.0","source":"github"}]}}""", Encoding.UTF8, "application/json"),
            });
    }
}
