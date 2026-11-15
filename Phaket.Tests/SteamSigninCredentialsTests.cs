using Phaket.Core.Accounts;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Steam install credentials on the Accounts page (PAK-60): status line, one-time
// sign-in dialog with store consent, confirmed deletion, cancel and leak checks.
public class SteamSigninCredentialsTests
{
    private static readonly LocalizationService Loc = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SteamSigninDialogViewModel Vm(out SteamCredentialStore store)
    {
        store = new SteamCredentialStore(new InMemorySecretStore());
        return new SteamSigninDialogViewModel(store, Loc);
    }

    [Fact]
    public async Task StatusReflectsTheStore()
    {
        var vm = Vm(out var store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasCredentials);
        Assert.Equal(Loc["Account.SteamCred.None"], vm.StateText);
        Assert.Equal(Loc["Account.SteamCred.SignIn"], vm.ActionText);
        await store.SaveAsync("tester", "stored-password", Ct);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasCredentials);
        Assert.Equal(string.Format(Loc["Account.SteamCred.Saved"], "tester"), vm.StateText);
        Assert.Equal(Loc["Account.SteamCred.Change"], vm.ActionText);
        // The page never learns the password.
        Assert.DoesNotContain("stored-password", vm.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SigninSavesWithConsentAndReturnsCredentials()
    {
        var vm = Vm(out var store);
        var asked = vm.AskAsync("");
        Assert.True(vm.IsOpen);
        Assert.True(vm.Remember);
        vm.Username = " tester ";
        vm.Password = "secret123";
        await vm.ConfirmCommand.ExecuteAsync(null);
        var answer = await asked;
        Assert.Equal("tester", answer!.Username);
        Assert.Equal("secret123", answer.Password);
        Assert.False(vm.IsOpen);
        Assert.True(vm.HasCredentials);
        Assert.Equal("tester", vm.CredentialUsername);
        Assert.Equal("secret123", (await store.ReadAsync(Ct))!.Password);
        // The dialog clears the typed password.
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public async Task WithoutConsentNothingIsStored()
    {
        var vm = Vm(out var store);
        var asked = vm.AskAsync("");
        vm.Username = "tester";
        vm.Password = "secret123";
        vm.Remember = false;
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.NotNull(await asked);
        Assert.False(vm.HasCredentials);
        Assert.Null(await store.ReadAsync(Ct));
    }

    [Fact]
    public async Task CancelReturnsNoCredentialsAndStoresNothing()
    {
        var vm = Vm(out var store);
        var asked = vm.AskAsync("");
        vm.Password = "secret123";
        vm.CancelCommand.Execute(null);
        Assert.Null(await asked);
        Assert.False(vm.IsOpen);
        Assert.Equal("", vm.Password);
        Assert.Null(await store.ReadAsync(Ct));
    }

    [Theory]
    [InlineData("", "secret", "Account.SteamCred.Error.Username")]
    [InlineData("tester", "", "Account.SteamCred.Error.Password")]
    [InlineData("tester", "two\nlines", "Account.SteamCred.Error.Password")]
    public async Task InvalidInputKeepsDialogOpenAndStoresNothing(string username, string password, string key)
    {
        var vm = Vm(out var store);
        var asked = vm.AskAsync("");
        vm.Username = username;
        vm.Password = password;
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.IsOpen);
        Assert.Equal(Loc[key], vm.StatusMessage);
        Assert.Null(await store.ReadAsync(Ct));
        vm.CancelCommand.Execute(null);
        Assert.Null(await asked);
    }

    [Fact]
    public async Task DeleteNeedsConfirmationAndClearsTheStore()
    {
        var vm = Vm(out var store);
        await store.SaveAsync("tester", "secret123", Ct);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasCredentials);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.NotNull(await store.ReadAsync(Ct));
        vm.ConfirmDelete = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Null(await store.ReadAsync(Ct));
        Assert.False(vm.HasCredentials);
        Assert.Equal("", vm.CredentialUsername);
        Assert.Equal(Loc["Account.SteamCred.Deleted"], vm.StatusMessage);
        Assert.False(vm.ConfirmDelete);
    }

    [Fact]
    public async Task LockedStoreReportsItsOwnErrorOnEveryAction()
    {
        var store = new SteamCredentialStore(new FailingStore("SECRET_STORE_LOCKED"));
        var vm = new SteamSigninDialogViewModel(store, Loc);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(Loc["Account.SteamCred.Error.Locked"], vm.StatusMessage);
        Assert.False(vm.HasCredentials);
        var asked = vm.AskAsync("tester");
        vm.Password = "secret123";
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Loc["Account.SteamCred.Error.Locked"], vm.StatusMessage);
        // Saving failed, so the section must not claim a stored sign-in.
        Assert.False(vm.HasCredentials);
        var answer = await asked;
        Assert.NotNull(answer);
        vm.ConfirmDelete = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(Loc["Account.SteamCred.Error.Locked"], vm.StatusMessage);
    }

    [Fact]
    public async Task MessagesNeverContainThePassword()
    {
        var vm = Vm(out var store);
        await store.SaveAsync("tester", "secret123", Ct);
        await vm.RefreshCommand.ExecuteAsync(null);
        var asked = vm.AskAsync("tester");
        vm.Username = "tester";
        vm.Password = "new secret456";
        await vm.ConfirmCommand.ExecuteAsync(null);
        await asked;
        string[] texts = [vm.StateText, vm.StatusMessage, vm.Password, vm.Username, vm.CredentialUsername];
        Assert.All(texts, text => Assert.DoesNotContain("secret123", text, StringComparison.Ordinal));
        Assert.All(texts, text => Assert.DoesNotContain("new secret456", text, StringComparison.Ordinal));
        Assert.DoesNotContain("secret123", (await store.ReadAsync(Ct))!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountsPageExposesTheSectionAndSharesTheStore()
    {
        var accounts = TestAccounts.Create();
        var page = new AccountsViewModel(accounts);
        Assert.Same(page.SteamSignin.Store, accounts.SteamCredentials);
        await page.SteamSignin.RefreshCommand.ExecuteAsync(null);
        Assert.False(page.SteamSignin.HasCredentials);
        await accounts.SteamCredentials.SaveAsync("tester", "secret123", Ct);
        await page.SteamSignin.RefreshCommand.ExecuteAsync(null);
        Assert.True(page.SteamSignin.HasCredentials);
        Assert.Equal("tester", page.SteamSignin.CredentialUsername);
    }

    // The Steam install flow must read the very same store the Accounts page writes to.
    [Fact]
    public void LibraryPassesTheCredentialStoreToTheInstallFlow()
    {
        var accounts = TestAccounts.Create();
        var library = new LibraryViewModel(_ => { }, accounts, null);
        var field = typeof(LibraryViewModel).GetField("steamCredentials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Same(accounts.SteamCredentials, field.GetValue(library));
    }

    private sealed class FailingStore(string code) : ISecretStore
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Zablokowany / Locked.");
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Zablokowany / Locked.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Zablokowany / Locked.");
    }
}
