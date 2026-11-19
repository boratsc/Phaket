using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Services;
using Phaket.Core.Accounts;
using Phaket.Core.Tools;

namespace Phaket.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "settings.json");

    internal static string DefaultSecretDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket");

    // Secret keys are the base names of the protected account blobs (see TokenProtector),
    // plus the Steam install sign-in, which is stored under its own key (PAK-60).
    // "steam-apikey" is no longer written anywhere (the local Steam library needs no key);
    // it stays in the list so deleting secrets also clears it from existing installs.
    private static readonly IReadOnlyList<string> DefaultSecretKeys = ["epic", "steam", "itch", "account", "steam-apikey", SteamCredentialStore.SecretKey];

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

    public ObservableCollection<string> Languages { get; } = [];
    [ObservableProperty] private string selectedSection = "general";
    public bool IsGeneralSection => SelectedSection == "general";
    public bool IsCatalogSection => SelectedSection == "catalog";
    public bool IsToolsSection => SelectedSection == "tools";
    public bool IsSecretsSection => SelectedSection == "secrets";

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(IsCatalogSection));
        OnPropertyChanged(nameof(IsToolsSection));
        OnPropertyChanged(nameof(IsSecretsSection));
    }

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (section is "general" or "catalog" or "tools" or "secrets") SelectedSection = section;
    }

    // Narzędzia: the version pins Core/Tools reads from settings.json, plus the
    // release list and a forced update check for each tool.
    public ToolSettingsSection Legendary { get; }
    public ToolSettingsSection Depot { get; }

    internal string SettingsFile => settingsPath;

    [ObservableProperty]
    private string selectedLanguage = LocalizationService.Instance.CurrentLanguage;

    public SettingsViewModel(string? settingsPath = null)
        : this(settingsPath, null, null) { }

    // Test seam for the tools section: provider (fake HTTP), its download root and the settings path.
    internal sealed record ToolSeam(ToolProvider Provider, string ToolsRoot);

    internal SettingsViewModel(string? settingsPath, SecretStoreSeam? secrets, ToolSeam? toolSeam = null)
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
        foreach (var language in LocalizationService.Instance.AvailableLanguages)
            Languages.Add(language);
        Legendary = new ToolSettingsSection(this, toolProvider, toolsRoot, "legendary");
        Depot = new ToolSettingsSection(this, toolProvider, toolsRoot, "depotdownloader");
        Load();
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
                try { File.Delete(Path.Combine(secretDirectory, key + ".bin")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Loc("Settings.Secrets.Error.Unavailable"); }
            }
            SecretStatusText = error.Length > 0 ? error : Loc("Settings.Secrets.Deleted");
        }
        finally
        {
            SecretsBusy = false;
            ConfirmDelete = false;
        }
        await UpdateStatusAsync();
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        LocalizationService.Instance.CurrentLanguage = value;
        Save();
    }

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
