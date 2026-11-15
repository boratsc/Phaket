using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed record BuildOption(string Name, string? DepotId = null, string? ManifestId = null, long? SizeBytes = null, string? Arch = null, string? Branch = null, string? BuildId = null);
public sealed record SteamInstallPreview(DepotInstallPlan Plan, long? SizeBytes);

public partial class SteamInstallFlowViewModel : ObservableObject
{
    private readonly OwnedSteamGame game;
    private readonly GameDefinition definition;
    private readonly DepotDownloaderInstallAdapter adapter;
    private readonly Func<Task<string?>> picker;
    private readonly Func<Task<IReadOnlyList<BuildOption>>>? builds;
    private readonly LocalizationService loc;

    // Steam signs in once in Phaket (PAK-60): the stored credentials answer the tool's
    // password prompt automatically; without them the one-time sign-in dialog is shown.
    // Null when no secret store was given, which keeps the previous manual behaviour.
    private readonly SteamSigninDialogViewModel? signin;

    // Account of the running install: the stored sign-in, or the one just entered
    // (kept in memory only when the user did not consent to storing it).
    private SteamInstallCredentials? install;
    // Auto-fill answers the first password prompt only. A second one means the tool
    // rejected the stored value, so the user is asked again instead of looping on it.
    private bool passwordAutoFilled;

    [ObservableProperty] private string targetDir;
    [ObservableProperty] private string osArch = "";
    [ObservableProperty] private BuildOption? selectedBuild;
    [ObservableProperty] private IReadOnlyList<BuildOption> buildOptions;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private SteamInstallPreview? preview;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private bool consentSession;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private bool consentOverwrite;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private bool acceptPlan;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private bool installing;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string twoFactorCode = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string logTail = "";
    [ObservableProperty] private bool success;
    [ObservableProperty] private bool dialogCredentialOpen;
    [ObservableProperty] private bool dialogTwoFactorOpen;
    private TaskCompletionSource<string>? pendingCredential;

    public SteamSigninDialogViewModel? SigninDialog => signin;
    public Action? OnInstalled { get; set; }
    public string ArchitectureWarning => game.AppId == "945360" ? loc["SteamInstall.ArchitectureWarning"] : "";
    public bool ArchitectureUnknown => OsArch is not ("32" or "64");
    public bool HasPreview => Preview is not null;
    public bool HasConflicts => Preview?.Plan.Conflicts.Count > 0;
    public string PreviewSize => Preview?.SizeBytes is long size ? $"{size:N0} B" : loc["SteamInstall.UnknownSize"];
    // With a stored sign-in the account name comes from the secret store, so it is not editable here.
    public bool StoredCredentialKnown => signin?.Stored is not null;
    public bool CanEditUsername => !Installing && signin?.Stored is null;

    public SteamInstallFlowViewModel(OwnedSteamGame game, GameDefinition definition, DepotDownloaderInstallAdapter adapter,
        Func<Task<string?>> picker, Func<Task<IReadOnlyList<BuildOption>>>? builds = null, LocalizationService? loc = null,
        SteamCredentialStore? credentialStore = null)
    {
        this.game = game;
        this.definition = definition;
        this.adapter = adapter;
        this.picker = picker;
        this.builds = builds;
        this.loc = loc ?? LocalizationService.Instance;
        signin = credentialStore is null ? null : new SteamSigninDialogViewModel(credentialStore, this.loc);
        targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Phaket", definition.Slug);
        buildOptions = [new(this.loc["SteamInstall.LatestFallback"])];
        selectedBuild = buildOptions[0];
    }

    partial void OnTargetDirChanged(string value) => ResetPlan();
    partial void OnOsArchChanged(string value) { ResetPlan(); OnPropertyChanged(nameof(ArchitectureUnknown)); }
    partial void OnSelectedBuildChanged(BuildOption? value)
    {
        OsArch = value?.Arch?.ToLowerInvariant() switch { "x64" or "64" => "64", "x86" or "32" => "32", _ => "" };
        ResetPlan();
    }
    partial void OnPreviewChanged(SteamInstallPreview? value) { OnPropertyChanged(nameof(PreviewSize)); OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(HasConflicts)); }
    partial void OnInstallingChanged(bool value) => OnPropertyChanged(nameof(CanEditUsername));
    private void ResetPlan() { Preview = null; AcceptPlan = false; Success = false; }

    // The host OS the artifact OS is derived from; the game's own platform wins (PAK-65).
    static string HostOs() => OperatingSystem.IsWindows() ? "windows" : "linux";

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var path = await picker();
        if (path is not null) TargetDir = path;
    }

    [RelayCommand]
    private async Task LoadBuildsAsync()
    {
        if (builds is null) return;
        try
        {
            var options = await builds();
            BuildOptions = options.Count > 0 ? options : [new(loc["SteamInstall.LatestFallback"])];
            SelectedBuild = BuildOptions[0];
        }
        catch (Exception) { BuildOptions = [new(loc["SteamInstall.LatestFallback"])]; SelectedBuild = BuildOptions[0]; Status = loc["SteamInstall.Error.Cache"]; }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        await LoadBuildsAsync();
        await LoadStoredAsync();
    }

    // Shows which account installs will use; the password never leaves the secret store.
    private async Task LoadStoredAsync()
    {
        if (signin is null) return;
        await signin.RefreshAsync();
        if (signin.Stored is { } stored) Username = stored.Username;
        OnPropertyChanged(nameof(StoredCredentialKnown));
        OnPropertyChanged(nameof(CanEditUsername));
    }

    [RelayCommand]
    private void Prepare()
    {
        ResetPlan();
        Status = LogTail = "";
        try
        {
            if (ArchitectureUnknown) { Status = loc["SteamInstall.Error.ArchRequired"]; return; }
            var plan = adapter.BuildInstallPlan(game, TargetDir, definition, HostOs(), OsArch, SelectedBuild?.DepotId, SelectedBuild?.ManifestId);
            Preview = new(plan, SelectedBuild?.SizeBytes);
        }
        catch (PhaketException ex) { ShowError(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Status = loc["SteamInstall.Error.Failed"]; }
    }

    private bool CanExecute() => !Installing && Preview is not null && ConsentSession && AcceptPlan && (Preview.Plan.Conflicts.Count == 0 || ConsentOverwrite);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var plan = Preview?.Plan;
        if (plan is null || Installing) return;
        var missing = !ConsentSession ? "ConsentRequired" : !AcceptPlan ? "PlanNotAccepted" : plan.Conflicts.Count > 0 && !ConsentOverwrite ? "OverwriteRequired" : null;
        if (missing is not null) { Status = loc["SteamInstall.Error." + missing]; return; }
        Installing = true;
        Status = LogTail = "";
        Success = false;
        passwordAutoFilled = false;
        install = null;
        try
        {
            var progress = new Progress<string>(line => LogTail += line);
            var context = SynchronizationContext.Current;
            var account = await ResolveAccountAsync(context);
            // No sign-in (cancelled, invalid or an unreachable store): the tool is never started.
            if (account is null) { if (Status.Length == 0) Status = loc["SteamInstall.Error.CredentialCancelled"]; return; }
            await adapter.ExecuteInstallAsync(plan, account, (prompt, ct) => RequestCredentialAsync(prompt, ct, context),
                ConsentSession, AcceptPlan, progress, overwriteConsent: ConsentOverwrite, rememberSessionConsent: ConsentSession && OperatingSystem.IsWindows());
            Success = true;
            Status = loc["SteamInstall.Success"];
            OnInstalled?.Invoke();
        }
        catch (PhaketException ex) { ShowError(ex); }
        catch (OperationCanceledException) { Status = loc["SteamInstall.Error.CredentialCancelled"]; }
        catch (TimeoutException) { Status = loc["SteamInstall.Error.CredentialCancelled"]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = loc["SteamInstall.Error.Failed"]; }
        catch (Exception ex) when (ex is SecretStoreException or CryptographicException)
        { Status = signin is null ? loc["SteamInstall.Error.Failed"] : signin.StoreErrorMessage(ex); }
        finally { Password = TwoFactorCode = ""; DialogCredentialOpen = DialogTwoFactorOpen = false; pendingCredential = null; install = null; Installing = false; }
    }

    // Account name the tool runs with. Null means the install must not start.
    private async Task<string?> ResolveAccountAsync(SynchronizationContext? context)
    {
        if (signin is null) return Username;
        if (signin.Stored is null) await LoadStoredAsync();
        // A secret store that refused the read must not silently fall back to asking.
        if (signin.Stored is null && signin.HasStatusMessage) { Status = signin.StatusMessage; return null; }
        if (signin.Stored is { } stored) { install = stored; return stored.Username; }
        if (!SteamCredentialStore.IsValidUsername(Username)) { Status = loc["SteamInstall.Error.Username"]; return null; }
        var answer = await signin.AskAsync(Username);
        if (answer is null) return null;
        install = answer;
        OnPropertyChanged(nameof(StoredCredentialKnown));
        OnPropertyChanged(nameof(CanEditUsername));
        return answer.Username;
    }

    private async Task<string> RequestCredentialAsync(DepotPrompt prompt, CancellationToken ct, SynchronizationContext? context)
    {
        if (prompt == DepotPrompt.Password && !passwordAutoFilled && (signin?.Stored ?? install) is { } stored)
        {
            passwordAutoFilled = true;
            return stored.Password;
        }
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            pendingCredential = pending;
            if (prompt == DepotPrompt.Password) { Password = ""; DialogCredentialOpen = true; }
            else { TwoFactorCode = ""; DialogTwoFactorOpen = true; }
        }
        if (context is null || SynchronizationContext.Current == context) Show(); else context.Send(_ => Show(), null);
        return await pending.Task.WaitAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ConfirmCredential() { if (DialogCredentialOpen && !string.IsNullOrEmpty(Password)) { DialogCredentialOpen = false; pendingCredential?.TrySetResult(Password); Password = ""; } }

    [RelayCommand]
    private void ConfirmTwoFactor() { if (DialogTwoFactorOpen && !string.IsNullOrEmpty(TwoFactorCode)) { DialogTwoFactorOpen = false; pendingCredential?.TrySetResult(TwoFactorCode); TwoFactorCode = ""; } }

    [RelayCommand]
    private void CancelDialog() { DialogCredentialOpen = DialogTwoFactorOpen = false; pendingCredential?.TrySetCanceled(); }

    private void ShowError(PhaketException ex)
    {
        var key = ex.Error.Code switch
        {
            "DEPOTDOWNLOADER_NOT_FOUND" => "NotFound", "DEPOTDOWNLOADER_INSTALL_FAILED" => "Failed",
            "DEPOTDOWNLOADER_INSTALL_UNVERIFIED" => "Unverified", ErrorCodes.PathOutOfBounds => "PathOutOfBounds",
            "CONSENT_REQUIRED" => "ConsentRequired", "PLAN_NOT_ACCEPTED" => "PlanNotAccepted",
            "OVERWRITE_CONSENT_REQUIRED" => "OverwriteRequired", "DEPOTDOWNLOADER_SESSION_CONSENT_REQUIRED" => "ConsentRequired",
            _ => "Failed"
        };
        Status = loc["SteamInstall.Error." + key];
        if (ex.Error.Parameters.TryGetValue("tail", out var tail) && tail.Length > 0) { LogTail = tail; Status += Environment.NewLine + tail; }
    }
}
