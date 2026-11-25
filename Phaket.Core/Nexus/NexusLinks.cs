using System.Globalization;

namespace Phaket.Core.Nexus;

// nxm://{gameDomain}/mods/{modId}/files/{fileId}?key=...&expires=...
// The key/expires pair is a short-lived download token minted by the site; it is never
// persisted or logged (see PAK-117).
public sealed record NxmLink(string GameDomain, int ModId, int FileId, string? Key, string? Expires)
{
    public bool HasDownloadToken => !string.IsNullOrEmpty(Key) && !string.IsNullOrEmpty(Expires);

    public static bool TryParse(string? value, out NxmLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length > 0 || !IsDomain(uri.IdnHost))
            return false;

        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("files", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int modId) || modId <= 0) return false;
        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int fileId) || fileId <= 0) return false;

        string? key = null, expires = null;
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            string name = pair[..equals];
            string raw = Uri.UnescapeDataString(pair[(equals + 1)..]);
            if (name.Equals("key", StringComparison.OrdinalIgnoreCase)) key = raw;
            else if (name.Equals("expires", StringComparison.OrdinalIgnoreCase)) expires = raw;
        }
        // An expired or malformed token is left for the API to judge: the site sets both values together.
        if (expires is { Length: > 0 } && !expires.All(char.IsAsciiDigit)) expires = null;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(expires)) { key = null; expires = null; }

        link = new(uri.IdnHost.ToLowerInvariant(), modId, fileId, key, expires);
        return true;
    }

    // Keeps the token out of logs, exception text and UI.
    public override string ToString() => $"nxm://{GameDomain}/mods/{ModId}/files/{FileId}";

    internal static bool IsDomain(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

// A mod page from the site or a bare id typed next to the selected game. A numeric game
// id in /games/{id}/mods/{mod} is kept so the caller can check it against the chosen game.
public sealed record NexusModReference(string? Domain, int ModId, int? GameId = null)
{
    public static bool TryParse(string? value, out NexusModReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int bareId) && bareId > 0)
        {
            reference = new(null, bareId);
            return true;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(uri.Host, "www.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "nexusmods.com", StringComparison.OrdinalIgnoreCase)) ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Contains('@', StringComparison.Ordinal))
            return false;

        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? domain = null;
        int? gameId = null;
        int idIndex;
        if (parts.Length >= 4 && parts[0].Equals("games", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Equals("mods", StringComparison.OrdinalIgnoreCase))
        {
            idIndex = 3;
            if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedGameId) && parsedGameId > 0)
                gameId = parsedGameId;
            else if (NxmLink.IsDomain(parts[1]))
                domain = parts[1].ToLowerInvariant();
            else
                return false;
        }
        else if (parts.Length >= 3 && parts[1].Equals("mods", StringComparison.OrdinalIgnoreCase))
            idIndex = 2;
        else
            return false;
        if (!int.TryParse(parts[idIndex], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0) return false;

        if (domain is null && gameId is null && parts[1].Equals("mods", StringComparison.OrdinalIgnoreCase) && NxmLink.IsDomain(parts[0]))
            domain = parts[0].ToLowerInvariant();
        reference = new(domain, id, gameId);
        return true;
    }
}