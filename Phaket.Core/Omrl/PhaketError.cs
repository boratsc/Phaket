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
    public const string ArtifactDownloadTimeout = "ARTIFACT_DOWNLOAD_TIMEOUT";
    public const string ArtifactSizeMismatch = "ARTIFACT_SIZE_MISMATCH";
    public const string ModSourceInvalid = "MOD_SOURCE_INVALID";
    public const string ModSourceHostNotAllowed = "MOD_SOURCE_HOST_NOT_ALLOWED";
    // No catalog artifact of this mod version serves the installation's arch/store.
    public const string ModArtifactUnavailable = "MOD_ARTIFACT_UNAVAILABLE";
    // The catalog artifact carries no declared SHA-256. The observed hash is never promoted
    // to a pin: without a server-declared hash there is nothing to trust (no TOFU).
    public const string ModArtifactHashUndeclared = "MOD_ARTIFACT_HASH_UNDECLARED";
    // PAK-97: the plan combines a SusModder full mod with a dll the upstream matrix marks "does not work".
    public const string ModCompatibilityBlocked = "MOD_COMPATIBILITY_BLOCKED";
    // PAK-51: the catalog response carries a signature that does not match its body, or a key id
    // outside the built-in keys. The response is refused and the last good snapshot is kept.
    public const string CatalogSignatureInvalid = "CATALOG_SIGNATURE_INVALID";
    public const string CatalogSignatureUnknownKey = "CATALOG_SIGNATURE_UNKNOWN_KEY";
    // A replayed or stale catalog answer (revision not newer than the cached one).
    public const string CatalogRevisionStale = "CATALOG_REVISION_STALE";
    public const string ArchiveInvalid = "ARCHIVE_INVALID";
    public const string ArchiveFormatUnsupported = "ARCHIVE_FORMAT_UNSUPPORTED";
    public const string PlanNotAccepted = "PLAN_NOT_ACCEPTED";
    public const string BaseGameMismatch = "BASE_GAME_MISMATCH";
    public const string ArchiveUnsafe = "ARCHIVE_UNSAFE";
    public const string OwnershipViolation = "OWNERSHIP_VIOLATION";
    public const string ConflictUnresolvable = "CONFLICT_UNRESOLVABLE";
    // PAK-142: the file was changed both in the installation and by the recipe since the
    // last agreed baseline. The host asks the user before either side is overwritten.
    public const string FileConflictRequiresDecision = "FILE_CONFLICT_REQUIRES_DECISION";
    // PAK-142: the baseline for this installation cannot be trusted (wrong install id or
    // an edited state hash), so it must be rebuilt before conflicts can be judged.
    public const string BaselineInvalid = "BASELINE_INVALID";
    public const string JournalDivergence = "JOURNAL_DIVERGENCE";
    public const string ValidationFailed = "VALIDATION_FAILED";
    // A recipe conditions on arch/buildId but the host supplied no such fact: refusing to plan
    // beats silently producing an empty plan that looks like a successful install.
    public const string InstallFactsMissing = "INSTALL_FACTS_MISSING";
    // The recipe matched no steps for these facts: nothing to write, so nothing is installed.
    public const string PlanEmpty = "PLAN_EMPTY";
    // A session folder or token file could not be made private: the secret is never written
    // where other accounts could read it (PAK-66, fail closed instead of fail open).
    public const string SessionPrivacyFailed = "SESSION_PRIVACY_FAILED";
    // Mod provision needs a pinned vanilla build; without the manifest the tool would fetch
    // the current depot and silently break the mod's version pin (PAK-102).
    public const string BaseGameManifestMissing = "BASE_GAME_MANIFEST_MISSING";
    // Steam sign-in lives in Phaket (PAK-60). A provision without it cannot answer the
    // tool's prompt, so it refuses with the one action that fixes it.
    public const string BaseGameSignInRequired = "BASE_GAME_SIGN_IN_REQUIRED";
    public const string BaseGameAuthCancelled = "BASE_GAME_AUTH_CANCELLED";
    public const string BaseGameTimeout = "BASE_GAME_TIMEOUT";
    public const string BaseGameToolFailed = "BASE_GAME_TOOL_FAILED";
    public const string EpicSignInRequired = "EPIC_SIGN_IN_REQUIRED";
    public const string DepotDownloaderDotnetMissing = "DEPOTDOWNLOADER_DOTNET_NOT_FOUND";
// PAK-141: save backups. The limit is checked before anything is copied.
    public const string SaveBackupTooLarge = "SAVE_BACKUP_TOO_LARGE";
    public const string SaveBackupHashMismatch = "SAVE_BACKUP_HASH_MISMATCH";
    public const string SaveBackupManifestInvalid = "SAVE_BACKUP_MANIFEST_INVALID";
    public const string SaveBackupRestoreRefused = "SAVE_BACKUP_RESTORE_REFUSED";
    // Deleting old backups is irreversible; it only runs with the user's explicit consent.
    public const string SaveBackupPruneConsentRequired = "SAVE_BACKUP_PRUNE_CONSENT_REQUIRED";
    // PAK-146: a package tree is never half-materialized onto a full disk; the worst-case
    // size (sum of cache files) is checked before the first copy.
    public const string DiskFull = "DISK_FULL";
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
