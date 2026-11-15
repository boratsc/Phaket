using Phaket.Core.Accounts;

namespace Phaket.Core.Tests;

public class ConsentFileStoreTests
{
    [Fact]
    public async Task RequiresConsentAndPassword()
    {
        var store = new ConsentFileStore(Path.GetTempPath(), false, "password");
        var error = await Assert.ThrowsAsync<SecretStoreException>(() => store.GetAsync("epic", TestContext.Current.CancellationToken));
        Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", error.Code);
        var locked = new ConsentFileStore(Path.GetTempPath(), true, null);
        error = await Assert.ThrowsAsync<SecretStoreException>(() => locked.SetAsync("epic", [1], TestContext.Current.CancellationToken));
        Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", error.Code);
    }

    [Fact]
    public async Task RoundTripsAndWrongPasswordDoesNotOverwrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConsentFileStore(directory, true, "correct password");
            await store.SetAsync("epic", [1, 2, 3], TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetAsync("epic", TestContext.Current.CancellationToken));
            var wrong = new ConsentFileStore(directory, true, "wrong password");
            await Assert.ThrowsAsync<SecretStoreException>(() => wrong.SetAsync("epic", [4], TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetAsync("epic", TestContext.Current.CancellationToken));
            await store.DeleteAsync("epic", TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync("epic", TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
