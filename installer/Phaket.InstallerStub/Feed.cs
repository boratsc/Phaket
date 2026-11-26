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

    public static StubOptions FromEnvironment() => new()
    {
        ManifestUri = Environment.GetEnvironmentVariable(InstallRunner.ManifestVariable) is { Length: > 0 } url
            ? new Uri(url)
            : new Uri(DefaultManifestUrl),
        WorkDirectory = Path.Combine(Path.GetTempPath(), "PhaketSetup-" + Environment.ProcessId),
        LocalAppData = Environment.GetEnvironmentVariable(InstallRunner.LocalAppDataVariable) is { Length: > 0 } dir
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    };

    internal const string DefaultManifestUrl = "https://phaket.app/api/releases?channel=release";

    // Skąd brać pliki: pola File są nazwami plików, puste/null → katalog manifestu, tak jak u Velopack.
    // Produkcja: downloadBaseUrl z kontraktu PAK-110 (CDN paczek). Nadpisanie dla CI/testów.
    public static Uri? OverrideDownloadBase => Environment.GetEnvironmentVariable("PHAKET_INSTALLER_DOWNLOAD_BASE") is { Length: > 0 } url
        ? new Uri(url)
        : null;
}