using Phaket.Core.Accounts;

public sealed class SecretStoreGuardTests
{
    [Fact]
    public async Task DefaultFactoryAndNativeProvidersAreGuarded()
    {
        if (Environment.GetEnvironmentVariable("PHAKET_KEYRING_PROBE") == "1")
            Assert.Skip("Native probe explicitly enabled.");
        var store = SecretStoreFactory.Create();
        Assert.IsType<TestMemorySecretStore>(store);
        var ct = TestContext.Current.CancellationToken;
        await store.SetAsync("guard", [42], ct);
        Assert.Equal(new byte[] { 42 }, await store.GetAsync("guard", ct));
        var linux = await Assert.ThrowsAsync<SecretStoreException>(() => new LinuxLibsecretStore().GetAsync("guard", ct));
        Assert.Equal("SECRET_STORE_TEST_GUARD", linux.Code);
        var windows = await Assert.ThrowsAsync<SecretStoreException>(() => new WindowsDpapiStore("unused").GetAsync("guard", ct));
        Assert.Equal("SECRET_STORE_TEST_GUARD", windows.Code);
    }
}
