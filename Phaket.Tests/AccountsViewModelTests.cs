using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Accounts;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Provider account VM state machine: login/logout/error/cancel, itch client-id
// gating, dispatcher refresh. Fake service — no network, no real stores.
public class AccountsViewModelTests
{
    private sealed class FakeAccount : IAccountService
    {
        public string Provider { get; init; } = "discord";
        public AccountInfo? Current { get; set; }
        public event Action? AccountChanged;
        public Exception? LoginError { get; set; }
        public int LoginCalls { get; private set; }

        public Task<AccountInfo> LoginAsync(CancellationToken ct = default)
        {
            LoginCalls++;
            if (LoginError is not null) return Task.FromException<AccountInfo>(LoginError);
            Current = new AccountInfo(Provider, "42", "tester", null, DateTimeOffset.UtcNow);
            return Task.FromResult(Current);
        }

        public Task LogoutAsync(CancellationToken ct = default) { Current = null; return Task.CompletedTask; }
        public void Raise() => AccountChanged?.Invoke();
    }

    private static ProviderAccountViewModel Vm(FakeAccount service, string clientId = "") =>
        new(service, "Test", "Chat", "Account.Login", clientId);

    [AvaloniaFact]
    public async Task Login_success_updates_state()
    {
        var service = new FakeAccount();
        var vm = Vm(service);
        Assert.True(vm.CanLogin);
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoggedIn);
        Assert.Equal("tester", vm.Username);
        Assert.Equal(LocalizationService.Instance["Account.Success"], vm.StatusMessage);
        Assert.True(vm.CanLogout);
        Assert.False(vm.CanLogin);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Login_failure_sets_error()
    {
        var vm = Vm(new FakeAccount { LoginError = new InvalidOperationException("boom") });
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.False(vm.IsLoggedIn);
        Assert.Equal(LocalizationService.Instance["Account.Error"], vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Login_cancelled_sets_cancelled()
    {
        var vm = Vm(new FakeAccount { LoginError = new OperationCanceledException() });
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Account.Cancelled"], vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Logout_clears_account()
    {
        var service = new FakeAccount();
        var vm = Vm(service);
        await vm.LoginCommand.ExecuteAsync(null);
        await vm.LogoutCommand.ExecuteAsync(null);
        Assert.False(vm.IsLoggedIn);
        Assert.Equal("", vm.Username);
        Assert.Equal(LocalizationService.Instance["Account.LoggedOut"], vm.StatusMessage);
        Assert.True(vm.CanLogin);
    }

    [AvaloniaFact]
    public async Task Itch_without_client_id_blocks_login()
    {
        var service = new FakeAccount { Provider = "itch" };
        var vm = Vm(service, clientId: "");
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal(0, service.LoginCalls);
        Assert.Equal(LocalizationService.Instance["Account.ItchNoClient"], vm.StatusMessage);
        Assert.False(vm.IsLoggedIn);
    }

    [AvaloniaFact]
    public void AccountChanged_refreshes_via_dispatcher()
    {
        var service = new FakeAccount();
        var vm = Vm(service);
        Assert.False(vm.IsLoggedIn);
        service.Current = new AccountInfo("discord", "7", "late", null, DateTimeOffset.UtcNow);
        service.Raise();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsLoggedIn);
        Assert.Equal("late", vm.Username);
    }
}
