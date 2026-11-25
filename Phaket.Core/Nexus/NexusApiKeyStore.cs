using System.Security.Cryptography;
using System.Text;
using Phaket.Core.Accounts;

namespace Phaket.Core.Nexus;

// The Nexus API key lives only in the platform secret store (DPAPI / libsecret),
// under its own key so the Settings "delete secrets" action covers it.
// When no native store answers, the key stays in memory for this session only.
public sealed class NexusApiKeyStore
{
    public const string SecretKey = "nexus-apikey";
    // OAuth2+PKCE will store its tokens here; nothing writes this key yet.
    public const string OAuthTokensSecretKey = "nexus-oauth-tokens";

    private readonly Func<ISecretStore> store;
    private string? memoryKey;
    private bool memoryOnly;

    public NexusApiKeyStore(ISecretStore? secretStore = null) : this(() => secretStore ?? SecretStoreFactory.Create()) { }

    public NexusApiKeyStore(Func<ISecretStore> storeAccessor) => store = storeAccessor;

    public bool IsMemoryOnly => memoryOnly;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (memoryOnly) return memoryKey;
        byte[]? data;
        try { data = await store().GetAsync(SecretKey, cancellationToken).ConfigureAwait(false); }
        catch (SecretStoreException) when (!cancellationToken.IsCancellationRequested)
        {
            memoryOnly = true;
            return memoryKey;
        }
        if (data is null) return null;
        try
        {
            string text = Encoding.UTF8.GetString(data).Trim();
            return IsValid(text) ? text : null;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    // Returns false when the platform store refused and the key was kept for this session only.
    public async Task<bool> SaveAsync(string key, CancellationToken cancellationToken = default)
    {
        string trimmed = key.Trim();
        if (!IsValid(trimmed)) throw new ArgumentException("Invalid Nexus API key.", nameof(key));
        bool persisted = true;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(trimmed);
            try { await store().SetAsync(SecretKey, bytes, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            memoryOnly = false;
        }
        catch (SecretStoreException) when (!cancellationToken.IsCancellationRequested)
        {
            persisted = false;
            memoryOnly = true;
        }
        memoryKey = trimmed;
        return persisted;
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        memoryKey = null;
        memoryOnly = false;
        try { await store().DeleteAsync(SecretKey, cancellationToken).ConfigureAwait(false); }
        catch (SecretStoreException) when (!cancellationToken.IsCancellationRequested) { /* nothing persisted to erase */ }
    }

    public async Task SaveOAuthTokensAsync(NexusOAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tokens);
        try { await store().SetAsync(OAuthTokensSecretKey, bytes, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<NexusOAuthTokens?> ReadOAuthTokensAsync(CancellationToken cancellationToken = default)
    {
        byte[]? data;
        try { data = await store().GetAsync(OAuthTokensSecretKey, cancellationToken).ConfigureAwait(false); }
        catch (SecretStoreException) when (!cancellationToken.IsCancellationRequested) { return null; }
        if (data is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<NexusOAuthTokens>(data);
        }
        catch (System.Text.Json.JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public async Task DeleteOAuthTokensAsync(CancellationToken cancellationToken = default)
    {
        try { await store().DeleteAsync(OAuthTokensSecretKey, cancellationToken).ConfigureAwait(false); }
        catch (SecretStoreException) when (!cancellationToken.IsCancellationRequested) { }
    }

    // Nexus keys are a single printable token; anything with spaces or control bytes is a paste error.
    public static bool IsValid(string? key) =>
        key is { Length: > 0 and <= 256 } && key.All(c => c is > ' ' and < (char)127);
}