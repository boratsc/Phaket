using Phaket.Core.Accounts;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

internal static class TestAccounts
{
    internal static AccountServices Create() => new(new InMemorySecretStore(), TempPath());
    // Signed-in-on-demand account for the wizard tests: no network, no store.
    internal sealed class FakeAccount(string provider = "steam") : IAccountService
    {
        public string Provider { get; } = provider;
        public AccountInfo? Current { get; private set; }
        public event Action? AccountChanged;
        public int LoginCalls { get; private set; }

        public Task<AccountInfo> LoginAsync(CancellationToken ct = default)
        {
            LoginCalls++;
            Current = new AccountInfo(Provider, "42", Provider + "-user", null, DateTimeOffset.UtcNow);
            AccountChanged?.Invoke();
            return Task.FromResult(Current);
        }

        public Task LogoutAsync(CancellationToken ct = default) { Current = null; return Task.CompletedTask; }
    }

    internal static string TempPath() => Path.Combine(Path.GetTempPath(), "phaket-test-" + Guid.NewGuid().ToString("N"));
    internal static MainWindowViewModel ViewModel()
    {
        string directory = TempPath();
        return new MainWindowViewModel(new AccountServices(new InMemorySecretStore(), directory),
            new SettingsViewModel(Path.Combine(directory, "settings.json")));
    }
}
