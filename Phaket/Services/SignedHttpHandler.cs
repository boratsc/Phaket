using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phaket.Services;

public sealed class SignedHttpHandler : DelegatingHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeviceKeyStore keyStore;
    private readonly ISignedRequestSigner signer;
    private readonly Uri endpoint;
    private readonly SemaphoreSlim registrationGate = new(1, 1);
    private volatile bool registered;
    private readonly bool enforceRegistrationResponse;

    public SignedHttpHandler(HttpMessageHandler innerHandler, DeviceKeyStore keyStore,
        ISignedRequestSigner signer, Uri endpoint, bool enforceRegistrationResponse = false) : base(innerHandler)
    {
        this.keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        this.signer = signer ?? throw new ArgumentNullException(nameof(signer));
        this.endpoint = DeviceIdentityServices.NormalizeEndpoint(endpoint);
        this.enforceRegistrationResponse = enforceRegistrationResponse;
    }

    public Task EnsureRegisteredAsync(CancellationToken cancellationToken = default) => RegisterDeviceCoreAsync(false, cancellationToken);

    public Task RegisterDeviceAsync(CancellationToken cancellationToken = default) => RegisterDeviceCoreAsync(true, cancellationToken);

    private async Task RegisterDeviceCoreAsync(bool force, CancellationToken cancellationToken)
    {
        if (registered || (!force && !IsRegistrationRequiredForEndpoint())) { registered = true; return; }
        await registrationGate.WaitAsync(cancellationToken);
        try
        {
            if (registered || (!force && !IsRegistrationRequiredForEndpoint())) { registered = true; return; }
            try
            {
                DevicePublicIdentity identity = await keyStore.GetPublicIdentityAsync(cancellationToken);
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    publicKey = Convert.ToBase64String(identity.PublicKey),
                    nick = identity.Nick
                }, JsonOptions);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/devices"))
                {
                    Content = new ByteArrayContent(body)
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                await ApplySignatureAsync(request, cancellationToken);
                using var response = await base.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (enforceRegistrationResponse)
                {
                    string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var registration = JsonDocument.Parse(responseJson);
                    if (!registration.RootElement.TryGetProperty("fingerprint", out var fingerprint) ||
                        fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString() != identity.Fingerprint[..8])
                        throw new InvalidDataException("The device registration response did not match this key fingerprint.");
                }
                registered = true;
                keyStore.SetOnline(true);
            }
            catch
            {
                registered = false;
                keyStore.SetOnline(false);
                throw;
            }
        }
        finally
        {
            registrationGate.Release();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RequiresSignature(request))
        {
            await EnsureRegisteredAsync(cancellationToken);
            await ApplySignatureAsync(request, cancellationToken);
        }

        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            keyStore.SetOnline(response.StatusCode != HttpStatusCode.Unauthorized && (int)response.StatusCode < 500);
            return response;
        }
        catch
        {
            keyStore.SetOnline(false);
            throw;
        }
    }

    private bool IsRegistrationRequiredForEndpoint() => DeviceIdentityServices.Current is { } services &&
        ReferenceEquals(services.KeyStore, keyStore) && services.RegistrationRequired;

    // Public reads stay unsigned, but the message list is member-only data and the server
    // requires a registered device (and its signature) for it.
    private static readonly Regex CommunityMessagePath = new(
        "^/communities/[A-Za-z0-9-]+/channels/[^/]+/messages$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool RequiresSignature(HttpRequestMessage request) =>
        request.Method != HttpMethod.Get && request.Method != HttpMethod.Head ||
        request.RequestUri is { IsAbsoluteUri: true } uri && CommunityMessagePath.IsMatch(uri.AbsolutePath);

    private async Task ApplySignatureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> headers = await signer.SignAsync(request, cancellationToken);
        foreach (var (name, value) in headers)
            if (!request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
    }
}
