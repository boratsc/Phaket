using System.Security.Cryptography;
using System.Text.Json;
using Phaket.Core.Accounts;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Phaket.Services;

public sealed record DevicePublicIdentity(byte[] PublicKey, string Fingerprint, string Nick);

public sealed class DeviceKeyStore
{
    public const string SecretKey = "device-ed25519";
    // The old shared default collided across users; it is kept only to migrate stored settings.
    public const string DefaultNick = "Phaket device";
    private const int PrivateKeyLength = 32;
    private readonly ISecretStore secrets;
    private readonly string settingsPath;
    private readonly SemaphoreSlim keyGate = new(1, 1);
    private int online;

    public event EventHandler? RegistrationStateChanged;

    private DevicePublicIdentity? cachedIdentity;
    public bool IsOnline => Volatile.Read(ref online) != 0;
    public string? CachedDeviceKey => Volatile.Read(ref cachedIdentity) is { } identity
        ? Convert.ToBase64String(identity.PublicKey) : null;

    public DeviceKeyStore(ISecretStore secrets, string settingsPath)
    {
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        this.settingsPath = Path.GetFullPath(settingsPath ?? throw new ArgumentNullException(nameof(settingsPath)));
    }

    public async Task<DevicePublicIdentity> GetPublicIdentityAsync(CancellationToken cancellationToken = default)
    {
        byte[] seed = await GetPrivateSeedAsync(cancellationToken);
        try
        {
            var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
            byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();
            var fingerprint = FingerprintOf(publicKey);
            var (nick, shouldPersistNick) = await GetNickAsync(fingerprint, cancellationToken);
            if (shouldPersistNick)
            {
                try { await SaveNickAsync(nick, cancellationToken); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { System.Diagnostics.Trace.TraceWarning("The default device nickname could not be saved: {0}", ex); }
            }
            var identity = new DevicePublicIdentity(publicKey, fingerprint, nick);
            Volatile.Write(ref cachedIdentity, identity);
            return identity;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        byte[] seed = await GetPrivateSeedAsync(cancellationToken);
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
            byte[] bytes = message.ToArray();
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return signer.GenerateSignature();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public async Task SaveNickAsync(string nick, CancellationToken cancellationToken = default)
    {
        string value = nick?.Trim() ?? "";
        if (!IsValidNick(value)) throw new ArgumentException("Invalid device nick.", nameof(nick));
        await SettingsFile.UpdateAsync(settingsPath, node => node["deviceNick"] = value, cancellationToken).ConfigureAwait(false);
    }

    internal void SetOnline(bool value)
    {
        int next = value ? 1 : 0;
        if (Interlocked.Exchange(ref online, next) != next)
            RegistrationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsValidNick(string? nick) => nick is { Length: > 0 and <= 40 }
        && !string.IsNullOrWhiteSpace(nick) && !nick.Any(char.IsControl);

    public static string FingerprintOf(byte[] publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..8];

    // Unique per device: the same fingerprint tail the identity card shows.
    public static string GeneratedNick(string fingerprint) =>
        LocalizationService.Instance["Identity.NickPrefix"] + "-" + fingerprint[..4];

    private async Task<(string Nick, bool ShouldPersist)> GetNickAsync(string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(settingsPath)) return (GeneratedNick(fingerprint), true);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (GeneratedNick(fingerprint), false);
            if (document.RootElement.TryGetProperty("deviceNick", out var nick))
            {
                var value = nick.ValueKind == JsonValueKind.String ? nick.GetString()!.Trim() : "";
                // A nickname the user chose is never replaced; only the old shared default is.
                if (IsValidNick(value)) return value == DefaultNick ? (GeneratedNick(fingerprint), true) : (value, false);
                return (GeneratedNick(fingerprint), false);
            }
            return (GeneratedNick(fingerprint), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (GeneratedNick(fingerprint), false);
        }
    }

    private async Task<byte[]> GetPrivateSeedAsync(CancellationToken cancellationToken)
    {
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            byte[]? stored = await secrets.GetAsync(SecretKey, cancellationToken);
            if (stored is not null) return ValidateSeed(stored);

            byte[] candidate = RandomNumberGenerator.GetBytes(PrivateKeyLength);
            try
            {
                await secrets.SetAsync(SecretKey, candidate, cancellationToken);
                stored = await secrets.GetAsync(SecretKey, cancellationToken);
                if (stored is null) throw new CryptographicException("The secret store did not retain the device key.");
                return ValidateSeed(stored);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(candidate);
            }
        }
        finally
        {
            keyGate.Release();
        }
    }

    private static byte[] ValidateSeed(byte[] seed)
    {
        if (seed.Length == PrivateKeyLength) return seed;
        CryptographicOperations.ZeroMemory(seed);
        throw new CryptographicException("The stored device key has an invalid length.");
    }
}
