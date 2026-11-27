namespace Phaket.InstallerStub;

public sealed class InstallException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Dane stubu: jeden adres, jeden katalog roboczy. Nadpisania tylko dla testów.</summary>
public sealed record StubOptions
{
    public required Uri ManifestUri { get; init; }
    public required string WorkDirectory { get; init; }
    public required string LocalAppData { get; init; }

    // PAK-110 (serwer = źródło prawdy): kanał release i manifest Windows. Serwer przyjmuje tylko
    // channel=release|beta oraz os=win|linux — każda inna wartość to 400 INVALID_PARAMETER.
    internal const string DefaultManifestUrl = "https://phaket.app/api/releases?channel=release&os=win";

    public static StubOptions FromEnvironment() => new()
    {
        ManifestUri = ReadUri(InstallRunner.ManifestVariable) ?? new Uri(DefaultManifestUrl),
        WorkDirectory = Path.Combine(Path.GetTempPath(), "PhaketSetup-" + Environment.ProcessId),
        LocalAppData = Environment.GetEnvironmentVariable(InstallRunner.LocalAppDataVariable) is { Length: > 0 } dir
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    };

    // Skąd brać pliki: pola File są nazwami plików, puste/null → katalog manifestu, tak jak u Velopack.
    // Produkcja: downloadBaseUrl z kontraktu PAK-110 (CDN paczek). Nadpisanie dla CI/testów.
    public static Uri? OverrideDownloadBase => ReadUri("PHAKET_INSTALLER_DOWNLOAD_BASE");

    // Tylko HTTPS: nadpisanie z env nie może skierować instalatora na HTTP (MITM podmienia paczkę
    // razem z hashem). Zły/niewspierany adres → null i błąd E403 przed pierwszym żądaniem.
    private static Uri? ReadUri(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } url
            ? Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null
            : null;

    // Ustawione, ale nie-HTTPS (albo niepoprawne): nazwa pola do kodu błędu E403.
    public static string? InvalidOverride =>
        IsInvalid("PHAKET_INSTALLER_MANIFEST", ReadUri(InstallRunner.ManifestVariable))
            ? "manifest"
            : IsInvalid("PHAKET_INSTALLER_DOWNLOAD_BASE", OverrideDownloadBase) ? "downloadBaseUrl" : null;

    private static bool IsInvalid(string variable, Uri? parsed) =>
        parsed is null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable));
}