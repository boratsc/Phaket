using Phaket.Core.Accounts;

namespace Phaket.Services;

// One instance per application window keeps account state shared across pages.
public sealed class AccountServices
{
    public AccountServices()
    {
        Epic = new EpicAccountService(null, url => OpenEpicAuth?.Invoke(url));
        Steam = new SteamAccountService();
        SteamApiKey = new SteamApiKeyStore();
        SteamCredentials = new SteamCredentialStore();
    }

    internal AccountServices(ISecretStore store, string directory)
    {
        Epic = new EpicAccountService(Path.Combine(directory, "epic.bin"), null, url => OpenEpicAuth?.Invoke(url), store);
        Steam = new SteamAccountService(Path.Combine(directory, "steam.bin"), null, null, _ => throw new InvalidOperationException("Browser unavailable"), store);
        SteamApiKey = new SteamApiKeyStore(Path.Combine(directory, "steam-apikey.bin"), store);
        SteamCredentials = new SteamCredentialStore(store);
    }

    public SteamApiKeyStore SteamApiKey { get; }
    public SteamCredentialStore SteamCredentials { get; }
    public Task<string?> GetSteamApiKeyAsync() => Task.FromResult(SteamApiKey.Load());

    public EpicAccountService Epic { get; }
    public SteamAccountService Steam { get; }
    public event Action<string>? OpenEpicAuth;
}
