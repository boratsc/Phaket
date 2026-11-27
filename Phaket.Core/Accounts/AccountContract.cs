using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Phaket.Core.Accounts;

// Multi-provider account contract (Discord, Steam, Epic, itch.io). Every
// provider is a public OAuth/OpenID client — no secrets ship in the app.
public sealed record AccountInfo(string Provider, string UserId, string Username, string? Extra, DateTimeOffset ObtainedAt);

public interface IAccountService
{
    string Provider { get; }
    AccountInfo? Current { get; }
    event Action? AccountChanged;
    Task<AccountInfo> LoginAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

internal static class AccountPkce
{
    internal static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Challenge(string verifier) => Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static string NewVerifier() => Encode(RandomNumberGenerator.GetBytes(64));
    internal static string NewState() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

internal static class LoopbackCallback
{
    // Receives one GET callback, validates the path, rejects duplicate
    // parameters, answers with a bilingual close-window page, returns the query.
    internal static async Task<Dictionary<string, string>> ReceiveQueryAsync(TcpListener listener, string expectedPath, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var line = new StringBuilder();
        var character = new char[1];
        while (line.Length < 8192 && await reader.ReadAsync(character.AsMemory(), ct) != 0 && character[0] != '\n') line.Append(character[0]);
        var parts = line.ToString().TrimEnd('\r').Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith('/') || !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var callback) || callback.AbsolutePath != expectedPath)
            throw new InvalidOperationException("Nieprawidłowy callback / Invalid callback.");
        var values = new Dictionary<string, string>();
        foreach (var part in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (!values.TryAdd(key, pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : ""))
                throw new InvalidOperationException("Powtórzony parametr callbacku / Duplicate callback parameter.");
        }
        const string html = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><p lang=\"pl\">Autoryzacja zakończona, możesz zamknąć to okno.</p><p lang=\"en\">Authorization complete, you can close this window.</p></body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
        return values;
    }

    internal static void ValidateState(Dictionary<string, string> values, string state)
    {
        if (!values.TryGetValue("state", out string? received) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(received)))
            throw new InvalidOperationException("Autoryzacja odrzucona / Authorization rejected.");
    }
}

// Provider tokens are stored by the platform secret store; legacy files are read only.
internal sealed class EncryptedStore(string path, ISecretStore? store = null)
{
    private readonly TokenProtector protector = new(path, store);
    public bool NeedsMigration { get; private set; }

    public byte[]? Load() => Task.Run(() => LoadAsync()).GetAwaiter().GetResult();

    internal async Task<byte[]?> LoadAsync(CancellationToken ct = default)
    {
        await SecretMigration.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)!;
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string metadata = Path.Combine(directory, "secrets-meta");
            string pending = Path.Combine(metadata, name + ".pending");
            string migrated = Path.Combine(metadata, name + ".migrated");
            string backup = Path.Combine(directory, "migrated", name + ".bak");
            bool inProgress = File.Exists(pending);
            byte[]? value = await protector.UnprotectAsync(ct).ConfigureAwait(false);
            NeedsMigration = false;
            if (!inProgress && value is null && File.Exists(migrated)) throw MigrationError();
            if (inProgress && !File.Exists(fullPath))
            {
                if (value is null || !File.Exists(backup)) throw MigrationError();
                File.WriteAllText(migrated, "1");
                File.Delete(pending);
                return value;
            }
            if (!inProgress && (value is not null || !File.Exists(fullPath))) return value;
            byte[] legacy = TokenProtector.LegacyRead(fullPath);
            NeedsMigration = true;
            if (!File.Exists(Path.Combine(metadata, "migration-consent")))
            {
                if (inProgress) throw MigrationError();
                return legacy;
            }
            if (!inProgress)
            {
                Directory.CreateDirectory(metadata);
                using (File.Create(pending)) { }
            }
            if (value is null)
            {
                await protector.ProtectAsync(legacy, ct).ConfigureAwait(false);
                value = await protector.UnprotectAsync(ct).ConfigureAwait(false);
            }
            if (value is null || !CryptographicOperations.FixedTimeEquals(value, legacy)) throw MigrationError();
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(fullPath, backup, false);
            File.WriteAllText(migrated, "1");
            File.Delete(pending);
            NeedsMigration = false;
            return value;
        }
        finally { SecretMigration.Gate.Release(); }
    }

    private static CryptographicException MigrationError() => new("SECRET_STORE_MIGRATION_FAILED: Nie udało się zweryfikować migracji / Secret migration verification failed.");

    public async Task SaveAsync(byte[] plaintext, CancellationToken ct)
    {
        await SecretMigration.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string metadata = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "secrets-meta");
            if (File.Exists(Path.Combine(metadata, Path.GetFileNameWithoutExtension(path) + ".pending"))) throw MigrationError();
            await protector.ProtectAsync(plaintext, ct).ConfigureAwait(false);
            NeedsMigration = false;
        }
        finally { SecretMigration.Gate.Release(); }
    }

    public void Delete()
    {
        SecretMigration.Gate.Wait();
        try
        {
            Task.Run(() => protector.DeleteAsync()).GetAwaiter().GetResult();
            DeleteFiles(path);
            NeedsMigration = false;
        }
        finally { SecretMigration.Gate.Release(); }
    }

    // Erasing a secret must leave nothing that decrypts it back (PAK-61): the legacy blob, the encrypted
    // backup that migration made and the legacy key file. A file that cannot be erased throws, so the
    // caller never reports a deletion that left a decryptable copy behind.
    internal static void DeleteFiles(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        string name = Path.GetFileNameWithoutExtension(fullPath);
        DeleteFile(fullPath);
        DeleteFile(Path.Combine(directory, "migrated", name + ".bak"));
        DeleteFile(Path.Combine(directory, "key.bin"));
        string metadata = Path.Combine(directory, "secrets-meta");
        if (Directory.Exists(metadata))
        {
            DeleteFile(Path.Combine(metadata, name + ".pending"));
            DeleteFile(Path.Combine(metadata, name + ".migrated"));
        }
    }

    private static void DeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SecretStoreException("SECRET_STORE_UNAVAILABLE", "Nie można usunąć pliku sekretu / Cannot delete a secret file.", ex); }
    }

    // "Delete secrets" must also drop the store entries that back the same keys; a provider without
    // a delete operation leaves them to the active store alone.
    internal static bool TryDeleteFromStore(ISecretStore store, string key) => store is not ISecretKeyDeleter deleter || deleter.TryDelete(key);
}

// Implemented by stores that can erase a key without the current session's password.
internal interface ISecretKeyDeleter
{
    bool TryDelete(string key);
}
