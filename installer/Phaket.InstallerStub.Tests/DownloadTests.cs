using System.Net;
using System.Net.Http;
using System.Text;

namespace Phaket.InstallerStub.Tests;

public sealed class DownloadTests : IDisposable
{
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("fake velopack package payload");
    private static readonly Uri ManifestUri = new("https://phaket.app/api/releases?channel=release");
    private static readonly ReleaseEntry Entry = new()
    {
        Version = "0.1.0",
        File = "Phaket-0.1.0-full.nupkg",
        Sha256 = Bytes.Sha256(Package)
    };

    private readonly string work = Path.Combine(Path.GetTempPath(), "phaket-stub-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(work)) Directory.Delete(work, true);
    }

    private static HttpClient Client(FakeHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task Downloads_package_with_matching_hash()
    {
        var handler = new FakeHandler(_ => Bytes.Ok(Package));

        var path = await Downloader.DownloadVerifiedAsync(Client(handler), TimeSpan.FromSeconds(5), ManifestUri,
            Entry, work, null, TestContext.Current.CancellationToken);

        Assert.Equal(Package, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("https://phaket.app/api/Phaket-0.1.0-full.nupkg", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Hash_mismatch_deletes_file_and_reports_code()
    {
        var bad = new ReleaseEntry { Version = Entry.Version, File = Entry.File, Sha256 = new string('0', 64) };
        var handler = new FakeHandler(_ => Bytes.Ok(Package));
        var progress = new List<int>();

        var error = await Assert.ThrowsAsync<InstallException>(() => Downloader.DownloadVerifiedAsync(
            Client(handler), TimeSpan.FromSeconds(5), ManifestUri, bad, work, new Sink(progress),
            TestContext.Current.CancellationToken));

        Assert.Equal("E500 SHA256", error.Code);
        Assert.False(File.Exists(Path.Combine(work, Entry.File)));
        Assert.Equal(100, progress[^1]);
    }

    [Fact]
    public async Task Http_error_is_reported_with_status_code()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<InstallException>(() => Downloader.DownloadVerifiedAsync(
            Client(handler), TimeSpan.FromSeconds(5), ManifestUri, Entry, work, null,
            TestContext.Current.CancellationToken));

        Assert.Equal("N404 package", error.Code);
    }

    [Fact]
    public async Task Network_failure_is_reported_as_download_error()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("no network"));

        var error = await Assert.ThrowsAsync<InstallException>(() => Downloader.DownloadVerifiedAsync(
            Client(handler), TimeSpan.FromSeconds(5), ManifestUri, Entry, work, null,
            TestContext.Current.CancellationToken));

        Assert.Equal("E501 download", error.Code);
        Assert.False(File.Exists(Path.Combine(work, Entry.File)));
    }

    [Fact]
    public async Task Absolute_https_file_url_is_downloaded_as_is()
    {
        // Feed z scripts/feed-manifest.py podaje File jako absolutny URL.
        var handler = new FakeHandler(_ => Bytes.Ok(Package));
        var entry = Entry with { File = "https://cdn.example/phaket/Phaket-0.1.0-full.nupkg" };

        var path = await Downloader.DownloadVerifiedAsync(Client(handler), TimeSpan.FromSeconds(5), ManifestUri,
            entry, work, null, TestContext.Current.CancellationToken);

        Assert.Equal("https://cdn.example/phaket/Phaket-0.1.0-full.nupkg", Assert.Single(handler.Requests));
        Assert.Equal("Phaket-0.1.0-full.nupkg", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("http://cdn.example/Phaket-0.1.0-full.nupkg")]   // tylko HTTPS
    [InlineData("file:///etc/passwd")]
    public async Task Non_https_absolute_file_is_rejected_before_any_request(string file)
    {
        var handler = new FakeHandler(_ => Bytes.Ok(Package));
        var entry = Entry with { File = file };

        var error = await Assert.ThrowsAsync<InstallException>(() => Downloader.DownloadVerifiedAsync(
            Client(handler), TimeSpan.FromSeconds(5), ManifestUri, entry, work, null,
            TestContext.Current.CancellationToken));

        Assert.Equal("E403 File", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unsafe_file_name_is_rejected_before_any_request()
    {
        var handler = new FakeHandler(_ => Bytes.Ok(Package));
        var entry = new ReleaseEntry { Version = "1.0.0", File = "../evil.nupkg", Sha256 = Entry.Sha256 };

        var error = await Assert.ThrowsAsync<InstallException>(() => Downloader.DownloadVerifiedAsync(
            Client(handler), TimeSpan.FromSeconds(5), ManifestUri, entry, work, null,
            TestContext.Current.CancellationToken));

        Assert.Equal("E403 File", error.Code);
        Assert.Empty(handler.Requests);
    }

    private sealed class Sink(List<int> values) : IProgress<int>
    {
        public void Report(int value) => values.Add(value);
    }
}