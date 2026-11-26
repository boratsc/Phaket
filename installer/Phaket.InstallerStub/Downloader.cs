using System.Security.Cryptography;

namespace Phaket.InstallerStub;

public static class Downloader
{
    /// <summary>
    /// Pobiera wpis z manifestu do katalogu roboczego, licząc SHA-256 w trakcie.
    /// Niezgodny hash kasuje plik i przerywa instalację; nazwa pliku jest sprawdzana, żeby
    /// manifest nie mógł wskazać ścieżki poza katalogiem roboczym.
    /// </summary>
    public static async Task<string> DownloadVerifiedAsync(
        HttpClient http, TimeSpan timeout, Uri manifestUri, ReleaseEntry entry, string workDirectory,
        IProgress<int>? percent, CancellationToken ct)
    {
        var localName = ManifestParser.LocalName(entry.File);
        if (localName.Length == 0) throw new InstallException("E403 File");
        Directory.CreateDirectory(workDirectory);
        var root = Path.GetFullPath(workDirectory + Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, localName));
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InstallException("E403 File");

        try
        {
            // File bywa absolutnym URL-em (feed generowany przez scripts/feed-manifest.py).
            var target = Uri.TryCreate(entry.File, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps
                ? absolute : new Uri(manifestUri, entry.File);
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InstallException(Messages.Code("N" + (int)response.StatusCode, "package"));
            var total = response.Content.Headers.ContentLength ?? 0;

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                long done = 0;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) percent?.Report((int)Math.Min(100, done * 100 / total));
                }
            }
            percent?.Report(100);

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                throw new InstallException("E500 SHA256");
            }
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(path);
            throw new InstallException("E501 download");
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}