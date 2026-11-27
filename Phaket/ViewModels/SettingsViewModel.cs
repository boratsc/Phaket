using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;
using Phaket.Core.Accounts;
using Phaket.Core.Nexus;
using Phaket.Core.Tools;

namespace Phaket.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    internal static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "settings.json");

    internal static string DefaultSecretDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");

    // Secret keys are the base names of the protected account blobs (see TokenProtector),
    // plus the Steam install sign-in, which is stored under its own key (PAK-60).
    // "steam-apikey" is no longer written anywhere (the local Steam library needs no key);
    // it stays in the list so deleting secrets also clears it from existing installs.
    private static readonly IReadOnlyList<string> DefaultSecretKeys =
        ["epic", "steam", "itch", "account", "steam-apikey", SteamCredentialStore.SecretKey,
        NexusApiKeyStore.SecretKey, NexusApiKeyStore.OAuthTokensSecretKey];

    // Test seam: active store, its directory, key list, provider label and migration entry point.
    internal sealed record SecretStoreSeam(
        ISecretStore Store,
        string SecretDirectory,
        IReadOnlyList<string>? Keys = null,
        string? Provider = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? Migrate = null);

    private readonly string settingsPath;
    private readonly string secretDirectory;
    private readonly IReadOnlyList<string> secretKeys;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> migrateSecrets;
    private readonly INexusService nexus;
    private readonly UpdateService updates;
    private ISecretStore activeStore;

    // Last address written to settings.json; typing an invalid one must not corrupt the file.
    private string persistedEndpoint;

    [ObservableProperty] private string catalogEndpoint = CatalogSettings.DefaultEndpoint;
    [ObservableProperty] private string catalogEndpointStatus = "";
    [ObservableProperty] private string secretProvider = "";
    [ObservableProperty] private string secretStateText = "";
    [ObservableProperty] private string secretHintText = "";
    [ObservableProperty] private string secretStatusText = "";
    [ObservableProperty] private bool needsMigration;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MigrateSecretsCommand))]
    private bool migrationConsent;

    [ObservableProperty] private bool fallbackConsent;
    [ObservableProperty] private string fallbackPassword = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DeleteSecretsCommand))]
    private bool confirmDelete;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MigrateSecretsCommand)),
     NotifyCanExecuteChangedFor(nameof(DeleteSecretsCommand)), NotifyCanExecuteChangedFor(nameof(UnlockFallbackCommand)),
     NotifyCanExecuteChangedFor(nameof(RefreshStatusCommand))]
    private bool secretsBusy;

    // Nexus (PAK-117): the API key is validated against validate.json and only ever
    // written to the platform secret store; the input is cleared right after saving.
    [ObservableProperty] private string nexusApiKey = "";
    [ObservableProperty] private string nexusStatusText = "";
    [ObservableProperty] private string nexusAccountText = "";
    [ObservableProperty] private string nexusPremiumText = "";
    [ObservableProperty] private string nexusLimitsText = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckNexusKeyCommand)),
     NotifyCanExecuteChangedFor(nameof(DeleteNexusKeyCommand)), NotifyCanExecuteChangedFor(nameof(SignInNexusCommand)),
     NotifyCanExecuteChangedFor(nameof(SignOutNexusCommand))]
    private bool nexusBusy;

    [ObservableProperty] private string nexusClientId = NexusOAuthOptions.DefaultClientId;
    [ObservableProperty] private bool nexusSignedIn;
    [ObservableProperty] private string nexusOAuthStatusText = "";
    private bool nexusSessionChecked;

    // Aktualizacje (PAK-121): the channel preference lives in settings.json and the status
    // text is sourced from UpdateService; a dev build (not installed) is information, not an error.
    [ObservableProperty] private bool updateBetaChannel;
    [ObservableProperty] private string updateCurrentVersion = "";
    [ObservableProperty] private string updateStatusText = "";
    [ObservableProperty] private int updateProgress;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadAndInstallUpdateCommand))]
    private bool updateAvailable;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckForAppUpdatesCommand)),
     NotifyCanExecuteChangedFor(nameof(DownloadAndInstallUpdateCommand))]
    private bool updateBusy;

    public ObservableCollection<string> Languages { get; } = [];
    [ObservableProperty] private string selectedSection = "general";
    public bool IsGeneralSection => SelectedSection == "general";
    public bool IsCatalogSection => SelectedSection == "catalog";
    public bool IsToolsSection => SelectedSection == "tools";
    public bool IsSecretsSection => SelectedSection == "secrets";
    public bool IsNexusSection => SelectedSection == "nexus";
    public bool IsUpdatesSection => SelectedSection == "updates";

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(IsCatalogSection));
        OnPropertyChanged(nameof(IsToolsSection));
        OnPropertyChanged(nameof(IsSecretsSection));
        OnPropertyChanged(nameof(IsNexusSection));
        OnPropertyChanged(nameof(IsUpdatesSection));
        // The secret store is read only when the section is opened, never in the background.
        if (value == "nexus" && !nexusSessionChecked) _ = RefreshNexusSessionAsync();
    }

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (section is "general" or "catalog" or "tools" or "secrets" or "nexus" or "updates") SelectedSection = section;
    }

    // Narzędzia: the version pins Core/Tools reads from settings.json, plus the
    // release list and a forced update check for each tool.
    public ToolSettingsSection Legendary { get; }
    public ToolSettingsSection Depot { get; }
    public ToolSettingsSection Aurelia { get; }

    internal string SettingsFile => settingsPath;

    // Wizard entry (PAK-133); wired by the shell, which owns the window that shows it.
    public OnboardingViewModel? Onboarding { get; set; }

    [RelayCommand]
    private void RunOnboarding() => Onboarding?.RestartCommand.Execute(null);

    [ObservableProperty]
    private string selectedLanguage = LocalizationService.Instance.CurrentLanguage;

    public SettingsViewModel(string? settingsPath = null)
        : this(settingsPath, null, null) { }

    // Test seam for the tools section: provider (fake HTTP), its download root and the settings path.
    internal sealed record ToolSeam(ToolProvider Provider, string ToolsRoot);

    internal SettingsViewModel(string? settingsPath, SecretStoreSeam? secrets, ToolSeam? toolSeam = null,
        INexusService? nexusService = null, Func<UpdateChannel, IUpdateSource>? updateSource = null)
    {
        this.settingsPath = settingsPath ?? DefaultSettingsPath;
        var toolProvider = toolSeam?.Provider ?? new ToolProvider();
        string toolsRoot = toolSeam?.ToolsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "tools");
        persistedEndpoint = CatalogSettings.Load(this.settingsPath);
        CatalogEndpoint = persistedEndpoint;
        var seam = secrets ?? new SecretStoreSeam(SecretStoreFactory.Create(), DefaultSecretDirectory);
        activeStore = seam.Store;
        secretDirectory = seam.SecretDirectory;
        secretKeys = seam.Keys ?? DefaultSecretKeys;
        SecretProvider = seam.Provider ?? ProviderFor(activeStore);
        migrateSecrets = seam.Migrate ?? ((keys, ct) => SecretMigration.MigrateAllAsync(secretDirectory, keys, activeStore, ct));
        // The key store follows activeStore, so a session-only fallback unlock also covers Nexus.
        nexus = nexusService ?? new NexusService(new NexusApiKeyStore(() => activeStore),
            appDataRoot: Path.GetDirectoryName(Path.GetFullPath(this.settingsPath)));
        updates = new UpdateService(this.settingsPath, updateSource);
        updateBetaChannel = updates.Channel == UpdateChannel.Beta;
        UpdateCurrentVersion = updates.CurrentVersion;
        foreach (var language in LocalizationService.Instance.AvailableLanguages)
            Languages.Add(language);
        Legendary = new ToolSettingsSection(this, toolProvider, toolsRoot, "legendary");
        Depot = new ToolSettingsSection(this, toolProvider, toolsRoot, "depotdownloader");
        Aurelia = new ToolSettingsSection(this, toolProvider, toolsRoot, "aurelia");
        Load();
        nexus.ConfigureClientId(NexusClientId);
        _ = RefreshStatusAsync();
    }

    private static string ProviderFor(ISecretStore store) => store switch
    {
        LinuxLibsecretStore => "libsecret",
        WindowsDpapiStore => "DPAPI",
        ConsentFileStore => "consent-file",
        _ => "in-memory"
    };

    [RelayCommand]
    private void SaveCatalogEndpoint()
    {
        string endpoint = CatalogEndpoint.Trim();
        if (!CatalogSettings.IsValid(endpoint))
        {
            CatalogEndpointStatus = Loc("Settings.Catalog.Invalid");
            return;
        }
        CatalogEndpoint = endpoint;
        persistedEndpoint = endpoint;
        CatalogEndpointStatus = Save() ? Loc("Settings.Catalog.Saved") : Loc("Settings.Catalog.SaveFailed");
    }

    public bool HasCatalogEndpointStatus => !string.IsNullOrEmpty(CatalogEndpointStatus);
    partial void OnCatalogEndpointStatusChanged(string value) => OnPropertyChanged(nameof(HasCatalogEndpointStatus));

    public bool HasNexusStatus => !string.IsNullOrEmpty(NexusStatusText);
    public bool HasNexusAccount => !string.IsNullOrEmpty(NexusAccountText);
    public bool HasNexusLimits => !string.IsNullOrEmpty(NexusLimitsText);
    public bool HasNexusOAuthStatus => !string.IsNullOrEmpty(NexusOAuthStatusText);
    partial void OnNexusStatusTextChanged(string value) => OnPropertyChanged(nameof(HasNexusStatus));
    partial void OnNexusAccountTextChanged(string value) => OnPropertyChanged(nameof(HasNexusAccount));
    partial void OnNexusLimitsTextChanged(string value) => OnPropertyChanged(nameof(HasNexusLimits));
    partial void OnNexusOAuthStatusTextChanged(string value) => OnPropertyChanged(nameof(HasNexusOAuthStatus));

    private bool CanRunNexus => !NexusBusy;

    // Preferred sign-in: OAuth2 authorization code + PKCE in the system browser.
    [RelayCommand(CanExecute = nameof(CanRunNexus))]
    private async Task SignInNexusAsync()
    {
        NexusBusy = true;
        NexusStatusText = "";
        NexusOAuthStatusText = Loc("Settings.Nexus.OAuth.Waiting");
        try
        {
            Save();
            NexusKeyStatus status = await nexus.SignInAsync(NexusClientId.Trim(), CancellationToken.None);
            NexusSignedIn = true;
            NexusOAuthStatusText = Loc("Settings.Nexus.OAuth.SignedIn");
            ShowNexusAccount(status);
        }
        catch (NexusApiException ex)
        {
            NexusOAuthStatusText = Loc(NexusMessages.KeyFor(ex.Code));
            NexusSignedIn = await HasNexusSessionAsync();
        }
        catch (SecretStoreException)
        {
            NexusOAuthStatusText = Loc("Nexus.Error.SecretStore");
            NexusSignedIn = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            NexusOAuthStatusText = Loc("Nexus.Error.Unavailable");
        }
        finally { NexusBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunNexus))]
    private async Task SignOutNexusAsync()
    {
        NexusBusy = true;
        try
        {
            await nexus.SignOutAsync(CancellationToken.None);
            NexusSignedIn = false;
            NexusOAuthStatusText = Loc("Settings.Nexus.OAuth.SignedOut");
            NexusAccountText = "";
            NexusPremiumText = "";
            NexusLimitsText = "";
        }
        catch (Exception ex) when (ex is NexusApiException or HttpRequestException or TaskCanceledException or InvalidOperationException or SecretStoreException)
        {
            NexusOAuthStatusText = Loc("Nexus.Error.Unavailable");
        }
        finally { NexusBusy = false; }
    }

    internal async Task RefreshNexusSessionAsync()
    {
        nexusSessionChecked = true;
        NexusSignedIn = await HasNexusSessionAsync();
        if (NexusSignedIn) NexusOAuthStatusText = Loc("Settings.Nexus.OAuth.SignedIn");
    }

    private async Task<bool> HasNexusSessionAsync()
    {
        try { return await nexus.HasOAuthSessionAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is NexusApiException or InvalidOperationException) { return false; }
    }

    private void ShowNexusAccount(NexusKeyStatus status)
    {
        NexusAccountText = string.Format(CultureInfo.CurrentCulture, Loc("Settings.Nexus.Account"), status.User.Name);
        NexusPremiumText = Loc(status.User.IsPremium ? "Settings.Nexus.Premium" : "Settings.Nexus.Free");
        NexusLimitsText = FormatLimits(status.Limits);
    }

    // The API key stays as the fallback when OAuth is unavailable or not set up.
    [RelayCommand(CanExecute = nameof(CanRunNexus))]
    private async Task CheckNexusKeyAsync()
    {
        NexusBusy = true;
        NexusStatusText = "";
        string typed = NexusApiKey.Trim();
        try
        {
            if (typed.Length > 0 && !NexusApiKeyStore.IsValid(typed))
            {
                NexusStatusText = Loc("Settings.Nexus.InvalidKey");
                return;
            }
            NexusKeyStatus status = typed.Length > 0
                ? await nexus.ValidateKeyAsync(typed, CancellationToken.None)
                : await nexus.ValidateStoredKeyAsync(CancellationToken.None);
            if (typed.Length > 0)
            {
                bool persisted = await nexus.SaveKeyAsync(typed, CancellationToken.None);
                NexusApiKey = "";
                NexusStatusText = Loc(persisted ? "Settings.Nexus.Saved" : "Settings.Nexus.MemoryOnly");
            }
            else NexusStatusText = Loc("Settings.Nexus.Valid");
            ShowNexusAccount(status);
        }
        catch (NexusApiException ex) { NexusStatusText = Loc(NexusMessages.KeyFor(ex.Code)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            NexusStatusText = Loc("Nexus.Error.Unavailable");
        }
        finally { NexusBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunNexus))]
    private async Task DeleteNexusKeyAsync()
    {
        NexusBusy = true;
        try
        {
            await nexus.DeleteKeyAsync(CancellationToken.None);
            NexusStatusText = Loc("Settings.Nexus.Deleted");
            NexusAccountText = "";
            NexusPremiumText = "";
            NexusLimitsText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            NexusStatusText = Loc("Nexus.Error.Unavailable");
        }
        finally { NexusBusy = false; }
    }

    // API Access page on the site; the user creates or copies the personal key there.
    [RelayCommand]
    private void OpenNexusApiPage()
    {
        try { Process.Start(new ProcessStartInfo("https://www.nexusmods.com/users/myaccount?tab=api+access") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static string FormatLimits(NexusRateLimits limits) => string.Format(CultureInfo.CurrentCulture,
        Loc("Settings.Nexus.Limits"),
        limits.HourlyRemaining?.ToString(CultureInfo.CurrentCulture) ?? "—",
        limits.DailyRemaining?.ToString(CultureInfo.CurrentCulture) ?? "—");

    // Aktualizacje (PAK-121): stable/beta is the user's preference; the OS part of the Velopack
    // channel (win-x64/linux-x64) is added by UpdateService.
    public bool IsStableChannel
    {
        get => !UpdateBetaChannel;
        set { if (value) UpdateBetaChannel = false; }
    }

    public bool IsBetaChannel
    {
        get => UpdateBetaChannel;
        set { if (value) UpdateBetaChannel = true; }
    }

    // A radio click always runs this command, even when the chosen channel is already active:
    // unlike an IsChecked write, it records the explicit preference (PAK-129).
    [RelayCommand]
    private void SelectChannel(string? channel)
    {
        if (channel is not ("stable" or "beta")) return;
        bool beta = channel == "beta";
        updates.SetChannel(beta ? UpdateChannel.Beta : UpdateChannel.Stable);
        UpdateBetaChannel = beta;
        UpdateAvailable = false;
        UpdateStatusText = "";
    }

    partial void OnUpdateBetaChannelChanged(bool value)
    {
        updates.SetChannel(value ? UpdateChannel.Beta : UpdateChannel.Stable);
        UpdateAvailable = false;
        UpdateStatusText = "";
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsBetaChannel));
    }

    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatusText);
    partial void OnUpdateStatusTextChanged(string value) => OnPropertyChanged(nameof(HasUpdateStatus));

    public bool HasUpdateProgress => UpdateBusy && UpdateProgress > 0;
    partial void OnUpdateProgressChanged(int value) => OnPropertyChanged(nameof(HasUpdateProgress));
    partial void OnUpdateBusyChanged(bool value) => OnPropertyChanged(nameof(HasUpdateProgress));

    internal UpdateService Updates => updates;

    private bool CanRunUpdate => !UpdateBusy;
    private bool CanInstallUpdate => UpdateAvailable && !UpdateBusy;

    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private async Task CheckForAppUpdatesAsync()
    {
        UpdateBusy = true;
        UpdateStatusText = Loc("Settings.Updates.Checking");
        try
        {
            UpdateCheckResult result = await updates.CheckAsync();
            UpdateCurrentVersion = updates.CurrentVersion;
            UpdateAvailable = result.Outcome == UpdateOutcome.Available;
            UpdateStatusText = result.Outcome switch
            {
                UpdateOutcome.DevelopmentMode => string.Format(CultureInfo.CurrentCulture,
                    Loc("Settings.Updates.DevMode"), UpdateCurrentVersion),
                UpdateOutcome.UpToDate => string.Format(CultureInfo.CurrentCulture,
                    Loc("Settings.Updates.UpToDate"), UpdateCurrentVersion),
                UpdateOutcome.Available => string.Format(CultureInfo.CurrentCulture,
                    Loc("Settings.Updates.Available"), result.Version ?? ""),
                _ => UpdateErrorText(result.Error)
            };
        }
        finally { UpdateBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task DownloadAndInstallUpdateAsync()
    {
        UpdateBusy = true;
        UpdateProgress = 0;
        UpdateStatusText = Loc("Settings.Updates.Downloading");
        try
        {
            var progress = new Progress<int>(value =>
            {
                UpdateProgress = value;
                UpdateStatusText = string.Format(CultureInfo.CurrentCulture,
                    Loc("Settings.Updates.DownloadingProgress"), value);
            });
            UpdateDownloadResult result = await updates.DownloadAsync(progress);
            if (!result.Success)
            {
                UpdateStatusText = UpdateErrorText(result.Error);
                return;
            }
            UpdateAvailable = false;
            UpdateStatusText = Loc("Settings.Updates.Installing");
            UpdateError apply = updates.ApplyAndRestart();
            if (apply != UpdateError.None) UpdateStatusText = UpdateErrorText(apply);
        }
        finally { UpdateBusy = false; }
    }

    private string UpdateErrorText(UpdateError error) => string.Format(CultureInfo.CurrentCulture,
        Loc(UpdateService.KeyFor(error)), UpdateCurrentVersion);

    // Tool pins live in the same settings.json, so choosing a version writes through the
    // merging Save(): only tools.<tool>.version is touched, every other node survives.
    private readonly Dictionary<string, string?> toolPins = new(StringComparer.Ordinal);

    // Only a version the user picked is written; the pins read at startup are seeded here so a
    // later save writes the full tools section in the shape Core/Tools expects.
    internal bool SetToolPin(string tool, string? version)
    {
        if (toolPins.TryGetValue(tool, out string? known) && string.Equals(known, version, StringComparison.Ordinal)) return true;
        toolPins[tool] = version;
        return Save();
    }

    internal void NoteToolPin(string tool, string? version) => toolPins[tool] = version;

    public bool HasSecretHint => !string.IsNullOrEmpty(SecretHintText);
    partial void OnSecretHintTextChanged(string value) => OnPropertyChanged(nameof(HasSecretHint));

    public bool HasSecretStatus => !string.IsNullOrEmpty(SecretStatusText);
    partial void OnSecretStatusTextChanged(string value) => OnPropertyChanged(nameof(HasSecretStatus));

    private bool CanMigrate => MigrationConsent && !SecretsBusy;
    private bool CanDelete => ConfirmDelete && !SecretsBusy;
    private bool CanManageSecrets => !SecretsBusy;

    // Reads a random key that was never written: reports availability without touching any secret.
    [RelayCommand(CanExecute = nameof(CanManageSecrets))]
    private async Task RefreshStatusAsync() => await UpdateStatusAsync();

    private async Task UpdateStatusAsync()
    {
        string state = "available", error = "";
        try
        {
            if (await activeStore.GetAsync("probe-" + Guid.NewGuid().ToString("N"), CancellationToken.None) is { } probe)
                CryptographicOperations.ZeroMemory(probe);
        }
        catch (SecretStoreException ex) { (state, error) = Describe(ex.Code); }
        catch (CryptographicException ex) { (state, error) = Describe(CodeOf(ex)); }
        // shortcut: legacy entries are detected by the <key>.bin file, not by EncryptedStore.NeedsMigration,
        // which is only known after a load; upgrade when the account services can report their own state.
        bool pending = secretKeys.Any(key => File.Exists(Path.Combine(secretDirectory, key + ".bin")));
        string hint = HintFor(state);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            NeedsMigration = pending;
            SecretStateText = LocalizationService.Instance["Settings.Secrets.State." + state];
            SecretHintText = hint;
            if (error.Length > 0) SecretStatusText = error;
        });
    }

    // Maps a SECRET_STORE_* code onto the state label and localized message.
    private static (string State, string Error) Describe(string code) => code switch
    {
        "SECRET_STORE_LOCKED" => ("locked", Loc("Settings.Secrets.Error.Locked")),
        "SECRET_STORE_CONSENT_REQUIRED" => ("unavailable", Loc("Settings.Secrets.Error.ConsentRequired")),
        "SECRET_STORE_TIMEOUT" => ("unavailable", Loc("Settings.Secrets.Error.Timeout")),
        _ => ("unavailable", Loc("Settings.Secrets.Error.Unavailable"))
    };

    private static string CodeOf(CryptographicException ex)
    {
        int colon = ex.Message.IndexOf(':');
        return ex.Message.StartsWith("SECRET_STORE_", StringComparison.Ordinal) && colon > 0 ? ex.Message[..colon] : "SECRET_STORE_UNAVAILABLE";
    }

    private static string Loc(string key) => LocalizationService.Instance[key];

    private string HintFor(string state) => state switch
    {
        "locked" => Loc("Settings.Secrets.Hint.UnlockWallet"),
        "unavailable" when SecretProvider == "libsecret" => Loc("Settings.Secrets.Hint.NoDaemon"),
        "unavailable" => Loc("Settings.Secrets.Hint.Unavailable"),
        _ => ""
    };

    [RelayCommand(CanExecute = nameof(CanMigrate))]
    private async Task MigrateSecretsAsync()
    {
        // ExecuteAsync ignores CanExecute, so the consent check is enforced here too.
        if (!CanMigrate) return;
        SecretsBusy = true;
        try
        {
            await SecretMigration.SetConsentAsync(secretDirectory, true, CancellationToken.None);
            await migrateSecrets(secretKeys, CancellationToken.None);
            SecretStatusText = Loc("Settings.Secrets.Migrated");
        }
        catch (Exception ex)
        {
            SecretStatusText = ex switch
            {
                SecretStoreException store => Describe(store.Code).Error,
                // Migration verification keeps the legacy file untouched; say so explicitly.
                CryptographicException crypto when crypto.Message.Contains("SECRET_STORE_MIGRATION_FAILED", StringComparison.Ordinal)
                    => Loc("Settings.Secrets.Error.MigrationFailed"),
                CryptographicException crypto => Describe(CodeOf(crypto)).Error,
                IOException or UnauthorizedAccessException => Loc("Settings.Secrets.Error.Unavailable"),
                _ => Loc("Settings.Secrets.Error.Unavailable")
            };
        }
        finally { SecretsBusy = false; }
        await UpdateStatusAsync();
    }

    // Session-only unlock of the consented file store; the password is never persisted.
    [RelayCommand(CanExecute = nameof(CanManageSecrets))]
    private async Task UnlockFallbackAsync()
    {
        if (!FallbackConsent || string.IsNullOrEmpty(FallbackPassword))
        {
            SecretStatusText = Loc("Settings.Secrets.Error.ConsentRequired");
            return;
        }
        string password = FallbackPassword;
        FallbackPassword = "";
        SecretsBusy = true;
        try
        {
            activeStore = new ConsentFileStore(secretDirectory, true, password);
            SecretProvider = ProviderFor(activeStore);
            SecretStatusText = Loc("Settings.Secrets.FallbackUnlocked");
        }
        finally { SecretsBusy = false; }
        await UpdateStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSecretsAsync()
    {
        if (!CanDelete) return;
        SecretsBusy = true;
        string error = "";
        try
        {
            foreach (string key in secretKeys)
            {
                try { await activeStore.DeleteAsync(key, CancellationToken.None); }
                catch (SecretStoreException ex) { error = Describe(ex.Code).Error; }
                catch (CryptographicException ex) { error = Describe(CodeOf(ex)).Error; }
                // A session-only fallback store is not the whole picture: the platform store may still hold the key.
                if (!EncryptedStore.TryDeleteFromStore(SecretStoreFactory.Create(), key))
                    error = error.Length > 0 ? error : Loc("Settings.Secrets.Error.Unavailable");
                try { EncryptedStore.DeleteFiles(Path.Combine(secretDirectory, key + ".bin")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Loc("Settings.Secrets.Error.Unavailable"); }
            }
            SecretStatusText = error.Length > 0 ? error : Loc("Settings.Secrets.Deleted");
        }
        finally
        {
            SecretsBusy = false;
            ConfirmDelete = false;
            NexusSignedIn = false;
        }
        await UpdateStatusAsync();
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        LocalizationService.Instance.CurrentLanguage = value;
        Save();
    }

    partial void OnNexusClientIdChanged(string value) => nexus.ConfigureClientId(value);

    private void Load()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("language", out var language)
                    && language.GetString() is { } value && Languages.Contains(value))
                {
                    SelectedLanguage = value;
                    LocalizationService.Instance.CurrentLanguage = value;
                }
                if (doc.RootElement.TryGetProperty("nexusClientId", out var clientId)
                    && clientId.GetString() is { Length: > 0 } stored)
                    NexusClientId = stored;
            }
        }
        catch { /* corrupt settings never block startup */ }
    }

    // settings.json is shared (language, catalog endpoint, tool pins from Core/Tools, theme,
    // device nick), so every write merges into the existing document under the common gate
    // instead of replacing it, and a corrupt document is left untouched rather than overwritten.
    private bool Save() => Services.SettingsFile.TryUpdate(settingsPath, node =>
    {
        node["language"] = SelectedLanguage;
        node["catalogEndpoint"] = persistedEndpoint;
        node["nexusClientId"] = NexusClientId.Trim();
        if (toolPins.Count > 0)
        {
            if (node["tools"] is not System.Text.Json.Nodes.JsonObject toolsNode) node["tools"] = toolsNode = [];
            foreach ((string tool, string? version) in toolPins)
            {
                if (toolsNode[tool] is not System.Text.Json.Nodes.JsonObject toolNode) toolsNode[tool] = toolNode = [];
                toolNode["version"] = version;
            }
        }
    });
}
