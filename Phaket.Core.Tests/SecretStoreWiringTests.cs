using System.Security.Cryptography;
using Phaket.Core.Accounts;
using Phaket.Core.Tests;

public sealed class SecretStoreWiringTests
{
    [Fact]
    public async Task RoundTripAndDelete()
    {
        var memory = new InMemorySecretStore();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "epic.bin");
        var store = new EncryptedStore(path, memory);
        byte[] data = [1, 2, 3];
        await store.SaveAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(data, await memory.GetAsync("epic", TestContext.Current.CancellationToken));
        Assert.Equal(data, store.Load());
        store.Delete();
        Assert.Null(store.Load());
    }

    [Fact]
    public void ReadsLegacyWithoutWriting()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "epic.bin");
            byte[] plain = [1, 2, 3], blob;
            if (OperatingSystem.IsWindows()) blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            else
            {
                byte[] key = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12);
                File.WriteAllBytes(Path.Combine(dir, "key.bin"), key);
                blob = new byte[12 + plain.Length + 16]; nonce.CopyTo(blob, 0);
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(nonce, plain, blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length, 16));
            }
            File.WriteAllText(path, Convert.ToBase64String(blob));
            var store = new EncryptedStore(path, new InMemorySecretStore());
            Assert.Equal(plain, store.Load());
            Assert.True(store.NeedsMigration);
            Assert.Equal(Convert.ToBase64String(blob), File.ReadAllText(path));
            store.Delete();
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ErrorsPreserveCode()
    {
        var store = new EncryptedStore("epic.bin", new FailingStore());
        var ex = await Assert.ThrowsAsync<CryptographicException>(() => store.SaveAsync([1], TestContext.Current.CancellationToken));
        Assert.Contains("SECRET_STORE_LOCKED", ex.Message);
        Assert.Contains("SECRET_STORE_LOCKED", Assert.Throws<CryptographicException>(() => store.Load()).Message);
        Assert.Contains("SECRET_STORE_LOCKED", Assert.Throws<CryptographicException>(() => store.Delete()).Message);
    }

    private sealed class FailingStore : ISecretStore
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => throw Error();
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw Error();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw Error();
        private static SecretStoreException Error() => new("SECRET_STORE_LOCKED", "Locked");
    }
}
