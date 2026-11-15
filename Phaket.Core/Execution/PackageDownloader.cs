using System.Security.Cryptography;
using Phaket.Core.Omrl;

namespace Phaket.Core.Execution;

// Transport seam so hosts (and tests) can serve artifact bytes without network.
public interface IPackageTransport
{
    Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken);
}

public sealed class HttpPackageTransport : IPackageTransport
{
    private readonly HttpClient http;
    public HttpPackageTransport(HttpClient? http = null) =>
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
    {
        var response = await this.http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}

public sealed record ArtifactDownloadProgress(string ArtifactId, long BytesReceived, long? TotalBytes);

// Fetches remote recipe artifacts into the package cache BEFORE execution.
// Trust boundary: https allowlist only, declared sha256 is mandatory, declared
// size is an upper bound, writes are atomic. Local package:// artifacts are
// never touched. The executor itself never downloads.
public static class PackageDownloader
{
    // Hard cap for artifacts without a declared size: never fill the disk.
    private const long MaxUndeclaredBytes = 512L * 1024 * 1024;

    public static async Task<IReadOnlyList<string>> EnsureArtifactsAsync(
        Recipe recipe, string packageRoot, IPackageTransport transport,
        IProgress<ArtifactDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(packageRoot);
        var downloaded = new List<string>();
        foreach (var artifact in recipe.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!artifact.Source.StartsWith("https://", StringComparison.Ordinal)) continue;
            var target = Path.Combine(packageRoot, artifact.Id + ".zip");
            if (!SourcePolicy.IsAllowedHttpsSource(artifact.Source, artifact.Sha256))
                throw new PhaketException(ErrorCodes.ArtifactDownloadUnavailable,
                    ("artifact", artifact.Id), ("detail", "source is not on the https allowlist"));
            if (artifact.Sha256 is null)
                throw new PhaketException(ErrorCodes.ValidationFailed,
                    ("detail", $"remote artifact '{artifact.Id}' declares no sha256; refusing unverified download"));
            if (File.Exists(target) && HashFile(target) == artifact.Sha256) continue;

            var temp = target + ".phaket-tmp";
            try
            {
                await using (var source = await transport.OpenReadAsync(artifact.Source, cancellationToken))
                await using (var destination = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var limit = artifact.SizeBytes ?? MaxUndeclaredBytes;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        received += read;
                        if (received > limit)
                            throw new PhaketException(ErrorCodes.ArtifactDownloadUnavailable,
                                ("artifact", artifact.Id), ("detail", "download exceeds declared size"));
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report(new ArtifactDownloadProgress(artifact.Id, received, artifact.SizeBytes));
                    }
                }
                if (HashFile(temp) != artifact.Sha256)
                    throw new PhaketException(ErrorCodes.ArtifactHashMismatch, ("artifact", artifact.Id));
                File.Move(temp, target, overwrite: true);
                downloaded.Add(target);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort temp cleanup */ }
                throw;
            }
        }
        return downloaded;
    }

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
