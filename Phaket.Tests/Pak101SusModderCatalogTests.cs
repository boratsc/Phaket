using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Phaket.Core.Catalog;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-101: SusModder rows in the mod browser — source filter, packaging/among badges,
// availability gating and the artifact variant shown for the selected target.
public sealed class Pak101SusModderCatalogTests
{
    private static ThunderstoreMod SusModder(string name, string externalId = "7") =>
        new("sus-" + name, "among-us", "SusModder", name, "SusModder-" + name, "Opis", "SusModder",
            "https://github.com/owner/" + name, null, false, DateTimeOffset.UnixEpoch,
            new ThunderstoreVersion("1.6.3b", "SusModder-" + name + "-1.6.3b", "", null, DateTimeOffset.UnixEpoch,
                [], Artifacts: [new("https://github.com/owner/release.zip", "x86", ["steam"], "a" + new string('0', 63), 42_000_000, "github", "release.zip")],
                Packaging: "full", AmongVersion: "ToU", Arch: "x86", Source: "susmodder",
                Availability: "available"),
            License: null, RepoAvailable: false, Packaging: "full", AmongVersion: "ToU", Source: "susmodder", ExternalId: externalId);

    private static ThunderstoreMod Thunderstore() =>
        new("ts", "among-us", "Owner", "Plain", "Owner-Plain", "Opis", "Owner", "https://thunderstore.io/p/plain",
            null, false, DateTimeOffset.UnixEpoch,
            new ThunderstoreVersion("2.0", "Owner-Plain-2.0", "https://example.com/plain.zip", 1024, DateTimeOffset.UnixEpoch, []));

    private sealed class Fake : ICatalogTransport
    {
        public required IReadOnlyList<ThunderstoreMod> Mods;
        public Task<CatalogResponse> GetAsync(string path, string? etag, CancellationToken cancellationToken)
        {
            object body = path.StartsWith("/mods/index") ? new { revision = 1, mods = Mods, nextCursor = (string?)null } :
                path.StartsWith("/mods/changes") ? new { revision = 1, reset = false, changes = Array.Empty<object>() } :
                new { mod = Mods[0], versions = Mods[0].Latest is var latest ? new[] { latest } : Array.Empty<ThunderstoreVersion>() };
            return Task.FromResult(new CatalogResponse(200, null, JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
    }

    private static async Task<ModCatalogViewModel> Loaded(string root, Fake fake)
    {
        var vm = new ModCatalogViewModel("among-us", root, fake, false);
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    private static async Task WaitForDetails(ModCatalogViewModel vm)
    {
        // Awaiting the lookup closes its SQLite store; polling for Details leaves the handle open
        // past the fixture cleanup on Windows.
        if (vm.PendingDetails is { } pending) await pending;
        Assert.NotNull(vm.Details);
    }

    [AvaloniaFact]
    public async Task Source_filter_keeps_both_catalogs_and_narrows_to_susmodder()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var vm = await Loaded(root, new Fake { Mods = [SusModder("Mira"), Thunderstore()] });
            Assert.Equal(2, vm.Mods.Count);
            vm.SetSourceFilterCommand.Execute("susmodder");
            Assert.True(vm.IsSusmodderSource);
            Assert.Equal("SusModder-Mira", Assert.Single(vm.Mods).FullName);
            vm.SetSourceFilterCommand.Execute("thunderstore");
            Assert.Equal("Owner-Plain", Assert.Single(vm.Mods).FullName);
            vm.SetSourceFilterCommand.Execute("all");
            Assert.Equal(2, vm.Mods.Count);
            vm.SearchText = "mira";
            Assert.Equal("SusModder-Mira", Assert.Single(vm.Mods).FullName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void SusModder_record_exposes_badges_and_availability()
    {
        var mod = SusModder("Mira");
        Assert.Equal("Catalog.Source.susmodder", mod.SourceKey);
        Assert.Equal("Packaging.Full", mod.PackagingKey);
        Assert.True(mod.HasPackaging);
        Assert.True(mod.IsFullPackage);
        Assert.True(mod.FollowsToU);
        Assert.False(mod.HasPinnedAmongVersion);
        Assert.True(mod.RepoUnavailable);
        Assert.True(mod.HasNoLicense);
        Assert.True(mod.Latest.IsAvailable);
        var missing = mod.Latest with { Availability = "unavailable", DownloadUrl = "" };
        Assert.True(missing.IsUnavailable);
        Assert.False(missing.IsAvailable);
        // An old cache without the availability field stays usable.
        Assert.True((mod.Latest with { Availability = null }).IsAvailable);
        var pinned = mod with { AmongVersion = "2026-9-29" };
        Assert.True(pinned.HasPinnedAmongVersion);
        Assert.False(pinned.FollowsToU);
    }

    [AvaloniaFact]
    public async Task Unavailable_version_stays_listed_but_cannot_start_a_plan()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var mod = SusModder("Mira") with
            {
                Latest = SusModder("Mira").Latest with { Availability = "unavailable", DownloadUrl = "" }
            };
            var vm = await Loaded(root, new Fake { Mods = [mod] });
            vm.SelectedMod = vm.Mods[0];
            await WaitForDetails(vm);
            var prepares = 0;
            vm.PrepareOverride = (_, _, _, target, _) =>
            {
                prepares++;
                return Task.FromResult(new InstallPreparation("among-us", mod.FullName, "1.6.3b", vm.Endpoint, target,
                    [], [], [], [], "", "", "", "", ""));
            };
            await vm.InstallVersionCommand.ExecuteAsync(vm.Versions.Single().Version);
            Assert.Equal(0, prepares);
            Assert.Equal(LocalizationService.Instance["ModInstall.VersionUnavailable"], vm.InstallStatus);
            Assert.False(vm.HasPreview);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // N3: the catalog button has its own gate, but BeginInstallAsync (the profile "Update"
    // path) has none — the service must refuse the version for both. No PrepareOverride here.
    [AvaloniaFact]
    public async Task Begin_install_refuses_an_unavailable_version_through_the_service()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var available = SusModder("Mira");
            var mod = available with { Latest = available.Latest with { Availability = "unavailable", DownloadUrl = "" } };
            var vm = await Loaded(root, new Fake { Mods = [mod] });

            await vm.BeginInstallAsync(mod.FullName, "1.6.3b", new InstallTarget("", "steam", true, "x86"));

            Assert.False(vm.HasPreview);
            Assert.Empty(vm.Preview);
            // The reason is the localized refusal and names the exact version.
            Assert.Contains("1.6.3b", vm.InstallStatus);
            Assert.NotEqual(LocalizationService.Instance["ModInstall.PrepareFailed"], vm.InstallStatus);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // N3: the profile list must not offer an update to a version the source no longer serves.
    [AvaloniaFact]
    public async Task Latest_version_skips_an_unavailable_row()
    {
        var root = TestAccounts.TempPath();
        var availableRoot = TestAccounts.TempPath();
        try
        {
            var available = SusModder("Mira");
            var unavailable = available with { Latest = available.Latest with { Availability = "unavailable" } };
            var vm = await Loaded(root, new Fake { Mods = [unavailable] });
            Assert.Null(vm.LatestVersion("SusModder-Mira"));

            // A cache from a previous snapshot must not poison the reloaded row.
            var reloaded = await Loaded(availableRoot, new Fake { Mods = [available] });
            Assert.Equal("1.6.3b", reloaded.LatestVersion("susmodder-mira"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(availableRoot)) Directory.Delete(availableRoot, true);
        }
    }

    [AvaloniaFact]
    public async Task Sandbox_target_reports_the_chosen_artifact_variant_per_version()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var vm = await Loaded(root, new Fake { Mods = [SusModder("Mira")] });
            vm.SelectedArch = "x86";
            vm.SelectedMod = vm.Mods[0];
            await WaitForDetails(vm);
            var row = Assert.Single(vm.Versions);
            Assert.True(row.IsAvailable);
            Assert.True(row.HasVariant);
            Assert.Contains("x86", row.VariantText);
            Assert.Contains("github", row.VariantText);
            Assert.Contains("MB", row.VariantText);
            Assert.False(row.VariantMissing);

            // x64 has no matching artifact in this catalog entry: the row explains the refusal
            // instead of hiding the version.
            vm.SelectedArch = "x64";
            Assert.True(Assert.Single(vm.Versions).VariantMissing);
            Assert.Single(vm.Versions);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void New_catalog_texts_exist_in_both_languages()
    {
        var loc = LocalizationService.Instance;
        string[] keys =
        [
            "Catalog.Source.susmodder", "Packaging.Label", "Packaging.Full", "Packaging.Dll", "Packaging.FullWarning",
            "AmongVersion.Game", "AmongVersion.ToU", "License.Label", "License.Unknown", "RepoUnavailable",
            "Catalog.Variant", "Catalog.VariantMissing", "Catalog.VariantProbed", "ModInstall.VersionUnavailable"
        ];
        foreach (var language in new[] { "pl-PL", "en" })
        {
            var set = loc.Keys(language);
            Assert.All(keys, key => Assert.Contains(key, set));
        }
        var previous = loc.CurrentLanguage;
        try
        {
            loc.CurrentLanguage = "pl-PL";
            Assert.Equal("SusModder", loc["Catalog.Source.susmodder"]);
            Assert.Equal("Pełny mod", loc["Packaging.Full"]);
            loc.CurrentLanguage = "en";
            Assert.Equal("Full mod", loc["Packaging.Full"]);
            Assert.Equal("Repository without a declared SPDX license", loc["License.Unknown"]);
        }
        finally { loc.CurrentLanguage = previous; }
    }
}

// PAK-101/PAK-103: a SusModder composition keeps its own source and publishes the
// contract item (source susmodder, full name, exact version, optional external id).
public sealed class Pak101SusModderCompositionTests
{
    private static ThunderstoreMod SusModder() =>
        new("sus-id", "among-us", "SusModder", "Mira", "SusModder-Mira", "Opis", "SusModder",
            "https://github.com/owner/mira", null, false, DateTimeOffset.UnixEpoch,
            new ThunderstoreVersion("1.6.3b", "SusModder-Mira-1.6.3b", "", null, DateTimeOffset.UnixEpoch, []),
            Packaging: "dll", AmongVersion: "ToU", Source: "susmodder", ExternalId: "Mira");

    [Fact]
    public void Adding_a_susmodder_mod_keeps_its_source_and_identity()
    {
        var root = TestAccounts.TempPath();
        try
        {
            var store = new ModpackStore(root);
            var pack = new ModpackDefinition { Name = "Pack", Author = "Author", GameId = "among-us", Version = "1" };
            store.Save(pack);
            var vm = new ModPackViewModel(pack, store, () => { }, root);
            vm.AddThunderstoreModCommand.Execute(SusModder());
            var item = Assert.Single(Assert.Single(store.ForGame("among-us")).Items);
            Assert.Equal("susmodder", item.Source);
            Assert.Equal("SusModder-Mira", item.Name);
            Assert.Equal("1.6.3b", item.Version);
            Assert.Equal("Mira", item.ModId);
            Assert.Null(item.Link);
            Assert.Equal("SusModder", vm.Mods.Single().SourceLabel);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MemorySecretStore : Phaket.Core.Accounts.ISecretStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value.ToArray() : null);
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
        {
            values[key] = value.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"slug":"mod-pack","version":1}""", Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task SusModder_snapshot_item_passes_client_validation_and_payload()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        try
        {
            var secretStore = new MemorySecretStore();
            await secretStore.SetAsync(DeviceKeyStore.SecretKey, new byte[32], cancellationToken: TestContext.Current.CancellationToken);
            var keys = new DeviceKeyStore(secretStore, Path.Combine(root, "settings.json"));
            var signer = new DeviceKeySigner(keys);
            var capture = new CaptureHandler();
            using var http = new HttpClient(new SignedHttpHandler(capture, keys, signer, new Uri("https://api.example.test/")))
            { BaseAddress = new Uri("https://api.example.test/") };
            using var api = new ModpackApiClient(http, root, signer);
            var pack = new ModpackDefinition
            {
                Name = "Pack", GameId = "among-us", Version = "1",
                Items = [new ModpackMod { Name = "SusModder-Mira", Version = "1.6.3b", Source = "susmodder", ModId = "Mira" }]
            };

            Assert.Equal(new ModpackPublishResult("mod-pack", 1), await api.PublishAsync(pack, "mod-pack", cancellationToken: TestContext.Current.CancellationToken));
            using var body = JsonDocument.Parse(capture.Body!);
            var item = body.RootElement.GetProperty("snapshot").GetProperty("items")[0];
            Assert.Equal("susmodder", item.GetProperty("source").GetString());
            Assert.Equal("SusModder-Mira", item.GetProperty("modName").GetString());
            Assert.Equal("1.6.3b", item.GetProperty("modVersion").GetString());
            Assert.Equal("Mira", item.GetProperty("modId").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("repo").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("link").ValueKind);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SusModder_external_id_longer_than_the_server_contract_is_rejected()
    {
        var root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        try
        {
            var secretStore = new MemorySecretStore();
            await secretStore.SetAsync(DeviceKeyStore.SecretKey, new byte[32], cancellationToken: TestContext.Current.CancellationToken);
            var keys = new DeviceKeyStore(secretStore, Path.Combine(root, "settings.json"));
            var signer = new DeviceKeySigner(keys);
            var capture = new CaptureHandler();
            using var http = new HttpClient(new SignedHttpHandler(capture, keys, signer, new Uri("https://api.example.test/")))
            { BaseAddress = new Uri("https://api.example.test/") };
            using var api = new ModpackApiClient(http, root, signer);
            var pack = new ModpackDefinition
            {
                Name = "Pack", GameId = "among-us", Version = "1",
                Items = [new ModpackMod { Name = "SusModder-Mira", Version = "1.6.3b", Source = "susmodder", ModId = new string('a', 101) }]
            };

            await Assert.ThrowsAsync<ArgumentException>(() => api.PublishAsync(pack, "mod-pack", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Null(capture.Body);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}