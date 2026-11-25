namespace Phaket.Core.Nexus;

// Response shapes of Nexus REST v1 that the free (non-Premium) key can read.
public sealed record NexusRateLimits(int? HourlyRemaining, int? DailyRemaining);

public sealed record NexusUser(string Name, bool IsPremium, string? ProfileUrl);

public sealed record NexusFileInfo(int FileId, string Name, string? FileName, string? Version, string? Category, long? SizeKb);

public sealed record NexusModMetadata(int ModId, string Name, string? Author, string? Version, string? Summary,
    long? Endorsements, bool Available);

public sealed record NexusDownloadLink(string Uri, string? Name, long? SizeKb);

// Reserved slot for the planned OAuth2+PKCE sign-in (separate issue): the secret store
// keeps access+refresh+expiry next to the API key so the switch needs no migration.
public sealed record NexusOAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

// Codes are stable identifiers; the UI maps each one to a bilingual message.
public sealed class NexusApiException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}