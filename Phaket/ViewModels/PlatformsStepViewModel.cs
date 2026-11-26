using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Accounts;
using Phaket.Core.Nexus;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-135: one row of the platforms step. It signs in through the existing service (or the
// existing Nexus key store) and only mirrors the store status: the wizard stores no token of
// its own, so the secret stores stay the single source of truth.
// shortcut: Nexus signs in by pasted key here (the one non-account flow), no OAuth row;
// add the browser flow when the wizard needs it to match Settings.
public sealed partial class PlatformSignInViewModel : ObservableObject, IDisposable
{
    private readonly NexusApiKeyStore? nexusKeys;
    private readonly IAccountService? accounts;
    private readonly PropertyChangedEventHandler languageChanged;

    public string Id { get; }
    public string DisplayName { get; }
    public string AutomationId => $"onboarding.platform.{Id}";
    // Only Nexus, whose key is not an account flow, gets a paste field.
    public bool HasKey => nexusKeys is not null;

    [ObservableProperty] private bool isSignedIn;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string keyInput = "";

    internal PlatformSignInViewModel(IAccountService accounts, string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
        this.accounts = accounts;
        languageChanged = (_, _) => NotifyLabels();
        LocalizationService.Instance.PropertyChanged += languageChanged;
        IsSignedIn = accounts.Current is not null;
        accounts.AccountChanged += OnAccountChanged;
    }

    internal PlatformSignInViewModel(string id, string displayName, NexusApiKeyStore nexusKeys)
    {
        Id = id;
        DisplayName = displayName;
        this.nexusKeys = nexusKeys;
        languageChanged = (_, _) => NotifyLabels();
        LocalizationService.Instance.PropertyChanged += languageChanged;
        Refresh();
    }

    public string LoginText => LocalizationService.Instance["Onboarding.Platforms.Login"];
    public string PasteText => LocalizationService.Instance["Onboarding.Platforms.PasteKey"];
    // Negation as a property: a compiled `!` binding does not re-evaluate on every change.
    public bool CanSignIn => !IsSignedIn;
    // A failed sign-in shows its own message; otherwise the status follows the store.
    public string? Status => StatusText.Length > 0 ? StatusText : SignedInText;
    public bool HasStatus => Status is not null;
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(Status));
    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(CanSignIn));
    }

    private string? SignedInText => !IsSignedIn ? null : accounts?.Current?.Username is { } username
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.Instance["Onboarding.Platforms.SignedInAs"], username)
        : nexusKeys is not null ? LocalizationService.Instance["Onboarding.Platforms.SignedIn"]
        : LocalizationService.Instance["Onboarding.Platforms.SignedInSteam"];

    // A sign-in often finishes on another thread; without a UI loop (headless or test calls)
    // it refreshes inline instead of queuing a job nobody runs.
    private void OnAccountChanged()
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Refresh();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private void NotifyLabels()
    {
        OnPropertyChanged(nameof(LoginText));
        OnPropertyChanged(nameof(PasteText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
    }

    // Status straight from the store; a failing or locked store reads as "not signed in"
    // (Settings is where that gets fixed).
    internal void Refresh()
    {
        if (nexusKeys is null) return;
        try { IsSignedIn = nexusKeys.ReadAsync().GetAwaiter().GetResult() is not null; }
        catch (Exception ex) when (IsExpected(ex)) { IsSignedIn = false; }
    }

    // The same entry point the accounts page uses: the provider's own sign-in flow.
    [RelayCommand]
    private async Task SignInAsync()
    {
        try
        {
            await accounts!.LoginAsync(CancellationToken.None);
            Succeeded();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            StatusText = LocalizationService.Instance["Onboarding.Platforms.Error"];
        }
    }

    // Nexus: a checked key, exactly what the Settings Nexus section offers.
    [RelayCommand]
    private async Task PasteKeyAsync()
    {
        try
        {
            string key = KeyInput.Trim();
            if (!NexusApiKeyStore.IsValid(key)) throw new ArgumentException("Invalid Nexus API key.");
            await nexusKeys!.SaveAsync(key, CancellationToken.None);
            KeyInput = "";
            Succeeded();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            StatusText = LocalizationService.Instance["Onboarding.Platforms.Error"];
        }
    }

    private void Succeeded()
    {
        StatusText = "";
        IsSignedIn = true;
    }

    // Sign-in failures (no network, refused key, locked store) are status messages, not crashes.
    private static bool IsExpected(Exception ex) => ex is
        NexusApiException or SecretStoreException or HttpRequestException or TaskCanceledException
        or InvalidOperationException or OperationCanceledException or ArgumentException
        or System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException;

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= languageChanged;
        // A disposed wizard must not keep observing an account service the shell shares.
        if (accounts is not null) accounts.AccountChanged -= OnAccountChanged;
    }
}

// PAK-135: the second wizard step, one page for Steam, Epic Games and Nexus. Each row runs
// the flow Settings already offers, so "Later" only leaves the step: the wizard writes no
// account state, and anything signed in is already in the stores either way.
public sealed partial class PlatformsStepViewModel : ObservableObject, IOnboardingStep, IDisposable
{
    private readonly PropertyChangedEventHandler languageChanged;

    public PlatformsStepViewModel(ISecretStore store, IAccountService steam, IAccountService epic)
    {
        var nexusKeys = new NexusApiKeyStore(store);
        Platforms =
        [
            new PlatformSignInViewModel(steam, "steam", "Steam"),
            new PlatformSignInViewModel(epic, "epic", "Epic Games"),
            new PlatformSignInViewModel("nexus", "Nexus Mods", nexusKeys)
        ];
        languageChanged = (_, _) => NotifyLabels();
        LocalizationService.Instance.PropertyChanged += languageChanged;
    }

    // Test seam: rows wired to fakes, so no sign-in ever runs against a real account.
    internal PlatformsStepViewModel(IAccountService steam, IAccountService epic, NexusApiKeyStore nexusKeys)
    {
        Platforms =
        [
            new PlatformSignInViewModel(steam, "steam", "Steam"),
            new PlatformSignInViewModel(epic, "epic", "Epic Games"),
            new PlatformSignInViewModel("nexus", "Nexus Mods", nexusKeys)
        ];
        languageChanged = (_, _) => NotifyLabels();
        LocalizationService.Instance.PropertyChanged += languageChanged;
    }

    public string StepId => "platforms";
    public string Title => LocalizationService.Instance["Onboarding.Platforms.Title"];
    public string Description => LocalizationService.Instance["Onboarding.Platforms.Description"];
    public string LaterText => LocalizationService.Instance["Onboarding.Platforms.Later"];
    public string InfoText => LocalizationService.Instance["Onboarding.Platforms.Info"];
    public IReadOnlyList<PlatformSignInViewModel> Platforms { get; }

    // A restart re-reads the stores, so whoever signed in meanwhile shows signed in.
    public void Reset()
    {
        foreach (var platform in Platforms) platform.Refresh();
    }

    private void NotifyLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(LaterText));
        OnPropertyChanged(nameof(InfoText));
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= languageChanged;
        foreach (var platform in Platforms) platform.Dispose();
    }
}