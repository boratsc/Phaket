using System.Net.Http.Headers;

namespace Phaket.Core.Nexus;

// How a Nexus request proves who is calling. Today this is always the personal API
// key in the apikey header; the type also carries Authorization: Bearer so the
// planned OAuth2 sign-in can be added as another provider without touching the client.
public sealed record NexusCredential(string Scheme, string Value)
{
    public static NexusCredential ApiKey(string key) => new("apikey", key);
    public static NexusCredential Bearer(string accessToken) => new("Bearer", accessToken);

    public void Apply(HttpRequestMessage request)
    {
        if (Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Value);
        else
            request.Headers.TryAddWithoutValidation(Scheme, Value);
    }
}

public interface INexusCredentialProvider
{
    Task<NexusCredential?> GetAsync(CancellationToken cancellationToken = default);
}

// Current implementation: the key the user pasted from the API Access page.
public sealed class NexusApiKeyCredentialProvider(NexusApiKeyStore keys) : INexusCredentialProvider
{
    public async Task<NexusCredential?> GetAsync(CancellationToken cancellationToken = default) =>
        await keys.ReadAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } key
            ? NexusCredential.ApiKey(key)
            : null;
}