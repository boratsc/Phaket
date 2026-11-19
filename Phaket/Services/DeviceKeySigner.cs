using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Phaket.Services;

public sealed class DeviceKeySigner : ISignedRequestSigner
{
    private const int MaxBodyBytes = 1_048_576;
    private readonly DeviceKeyStore keyStore;
    private readonly bool registerBeforeSigning;
    public string? DeviceKey => keyStore.CachedDeviceKey;

    public DeviceKeySigner(DeviceKeyStore keyStore, bool registerBeforeSigning = true)
    {
        this.keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        this.registerBeforeSigning = registerBeforeSigning;
    }

    public async Task<IReadOnlyDictionary<string, string>> SignAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null) throw new InvalidOperationException("A request URI is required for signing.");
        if (registerBeforeSigning && !IsDeviceRegistration(request) && !HasMatchingRegistrationHeader(request) &&
            DeviceIdentityServices.Current is { } services && ReferenceEquals(services.KeyStore, keyStore) && services.RegistrationRequired)
            await services.RegisterDeviceAsync(CatalogSettings.Load(), cancellationToken);

        if (request.Content?.Headers.ContentLength is > MaxBodyBytes)
            throw new InvalidDataException("The signed request body exceeds the server limit.");
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (body.Length > MaxBodyBytes) throw new InvalidDataException("The signed request body exceeds the server limit.");
        DevicePublicIdentity identity = await keyStore.GetPublicIdentityAsync(cancellationToken);
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string canonical = BuildCanonical(request.Method.Method, PathAndQuery(request.RequestUri), timestamp, nonce,
            Convert.ToHexStringLower(SHA256.HashData(body)));
        byte[] signature = await keyStore.SignAsync(Encoding.UTF8.GetBytes(canonical), cancellationToken);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Device-Key"] = Convert.ToBase64String(identity.PublicKey),
            ["X-Timestamp"] = timestamp,
            ["X-Nonce"] = nonce,
            ["X-Signature"] = Convert.ToBase64String(signature)
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return headers;
    }

    internal static string CreateCanonicalRepresentation(string method, string pathAndQuery, string timestamp,
        string nonce, ReadOnlySpan<byte> body) => BuildCanonical(method, pathAndQuery, timestamp, nonce,
            Convert.ToHexStringLower(SHA256.HashData(body)));

    internal static string BuildCanonical(string method, string pathAndQuery, string timestamp, string nonce, string bodyHash) =>
        $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{bodyHash}";

    internal static bool TimestampIsWithinWindow(long timestamp, long now) => timestamp >= now - 300 && timestamp <= now + 300;

    internal static bool VerifyForTest(byte[] publicKey, byte[] signature, string canonical)
    {
        try
        {
            var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            verifier.Init(false, new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(publicKey));
            byte[] message = Encoding.UTF8.GetBytes(canonical);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsDeviceRegistration(HttpRequestMessage request) => request.Method == HttpMethod.Post &&
        request.RequestUri is { IsAbsoluteUri: true } uri && uri.AbsolutePath == "/devices";

    private bool HasMatchingRegistrationHeader(HttpRequestMessage request) => request.Headers.TryGetValues("X-Device-Key", out var values) &&
        values.Contains(keyStore.CachedDeviceKey, StringComparer.Ordinal);

    private static string PathAndQuery(Uri uri)
    {
        string path = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
        return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
    }
}
