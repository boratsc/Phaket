using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Accounts;
using Phaket.Services;

namespace Phaket.ViewModels;

// The one Steam sign-in Phaket keeps for game installs (PAK-60, ADR-023/047).
// Shared by the Accounts page and the Steam install flow so both show the same
// state. The password goes to the install tool through its standard input only —
// never to a log, a command line, settings or the cloud. It is written to the
// platform secret store only when the user leaves the consent checkbox on.
public partial class SteamSigninDialogViewModel : ObservableObject
{
    private readonly LocalizationService loc;
    private TaskCompletionSource<SteamInstallCredentials?>? pending;

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool remember;
    [ObservableProperty] private bool hasCredentials;
    [ObservableProperty] private string credentialUsername = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeleteCommand))] private bool confirmDelete;

    public SteamCredentialStore Store { get; }

    // Last value read from the store; the install flow answers prompts from it.
    public SteamInstallCredentials? Stored { get; private set; }

    public SteamSigninDialogViewModel(SteamCredentialStore store, LocalizationService? loc = null)
    {
        Store = store;
        this.loc = loc ?? LocalizationService.Instance;
    }

    public bool HasStatusMessage => StatusMessage.Length > 0;
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    // "Sign in" while nothing is stored, "Change" once the install sign-in exists.
    public string ActionText => loc[HasCredentials ? "Account.SteamCred.Change" : "Account.SteamCred.SignIn"];
    public string StateText => HasCredentials
        ? string.Format(loc["Account.SteamCred.Saved"], CredentialUsername)
        : loc["Account.SteamCred.None"];
    partial void OnHasCredentialsChanged(bool value) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ActionText)); }
    partial void OnCredentialUsernameChanged(string value) => OnPropertyChanged(nameof(StateText));

    // Reads the stored account name; the password is never displayed anywhere.
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Stored = await Store.ReadAsync();
            HasCredentials = Stored is not null;
            CredentialUsername = Stored?.Username ?? "";
            StatusMessage = "";
        }
        catch (Exception ex) when (ex is SecretStoreException or CryptographicException or IOException or UnauthorizedAccessException)
        { Stored = null; HasCredentials = false; StatusMessage = StoreErrorMessage(ex); }
    }

    // Button entry point: sign in once, or replace the sign-in already stored.
    [RelayCommand]
    private Task StartAsync() => AskAsync(CredentialUsername);

    // Opens the dialog and waits for the answer; null means cancelled or timed out.
    public async Task<SteamInstallCredentials?> AskAsync(string prefilledUsername)
    {
        var waiting = new TaskCompletionSource<SteamInstallCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = waiting;
        Username = prefilledUsername;
        Password = "";
        // Fresh status line: a message from the previous attempt must not block the next save.
        StatusMessage = "";
        // Storing credentials is a choice, not a default: every opening starts unchecked (PAK-60).
        Remember = false;
        IsOpen = true;
        SteamInstallCredentials? answer;
        try { answer = await waiting.Task.WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { answer = null; }
        finally { pending = null; IsOpen = false; Password = ""; }
        return answer;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!IsOpen) return;
        string account = Username.Trim();
        // Validated before anything reaches the store: one account name, one password line.
        if (!SteamCredentialStore.IsValidUsername(account)) { StatusMessage = loc["Account.SteamCred.Error.Username"]; return; }
        if (!SteamCredentialStore.IsValidPassword(Password)) { StatusMessage = loc["Account.SteamCred.Error.Password"]; return; }
        var credentials = new SteamInstallCredentials(account, Password);
        Password = "";
        if (Remember)
        {
            try { await Store.SaveAsync(account, credentials.Password); }
            catch (Exception ex) when (ex is ArgumentException or SecretStoreException or CryptographicException or IOException or UnauthorizedAccessException)
            { StatusMessage = StoreErrorMessage(ex); }
            // Only a successful save marks the sign-in as stored.
            if (!HasStatusMessage) Keep(credentials);
        }
        IsOpen = false;
        var target = pending;
        pending = null;
        target?.TrySetResult(credentials);
    }

    private void Keep(SteamInstallCredentials credentials)
    {
        Stored = credentials;
        HasCredentials = true;
        CredentialUsername = credentials.Username;
        Username = credentials.Username;
    }

    [RelayCommand]
    private void Cancel()
    {
        Password = "";
        StatusMessage = "";
        IsOpen = false;
        var target = pending;
        pending = null;
        target?.TrySetCanceled();
    }

    private bool CanDelete() => ConfirmDelete && !IsOpen;
    partial void OnIsOpenChanged(bool value) => DeleteCommand.NotifyCanExecuteChanged();

    // Deletion needs an explicit confirmation in the UI.
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (!CanDelete()) return;
        StatusMessage = await RemoveAsync();
        ConfirmDelete = false;
    }

    public async Task<string> RemoveAsync()
    {
        try { await Store.DeleteAsync(); }
        catch (Exception ex) when (ex is SecretStoreException or CryptographicException or IOException or UnauthorizedAccessException) { return StoreErrorMessage(ex); }
        Stored = null;
        HasCredentials = false;
        CredentialUsername = "";
        return loc["Account.SteamCred.Deleted"];
    }

    // Maps a secret-store failure onto a bilingual, actionable message.
    public string StoreErrorMessage(Exception ex) => ex switch
    {
        SecretStoreException { Code: "SECRET_STORE_LOCKED" } => loc["Account.SteamCred.Error.Locked"],
        SecretStoreException { Code: "SECRET_STORE_TIMEOUT" } => loc["Account.SteamCred.Error.Timeout"],
        SecretStoreException { Code: "SECRET_STORE_CONSENT_REQUIRED" } => loc["Account.SteamCred.Error.Consent"],
        SecretStoreException => loc["Account.SteamCred.Error.Unavailable"],
        ArgumentException => loc["Account.SteamCred.Error.Username"],
        _ => loc["Account.SteamCred.Error.Unavailable"]
    };
}
