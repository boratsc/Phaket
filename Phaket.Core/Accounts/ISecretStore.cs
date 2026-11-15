namespace Phaket.Core.Accounts;

public interface ISecretStore
{
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
