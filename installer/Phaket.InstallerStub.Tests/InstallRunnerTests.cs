using System.IO.Compression;
using System.Net;
using System.Text;

namespace Phaket.InstallerStub.Tests;

// Cała ścieżka stubu z podstawionym HttpClient: manifest → paczka → SHA-256 → seed-install.
// Ścieżka Setup.exe nie jest tu odpalana (testy nie uruchamiają procesów instalacyjnych).
public sealed class InstallRunnerTests : IDisposable
{
    private const string PackageName = "Phaket-0.1.0-full.nupkg";
    private static readonly byte[] PackageBytes = MakeNupkg();
    private static readonly string PackageSha = Bytes.Sha256(PackageBytes);

    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-stub-run-" + Guid.NewGuid().ToString("N"));

    public InstallRunnerTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private StubOptions Options() => new()
    {
        ManifestUri = new Uri("https://feed.test/api/releases?channel=release"),
        WorkDirectory = Path.Combine(root, "work"),
        LocalAppData = Path.Combine(root, "localappdata")
    };

    private static FakeHandler Feed(string manifest, byte[]? package = null) => new(request =>
        request.RequestUri!.AbsolutePath.StartsWith("/api/releases", StringComparison.Ordinal)
            ? Bytes.Ok(Encoding.UTF8.GetBytes(manifest))
            : request.RequestUri.AbsolutePath.EndsWith(PackageName, StringComparison.Ordinal) && package is not null
                ? Bytes.Ok(package)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

    private sealed class RecordingUi : IProgressUi
    {
        public List<string> Stages { get; } = [];
        public string? Failure { get; private set; }

        public void Report(StubStage stage, int percent) => Stages.Add($"{stage}:{percent}");
        public void Fail(string code) => Failure = code;
    }

    [Fact]
    public async Task Seed_install_unpacks_package_and_writes_install_state()
    {
        var ui = new RecordingUi();
        var options = Options();

        var result = await InstallRunner.TestRunAsync(ui, options, Feed(Bytes.Manifest(("0.1.0", PackageName, PackageSha)), PackageBytes));

        Assert.True(result);
        Assert.Null(ui.Failure);
        Assert.Contains("Downloading:0", ui.Stages);
        Assert.Contains("Installing:100", ui.Stages);
        Assert.Contains("Done:100", ui.Stages);
        var install = Path.Combine(options.LocalAppData, "Phaket", "current");
        Assert.True(File.Exists(Path.Combine(install, "Phaket.exe")));
        Assert.Contains("0.1.0", await File.ReadAllTextAsync(Path.Combine(install, "sq.version"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(options.WorkDirectory));   // katalog roboczy sprzątnięty
    }

    [Fact]
    public async Task Missing_package_on_feed_fails_with_http_code()
    {
        var ui = new RecordingUi();

        var result = await InstallRunner.TestRunAsync(ui, Options(), Feed(Bytes.Manifest(("0.1.0", PackageName, PackageSha))));

        Assert.False(result);
        Assert.Equal("N404 package", ui.Failure);
    }

    [Fact]
    public async Task Wrong_hash_fails_with_checksum_code()
    {
        var ui = new RecordingUi();

        var result = await InstallRunner.TestRunAsync(ui, Options(),
            Feed(Bytes.Manifest(("0.1.0", PackageName, new string('0', 64))), PackageBytes));

        Assert.False(result);
        Assert.Equal("E500 SHA256", ui.Failure);
    }

    [Fact]
    public async Task Manifest_without_success_fails_before_download()
    {
        var ui = new RecordingUi();
        var handler = Feed("""{"success":false,"releases":[]}""");

        var result = await InstallRunner.TestRunAsync(ui, Options(), handler);

        Assert.False(result);
        Assert.Equal("E401 success", ui.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Network_failure_on_manifest_is_reported()
    {
        var ui = new RecordingUi();

        var result = await InstallRunner.TestRunAsync(ui, Options(), new FakeHandler(_ => throw new HttpRequestException("offline")));

        Assert.False(result);
        Assert.Equal("N000 manifest", ui.Failure);
    }

    [Fact]
    public async Task Manifest_http_error_carries_status_code()
    {
        var ui = new RecordingUi();

        var result = await InstallRunner.TestRunAsync(ui, Options(),
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        Assert.False(result);
        Assert.Equal("N503 manifest", ui.Failure);
    }

    [Fact]
    public void Feed_with_setup_exe_routes_to_setup_installer()
    {
        var manifest = ManifestParser.Parse(Bytes.Manifest(
            ("0.1.0", PackageName, PackageSha),
            ("0.1.0", "Phaket-Setup.exe", new string('A', 64))));

        Assert.Equal("Phaket-Setup.exe", FirstInstall.SetupEntry(manifest)!.File);
        Assert.Equal(PackageName, ManifestParser.SelectLatest(manifest)!.File);
    }

    [Fact]
    public async Task Seed_install_refuses_to_overwrite_a_real_velopack_install()
    {
        var local = Path.Combine(root, "localappdata");
        Directory.CreateDirectory(Path.Combine(local, "Phaket"));
        await File.WriteAllTextAsync(Path.Combine(local, "Phaket", "Update.exe"), "", TestContext.Current.CancellationToken);
        var manifest = Bytes.Manifest(("0.1.0", PackageName, PackageSha));
        var ui = new RecordingUi();
        var options = Options() with { LocalAppData = local };

        var result = await InstallRunner.TestRunAsync(ui, options, Feed(manifest, PackageBytes));

        Assert.False(result);
        Assert.Equal("E601 exists", ui.Failure);
    }

    private static byte[] MakeNupkg()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "lib/Phaket.exe", "sq.version" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write("x"u8);
            }
        }
        return buffer.ToArray();
    }
}