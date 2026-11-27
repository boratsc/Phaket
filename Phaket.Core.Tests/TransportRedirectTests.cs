using System.Net;
using System.Security.Cryptography;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// R2c: the default transport is fail-closed (redirects off, every URL allowlisted). An injected
// client that DOES auto-follow redirects must still be refused after the fact: the final
// RequestUri is checked, and nothing reaches the cache when it left the allowlist.
public sealed class TransportRedirectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-redirect-" + Guid.NewGuid());

    public TransportRedirectTests() => Directory.CreateDirectory(root);

    private sealed class AutoRedirectHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            var current = request;
            // Simulates what HttpClientHandler with AllowAutoRedirect = true would do: the fake
            // resolves the hop itself and reports the FINAL uri on the response, so the transport
            // cannot see the redirect through a 3xx status.
            for (var hops = 0; hops < 5; hops++)
            {
                var response = await send(current, uri);
                if ((int)response.StatusCode is not (>= 300 and < 400) || response.Headers.Location is null)
                {
                    response.RequestMessage = current;
                    return response;
                }
                uri = new Uri(new Uri(uri), response.Headers.Location).AbsoluteUri;
                current = new HttpRequestMessage(HttpMethod.Get, uri);
            }
            throw new InvalidOperationException("too many redirects");
        }
    }

    // A GitHub asset URL; a github.com asset must be https-allowlisted before it is fetched.
    private const string Source = "https://github.com/owner/repo/releases/download/v1/mod.zip";

    // The dangerous shape: an injected client follows the redirect silently and returns 200,
    // naming the off-allowlist URI on the response. Only the RequestUri post-check stands
    // between those bytes and the cache.
    private sealed class SilentRedirectHandler(string finalUrl, byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
                Content = new ByteArrayContent(payload),
            };
            return Task.FromResult(response);
        }
    }

    private static Recipe RecipeNeeding(byte[] payload) => new()
    {
        SchemaVersion = "omrl/v0alpha1",
        Id = "test.redirect", Version = "1.0.0",
        Metadata = new RecipeMetadata { Title = new Dictionary<string, string> { ["pl-PL"] = "T", ["en"] = "T" } },
        Target = new RecipeTarget { Game = "among-us", Systems = ["windows", "linux"] },
        Artifacts = [new RecipeArtifact
        {
            Id = "mod-bundle", Source = Source,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
        }],
        Steps = [],
    };

    private static byte[] Zip() => [0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4];

    [Fact]
    public async Task AutoRedirectToAHostOutsideTheAllowlistIsRefusedBeforeTheBytesAreUsed()
    {
        SourcePolicy.AllowGitHubRepository("owner/repo");
        var bytes = Zip();
        var handler = new AutoRedirectHandler((_, uri) => Task.FromResult(uri.Contains("github.com", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.com/payload.zip") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var transport = new HttpPackageTransport(http);
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(packageRoot);

        var ex = await Assert.ThrowsAsync<PhaketException>(() =>
            PackageDownloader.EnsureArtifactsAsync(RecipeNeeding(bytes), packageRoot, transport,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ModSourceHostNotAllowed, ex.Error.Code);
        // The transport's own redirect loop refuses the Location target before requesting it,
        // so the off-allowlist host is never contacted at all; the artifact never lands in the
        // cache and no temp file is left behind.
        Assert.DoesNotContain(handler.Requests, r => r.Contains("evil.example.com", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(packageRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SilentAutoRedirectLeavingTheAllowlistIsRefusedByTheRequestUriCheck()
    {
        SourcePolicy.AllowGitHubRepository("owner/repo");
        var bytes = Zip();
        using var http = new HttpClient(new SilentRedirectHandler("https://evil.example.com/payload.zip", bytes))
            { Timeout = TimeSpan.FromSeconds(5) };
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(packageRoot);

        var ex = await Assert.ThrowsAsync<PhaketException>(() =>
            PackageDownloader.EnsureArtifactsAsync(RecipeNeeding(bytes), packageRoot,
                new HttpPackageTransport(http), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ModSourceHostNotAllowed, ex.Error.Code);
        Assert.Equal("evil.example.com", ex.Error.Parameters["host"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(packageRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AutoRedirectOnTheAllowlistIsFollowedAndTheArtifactCached()
    {
        SourcePolicy.AllowGitHubRepository("owner/repo");
        var bytes = Zip();
        var handler = new AutoRedirectHandler((_, uri) => Task.FromResult(uri.Contains("github.com", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.githubusercontent.com/release/mod.zip") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var packageRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(packageRoot);

        var downloaded = await PackageDownloader.EnsureArtifactsAsync(RecipeNeeding(bytes), packageRoot,
            new HttpPackageTransport(http), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(packageRoot, "mod-bundle.zip"), Assert.Single(downloaded));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded[0], TestContext.Current.CancellationToken));
    }

    public void Dispose() { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
}
