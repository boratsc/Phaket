using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Accounts;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class DeviceKeyTests
{
    private sealed class MemorySecretStore : ISecretStore
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

    private sealed record CapturedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        List<CapturedRequest>? requests = null) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = requests ?? [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            string path = request.RequestUri!.PathAndQuery;
            Requests.Add(new CapturedRequest(request.Method.Method, path,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase), body));
            return await respond(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Canonical_request_and_fingerprint_match_server_vector()
    {
        var secretStore = new MemorySecretStore();
        await secretStore.SetAsync(DeviceKeyStore.SecretKey, new byte[32], cancellationToken: TestContext.Current.CancellationToken);
        var keys = new DeviceKeyStore(secretStore, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));
        DevicePublicIdentity identity = await keys.GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken);
        const string canonical = "POST\n/packages\n1000\nnonce-1234567890\nabc";
        Assert.Equal(canonical, DeviceKeySigner.BuildCanonical("post", "/packages", "1000", "nonce-1234567890", "abc"));

        byte[] signature = await keys.SignAsync(Encoding.UTF8.GetBytes(canonical), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(DeviceKeySigner.VerifyForTest(identity.PublicKey, signature, canonical));
        Assert.False(DeviceKeySigner.VerifyForTest(identity.PublicKey, signature, canonical + "changed"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(identity.PublicKey))[..8], identity.Fingerprint);
    }

    [Fact]
    public async Task Signer_hashes_exact_body_uses_fresh_nonce_and_five_minute_window()
    {
        var store = new MemorySecretStore();
        await store.SetAsync(DeviceKeyStore.SecretKey, new byte[32], cancellationToken: TestContext.Current.CancellationToken);
        var keys = new DeviceKeyStore(store, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));
        var signer = new DeviceKeySigner(keys);
        var first = new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/packages?game=valheim")
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("snapshot-body")) };
        var firstHeaders = await signer.SignAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        var second = new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/packages?game=valheim")
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("snapshot-body")) };
        var secondHeaders = await signer.SignAsync(second, cancellationToken: TestContext.Current.CancellationToken);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long timestamp = long.Parse(firstHeaders["X-Timestamp"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(DeviceKeySigner.TimestampIsWithinWindow(timestamp, now));
        Assert.True(DeviceKeySigner.TimestampIsWithinWindow(1_000, 700));
        Assert.True(DeviceKeySigner.TimestampIsWithinWindow(1_000, 1_300));
        Assert.False(DeviceKeySigner.TimestampIsWithinWindow(1_000, 699));
        Assert.False(DeviceKeySigner.TimestampIsWithinWindow(1_000, 1_301));
        Assert.NotEqual(firstHeaders["X-Nonce"], secondHeaders["X-Nonce"]);
        Assert.Equal(32, firstHeaders["X-Nonce"].Length);
        Assert.All(firstHeaders["X-Nonce"], c => Assert.True(char.IsAsciiHexDigit(c)));

        DevicePublicIdentity identity = await keys.GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken);
        string canonical = DeviceKeySigner.CreateCanonicalRepresentation("POST", "/packages?game=valheim",
            firstHeaders["X-Timestamp"], firstHeaders["X-Nonce"], Encoding.UTF8.GetBytes("snapshot-body"));
        byte[] signature = Convert.FromBase64String(firstHeaders["X-Signature"]);
        Assert.True(DeviceKeySigner.VerifyForTest(identity.PublicKey, signature, canonical));
    }

    [Fact]
    public async Task Device_key_roundtrips_through_secret_store_and_never_enters_settings_plaintext()
    {
        string root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.json");
            var secretStore = new ConsentFileStore(Path.Combine(root, "secrets"), true, "test-only-passphrase");
            var firstStore = new DeviceKeyStore(secretStore, settings);
            DevicePublicIdentity first = await firstStore.GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken);
            await firstStore.SaveNickAsync("Test device", cancellationToken: TestContext.Current.CancellationToken);

            var secondStore = new DeviceKeyStore(secretStore, settings);
            DevicePublicIdentity second = await secondStore.GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(first.PublicKey, second.PublicKey);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal("Test device", second.Nick);

            byte[] privateSeed = (await secretStore.GetAsync(DeviceKeyStore.SecretKey, cancellationToken: TestContext.Current.CancellationToken))!;
            try
            {
                string settingsText = File.ReadAllText(settings);
                string secretFile = File.ReadAllText(Path.Combine(root, "secrets", "consent", DeviceKeyStore.SecretKey + ".enc"));
                Assert.Contains("deviceNick", settingsText);
                Assert.DoesNotContain(Convert.ToBase64String(privateSeed), settingsText, StringComparison.Ordinal);
                Assert.DoesNotContain(Convert.ToHexStringLower(privateSeed), settingsText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToBase64String(privateSeed), secretFile, StringComparison.Ordinal);
                Assert.DoesNotContain(Convert.ToHexStringLower(privateSeed), secretFile, StringComparison.OrdinalIgnoreCase);
            }
            finally { CryptographicOperations.ZeroMemory(privateSeed); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Profile_publish_uses_signed_create_and_version_routes()
    {
        var secretStore = new MemorySecretStore();
        await secretStore.SetAsync(DeviceKeyStore.SecretKey, new byte[32], cancellationToken: TestContext.Current.CancellationToken);
        var keys = new DeviceKeyStore(secretStore, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));
        var signer = new DeviceKeySigner(keys, registerBeforeSigning: false);
        string fingerprint = (await keys.GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken)).Fingerprint[..8];
        string endpoint = $"https://{Guid.NewGuid():N}.example.test/";
        var requests = new List<CapturedRequest>();
        CaptureHandler CreateHandler() => new((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/devices" => $"{{\"deviceId\":\"device\",\"fingerprint\":\"{fingerprint}\",\"nick\":\"Phaket device\"}}",
            "/packages" => "{\"slug\":\"test-pack\",\"version\":1}",
            _ => "{\"slug\":\"test-pack\",\"version\":2}"
        })), requests);
        var identityServices = new DeviceIdentityServices(keys, signer, CreateHandler);
        var service = identityServices.CreateProfilePublisher(endpoint, CreateHandler());
        using var snapshot = JsonDocument.Parse("""{"schemaVersion":1,"items":[{"modName":"Mod A","modVersion":"1","source":"github","repo":"owner/repo","tag":"v1","assetId":"mod.zip"}]}""");

        await identityServices.RegisterDeviceAsync(endpoint, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new ProfilePublishResult("test-pack", 1), await service.PublishAsync(
            new ProfilePublishRequest("test-pack", "among-us", "Test pack", "", snapshot.RootElement), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new ProfilePublishResult("test-pack", 2), await service.PublishAsync(
            new ProfilePublishRequest("test-pack", "among-us", "Test pack", "", snapshot.RootElement, CreateNew: false), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["/devices", "/packages", "/packages/test-pack/versions"], requests.Select(request => request.Path));
        Assert.All(requests, request => Assert.Equal("POST", request.Method));
        foreach (var request in requests)
        {
            string keyHeader = AssertHeader(request, "X-Device-Key");
            string timestampHeader = AssertHeader(request, "X-Timestamp");
            string nonce = AssertHeader(request, "X-Nonce");
            byte[] signature = Convert.FromBase64String(AssertHeader(request, "X-Signature"));
            byte[] publicKey = Convert.FromBase64String(keyHeader);
            Assert.Equal((await keys.GetPublicIdentityAsync(cancellationToken: TestContext.Current.CancellationToken)).PublicKey, publicKey);
            Assert.True(long.TryParse(timestampHeader, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long timestamp));
            Assert.True(DeviceKeySigner.TimestampIsWithinWindow(timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Assert.Matches("^[0-9a-f]{32}$", nonce);
            Assert.Equal(64, signature.Length);
            string canonical = DeviceKeySigner.CreateCanonicalRepresentation(request.Method, request.Path,
                timestampHeader, nonce, request.Body);
            Assert.True(DeviceKeySigner.VerifyForTest(publicKey, signature, canonical));
        }
    }

    [Fact]
    public async Task Modpack_publish_serializes_server_snapshot_and_signs_request()
    {
        string root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        try
        {
            var secretStore = new MemorySecretStore();
            await secretStore.SetAsync(DeviceKeyStore.SecretKey, new byte[32], cancellationToken: TestContext.Current.CancellationToken);
            var keys = new DeviceKeyStore(secretStore, Path.Combine(root, "settings.json"));
            var signer = new DeviceKeySigner(keys);
            var capture = new CaptureHandler((_, _) => Task.FromResult(JsonResponse("{\"slug\":\"mod-pack\",\"version\":1}")));
            using var http = new HttpClient(new SignedHttpHandler(capture, keys, signer, new Uri("https://api.example.test/")))
            { BaseAddress = new Uri("https://api.example.test/") };
            using var api = new ModpackApiClient(http, root, signer);
            var pack = new ModpackDefinition
            {
                Name = "Mod pack", Description = "Snapshot", GameId = "among-us",
                Items = [new ModpackMod { Name = "Mod A", Version = "1.0", Source = "github", Repository = "owner/repo", Tag = "v1", AssetId = "mod.zip" }]
            };

            Assert.Equal(new ModpackPublishResult("mod-pack", 1), await api.PublishAsync(pack, "mod-pack", cancellationToken: TestContext.Current.CancellationToken));
            CapturedRequest published = Assert.Single(capture.Requests, request => request.Path == "/packages");
            Assert.Contains("X-Signature", published.Headers.Keys);
            using var body = JsonDocument.Parse(published.Body);
            Assert.Equal(1, body.RootElement.GetProperty("snapshot").GetProperty("schemaVersion").GetInt32());
            Assert.Equal("github", body.RootElement.GetProperty("snapshot").GetProperty("items")[0].GetProperty("source").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Accounts_identity_card_renders_headless()
    {
        string root = TestAccounts.TempPath();
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new AccountServices(new MemorySecretStore(), root);
            var viewModel = new AccountsViewModel(accounts);
            await viewModel.DeviceIdentity.Initialization;
            var window = new Window { Width = 1280, Height = 800, Content = new AccountsView { DataContext = viewModel } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save("/tmp/opencode/phaket-render/32-identity.png", PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string AssertHeader(CapturedRequest request, string name)
    {
        Assert.True(request.Headers.TryGetValue(name, out string? value), $"Missing {name} on {request.Method} {request.Path}.");
        Assert.False(string.IsNullOrWhiteSpace(value));
        return value!;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
