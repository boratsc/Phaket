using System.Net.Http;
using Phaket.Core.Accounts;

namespace Phaket.Services;

public sealed class DeviceIdentityServices
{
    private static DeviceIdentityServices? current;
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);
    private static readonly object RegistrationIdentityGate = new();
    private static string? registeredDeviceAndEndpoint;
    private readonly Func<HttpMessageHandler> httpMessageHandlerFactory;

    public static DeviceIdentityServices? Current => Volatile.Read(ref current);
    public DeviceKeyStore KeyStore { get; }
    public ISignedRequestSigner Signer { get; }
    public ISignedRequestSigner CommunitySigner { get; }
    private int registrationRequired;

    public DeviceIdentityServices(DeviceKeyStore keyStore, ISignedRequestSigner signer,
        Func<HttpMessageHandler>? httpMessageHandlerFactory = null)
    {
        KeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        Signer = signer ?? throw new ArgumentNullException(nameof(signer));
        this.httpMessageHandlerFactory = httpMessageHandlerFactory ?? (() => new HttpClientHandler());
        CommunitySigner = new DeviceKeySigner(KeyStore, registerBeforeSigning: false);
        registrationRequired = 1;
        KeyStore.RegistrationStateChanged += (_, _) =>
        {
            if (!KeyStore.IsOnline) Interlocked.Exchange(ref registrationRequired, 1);
        };
    }

    public static void SetCurrent(DeviceIdentityServices services) => Volatile.Write(ref current, services ?? throw new ArgumentNullException(nameof(services)));

    public HttpClient CreateHttpClient(string endpoint)
    {
        Uri address = NormalizeEndpoint(endpoint);
        return CreateHttpClient(address, CommunitySigner, TimeSpan.FromSeconds(15), httpMessageHandlerFactory());
    }

    public HttpProfilePublishService CreateProfilePublisher(string endpoint) =>
        CreateProfilePublisher(endpoint, httpMessageHandlerFactory());

    public HttpProfilePublishService CreateProfilePublisher(string endpoint, HttpMessageHandler handler)
    {
        Uri address = NormalizeEndpoint(endpoint);
        return new(CreateHttpClient(address, Signer, TimeSpan.FromSeconds(15), handler), address, Signer);
    }

    public async Task RegisterDeviceAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        Uri normalized = NormalizeEndpoint(endpoint);
        // One identity per device and endpoint: always the fingerprint, never the base64 key,
        // so the same device is not registered twice under two different ids.
        string registrationId = (await KeyStore.GetPublicIdentityAsync(cancellationToken).ConfigureAwait(false)).Fingerprint
            + "|" + normalized.AbsoluteUri;
        await RegistrationGate.WaitAsync(cancellationToken);
        try
        {
            lock (RegistrationIdentityGate)
            {
                if (registeredDeviceAndEndpoint == registrationId && KeyStore.IsOnline)
                {
                    Interlocked.Exchange(ref registrationRequired, 0);
                    return;
                }
            }
            using var handler = new SignedHttpHandler(httpMessageHandlerFactory(), KeyStore, Signer, normalized, enforceRegistrationResponse: true);
            try
            {
                await handler.RegisterDeviceAsync(cancellationToken);
                lock (RegistrationIdentityGate) registeredDeviceAndEndpoint = registrationId;
                Interlocked.Exchange(ref registrationRequired, 0);
            }
            catch
            {
                lock (RegistrationIdentityGate) registeredDeviceAndEndpoint = null;
                Interlocked.Exchange(ref registrationRequired, 1);
                throw;
            }
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    internal bool RegistrationRequired => Volatile.Read(ref registrationRequired) != 0;

    internal static Uri NormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new UriFormatException("A valid HTTP or HTTPS API endpoint is required.");
        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
            throw new UriFormatException("A valid HTTP or HTTPS API endpoint is required.");
        return NormalizeEndpoint(uri);
    }

    private HttpClient CreateHttpClient(Uri address, ISignedRequestSigner signer, TimeSpan timeout, HttpMessageHandler innerHandler) => new(
        new SignedHttpHandler(innerHandler, KeyStore, signer, address, enforceRegistrationResponse: true), true)
    {
        BaseAddress = address,
        Timeout = timeout
    };
}
