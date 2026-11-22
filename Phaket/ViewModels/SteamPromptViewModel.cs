using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Platforms;

namespace Phaket.ViewModels;

// Password / Steam Guard dialog shared by the library game install and the mod
// provision (PAK-102). One implementation means both flows answer DepotDownloader
// prompts identically instead of keeping two copies of the auth UI.
public partial class SteamPromptViewModel : ObservableObject
{
    // The mod flow starts deep inside the shell (game -> mods -> catalog), so the window
    // level overlay binds this instance; the library keeps its own instance inline.
    public static SteamPromptViewModel Shared { get; } = new();

    [ObservableProperty] private bool credentialOpen;
    [ObservableProperty] private bool twoFactorOpen;
    // Steam Guard through the mobile app asks for no text answer: the tool just waits
    // for the phone confirmation. The dialog says so and offers a way out.
    [ObservableProperty] private bool mobileConfirmationOpen;
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string twoFactorCode = "";
    public bool HasDialog => CredentialOpen || TwoFactorOpen || MobileConfirmationOpen;
    partial void OnCredentialOpenChanged(bool value) => OnPropertyChanged(nameof(HasDialog));
    partial void OnTwoFactorOpenChanged(bool value) => OnPropertyChanged(nameof(HasDialog));
    partial void OnMobileConfirmationOpenChanged(bool value) => OnPropertyChanged(nameof(HasDialog));

    // A DepotDownloader output line carries no answer, so this is a notice rather than a
    // request; the host shows it while the tool waits for the phone.
    public void ShowMobileConfirmation() => MobileConfirmationOpen = true;

    // Provision output companion to the prompt: the notice line opens the dialog, a null
    // report (run finished, failed or cancelled) closes it. Create it on the UI thread so
    // Progress delivers there, like the library install does.
    public static IProgress<string?> MobileConfirmationSink() =>
        new Progress<string?>(line => { if (line is null) Shared.MobileConfirmationOpen = false; else Shared.ShowMobileConfirmation(); });

    private TaskCompletionSource<string>? pending;
    // One dialog, one owner: overlapping installs (catalog + modpack) both reach this
    // instance, and a Guard code typed for one must never answer the other tool's prompt.
    private readonly SemaphoreSlim promptGate = new(1, 1);

    // Opens the matching dialog on the caller's synchronization context (the install runs
    // off the UI thread) and waits up to five minutes for an answer. A cancel or timeout
    // surfaces as OperationCanceledException / TimeoutException, never as an empty answer.
    public async Task<string> RequestAsync(DepotPrompt prompt, CancellationToken ct, SynchronizationContext? context = null, TimeSpan? timeout = null)
    {
        // Serialize prompts: the second caller waits for the first answer (or its timeout or
        // cancel) so an answer can only ever reach the pending request that showed the dialog.
        await promptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Show()
            {
                pending = waiting;
                // A text prompt replaces the mobile notice: the tool moved past the wait.
                MobileConfirmationOpen = false;
                if (prompt == DepotPrompt.Password) { Password = ""; CredentialOpen = true; }
                else { TwoFactorCode = ""; TwoFactorOpen = true; }
            }
            void OnUi(Action action)
            {
                if (context is null || SynchronizationContext.Current == context) action();
                else context.Send(_ => action(), null);
            }
            OnUi(Show);
            try { return await waiting.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
            finally
            {
                // A timeout or cancel must not leave the dialog up: the install is over.
                if (pending == waiting) { pending = null; OnUi(() => CredentialOpen = TwoFactorOpen = false); }
            }
        }
        finally { promptGate.Release(); }
    }

    [RelayCommand]
    private void ConfirmCredential()
    {
        if (!CredentialOpen || string.IsNullOrEmpty(Password)) return;
        CredentialOpen = false;
        pending?.TrySetResult(Password);
        Password = "";
    }

    [RelayCommand]
    private void ConfirmTwoFactor()
    {
        if (!TwoFactorOpen || string.IsNullOrEmpty(TwoFactorCode)) return;
        TwoFactorOpen = false;
        pending?.TrySetResult(TwoFactorCode);
        TwoFactorCode = "";
    }

    [RelayCommand]
    private void CancelDialog() { CredentialOpen = TwoFactorOpen = MobileConfirmationOpen = false; pending?.TrySetCanceled(); }
}