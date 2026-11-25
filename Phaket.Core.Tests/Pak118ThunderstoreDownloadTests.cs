using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class Pak118ThunderstoreDownloadTests
{
    private sealed class CountingTransport(byte[] payload) : IPackageTransport
    {
        public int Calls { get; private set; }

        public Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }
    }

    // PAK-129: file_size comes from the catalog and is untrusted; a value above the hard
    // ceiling must be refused before the transport is even opened.
    [Fact]
    public async Task Declared_size_above_the_hard_ceiling_is_refused_before_the_request()
    {
        var transport = new CountingTransport([]);

        var ex = await Assert.ThrowsAsync<PhaketException>(() => PackageDownloader.ReadPackageAsync(transport,
            "https://thunderstore.io/x/", "Huge", PackageDownloader.MaxUndeclaredBytes + 1,
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ArtifactSizeMismatch, ex.Error.Code);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Stream_over_the_declared_size_reports_a_size_mismatch()
    {
        var transport = new CountingTransport(new byte[4096]);

        var ex = await Assert.ThrowsAsync<PhaketException>(() => PackageDownloader.ReadPackageAsync(transport,
            "https://thunderstore.io/x/", "Big", 1024, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ArtifactSizeMismatch, ex.Error.Code);
        string message = ex.LocalizedMessage("en");
        Assert.Contains("4096", message);
        Assert.Contains("1024", message);
    }
    // Opt-in only: hits the real CDN. The catalog declares file_size from the same API, so a
    // mismatch here means the exact-size check would refuse a legitimate Thunderstore package.
    [Fact]
    public async Task LivePackageDownloadMatchesThePublishedSizeAndIsAZip()
    {
        if (Environment.GetEnvironmentVariable("PHAKET_TS_LIVE") != "1")
            Assert.Skip("Set PHAKET_TS_LIVE=1 to download a real Thunderstore package.");

        var bytes = await PackageDownloader.ReadPackageAsync(new HttpPackageTransport(),
            "https://thunderstore.io/package/download/Cosmella/Outfit_Presets/1.1.1/",
            "Cosmella-Outfit_Presets", 76767, TestContext.Current.CancellationToken);

        Assert.Equal(76767, bytes.Length);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));
    }
}