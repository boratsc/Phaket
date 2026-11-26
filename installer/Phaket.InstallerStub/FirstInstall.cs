using System.Diagnostics;
using System.IO.Compression;

namespace Phaket.InstallerStub;

/// <summary>
/// Pierwsza instalacja. WYBADANE (Velopack 1.2.161, api.nuget.org velopack 1.2.161: Velopack.xml
/// + docs.velopack.io /distributing/self-hosting): NIE ma wspieranej ścieżki instalacji z
/// lokalnego .nupkg. Update.exe zna tylko `apply --package` (na JUŻ zainstalowanej aplikacji,
/// zakorzenionej w %LocalAppData%), `start` i `patch`; VelopackApp nie instaluje, wpływa jedynie
/// na hooki. Dlatego pierwsza instalacja idzie przez instalator z feedu:
/// <list type="number">
/// <item>manifest publikuje Phaket-Setup.exe → pobieramy go, weryfikujemy SHA-256 i uruchamiamy
/// `--silent` (instalacja per-user, tworzy Update.exe + sq.version, więc aktualizacje działają);</item>
/// <item>brak Setup.exe w manifeście → seed-install (fallback, ograniczony — patrz poniżej).</item>
/// </list>
/// </summary>
public static class FirstInstall
{
    public const string SetupFileName = "Phaket-Setup.exe";

    public static ReleaseEntry? SetupEntry(Manifest manifest) =>
        manifest.Releases.FirstOrDefault(r =>
            string.Equals(r.File, SetupFileName, StringComparison.OrdinalIgnoreCase));

    public static int RunSetup(string setupPath)
    {
        // Setup.exe --silent nie pokazuje własnego okna; pasek postępu prowadzi ten stub.
        var start = new ProcessStartInfo(setupPath, "--silent") { UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InstallException("E600 setup");
        process.WaitForExit();
        return process.ExitCode;
    }

    // -------------------------------------------------------------------------------------------
    // SEED-INSTALL (fallback). Velopack NIE rozpoznaje takiej instalacji jako własnej (brak
    // Update.exe → UpdateManager.IsInstalled == false, Phaket pokaże tryb deweloperski bez
    // aktualizacji), brak skrótu w menu Start i deinstalatora.
    // shortcut: seed-install tylko dopóki manifest nie publikuje Phaket-Setup.exe (wtedy
    // włącza się ścieżka Setup.exe); wtedy usuń tę gałąź.
    // -------------------------------------------------------------------------------------------
    public static string SeedInstall(string nupkgPath, string version, string localAppData)
    {
        var root = Path.GetFullPath(Path.Combine(localAppData, "Phaket"));
        var target = Path.GetFullPath(Path.Combine(root, "current"));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InstallException("E403 install");
        if (Directory.Exists(root) && File.Exists(Path.Combine(root, "Update.exe")))
            throw new InstallException("E601 exists");   // prawdziwa instalacja Velopack — nie nadpisujemy

        var staging = Path.GetFullPath(Path.Combine(root, "staging"));
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        try
        {
            using (var archive = ZipFile.OpenRead(nupkgPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/');
                    if (relative.Length == 0 || relative.EndsWith('/')) continue;
                    if (relative.StartsWith('/') || relative.Contains("..", StringComparison.Ordinal)) continue;
                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }

            // Paczka Velopack trzyma pliki aplikacji w lib/; lib/ staje się korzeniem instalacji.
            var lib = Path.Combine(staging, "lib");
            var from = Directory.Exists(lib) ? lib : staging;
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(from, target);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);

            File.WriteAllText(Path.Combine(target, "sq.version"),
                string.Join('\n', version, "0.1.0", "win-x64-release", BuildNumber(version)) + "\n");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new InstallException("E601 seed");
        }
    }

    // Instalacja Velopack: %LocalAppData%\Phaket\Phaket.exe to stub uruchamiający current/Phaket.exe.
    public static string? FindExecutable(string installDir) =>
        new[] { Path.Combine(installDir, "Phaket.exe"), Path.Combine(installDir, "current", "Phaket.exe") }
            .FirstOrDefault(File.Exists);

    public static void Launch(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });

    // Numer builda w stylu MSIX (major*10^8 + minor*10^4 + patch), jakiego Velopack używa w sq.version.
    internal static int BuildNumber(string version)
    {
        var key = ManifestParser.VersionKey(version);
        return int.Parse(key[0]) * 1_00_00_00 + int.Parse(key[1]) * 1_00_00 + int.Parse(key[2]);
    }
}