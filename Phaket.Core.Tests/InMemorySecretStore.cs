using System.Collections.Concurrent;
using Phaket.Core.Accounts;

namespace Phaket.Core.Tests;

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> values = new(StringComparer.Ordinal);

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(values.TryGetValue(key, out var value) ? value.ToArray() : null);
    }

    public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        values[key] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
