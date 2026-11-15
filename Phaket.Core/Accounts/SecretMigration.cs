namespace Phaket.Core.Accounts;

public static class SecretMigration
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SetConsentAsync(string directory, bool consent, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string metadata = Path.Combine(Path.GetFullPath(directory), "secrets-meta");
            string marker = Path.Combine(metadata, "migration-consent");
            if (consent) { Directory.CreateDirectory(metadata); await File.WriteAllTextAsync(marker, "1", ct).ConfigureAwait(false); }
            else if (Directory.Exists(metadata)) File.Delete(marker);
        }
        finally { Gate.Release(); }
    }

    public static async Task MigrateAllAsync(string directory, IEnumerable<string> keys, ISecretStore? store = null, CancellationToken ct = default)
    {
        string fullDirectory = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(fullDirectory, "secrets-meta", "migration-consent")))
            throw new SecretStoreException("SECRET_STORE_CONSENT_REQUIRED", "Wymagana zgoda na migrację / Migration consent is required.");
        string[] validatedKeys = keys.ToArray();
        foreach (string key in validatedKeys)
        {
            if (string.IsNullOrEmpty(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new ArgumentException("Invalid secret key.", nameof(keys));
        }
        foreach (string key in validatedKeys)
        {
            var encrypted = new EncryptedStore(Path.Combine(fullDirectory, key + ".bin"), store);
            await encrypted.LoadAsync(ct).ConfigureAwait(false);
            if (encrypted.NeedsMigration)
                throw new SecretStoreException("SECRET_STORE_CONSENT_REQUIRED", "Wymagana zgoda na migrację / Migration consent is required.");
        }
    }
}
