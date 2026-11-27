using System.Globalization;

namespace Phaket.InstallerStub;

/// <summary>
/// Jeden przebieg instalacji: manifest → full.nupkg (+ SHA-256) → Setup.exe z feedu albo seed-install.
/// Logika nie wie, czy pasek jest okienkiem Win32, czy konsolą.
/// </summary>
public static class InstallRunner
{
    public const int Ok = 0;
    public const int Failed = 1;

    public const string ManifestVariable = "PHAKET_INSTALLER_MANIFEST";
    public const string LocalAppDataVariable = "PHAKET_INSTALLER_LOCALAPPDATA";
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(IProgressUi ui, StubOptions options, TextWriter? log = null) =>
        await Run(ui, options, log, http: null, launch: true);

    // Seam testowy: podstawiony HttpClient, seed-install bez uruchamiania Phaket.exe.
    internal static async Task<bool> TestRunAsync(IProgressUi ui, StubOptions options, HttpMessageHandler handler)
    {
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await Run(ui, options, log: null, http, launch: false) == Ok;
    }

    private static async Task<int> Run(IProgressUi ui, StubOptions options, TextWriter? log, HttpClient? http, bool launch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        http ??= new HttpClient { Timeout = NetworkTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhaketSetup/0.1 (+https://phaket.app)");

        ui.Report(StubStage.Downloading, 0);
        // Nadpisania env: rozpoznane i odrzucone przed pierwszym żądaniem (zły/niewspierany adres).
        if (StubOptions.InvalidOverride is { } invalid) { ui.Fail($"E403 {invalid}"); return Failed; }
        try
        {
            // PRZED pierwszym żądaniem: HTTP (=MITM podmienia paczkę i hash) jest odrzucany.
            RequireHttps(options.ManifestUri, "manifest");
            var feed = await ReadManifestAsync(http, options.ManifestUri, cts.Token);
            var entry = ManifestParser.SelectLatest(feed) ?? throw new InstallException("E404 full.nupkg");
            // Baza z downloadBaseUrl odpowiedzi nie może zejść na HTTP — wpisy mają hashe z tego
            // manifestu, więc każdy serwer z takim manifestem musiałby być zaufany z góry.
            var baseUri = DownloadBase.Resolve(options.ManifestUri, feed);
            RequireHttps(baseUri, "downloadBaseUrl");

            var package = await Downloader.DownloadVerifiedAsync(http, NetworkTimeout,
                baseUri, entry, options.WorkDirectory, new PercentSink(ui), cts.Token);

            if (FirstInstall.SetupEntry(feed) is { } setup)
            {
                var setupPath = await Downloader.DownloadVerifiedAsync(http, NetworkTimeout,
                    baseUri, setup, options.WorkDirectory, null, cts.Token);
                ui.Report(StubStage.Installing, 100);
                log?.WriteLine("install=setup.exe");
                var exit = FirstInstall.RunSetup(setupPath);
                if (exit != 0)
                    throw new InstallException(Messages.Code("E600 setup", "exit" + exit.ToString(CultureInfo.InvariantCulture)));
                ui.Report(StubStage.Done, 100);
                return Ok;
            }

            ui.Report(StubStage.Installing, 100);
            log?.WriteLine("install=seed");
            var installDir = FirstInstall.SeedInstall(package, entry.Version, options.LocalAppData);
            var exe = FirstInstall.FindExecutable(installDir) ?? throw new InstallException("E602 exe");
            if (launch) FirstInstall.Launch(exe);
            ui.Report(StubStage.Done, 100);
            return Ok;
        }
        catch (Exception ex) when (ex is InstallException or ManifestException)
        {
            ui.Fail(ex is InstallException install ? install.Code : ex.Message);
            return Failed;
        }
        catch (OperationCanceledException)
        {
            ui.Fail("E502 timeout");
            return Failed;
        }
        catch (Exception)
        {
            ui.Fail("E503 install");
            return Failed;
        }
        finally
        {
            Cleanup(options.WorkDirectory);
        }
    }

    private static void RequireHttps(Uri uri, string detail)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InstallException($"E403 {detail}");
    }

    private static async Task<Manifest> ReadManifestAsync(HttpClient http, Uri manifestUri, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            using var response = await http.GetAsync(manifestUri, ct);
            if (!response.IsSuccessStatusCode)
                throw new InstallException(Messages.Code("N" + (int)response.StatusCode, "manifest"));
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new InstallException("N000 manifest");
        }
        if (bytes.Length > ManifestParser.MaxBytes) throw new InstallException("E400 manifest");
        return ManifestParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static void Cleanup(string workDirectory)
    {
        try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Etap „Sprawdzam plik…" pojawia się, zanim hash zostanie policzony; download raportuje 0–100%.
    private sealed class PercentSink(IProgressUi ui) : IProgress<int>
    {
        public void Report(int value) => ui.Report(StubStage.Downloading, value);
    }
}