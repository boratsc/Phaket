namespace Phaket.InstallerStub.Tests;

// Stub nie może pobierać niczego poza HTTPS: nadpisania env (HTTP) są odrzucane PRZED pierwszym
// żądaniem, a downloadBaseUrl z odpowiedzi manifestu nie może zejść na HTTP.
// Kolekcja serializuje dostęp do zmiennych procesu (równoległe testy dzieliłyby env).
[CollectionDefinition(nameof(EnvironmentVariables))]
public sealed class EnvironmentVariables;

[Collection(nameof(EnvironmentVariables))]
public sealed class TransportSecurityTests
{
    private const string PackageName = "Phaket-0.1.0-full.nupkg";
    // Zip z lib/Phaket.exe: seed-install rozpakowuje paczkę (sam tekst = E601 seed).
    private static readonly byte[] PackageBytes = MakeNupkg();

    private static byte[] MakeNupkg()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var stream = zip.CreateEntry("lib/Phaket.exe").Open();
            stream.Write("x"u8);
        }
        return buffer.ToArray();
    }
    private static readonly string PackageSha = Bytes.Sha256(PackageBytes);

    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-stub-tls-" + Guid.NewGuid().ToString("N"));

    private StubOptions Options() => new()
    {
        ManifestUri = new Uri("https://feed.test/api/releases?channel=release"),
        WorkDirectory = Path.Combine(root, "work"),
        LocalAppData = Path.Combine(root, "localappdata")
    };

    private static FakeHandler Feed(string manifest) => new(request =>
        request.RequestUri!.AbsolutePath.StartsWith("/api/releases", StringComparison.Ordinal)
            ? Bytes.Ok(System.Text.Encoding.UTF8.GetBytes(manifest))
            : request.RequestUri.AbsoluteUri.EndsWith(PackageName, StringComparison.Ordinal)
                ? Bytes.Ok(PackageBytes)
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

    private sealed class RecordingUi : IProgressUi
    {
        public string? Failure { get; private set; }
        public void Report(StubStage stage, int percent) { }
        public void Fail(string code) => Failure = code;
    }

    // Manifest, którego downloadBaseUrl wskazuje HTTP — resztę pól ma poprawną.
    private static string HttpBaseManifest() => $$"""
        {"success":true,"latestVersion":"0.1.0","downloadBaseUrl":"http://cdn.example/phaket/",
         "releases":[{"Version":"0.1.0","File":"{{PackageName}}","SHA256":"{{PackageSha}}"}]}
        """;

    // Ustawia zmienne env i przywraca poprzednie wartości nawet po asercji — kolejny test
    // w kolekcji nie może odziedziczyć nadpisania, które ten test świadomie podłożył.
    private static async Task Env(Func<Task> body, params (string Name, string? Value)[] values)
    {
        var saved = values.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
        try
        {
            foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
            await body();
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public Task Http_manifest_override_is_refused_without_any_request() => Env(async () =>
    {
        var ui = new RecordingUi();
        var handler = Feed(Bytes.Manifest(("0.1.0", PackageName, PackageSha)));

        var result = await InstallRunner.TestRunAsync(ui, Options(), handler);

        Assert.False(result);
        Assert.Equal("E403 manifest", ui.Failure);
        Assert.Empty(handler.Requests);   // zero żądań: odrzucenie przed siecią
    }, ("PHAKET_INSTALLER_MANIFEST", "http://evil.example/api/releases"));

    [Fact]
    public Task Non_http_scheme_in_manifest_override_is_refused() => Env(async () =>
    {
        var ui = new RecordingUi();
        var handler = Feed(Bytes.Manifest(("0.1.0", PackageName, PackageSha)));

        var result = await InstallRunner.TestRunAsync(ui, Options(), handler);

        Assert.False(result);
        Assert.Equal("E403 manifest", ui.Failure);
        Assert.Empty(handler.Requests);
    }, ("PHAKET_INSTALLER_MANIFEST", "file:///etc/releases"));

    [Fact]
    public Task Http_download_base_override_is_refused_without_any_request() => Env(async () =>
    {
        var ui = new RecordingUi();
        var handler = Feed(Bytes.Manifest(("0.1.0", PackageName, PackageSha)));

        var result = await InstallRunner.TestRunAsync(ui, Options(), handler);

        Assert.False(result);
        Assert.Equal("E403 downloadBaseUrl", ui.Failure);
        Assert.Empty(handler.Requests);
    }, ("PHAKET_INSTALLER_DOWNLOAD_BASE", "http://evil.example/phaket/"));

    [Fact]
    public Task Https_overrides_still_work() => Env(async () =>
    {
        var ui = new RecordingUi();
        var handler = Feed(Bytes.Manifest(("0.1.0", PackageName, PackageSha)));

        var result = await InstallRunner.TestRunAsync(ui, Options(), handler);

        Assert.True(result, ui.Failure);
        Assert.Null(ui.Failure);
        Assert.Contains(handler.Requests, url => url.StartsWith("https://cdn.example/phaket/", StringComparison.Ordinal));
    },
    ("PHAKET_INSTALLER_MANIFEST", "https://feed.test/api/releases?channel=release"),
    ("PHAKET_INSTALLER_DOWNLOAD_BASE", "https://cdn.example/phaket/"));

    [Fact]
    public async Task Http_download_base_from_manifest_is_refused_before_download()
    {
        var ui = new RecordingUi();
        var handler = Feed(HttpBaseManifest());

        var result = await InstallRunner.TestRunAsync(ui, Options(), handler);

        Assert.False(result);
        Assert.Equal("E403 downloadBaseUrl", ui.Failure);
        Assert.Single(handler.Requests);   // tylko manifest; paczka nie ruszona
        Assert.StartsWith("https://feed.test/api/releases", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public Task From_environment_refuses_http_and_keeps_default() => Env(() =>
    {
        var options = StubOptions.FromEnvironment();

        Assert.Equal(Uri.UriSchemeHttps, options.ManifestUri.Scheme);
        Assert.Null(StubOptions.OverrideDownloadBase);
        Assert.Equal("manifest", StubOptions.InvalidOverride);
        return Task.CompletedTask;
    },
    ("PHAKET_INSTALLER_MANIFEST", "http://evil.example/api/releases"),
    ("PHAKET_INSTALLER_DOWNLOAD_BASE", "http://evil.example/"));
}