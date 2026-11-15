using Phaket.Core.Accounts;

namespace Phaket.Core.Tests;

public class LinuxLibsecretStoreTests
{
    private sealed class Fake(Func<CancellationToken, string?> call) : ILibsecretNative
    {
        public string? Invoke(string operation, string key, string? value, CancellationToken cancellationToken) => call(cancellationToken);
    }

    [Fact]
    public async Task MissingLibraryIsUnavailable()
    {
        var store = new LinuxLibsecretStore(new Fake(_ => throw new DllNotFoundException()));
        Assert.Equal("SECRET_STORE_UNAVAILABLE", (await Assert.ThrowsAsync<SecretStoreException>(() => store.GetAsync("test", TestContext.Current.CancellationToken))).Code);
    }

    [Theory]
    [InlineData(true, 2, "SECRET_STORE_LOCKED")]
    [InlineData(false, 2, "SECRET_STORE_UNAVAILABLE")]
    [InlineData(true, 1, "SECRET_STORE_UNAVAILABLE")]
    public void NativeErrorMapping(bool domain, int code, string expected)
    {
        var error = LibsecretNative.MapError(domain, code, "bus detail");
        Assert.Equal(expected, error.Code);
        Assert.Contains("bus detail", error.Message);
    }

    [Fact]
    public async Task TimeoutCancelsNativeOperation()
    {
        var store = new LinuxLibsecretStore(new Fake(token => { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return null; }), TimeSpan.FromMilliseconds(50));
        Assert.Equal("SECRET_STORE_TIMEOUT", (await Assert.ThrowsAsync<SecretStoreException>(() => store.GetAsync("test", TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public void FactorySelectsPlatformOrExplicitFallback()
    {
        Assert.IsType<ConsentFileStore>(SecretStoreFactory.Create(true, "password"));
        Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", Assert.Throws<SecretStoreException>(() => SecretStoreFactory.Create(password: "password")).Code);
        Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", Assert.Throws<SecretStoreException>(() => SecretStoreFactory.Create(true)).Code);
        Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", Assert.Throws<SecretStoreException>(() => SecretStoreFactory.Create(directory: "unused")).Code);
    }

    [Fact]
    public async Task ExplicitFileFallbackRoundTrips()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phaket-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = SecretStoreFactory.Create(true, "password", directory);
            await store.SetAsync("test", [1, 2, 3], TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetAsync("test", TestContext.Current.CancellationToken));
            await store.DeleteAsync("test", TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync("test", TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProviderRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("PHAKET_KEYRING_PROBE") != "1") Assert.Skip("Set PHAKET_KEYRING_PROBE=1 to run the native keyring probe.");
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux only");
        var store = new LinuxLibsecretStore();
        string key = "probe-" + Guid.NewGuid().ToString("N");
        try { await store.GetAsync(key, TestContext.Current.CancellationToken); }
        catch (SecretStoreException ex) when (ex.Code is "SECRET_STORE_UNAVAILABLE" or "SECRET_STORE_LOCKED" or "SECRET_STORE_TIMEOUT") { Assert.Skip("Secret Service unavailable: " + ex.Code); }
        try
        {
            Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
            await store.SetAsync(key, [0, 1, 255, 0], TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 0, 1, 255, 0 }, await store.GetAsync(key, TestContext.Current.CancellationToken));
            await store.SetAsync(key, [7, 8], TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 7, 8 }, await store.GetAsync(key, TestContext.Current.CancellationToken));
            await store.DeleteAsync(key, TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
        }
        finally { await store.DeleteAsync(key, CancellationToken.None); }
    }
}
