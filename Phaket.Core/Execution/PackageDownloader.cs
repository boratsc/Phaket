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
    private static readonly HashSet<string> GitHubAssetHosts = new(StringComparer.OrdinalIgnoreCase)
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    public HttpPackageTransport(HttpClient? http = null, TimeSpan? timeout = null)
    {
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        this.timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (this.timeout <= TimeSpan.Zero && this.timeout != System.Threading.Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<Stream> OpenReadAsync(string url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!SourcePolicy.IsAllowedTransportSource(current)
                || GitHubAssetHosts.Contains(new Uri(current).Host) && !SourcePolicy.IsAllowedHttpsSource(current))
                throw new PhaketException(ErrorCodes.ModSourceHostNotAllowed,
                    ("host", Uri.TryCreate(current, UriKind.Absolute, out var blocked) ? blocked.Host : "invalid"));
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var timeoutCancellation = new CancellationTokenSource(timeout);
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
            {
                linkedCancellation.Dispose();
                timeoutCancellation.Dispose();
                throw new PhaketException(ErrorCodes.ArtifactDownloadTimeout, ("artifact", current));
            }
            catch
            {
                linkedCancellation.Dispose();
                timeoutCancellation.Dispose();
                throw;
            }
            if (response.RequestMessage?.RequestUri is { } finalUri
                && (!SourcePolicy.IsAllowedTransportSource(finalUri.AbsoluteUri)
                    || GitHubAssetHosts.Contains(finalUri.Host) && !SourcePolicy.IsAllowedHttpsSource(finalUri.AbsoluteUri)))
            {
                response.Dispose();
                linkedCancellation.Dispose();
                timeoutCancellation.Dispose();
                throw new PhaketException(ErrorCodes.ModSourceHostNotAllowed, ("host", finalUri.Host));
            }
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = new Uri(new Uri(current), location);
                response.Dispose();
                linkedCancellation.Dispose();
                timeoutCancellation.Dispose();
                if (!SourcePolicy.IsAllowedTransportSource(next.AbsoluteUri)
                    || GitHubAssetHosts.Contains(next.Host) && !SourcePolicy.IsAllowedHttpsSource(next.AbsoluteUri))
                    throw new PhaketException(ErrorCodes.ModSourceHostNotAllowed, ("host", next.Host));
                current = next.AbsoluteUri;
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                return new ResponseStream(await response.Content.ReadAsStreamAsync(linkedCancellation.Token), response, linkedCancellation, timeoutCancellation);
            }
            catch
            {
                response.Dispose();
                linkedCancellation.Dispose();
                timeoutCancellation.Dispose();
                throw;
            }
        }
        throw new PhaketException(ErrorCodes.ArtifactDownloadUnavailable,
            ("artifact", url), ("detail", "too many redirects"));
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response,
        CancellationTokenSource linkedCancellation, CancellationTokenSource timeoutCancellation) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, linkedCancellation.Token);
            try { return await inner.ReadAsync(buffer, linked.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
            {
                throw new PhaketException(ErrorCodes.ArtifactDownloadTimeout, ("artifact", response.RequestMessage?.RequestUri?.AbsoluteUri ?? "download"));
            }
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, linkedCancellation.Token);
            try { return await inner.ReadAsync(buffer, offset, count, linked.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
            {
                throw new PhaketException(ErrorCodes.ArtifactDownloadTimeout, ("artifact", response.RequestMessage?.RequestUri?.AbsoluteUri ?? "download"));
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); linkedCancellation.Dispose(); timeoutCancellation.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            linkedCancellation.Dispose();
            timeoutCancellation.Dispose();
            await base.DisposeAsync();
        }
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
            // Remote artifacts land in the package root under their artifact id; a
            // bare-file artifact (single plugin .dll) keeps that name without ".zip".
            var target = Path.Combine(packageRoot, artifact.Id + (artifact.Format == "zip" ? ".zip" : ""));
            var githubAsset = SourcePolicy.IsGitHubAssetUrl(artifact.Source);
            if (!SourcePolicy.IsAllowedHttpsSource(artifact.Source, artifact.Sha256))
                throw new PhaketException(ErrorCodes.ArtifactDownloadUnavailable,
                    ("artifact", artifact.Id), ("detail", "source is not on the https allowlist"));
            if (artifact.Sha256 is null && !githubAsset)
                throw new PhaketException(ErrorCodes.ValidationFailed,
                    ("detail", $"remote artifact '{artifact.Id}' declares no sha256; refusing unverified download"));
            if (artifact.SizeBytes is < 0)
                throw new PhaketException(ErrorCodes.ModSourceInvalid, ("source", githubAsset ? "GitHub" : "remote"), ("detail", "negative expected size"));
            if (File.Exists(target) && artifact.Sha256 is { } cachedHash && HashFile(target) == cachedHash)
            {
                if (githubAsset && artifact.SizeBytes is { } expectedSize && new FileInfo(target).Length != expectedSize)
                    throw SizeMismatch(artifact.Id, new FileInfo(target).Length, expectedSize);
                continue;
            }

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
                            throw githubAsset && artifact.SizeBytes is { } expected
                                ? SizeMismatch(artifact.Id, received, expected)
                                : new PhaketException(ErrorCodes.ArtifactDownloadUnavailable,
                                    ("artifact", artifact.Id), ("detail", "download exceeds declared size"));
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report(new ArtifactDownloadProgress(artifact.Id, received, artifact.SizeBytes));
                    }
                }
                if (githubAsset && artifact.SizeBytes is { } exactSize && new FileInfo(temp).Length != exactSize)
                    throw SizeMismatch(artifact.Id, new FileInfo(temp).Length, exactSize);
                if (artifact.Sha256 is { } expectedHash && HashFile(temp) != expectedHash)
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

    private static PhaketException SizeMismatch(string artifact, long actual, long expected) =>
        new(ErrorCodes.ArtifactSizeMismatch, ("artifact", artifact),
            ("actual", actual.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("expected", expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
