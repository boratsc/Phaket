using System.Security.Cryptography;
using System.Text.Json;

namespace Phaket.Core.Accounts;

// Steam sign-in used only for game installs (PAK-60, ADR-023/047): the user logs
// in once in Phaket, the credentials go to the platform secret store and the
// install adapter feeds them to the tool's standard input on every later install.
public sealed class SteamInstallCredentials(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;

    // Never let the password reach a log, an exception message or a trace.
    public override string ToString() => Username + " · [REDACTED]";
}

public sealed class SteamCredentialStore(ISecretStore? secretStore = null)
{
    // Single fixed key in the platform store; also listed by the Settings "delete secrets" action.
    public const string SecretKey = "steam-install-credentials";

    private readonly ISecretStore store = secretStore ?? SecretStoreFactory.Create();

    public async Task<SteamInstallCredentials?> ReadAsync(CancellationToken ct = default)
    {
        byte[]? data = await store.GetAsync(SecretKey, ct).ConfigureAwait(false);
        if (data is null) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<CredentialBlob>(data);
            if (parsed is null || !IsValidUsername(parsed.Username?.Trim() ?? "") || !IsValidPassword(parsed.Password ?? ""))
                throw new JsonException("Invalid credential record.");
            return new SteamInstallCredentials(parsed.Username!.Trim(), parsed.Password!);
        }
        catch (JsonException ex)
        { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Zapisane poświadczenia są uszkodzone / Stored credentials are corrupt.", ex); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public async Task SaveAsync(string username, string password, CancellationToken ct = default)
    {
        string account = username.Trim();
        if (!IsValidUsername(account)) throw new ArgumentException("Invalid Steam account name.", nameof(username));
        if (!IsValidPassword(password)) throw new ArgumentException("Invalid Steam password.", nameof(password));
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new CredentialBlob(account, password));
        try { await store.SetAsync(SecretKey, data, ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public Task DeleteAsync(CancellationToken ct = default) => store.DeleteAsync(SecretKey, ct);

    // DepotDownloader takes the account name as an argument and the password as one stdin
    // line, so neither may contain control characters and both must stay within bounds.
    internal static bool IsValidUsername(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() == value && value.Length <= 128 && !ContainsControl(value);
    internal static bool IsValidPassword(string value) => !string.IsNullOrEmpty(value) && value.Length <= 1024 && !ContainsControl(value);
    private static bool ContainsControl(string value) { foreach (char c in value) if (char.IsControl(c)) return true; return false; }

    private sealed record CredentialBlob(string? Username, string? Password);
}
