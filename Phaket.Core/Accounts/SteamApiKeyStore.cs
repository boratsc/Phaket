using System.Text;

namespace Phaket.Core.Accounts;

public sealed class SteamApiKeyStore(string? path = null, ISecretStore? secretStore = null)
{
    private readonly EncryptedStore store = new(Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "steam-apikey.bin")), secretStore);
    public string? Load() => store.Load() is { } data ? Encoding.UTF8.GetString(data) : null;
    public Task SaveAsync(string key, CancellationToken ct = default) => store.SaveAsync(Encoding.UTF8.GetBytes(key.Trim()), ct);
}
