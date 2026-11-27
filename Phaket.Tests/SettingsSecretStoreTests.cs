using System.Security.Cryptography;
using Phaket.Core.Accounts;
using Phaket.Core.Tests;
using Phaket.Services;
using Phaket.ViewModels;
using Avalonia.Headless.XUnit;

namespace Phaket.Tests;

// Settings secret-store section (PAK-61 / K5c): status, migration consent,
// fallback unlock and confirmed deletion, all against fake stores and a seam.
public class SettingsSecretStoreTests
{
    private static readonly byte[] Plain = [1, 2, 3];

    private sealed record Harness(string Directory, SettingsViewModel ViewModel, FakeStore Store) : IDisposable
    {
        public void Dispose()
        {
            LocalizationService.Instance.CurrentLanguage = "pl-PL";
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }

    private static Harness Create(FakeStore? store = null, string? provider = null, IReadOnlyList<string>? keys = null,
                                  Func<IReadOnlyList<string>, CancellationToken, Task>? migrate = null, string language = "pl-PL")
    {
        string dir = System.IO.Directory.CreateTempSubdirectory("phaket-secrets-").FullName;
        var fake = store ?? new FakeStore();
        LocalizationService.Instance.CurrentLanguage = language;
        var vm = new SettingsViewModel(
            Path.Combine(dir, "settings.json"),
            new SettingsViewModel.SecretStoreSeam(fake, dir, keys ?? ["epic"], provider, migrate));
        return new Harness(dir, vm, fake);
    }

    private static string CreateLegacy(string directory, string key = "epic")
    {
        byte[] blob;
        if (OperatingSystem.IsWindows()) blob = ProtectedData.Protect(Plain, null, DataProtectionScope.CurrentUser);
        else
        {
            byte[] secret = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12);
            File.WriteAllBytes(Path.Combine(directory, "key.bin"), secret);
            blob = new byte[12 + Plain.Length + 16];
            nonce.CopyTo(blob, 0);
            using var aes = new AesGcm(secret, 16);
            aes.Encrypt(nonce, Plain, blob.AsSpan(12, Plain.Length), blob.AsSpan(12 + Plain.Length, 16));
        }
        string path = Path.Combine(directory, key + ".bin");
        File.WriteAllText(path, Convert.ToBase64String(blob));
        return path;
    }

    // Recording fake: real values when no failure code is set, otherwise every
    // operation throws the given SECRET_STORE_* error.
    private sealed class FakeStore(string? failCode = null) : ISecretStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        public List<string> DeletedKeys { get; } = [];
        public List<string> ReadKeys { get; } = [];

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            ReadKeys.Add(key);
            if (failCode is not null) throw Failure(failCode);
            return Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
        {
            if (failCode is not null) throw Failure(failCode);
            values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (failCode is not null) throw Failure(failCode);
            values.Remove(key);
            DeletedKeys.Add(key);
            return Task.CompletedTask;
        }

        private static SecretStoreException Failure(string code) => new(code, "Magazyn jest zablokowany / Secret store is locked.");
    }

    [AvaloniaFact]
    public async Task Status_reports_provider_and_available_state()
    {
        using var h = Create(provider: "libsecret");
        await h.ViewModel.RefreshStatusCommand.ExecuteAsync(null);
        Assert.Equal("libsecret", h.ViewModel.SecretProvider);
        Assert.Equal("dostępny", h.ViewModel.SecretStateText);
        Assert.False(h.ViewModel.NeedsMigration);
        Assert.Equal("", h.ViewModel.SecretHintText);
        // The probe reads a random key, never a stored account secret.
        Assert.All(h.Store.ReadKeys, key => Assert.StartsWith("probe-", key, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Status_reports_locked_state_and_unlock_hint()
    {
        using var h = Create(new FakeStore("SECRET_STORE_LOCKED"), provider: "libsecret");
        await h.ViewModel.RefreshStatusCommand.ExecuteAsync(null);
        Assert.Equal("zablokowany", h.ViewModel.SecretStateText);
        Assert.Equal(h.ViewModel.SecretHintText, LocalizationService.Instance["Settings.Secrets.Hint.UnlockWallet"]);
        Assert.Contains("zablokowany", h.ViewModel.SecretStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Status_reports_unavailable_state_with_install_hint_on_libsecret()
    {
        using var h = Create(new FakeStore("SECRET_STORE_UNAVAILABLE"), provider: "libsecret", language: "en");
        await h.ViewModel.RefreshStatusCommand.ExecuteAsync(null);
        Assert.Equal("unavailable", h.ViewModel.SecretStateText);
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Hint.NoDaemon"], h.ViewModel.SecretHintText);
        Assert.Contains("gnome-keyring-daemon", h.ViewModel.SecretHintText);
    }

    [AvaloniaFact]
    public async Task Legacy_files_raise_needs_migration_flag()
    {
        using var h = Create();
        CreateLegacy(h.Directory);
        await h.ViewModel.RefreshStatusCommand.ExecuteAsync(null);
        Assert.True(h.ViewModel.NeedsMigration);
    }

    [AvaloniaFact]
    public async Task Migration_without_consent_is_not_executable()
    {
        List<string[]> migrated = [];
        using var h = Create(migrate: (keys, _) => { migrated.Add(keys.ToArray()); return Task.CompletedTask; });
        Assert.False(h.ViewModel.MigrateSecretsCommand.CanExecute(null));
        await h.ViewModel.MigrateSecretsCommand.ExecuteAsync(null);
        Assert.Empty(migrated);
        Assert.False(File.Exists(Path.Combine(h.Directory, "secrets-meta", "migration-consent")));
    }

    [AvaloniaFact]
    public async Task Migration_with_consent_runs_migration_and_records_consent()
    {
        List<string[]> migrated = [];
        using var h = Create(migrate: (keys, _) => { migrated.Add(keys.ToArray()); return Task.CompletedTask; });
        h.ViewModel.MigrationConsent = true;
        Assert.True(h.ViewModel.MigrateSecretsCommand.CanExecute(null));
        await h.ViewModel.MigrateSecretsCommand.ExecuteAsync(null);
        Assert.Equal(["epic"], Assert.Single(migrated));
        Assert.Equal("1", File.ReadAllText(Path.Combine(h.Directory, "secrets-meta", "migration-consent")));
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Migrated"], h.ViewModel.SecretStatusText);
    }

    [AvaloniaFact]
    public async Task Default_migration_delegate_calls_core_migration_and_moves_legacy_file()
    {
        using var h = Create();
        string legacy = CreateLegacy(h.Directory), original = File.ReadAllText(legacy);
        await h.ViewModel.RefreshStatusCommand.ExecuteAsync(null);
        Assert.True(h.ViewModel.NeedsMigration);
        h.ViewModel.MigrationConsent = true;
        await h.ViewModel.MigrateSecretsCommand.ExecuteAsync(null);
        Assert.False(File.Exists(legacy));
        Assert.Equal(original, File.ReadAllText(Path.Combine(h.Directory, "migrated", "epic.bak")));
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Migrated"], h.ViewModel.SecretStatusText);
        Assert.False(h.ViewModel.NeedsMigration);
    }

    [AvaloniaFact]
    public async Task Default_migration_without_consent_reports_error_and_keeps_legacy_file()
    {
        using var h = Create();
        string legacy = CreateLegacy(h.Directory), original = File.ReadAllText(legacy);
        // Direct call without consent: core refuses, nothing is moved.
        var error = await Assert.ThrowsAsync<SecretStoreException>(() => SecretMigration.MigrateAllAsync(h.Directory, ["epic"], h.Store, TestContext.Current.CancellationToken));
        Assert.Equal("SECRET_STORE_CONSENT_REQUIRED", error.Code);
        Assert.Equal(original, File.ReadAllText(legacy));
    }

    [AvaloniaFact]
    public async Task Fallback_unlock_without_password_reports_localized_error()
    {
        using var h = Create(provider: "libsecret");
        h.ViewModel.FallbackConsent = true;
        await h.ViewModel.UnlockFallbackCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Error.ConsentRequired"], h.ViewModel.SecretStatusText);
        Assert.Equal("libsecret", h.ViewModel.SecretProvider);
        h.ViewModel.FallbackPassword = "hunter2";
        h.ViewModel.FallbackConsent = false;
        await h.ViewModel.UnlockFallbackCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Error.ConsentRequired"], h.ViewModel.SecretStatusText);
    }

    [AvaloniaFact]
    public async Task Fallback_unlock_switches_store_and_never_persists_password()
    {
        using var h = Create(provider: "libsecret");
        h.ViewModel.FallbackConsent = true;
        h.ViewModel.FallbackPassword = "hunter2";
        await h.ViewModel.UnlockFallbackCommand.ExecuteAsync(null);
        Assert.Equal("consent-file", h.ViewModel.SecretProvider);
        Assert.Equal("", h.ViewModel.FallbackPassword);
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.FallbackUnlocked"], h.ViewModel.SecretStatusText);
        Assert.False(File.Exists(Path.Combine(h.Directory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(h.Directory, "secrets-meta", "fallback-password")));
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.State.available"], h.ViewModel.SecretStateText);
    }

    [AvaloniaFact]
    public async Task Delete_without_confirmation_is_not_executable()
    {
        using var h = Create();
        CreateLegacy(h.Directory);
        Assert.False(h.ViewModel.DeleteSecretsCommand.CanExecute(null));
        await h.ViewModel.DeleteSecretsCommand.ExecuteAsync(null);
        Assert.Empty(h.Store.DeletedKeys);
        Assert.True(File.Exists(Path.Combine(h.Directory, "epic.bin")));
    }

    [AvaloniaFact]
    public async Task Delete_with_confirmation_removes_all_keys_and_legacy_files()
    {
        using var h = Create(keys: ["epic", "steam", "itch", "account", "steam-apikey"]);
        foreach (string key in new[] { "epic", "steam" }) File.WriteAllText(Path.Combine(h.Directory, key + ".bin"), "legacy");
        h.ViewModel.ConfirmDelete = true;
        await h.ViewModel.DeleteSecretsCommand.ExecuteAsync(null);
        Assert.Equal(["epic", "steam", "itch", "account", "steam-apikey"], h.Store.DeletedKeys);
        Assert.False(File.Exists(Path.Combine(h.Directory, "epic.bin")));
        Assert.False(File.Exists(Path.Combine(h.Directory, "steam.bin")));
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Deleted"], h.ViewModel.SecretStatusText);
        Assert.False(h.ViewModel.ConfirmDelete);
        Assert.False(h.ViewModel.NeedsMigration);
    }

    // R3c 4: the device key is a secret too. Deleting secrets drops it from the store and the
    // key store forgets the identity; a new key is made only on the next signing request.
    [AvaloniaFact]
    public async Task Delete_with_confirmation_removes_the_device_key_and_a_new_one_comes_on_demand()
    {
        using var h = Create(keys: ["epic", DeviceKeyStore.SecretKey]);
        var keys = new DeviceKeyStore(h.Store, Path.Combine(h.Directory, "settings.json"));
        DeviceIdentityServices.SetCurrent(new DeviceIdentityServices(keys, new DeviceKeySigner(keys, registerBeforeSigning: false)));
        string oldKey = Convert.ToBase64String((await keys.GetPublicIdentityAsync(TestContext.Current.CancellationToken)).PublicKey);
        Assert.NotNull(await h.Store.GetAsync(DeviceKeyStore.SecretKey, TestContext.Current.CancellationToken));

        h.ViewModel.ConfirmDelete = true;
        await h.ViewModel.DeleteSecretsCommand.ExecuteAsync(null);

        Assert.Contains(DeviceKeyStore.SecretKey, h.Store.DeletedKeys);
        Assert.Null(await h.Store.GetAsync(DeviceKeyStore.SecretKey, TestContext.Current.CancellationToken));
        Assert.Null(keys.CachedDeviceKey);

        // A later request makes a different key: the old identity is gone for good.
        string newKey = Convert.ToBase64String((await keys.GetPublicIdentityAsync(TestContext.Current.CancellationToken)).PublicKey);
        Assert.NotEqual(oldKey, newKey);
    }

    [AvaloniaFact]
    public async Task Delete_after_migration_leaves_no_decryptable_copy()
    {
        using var h = Create();
        string legacy = CreateLegacy(h.Directory), original = File.ReadAllText(legacy);
        h.ViewModel.MigrationConsent = true;
        await h.ViewModel.MigrateSecretsCommand.ExecuteAsync(null);
        string backup = Path.Combine(h.Directory, "migrated", "epic.bak"), legacyKey = Path.Combine(h.Directory, "key.bin");
        Assert.Equal(original, File.ReadAllText(backup));
        if (!OperatingSystem.IsWindows()) Assert.True(File.Exists(legacyKey));

        h.ViewModel.ConfirmDelete = true;
        await h.ViewModel.DeleteSecretsCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Deleted"], h.ViewModel.SecretStatusText);
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(legacyKey));
        Assert.False(File.Exists(Path.Combine(h.Directory, "secrets-meta", "epic.migrated")));
        Assert.False(File.Exists(Path.Combine(h.Directory, "secrets-meta", "epic.pending")));
        // The store entry is gone too: nothing can read the secret back.
        Assert.Null(await h.Store.GetAsync("epic", TestContext.Current.CancellationToken));
        var reader = new EncryptedStore(Path.Combine(h.Directory, "epic.bin"), h.Store);
        Assert.Null(await reader.LoadAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Delete_reports_store_failure_and_still_clears_confirmation()
    {
        using var h = Create(new FakeStore("SECRET_STORE_LOCKED"), keys: ["epic", "steam"]);
        h.ViewModel.ConfirmDelete = true;
        await h.ViewModel.DeleteSecretsCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["Settings.Secrets.Error.Locked"], h.ViewModel.SecretStatusText);
        Assert.False(h.ViewModel.ConfirmDelete);
    }
}
