using System.Security.Cryptography;
using System.Text;

namespace Phaket.Core.Accounts;

public sealed class WindowsDpapiStore(string directory) : ISecretStore, ISecretKeyDeleter
{
    private static readonly byte[] Header = "PHKT2\x01"u8.ToArray();
    private static readonly byte[] Entropy = SHA256.HashData("org.phaket.secretstore.v1"u8);

    internal static string PathFor(string directory, string key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Invalid secret key.", nameof(key));
        return Path.Combine(directory, "secrets", key + ".dpapi");
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        SecretStoreFactory.CheckNativeAccess();
        string path = PathFor(directory, key);
        if (!OperatingSystem.IsWindows()) throw UnavailablePlatform();
        try
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(await File.ReadAllTextAsync(path, cancellationToken)); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            bool current = raw.AsSpan().StartsWith(Header);
            return ProtectedData.Unprotect(current ? raw[Header.Length..] : raw, current ? Entropy : null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or FormatException)
        { throw Unavailable(ex); }
    }

    public async Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        SecretStoreFactory.CheckNativeAccess();
        string path = PathFor(directory, key);
        if (!OperatingSystem.IsWindows()) throw UnavailablePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        string secretDirectory = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(secretDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] encrypted = ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
            byte[] data = new byte[Header.Length + encrypted.Length];
            Header.CopyTo(data, 0);
            encrypted.CopyTo(data, Header.Length);
            Directory.CreateDirectory(secretDirectory);
            await File.WriteAllTextAsync(temp, Convert.ToBase64String(data), Encoding.ASCII, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { throw Unavailable(ex); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        SecretStoreFactory.CheckNativeAccess();
        string path = PathFor(directory, key);
        if (!OperatingSystem.IsWindows()) throw UnavailablePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Unavailable(ex); }
        return Task.CompletedTask;
    }

    // DPAPI unlocks with the logged-in user, so "delete secrets" needs no extra password here (PAK-61).
    public bool TryDelete(string key)
    {
        try { DeleteAsync(key).GetAwaiter().GetResult(); return true; }
        catch (Exception e) when (e is SecretStoreException or OperationCanceledException) { return false; }
    }

    private static SecretStoreException UnavailablePlatform() => new("SECRET_STORE_UNAVAILABLE", "Magazyn DPAPI jest niedostępny / DPAPI store is unavailable.");

    private static SecretStoreException Unavailable(Exception ex) => new("SECRET_STORE_UNAVAILABLE", "Magazyn sekretów jest niedostępny / Secret store is unavailable.", ex);
}
