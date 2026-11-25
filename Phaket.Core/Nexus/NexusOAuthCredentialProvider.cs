using System.Net.Http;

namespace Phaket.Core.Nexus;

// Bearer provider backed by the OAuth tokens in the secret store. An access token
// close to expiry is refreshed first; a rejected refresh logs the user out and the
// terminal NEXUS_OAUTH_REVOKED reaches the UI instead of silently using the key.
public sealed class NexusOAuthCredentialProvider(NexusApiKeyStore keys, HttpClient http,
    Func<NexusOAuthOptions> options, TimeProvider? timeProvider = null) : INexusCredentialProvider
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<NexusCredential?> GetAsync(CancellationToken cancellationToken = default)
    {
        NexusOAuthTokens? tokens = await keys.ReadOAuthTokensAsync(cancellationToken).ConfigureAwait(false);
        if (tokens is null) return null;
        if (tokens.ExpiresAt <= time.GetUtcNow().AddSeconds(30))
        {
            try
            {
                tokens = await NexusOAuth.RefreshAsync(http, options(), tokens, time, cancellationToken).ConfigureAwait(false);
                await keys.SaveOAuthTokensAsync(tokens, cancellationToken).ConfigureAwait(false);
            }
            catch (NexusApiException ex) when (ex.Code == "NEXUS_OAUTH_REVOKED")
            {
                await keys.DeleteOAuthTokensAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (NexusApiException)
            {
                // Temporary failure: the request may still work with the API key fallback.
                return null;
            }
        }
        return NexusCredential.Bearer(tokens.AccessToken);
    }
}

// First provider that has a credential wins: OAuth Bearer, then the personal API key.
public sealed class NexusCredentialChain(params INexusCredentialProvider[] providers) : INexusCredentialProvider
{
    public async Task<NexusCredential?> GetAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            if (await provider.GetAsync(cancellationToken).ConfigureAwait(false) is { } credential) return credential;
        }
        return null;
    }
}