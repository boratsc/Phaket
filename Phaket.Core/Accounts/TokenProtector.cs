using System.Security.Cryptography;

namespace Phaket.Core.Accounts;

internal sealed class TokenProtector(string storePath, ISecretStore? store = null)
{
    private readonly ISecretStore store = store ?? SecretStoreFactory.Create();
    private readonly string key = Path.GetFileNameWithoutExtension(storePath);

    public async Task ProtectAsync(byte[] data, CancellationToken ct = default)
    {
        try { await store.SetAsync(key, data, ct).ConfigureAwait(false); }
        catch (SecretStoreException ex) { throw new CryptographicException($"{ex.Code}: {ex.Message}", ex); }
    }

    public async Task<byte[]?> UnprotectAsync(CancellationToken ct = default)
    {
        try { return await store.GetAsync(key, ct).ConfigureAwait(false); }
        catch (SecretStoreException ex) { throw new CryptographicException($"{ex.Code}: {ex.Message}", ex); }
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        try { await store.DeleteAsync(key, ct).ConfigureAwait(false); }
        catch (SecretStoreException ex) { throw new CryptographicException($"{ex.Code}: {ex.Message}", ex); }
    }

    internal static byte[] LegacyRead(string path)
    {
        byte[] data = Convert.FromBase64String(File.ReadAllText(path));
        if (OperatingSystem.IsWindows()) return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        if (data.Length < 28) throw new CryptographicException("Invalid account data.");
        byte[] key = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "key.bin"));
        if (key.Length != 32) throw new CryptographicException("Invalid account key.");
        byte[] output = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, output.Length), data.AsSpan(12 + output.Length, 16), output);
        return output;
    }
}
