namespace Phaket.Core.Omrl;

// Stable machine codes (spec §10). Never localized; messages come from resources.
public static class ErrorCodes
{
    public const string UnsupportedSchemaVersion = "UNSUPPORTED_SCHEMA_VERSION";
    public const string UnknownOperation = "UNKNOWN_OPERATION";
    public const string InvalidUri = "INVALID_URI";
    public const string PathOutOfBounds = "PATH_OUT_OF_BOUNDS";
    public const string MissingCapability = "MISSING_CAPABILITY";
    public const string InvalidVersion = "INVALID_VERSION";
    public const string InvalidVersionRange = "INVALID_VERSION_RANGE";
    public const string UnsupportedGameVersion = "UNSUPPORTED_GAME_VERSION";
    public const string AmbiguousTarget = "AMBIGUOUS_TARGET";
    public const string GameBuildUnavailable = "GAME_BUILD_UNAVAILABLE";
    public const string LoaderUnavailable = "LOADER_UNAVAILABLE";
    public const string ArtifactMissing = "ARTIFACT_MISSING";
    public const string ArtifactHashMismatch = "ARTIFACT_HASH_MISMATCH";
    public const string ArtifactDownloadUnavailable = "ARTIFACT_DOWNLOAD_UNAVAILABLE";
    public const string ArchiveUnsafe = "ARCHIVE_UNSAFE";
    public const string OwnershipViolation = "OWNERSHIP_VIOLATION";
    public const string ConflictUnresolvable = "CONFLICT_UNRESOLVABLE";
    public const string JournalDivergence = "JOURNAL_DIVERGENCE";
    public const string ValidationFailed = "VALIDATION_FAILED";
}

public sealed record PhaketError(string Code, IReadOnlyDictionary<string, string> Parameters)
{
    public string Message(string locale) => ErrorMessages.Format(Code, locale, Parameters);
}

public sealed class PhaketException : Exception
{
    public PhaketError Error { get; }

    public PhaketException(string code, params (string Key, string Value)[] parameters)
        : base(code)
    {
        Error = new PhaketError(code, parameters.ToDictionary(p => p.Key, p => p.Value));
    }

    public string LocalizedMessage(string locale) => Error.Message(locale);
}
