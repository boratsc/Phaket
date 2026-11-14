using System.Security.Cryptography;

namespace Phaket.Core.Accounts;

internal interface ITokenProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

internal sealed class TokenProtector(string storePath) : ITokenProtector
{
    private string KeyPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storePath))!, "key.bin");

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private byte[] Key()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        try
        {
            using var file = new FileStream(KeyPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            byte[] key = RandomNumberGenerator.GetBytes(32);
            file.Write(key);
            return key;
        }
        catch (IOException) when (File.Exists(KeyPath)) { }
        File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        byte[] existing = File.ReadAllBytes(KeyPath);
        if (existing.Length != 32) throw new CryptographicException("Invalid account key.");
        return existing;
    }

    public byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        byte[] output = new byte[12 + data.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(output.AsSpan(0, 12), data, output.AsSpan(12, data.Length), output.AsSpan(12 + data.Length, 16));
        return output;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        if (data.Length < 28) throw new CryptographicException("Invalid account data.");
        byte[] output = new byte[data.Length - 28];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, output.Length), data.AsSpan(12 + output.Length, 16), output);
        return output;
    }
}
