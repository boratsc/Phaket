using System.Security.Cryptography;
using System.Text;

namespace Phaket.Core.Accounts;

// This store must only be selected explicitly after the user has consented.
public sealed class ConsentFileStore(string directory, bool consent, string? password) : ISecretStore
{
    private static readonly byte[] Header = "PHKTCFS1"u8.ToArray();
    private const int Iterations = 210_000;
    private readonly SemaphoreSlim gate = new(1, 1);

    private void CheckConsent()
    {
        if (!consent || string.IsNullOrEmpty(password))
            throw new SecretStoreException("SECRET_STORE_CONSENT_REQUIRED", "Wymagana zgoda i hasło / Consent and password are required.");
    }

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Invalid secret key.", nameof(key));
        return Path.Combine(directory, "consent", key + ".enc");
    }

    private byte[] Decrypt(byte[] data, string key)
    {
        if (data.Length < Header.Length + 16 + 12 + 16 || !data.AsSpan().StartsWith(Header))
            throw new CryptographicException("Invalid secret store format.");
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password!, data.AsSpan(Header.Length, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            int offset = Header.Length + 16;
            byte[] plain = new byte[data.Length - offset - 12 - 16];
            using var aes = new AesGcm(derived, 16);
            aes.Decrypt(data.AsSpan(offset, 12), data.AsSpan(offset + 12, plain.Length), data.AsSpan(data.Length - 16), plain, Encoding.UTF8.GetBytes(key));
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        CheckConsent();
        string path = PathFor(key);
        try { return Decrypt(await File.ReadAllBytesAsync(path, cancellationToken), key); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Nie można odblokować magazynu / Cannot unlock secret store.", ex); }
    }

    public async Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        CheckConsent();
        ArgumentNullException.ThrowIfNull(value);
        string path = PathFor(key);
        await gate.WaitAsync(cancellationToken);
        string secretDirectory = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(secretDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Verify the existing password before replacing a secret.
            byte[]? existing = await GetAsync(key, cancellationToken);
            if (existing is not null) CryptographicOperations.ZeroMemory(existing);
            byte[] data = new byte[Header.Length + 16 + 12 + value.Length + 16];
            Header.CopyTo(data, 0);
            RandomNumberGenerator.Fill(data.AsSpan(Header.Length, 16 + 12));
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password!, data.AsSpan(Header.Length, 16), Iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                int offset = Header.Length + 16;
                using var aes = new AesGcm(derived, 16);
                aes.Encrypt(data.AsSpan(offset, 12), value, data.AsSpan(offset + 12, value.Length), data.AsSpan(data.Length - 16), Encoding.UTF8.GetBytes(key));
            }
            finally { CryptographicOperations.ZeroMemory(derived); }
            Directory.CreateDirectory(secretDirectory);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options)) await stream.WriteAsync(data, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Nie można zapisać sekretu / Cannot save secret.", ex); }
        finally { if (File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        CheckConsent();
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(PathFor(key)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Nie można usunąć sekretu / Cannot delete secret.", ex); }
        return Task.CompletedTask;
    }
}
