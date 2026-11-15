using System.Text;
using Phaket.Core.Accounts;
using Phaket.Core.Tests;

// Steam install credentials in the platform secret store (PAK-60): round-trip,
// deletion, validation, distinct SECRET_STORE_* errors and no password leaks.
public sealed class SteamCredentialStoreTests
{
    private const string User = "phaket.tester";
    private const string Pass = "S3kretne hasło!";

    [Fact]
    public async Task RoundTripAndDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemorySecretStore();
        var store = new SteamCredentialStore(memory);
        Assert.Null(await store.ReadAsync(ct));
        await store.SaveAsync(User, Pass, ct);
        var read = await store.ReadAsync(ct);
        Assert.Equal(User, read!.Username);
        Assert.Equal(Pass, read.Password);
        // Everything lives under the single documented key, nowhere else.
        Assert.NotNull(await memory.GetAsync(SteamCredentialStore.SecretKey, ct));
        await store.DeleteAsync(ct);
        Assert.Null(await store.ReadAsync(ct));
        Assert.Null(await memory.GetAsync(SteamCredentialStore.SecretKey, ct));
    }

    [Fact]
    public async Task UsernameIsTrimmedBeforeSaving()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore(new InMemorySecretStore());
        await store.SaveAsync("  " + User + "  ", Pass, ct);
        Assert.Equal(User, (await store.ReadAsync(ct))!.Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("new\nline")]
    [InlineData("tab\tname")]
    [InlineData("nul\0char")]
    public async Task InvalidUsernamesAreRejectedAndNothingIsStored(string username)
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemorySecretStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new SteamCredentialStore(memory).SaveAsync(username, Pass, ct));
        Assert.Null(await memory.GetAsync(SteamCredentialStore.SecretKey, ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("with\nnewline")]
    [InlineData("with\r\nline break")]
    [InlineData("bell\at")]
    public async Task InvalidPasswordsAreRejectedAndNothingIsStored(string password)
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemorySecretStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new SteamCredentialStore(memory).SaveAsync(User, password, ct));
        Assert.Null(await memory.GetAsync(SteamCredentialStore.SecretKey, ct));
    }

    [Fact]
    public async Task OverLongValuesAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore(new InMemorySecretStore());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new string('a', 129), Pass, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(User, new string('b', 1025), ct));
        await store.SaveAsync(new string('a', 128), new string('b', 1024), ct);
        Assert.NotNull(await store.ReadAsync(ct));
    }

    [Fact]
    public async Task CorruptRecordReportsUnavailableWithoutLeakingPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemorySecretStore();
        const string secret = "top-secret-password";
        await memory.SetAsync(SteamCredentialStore.SecretKey, Encoding.UTF8.GetBytes("{\"username\":\"u\",\"password\":\"" + secret + "\"} trailing"), ct);
        var error = await Assert.ThrowsAsync<SecretStoreException>(() => new SteamCredentialStore(memory).ReadAsync(ct));
        Assert.Equal("SECRET_STORE_UNAVAILABLE", error.Code);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRecordIsNotAnErrorAndDeleteIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore(new InMemorySecretStore());
        Assert.Null(await store.ReadAsync(ct));
        await store.DeleteAsync(ct);
        Assert.Null(await store.ReadAsync(ct));
    }

    [Theory]
    [InlineData("SECRET_STORE_LOCKED")]
    [InlineData("SECRET_STORE_TIMEOUT")]
    [InlineData("SECRET_STORE_UNAVAILABLE")]
    [InlineData("SECRET_STORE_CONSENT_REQUIRED")]
    public async Task StoreFailuresKeepTheirOwnCodeOnEveryOperation(string code)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore(new FailingStore(code));
        Assert.Equal(code, (await Assert.ThrowsAsync<SecretStoreException>(() => store.ReadAsync(ct))).Code);
        Assert.Equal(code, (await Assert.ThrowsAsync<SecretStoreException>(() => store.SaveAsync(User, Pass, ct))).Code);
        Assert.Equal(code, (await Assert.ThrowsAsync<SecretStoreException>(() => store.DeleteAsync(ct))).Code);
    }

    [Fact]
    public async Task ToStringNeverExposesThePassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore(new InMemorySecretStore());
        await store.SaveAsync(User, Pass, ct);
        var read = await store.ReadAsync(ct);
        Assert.DoesNotContain(Pass, read!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Pass, $"{read}", StringComparison.Ordinal);
        Assert.Contains(User, read.ToString(), StringComparison.Ordinal);
    }

    // Opt-in (PHAKET_KEYRING_PROBE=1): the fixed key must be accepted by the real
    // native provider, not just by the in-memory fake.
    [Fact]
    public async Task NativeProviderAcceptsTheFixedKey()
    {
        if (Environment.GetEnvironmentVariable("PHAKET_KEYRING_PROBE") != "1") Assert.Skip("Set PHAKET_KEYRING_PROBE=1 to run the native keyring probe.");
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux only");
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore(new LinuxLibsecretStore());
        try
        {
            await store.SaveAsync(User, Pass, ct);
            Assert.Equal(Pass, (await store.ReadAsync(ct))!.Password);
            await store.DeleteAsync(ct);
            Assert.Null(await store.ReadAsync(ct));
        }
        catch (SecretStoreException ex) when (ex.Code is "SECRET_STORE_UNAVAILABLE" or "SECRET_STORE_LOCKED" or "SECRET_STORE_TIMEOUT")
        { Assert.Skip("Secret Service unavailable: " + ex.Code); }
    }

    // The default store is the guarded factory: in tests the in-memory provider, never
    // libsecret or DPAPI, so the PAK-61 native barrier stays intact.
    [Fact]
    public async Task DefaultStoreUsesTheGuardedFactory()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SteamCredentialStore();
        await store.SaveAsync(User, Pass, ct);
        Assert.Equal(Pass, (await store.ReadAsync(ct))!.Password);
        await store.DeleteAsync(ct);
        Assert.Null(await store.ReadAsync(ct));
    }

    private sealed class FailingStore(string code) : ISecretStore
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Magazyn niedostępny / Store unavailable.");
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Magazyn niedostępny / Store unavailable.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Magazyn niedostępny / Store unavailable.");
    }
}
