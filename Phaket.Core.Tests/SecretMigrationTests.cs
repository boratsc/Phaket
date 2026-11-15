using System.Security.Cryptography;
using Phaket.Core.Accounts;
using Phaket.Core.Tests;

public sealed class SecretMigrationTests
{
    private static readonly byte[] Plain = [1, 2, 3];

    private static string CreateLegacy(string directory, string key = "epic")
    {
        byte[] blob;
        if (OperatingSystem.IsWindows()) blob = ProtectedData.Protect(Plain, null, DataProtectionScope.CurrentUser);
        else
        {
            byte[] secret = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12);
            File.WriteAllBytes(Path.Combine(directory, "key.bin"), secret);
            blob = new byte[12 + Plain.Length + 16];
            nonce.CopyTo(blob, 0);
            using var aes = new AesGcm(secret, 16);
            aes.Encrypt(nonce, Plain, blob.AsSpan(12, Plain.Length), blob.AsSpan(12 + Plain.Length, 16));
        }
        string path = Path.Combine(directory, key + ".bin");
        File.WriteAllText(path, Convert.ToBase64String(blob));
        return path;
    }

    [Fact]
    public async Task ConsentMigratesAndRecordsBackupAndIsIdempotent()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = CreateLegacy(dir), original = File.ReadAllText(path);
            var memory = new InMemorySecretStore();
            await SecretMigration.SetConsentAsync(dir, true, TestContext.Current.CancellationToken);
            var store = new EncryptedStore(path, memory);
            Assert.Equal(Plain, store.Load());
            Assert.False(store.NeedsMigration);
            Assert.False(File.Exists(path));
            Assert.Equal(original, File.ReadAllText(Path.Combine(dir, "migrated", "epic.bak")));
            Assert.True(File.Exists(Path.Combine(dir, "secrets-meta", "epic.migrated")));
            await SecretMigration.MigrateAllAsync(dir, ["epic", "epic"], memory, TestContext.Current.CancellationToken);
            Assert.Equal(Plain, store.Load());
            await memory.DeleteAsync("epic", TestContext.Current.CancellationToken);
            Assert.Contains("SECRET_STORE_MIGRATION_FAILED", Assert.Throws<CryptographicException>(() => store.Load()).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FailedVerificationKeepsLegacyAndDoesNotReturnCorruptValue()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = CreateLegacy(dir), original = File.ReadAllText(path);
            await SecretMigration.SetConsentAsync(dir, true, TestContext.Current.CancellationToken);
            var store = new EncryptedStore(path, new CorruptStore());
            Assert.Contains("SECRET_STORE_MIGRATION_FAILED", Assert.Throws<CryptographicException>(() => store.Load()).Message);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(Path.Combine(dir, "migrated", "epic.bak")));
            Assert.Contains("SECRET_STORE_MIGRATION_FAILED", Assert.Throws<CryptographicException>(() => store.Load()).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task NoConsentReadsLegacyButDoesNotMigrate()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = CreateLegacy(dir);
            var memory = new InMemorySecretStore();
            var store = new EncryptedStore(path, memory);
            Assert.Equal(Plain, store.Load());
            Assert.True(store.NeedsMigration);
            Assert.True(File.Exists(path));
            Assert.Null(await memory.GetAsync("epic", TestContext.Current.CancellationToken));
            Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", (await Assert.ThrowsAsync<SecretStoreException>(() => SecretMigration.MigrateAllAsync(dir, ["epic"], memory, TestContext.Current.CancellationToken))).Code);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SimulatedWindowsPathsCoexistDuringMigration()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = CreateLegacy(dir), original = File.ReadAllText(path);
            var fake = new SimulatedDpapiStore(dir);
            await fake.SetAsync("other", [5], TestContext.Current.CancellationToken);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(WindowsDpapiStore.PathFor(dir, "other")));
            await SecretMigration.SetConsentAsync(dir, true, TestContext.Current.CancellationToken);
            await SecretMigration.MigrateAllAsync(dir, ["epic"], fake, TestContext.Current.CancellationToken);
            Assert.Equal(Plain, await fake.GetAsync("epic", TestContext.Current.CancellationToken));
            Assert.Equal(original, File.ReadAllText(Path.Combine(dir, "migrated", "epic.bak")));
            Assert.True(File.Exists(WindowsDpapiStore.PathFor(dir, "epic")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StoreBlobsNeverShareTheLegacyPathForTheSameKey()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = CreateLegacy(dir), original = File.ReadAllText(path);
            Assert.Equal(Path.Combine(dir, "epic.bin"), path);
            Assert.NotEqual(path, WindowsDpapiStore.PathFor(dir, "epic"));
            Assert.Equal(Path.Combine(dir, "secrets", "epic.dpapi"), WindowsDpapiStore.PathFor(dir, "epic"));

            var fake = new SimulatedDpapiStore(dir);
            await fake.SetAsync("epic", Plain, TestContext.Current.CancellationToken);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.True(File.Exists(Path.Combine(dir, "secrets", "epic.dpapi")));

            var consent = new ConsentFileStore(dir, true, "password");
            await consent.SetAsync("epic", Plain, TestContext.Current.CancellationToken);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(Path.Combine(dir, "consent", "epic.enc"), Directory.EnumerateFiles(Path.Combine(dir, "consent")).Single());
            Assert.True(File.Exists(Path.Combine(dir, "epic.bin")));

            // Both stores wrote next to the legacy file; a load must not move or rewrite it.
            var store = new EncryptedStore(path, fake);
            Assert.Equal(Plain, store.Load());
            Assert.False(store.NeedsMigration);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(Path.Combine(dir, "migrated", "epic.bak")));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class SimulatedDpapiStore(string directory) : ISecretStore
    {
        public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            string path = WindowsDpapiStore.PathFor(directory, key);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        public async Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
        {
            string path = WindowsDpapiStore.PathFor(directory, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, value, cancellationToken);
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            File.Delete(WindowsDpapiStore.PathFor(directory, key));
            return Task.CompletedTask;
        }
    }

    private sealed class CorruptStore : ISecretStore
    {
        private byte[]? value;
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task SetAsync(string key, byte[] data, CancellationToken cancellationToken = default) { value = [99]; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { value = null; return Task.CompletedTask; }
    }
}
