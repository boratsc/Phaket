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

// Encrypted (TokenProtector) atomic file store for provider tokens.
internal sealed class EncryptedStore(string path)
{
    private readonly TokenProtector protector = new(path);

    public byte[]? Load()
    {
        try { return protector.Unprotect(Convert.FromBase64String(File.ReadAllText(path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException) { return null; }
    }

    public async Task SaveAsync(byte[] plaintext, CancellationToken ct)
    {
        byte[] encrypted = protector.Protect(plaintext);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(encrypted));
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    public void Delete()
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
