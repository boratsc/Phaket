using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phaket.Core.Omrl;
using Phaket.Core.Tools;

namespace Phaket.Core.Tests;

public sealed class ToolProviderTests
{
    private static string LegendaryAsset => OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_windows_arm64.exe" : "legendary_windows_x64.exe" : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_linux_arm64" : "legendary_linux_x64";
    private static readonly string Rid = (OperatingSystem.IsWindows() ? "win" : "linux") + "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
    private static readonly string Arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    private static string DepotAsset => $"DepotDownloader-{(OperatingSystem.IsWindows() ? "windows" : "linux")}-{Arch}.zip";
    private static string DepotExe => OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader";

    [Fact]
    public async Task LegendaryDownloadsAssetOnlyWhenDigestMatches()
    {
        byte[] bytes = "verified tool"u8.ToArray();
        using var fixture = new Fixture(Release("1.0", LegendaryAsset, bytes, Digest(bytes)), bytes);
        string path = await fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsMismatchedDigest()
    {
        using var fixture = new Fixture(Release("1.0", LegendaryAsset, "bad"u8.ToArray(), new string('0', 64)), "bad"u8.ToArray());
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
    }

    [Fact]
    public async Task LegendaryWithoutApiDigestIsRejected()
    {
        using var fixture = new Fixture(Release("1.0", LegendaryAsset, "bad"u8.ToArray(), null), "bad"u8.ToArray());
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
    }

    [Fact]
    public async Task DepotLatestFallsBackToKnownHashedRelease()
    {
        string asset = DepotAsset;
        byte[] zip = MakeZip(DepotExe, "fake dll"u8.ToArray());
        string releases = Releases(
            ReleaseJson("DepotDownloader_9.9.0", asset, [], null),
            ReleaseJson("DepotDownloader_3.3.0", asset, zip, Digest(zip)));
        using var fixture = new Fixture(releases, zip);
        string path = await fixture.Provider.EnsureAsync("depotdownloader", ct: TestContext.Current.CancellationToken);
        Assert.EndsWith(Path.Combine("DepotDownloader_3.3.0", DepotExe), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DepotAssetNameUsesUpstreamPlatformNames()
    {
        string asset = DepotAsset;
        HttpRequestMessage? requested = null;
        using var fixture = new Fixture(_ => Release("DepotDownloader_3.4.0", asset, [], null), "mismatch"u8.ToArray(), request => requested = request);
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("depotdownloader", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
        Assert.Equal("https://asset.test/tool", requested?.RequestUri!.ToString());
    }

    [Fact]
    public async Task DepotSelectsOlderTableHashedRelease()
    {
        string asset = DepotAsset;
        HttpRequestMessage? requested = null;
        using var fixture = new Fixture(_ => Releases(
            ReleaseJson("DepotDownloader_9.9.0", asset, [], null),
            ReleaseJson("DepotDownloader_3.4.0", asset, [], null)), "untrusted archive"u8.ToArray(), request => requested = request);
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("depotdownloader", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
        Assert.Equal("https://asset.test/tool", requested?.RequestUri!.ToString());
    }

    [Fact]
    public async Task DepotPinnedVersionWithoutTrustedHashIsRejected()
    {
        string asset = DepotAsset;
        byte[] zip = MakeZip(DepotExe, []);
        using var fixture = new Fixture(Release("unknown-version", asset, zip, null), zip);
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("depotdownloader", "unknown-version", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_UNVERIFIED_VERSION", ex.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnsafeZipEntries(bool symlink)
    {
        string asset = DepotAsset;
        byte[] zip = MakeZip(symlink ? "evil" : "../evil", "payload"u8.ToArray(), symlink ? unchecked((int)(0xA000 << 16)) : 0);
        using var fixture = new Fixture(Release("DepotDownloader_3.3.0", asset, zip, Digest(zip)), zip);
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("depotdownloader", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
    }

    [Fact]
    public async Task SafeZipExtractsExpectedEntry()
    {
        string asset = DepotAsset;
        byte[] zip = MakeZip(DepotExe, "valid"u8.ToArray());
        using var fixture = new Fixture(Release("DepotDownloader_3.3.0", asset, zip, Digest(zip)), zip);
        string path = await fixture.Provider.EnsureAsync("depotdownloader", ct: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));
        Assert.Equal("valid", await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptZipIsVerificationFailure()
    {
        string asset = DepotAsset;
        byte[] bytes = "not a zip"u8.ToArray();
        using var fixture = new Fixture(Release("DepotDownloader_3.3.0", asset, bytes, Digest(bytes)), bytes);
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("depotdownloader", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
    }

    [Fact]
    public async Task OfflinePinnedVersionDoesNotUseDifferentInstalledVersion()
    {
        using var fixture = new Fixture(_ => throw new HttpRequestException());
        fixture.Install("legendary", "other");
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("legendary", "X", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_OFFLINE", ex.Error.Code);
    }

    [Fact]
    public async Task EmptyDownloadIsRejected()
    {
        using var fixture = new Fixture(Release("1.0", LegendaryAsset, [], Digest([])), []);
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_DOWNLOAD_FAILED", ex.Error.Code);
    }

    [Fact]
    public async Task OfflineReturnsExistingUnpinnedInstallation()
    {
        using var fixture = new Fixture(_ => throw new HttpRequestException());
        string path = fixture.Install("legendary", "old");
        Assert.Equal(path, await fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OfflineWithoutExistingInstallationFails()
    {
        using var fixture = new Fixture(_ => throw new HttpRequestException());
        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken));
        Assert.Equal("TOOL_OFFLINE", ex.Error.Code);
    }

    [Fact]
    public async Task FreshUpdateCacheUsesInstalledBinaryWithoutHttp()
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("Unexpected HTTP request"));
        string path = fixture.Install("legendary", "old");
        File.WriteAllText(Path.Combine(fixture.Root, "legendary", ".updatecheck"), DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(path, await fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ExistingPinUsesInstalledBinaryWithoutHttp()
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("Unexpected HTTP request"));
        string path = fixture.Install("legendary", "pinned");
        Assert.Equal(path, await fixture.Provider.EnsureAsync("legendary", "pinned", ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ForceRefreshKeepsExistingVersionAndRefreshesCache()
    {
        byte[] bytes = "verified tool"u8.ToArray();
        using var fixture = new Fixture(Release("1.0", LegendaryAsset, bytes, Digest(bytes)), bytes);
        string path = await fixture.Provider.EnsureAsync("legendary", ct: TestContext.Current.CancellationToken);
        string cache = Path.Combine(fixture.Root, "legendary", ".updatecheck");
        File.WriteAllText(cache, "2000-01-01T00:00:00.0000000+00:00");

        string refreshed = await fixture.Provider.EnsureAsync("legendary", forceRefresh: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(path, refreshed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(DateTimeOffset.Parse(File.ReadAllText(cache)) > DateTimeOffset.Parse("2000-01-01T00:00:00.0000000+00:00"));
    }

    [Fact]
    public async Task BrokenVersionFolderIsReplacedInsteadOfReturned()
    {
        byte[] bytes = "verified tool"u8.ToArray();
        using var fixture = new Fixture(Release("1.0", LegendaryAsset, bytes, Digest(bytes)), bytes);
        // An interrupted earlier download left the version folder without its binary.
        Directory.CreateDirectory(Path.Combine(fixture.Root, "legendary", "1.0"));

        string path = await fixture.Provider.EnsureAsync("legendary", "1.0", ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    // PAK-137: Aurelia publishes its checksum only as the SHA256SUMS asset next to the binaries, so
    // the provider fetches it instead of reading an API digest.
    [Fact]
    public async Task AureliaVerifiesAgainstTheChecksumsAsset()
    {
        string asset = AureliaAsset;
        byte[] bytes = "aurelia binary"u8.ToArray();
        string sums = $"{Digest(bytes)}  {asset}\n{new string('0', 64)}  aurelia_linux_arm64\n";
        HttpRequestMessage? sumsRequest = null;
        using var fixture = new Fixture(
            _ => $"[{AureliaRelease("v0.3.1", asset)}]",
            request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(sums) : bytes,
            request => { if (request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal)) sumsRequest = request; });

        string path = await fixture.Provider.EnsureAsync("aurelia", "v0.3.1", ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("https://github.com/Drackrath/Aurelia/releases/download/v0.3.1/SHA256SUMS", sumsRequest?.RequestUri!.ToString());
        Assert.EndsWith(Path.Combine("v0.3.1", asset), path);
    }

    [Fact]
    public async Task AureliaWithoutItsChecksumIsRejected()
    {
        string asset = AureliaAsset;
        // The release has no SHA256SUMS asset: the download must never happen unverified.
        using var fixture = new Fixture(Releases(ReleaseJson("v0.3.1", asset, [], null)));

        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("aurelia", ct: TestContext.Current.CancellationToken));

        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
    }

    // A checksum line that is not a 64-hex digest is ignored, so the download stays unverified.
    [Fact]
    public async Task AureliaIgnoresAMalformedChecksumLine()
    {
        string asset = AureliaAsset;
        string sums = $"not-a-digest  {asset}\n";
        using var fixture = new Fixture(
            _ => $"[{AureliaRelease("v0.3.1", asset)}]",
            request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(sums) : "aurelia binary"u8.ToArray());

        var ex = await Assert.ThrowsAsync<PhaketException>(() => fixture.Provider.EnsureAsync("aurelia", ct: TestContext.Current.CancellationToken));

        Assert.Equal("TOOL_VERIFICATION_FAILED", ex.Error.Code);
    }

    // Aurelia names its assets x86_64 (not Depot's x64) and arm64, no archive.
    private static string AureliaAsset => "aurelia_" + (OperatingSystem.IsWindows() ? "windows" : "linux") + "_" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64") + (OperatingSystem.IsWindows() ? ".exe" : "");

    // A release with the bare binary (no API digest) and the SHA256SUMS asset the provider must read.
    private static string AureliaRelease(string version, string name) => JsonSerializer.Serialize(new
    {
        tag_name = version,
        assets = new[]
        {
            new { name, browser_download_url = $"https://github.com/Drackrath/Aurelia/releases/download/{version}/{name}", digest = (string?)null },
            new { name = "SHA256SUMS", browser_download_url = $"https://github.com/Drackrath/Aurelia/releases/download/{version}/SHA256SUMS", digest = (string?)null }
        }
    });

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Release(string version, string name, byte[] bytes, string? digest) => Releases(ReleaseJson(version, name, bytes, digest));
    private static string ReleaseJson(string version, string name, byte[] bytes, string? digest) => JsonSerializer.Serialize(new[] { new { tag_name = version, assets = new[] { new { name, browser_download_url = "https://asset.test/tool", digest = digest == null ? null : "sha256:" + digest } } } });
    private static string Releases(params string[] releases) => "[" + string.Join(',', releases.Select(x => x[1..^1])) + "]";
    private static byte[] MakeZip(string entryName, byte[] data, int externalAttributes = 0)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(entryName);
            entry.ExternalAttributes = externalAttributes;
            using var output = entry.Open(); output.Write(data);
        }
        return stream.ToArray();
    }

    // PAK-69 follow-up: provision resolves the managed binary from disk, without network,
    // so ToolBaseGameSource works with the tools installed in Settings.
    [Fact]
    public void ResolveInstalledFindsTheManagedBinaryOffline()
    {
        using var fixture = new Fixture("[]");
        string installed = fixture.Install("legendary", "0.21.1");

        Assert.Equal(installed, fixture.Provider.ResolveInstalled("legendary"));
        Assert.Null(fixture.Provider.ResolveInstalled("depotdownloader"));
        Assert.Equal(0, fixture.RequestCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient client;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "phaket-tool-tests-" + Guid.NewGuid().ToString("N"));
        public ToolProvider Provider { get; }
        public int RequestCount { get; private set; }

        public Fixture(string releases, byte[]? bytes = null) : this(_ => releases, bytes) { }
        public Fixture(Func<HttpRequestMessage, string> response, byte[]? bytes = null, Action<HttpRequestMessage>? requested = null)
            : this(response, _ => bytes ?? [], requested) { }
        public Fixture(Func<HttpRequestMessage, string> response, Func<HttpRequestMessage, byte[]> body, Action<HttpRequestMessage>? requested = null)
        {
            Directory.CreateDirectory(Root);
            client = new HttpClient(new Handler(request =>
            {
                RequestCount++;
                if (request.RequestUri!.Host == "api.github.com") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") };
                requested?.Invoke(request); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(request)) };
            }));
            Provider = new ToolProvider(Root, client);
        }

        public string Install(string tool, string version)
        {
            string name = tool == "legendary" ? (OperatingSystem.IsWindows() ? "legendary_windows_x64.exe" : "legendary_linux_x64") : DepotExe;
            string path = Path.Combine(Root, tool, version, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "existing"); return path;
        }
        public void Dispose() { client.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
