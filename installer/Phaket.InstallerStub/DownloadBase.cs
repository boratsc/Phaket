namespace Phaket.InstallerStub;

/// <summary>Katalog, z którego pobieramy pliki wskazane w manifeście.</summary>
public static class DownloadBase
{
    // Kolejność: nadpisanie (CI/testy) → downloadBaseUrl z manifestu → URI manifestu (bez query,
    // żeby ?channel=release nie przeszedł do adresu pliku).
    public static Uri Resolve(Uri manifestUri, Manifest manifest)
    {
        var baseUrl = StubOptions.OverrideDownloadBase
            ?? (manifest.DownloadBaseUrl.Length > 0 ? new Uri(manifest.DownloadBaseUrl) : null)
            ?? new Uri(manifestUri.GetLeftPart(UriPartial.Path));
        return baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }
}