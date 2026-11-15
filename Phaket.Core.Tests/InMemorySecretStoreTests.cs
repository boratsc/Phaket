namespace Phaket.Core.Tests;

public class InMemorySecretStoreTests
{
    [Fact]
    public async Task StoresIndependentCopiesAndDeletesIdempotently()
    {
        var store = new InMemorySecretStore();
        byte[] input = [0, 1, 255];
        await store.SetAsync("epic", input, TestContext.Current.CancellationToken);
        input[0] = 42;
        var result = await store.GetAsync("epic", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0, 1, 255 }, result);
        result![1] = 42;
        Assert.Equal(new byte[] { 0, 1, 255 }, await store.GetAsync("epic", TestContext.Current.CancellationToken));
        await store.DeleteAsync("epic", TestContext.Current.CancellationToken);
        await store.DeleteAsync("epic", TestContext.Current.CancellationToken);
        Assert.Null(await store.GetAsync("epic", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledOperationsDoNotChangeData()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync("epic", [1], TestContext.Current.CancellationToken);
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetAsync("epic", [2], cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync("epic", cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync("epic", cancelled));
        Assert.Equal(new byte[] { 1 }, await store.GetAsync("epic", TestContext.Current.CancellationToken));
    }
}
